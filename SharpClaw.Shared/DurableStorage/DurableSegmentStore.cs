using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SharpClaw.Shared.DurableStorage;

/// <summary>
/// Provider-independent append-only segmented record store. The store owns no
/// EF types, provider selector, database connection, or global record index.
/// </summary>
public sealed class DurableSegmentStore : IAsyncDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly byte[] SegmentMagic = "SCLOG001"u8.ToArray();
    private const int SegmentHeaderBytes = 40;
    private const int FooterBodyBytes = 48;
    private const int FooterBytes = sizeof(int) + FooterBodyBytes;
    private const int MaxFrameOverheadBytes = 101;
    private const int FooterMarker = -1;
    private const string ManifestFileName = ".stream.manifest";
    private const string StreamIdentityFileName = ".stream.identity";
    private const int MaxStreamIdentityBytes = 4096;
    private const string IdempotencyFileName = ".idempotency";
    private const string ArtifactReferenceFileName = ".artifact-refs";
    private const int IdempotencyEntryBytes = sizeof(long) + 16;
    private const int ArtifactReferenceEntryBytes = sizeof(long) + 16 + 32;
    private readonly DurableStorageOptions _options;
    private readonly DurableStreamPathEncoder _paths;
    private readonly ConcurrentDictionary<string, StreamState> _states = new();
    private readonly ConcurrentDictionary<string, byte> _verifiedSegments = new();
    private readonly FileStream? _writerLease;
    private string? _degradedReason;
    private DateTimeOffset? _lastSuccessfulFlush;
    private int _disposeState;

    public DurableSegmentStore(DurableStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options;
        _paths = new DurableStreamPathEncoder(options.RootDirectory);

        Directory.CreateDirectory(options.RootDirectory);
        if (options.AcquireWriterLease)
        {
            _writerLease = new FileStream(
                Path.Combine(options.RootDirectory, ".writer.lease"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                1,
                FileOptions.WriteThrough);
        }
    }

    public int MaxRecordBytes => _options.MaxRecordBytes;

    public static int MeasureEncodedRecordBody(DurableRecordWrite record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return SerializeRecordBody(record).Length;
    }

    public async ValueTask<DurableAppendReceipt> AppendAsync(
        DurableStreamKey key,
        DurableRecordWrite record,
        DurableWriteMode writeMode = DurableWriteMode.Durable,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record);

        var state = await AcquireStateAsync(key, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(state, cancellationToken).ConfigureAwait(false);
            if (record.Idempotent)
            {
                await EnsureIdempotencyReadyAsync(state, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (record.Idempotent
                && state.IdempotentRecords.TryGetValue(
                    record.RecordId,
                    out var existingSequence))
            {
                return new DurableAppendReceipt(
                    existingSequence,
                    state.RecordCount,
                    record.Timestamp);
            }
            var sequence = state.NextSequence;
            var active = await EnsureActiveSegmentAsync(
                state,
                sequence,
                cancellationToken).ConfigureAwait(false);
            var frame = BuildFrame(active.SegmentId, sequence, record);

            if (ShouldRotate(active, frame.Length + sizeof(int)))
            {
                await SealActiveAsync(state, cancellationToken).ConfigureAwait(false);
                active = await EnsureActiveSegmentAsync(
                    state,
                    sequence,
                    cancellationToken).ConfigureAwait(false);
                frame = BuildFrame(active.SegmentId, sequence, record);
            }

            if (record.Artifact is { } artifact)
            {
                await AppendArtifactReferenceAsync(
                        state,
                        sequence,
                        artifact.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await WriteInt32Async(active.Stream, frame.Length, cancellationToken)
                .ConfigureAwait(false);
            await active.Stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            active.Count++;
            active.LastSequence = sequence;
            if (state.FirstAvailableSequence >= state.NextSequence)
                state.FirstAvailableSequence = sequence;
            state.NextSequence = checked(sequence + 1);
            state.RecordCount++;
            state.LastTimestamp = record.Timestamp;
            state.EncodedBytes += sizeof(int) + frame.Length;

            if (writeMode == DurableWriteMode.Durable)
                FlushToDisk(active.Stream);

            if (writeMode == DurableWriteMode.Durable)
                _lastSuccessfulFlush = DateTimeOffset.UtcNow;

            if (record.Idempotent)
            {
                state.IdempotentRecords.Add(record.RecordId, sequence);
                await AppendIdempotencyEntryAsync(
                        state,
                        sequence,
                        record.RecordId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new DurableAppendReceipt(
                sequence,
                state.RecordCount,
                record.Timestamp);
        }
        catch (Exception ex)
        {
            _degradedReason = ex.Message;
            throw;
        }
        finally
        {
            ReleaseState(state);
        }
    }

    public async ValueTask<DurableAppendReceipt?> FindIdempotentAppendAsync(
        DurableStreamKey key,
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (recordId == Guid.Empty)
            throw new ArgumentException("Record ID is required.", nameof(recordId));
        var state = await AcquireStateAsync(key, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(state, cancellationToken).ConfigureAwait(false);
            await EnsureIdempotencyReadyAsync(state, cancellationToken)
                .ConfigureAwait(false);
            return state.IdempotentRecords.TryGetValue(recordId, out var sequence)
                ? new DurableAppendReceipt(
                    sequence,
                    state.RecordCount,
                    state.LastTimestamp ?? DateTimeOffset.MinValue)
                : null;
        }
        finally
        {
            ReleaseState(state);
        }
    }

    public async ValueTask<DurableRecordPage> ReadAsync(
        DurableStreamKey key,
        long nextSequence,
        DurableReadOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);
        ValidateReadOptions(options);
        if (nextSequence < 1)
            throw new ArgumentOutOfRangeException(nameof(nextSequence));

        var state = await AcquireStateAsync(key, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(state, cancellationToken).ConfigureAwait(false);
            var lastAvailable = state.NextSequence - 1;
            var snapshot = Math.Min(
                options.ThroughSequence ?? lastAvailable,
                lastAvailable);
            if (snapshot < nextSequence)
                return new DurableRecordPage(
                    [],
                    null,
                    false,
                    0,
                    snapshot,
                    state.FirstAvailableSequence,
                    state.ExpiredRecordCount);

            var records = new List<DurableRecord>(options.Take);
            var returnedBytes = 0;
            long scannedBytes = 0;
            long? continuation = null;

            foreach (var segment in EnumerateSegments(state))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (segment.LastSequence is { } segmentLast
                    && segmentLast < nextSequence)
                {
                    continue;
                }

                if (segment.IsSealed)
                    await VerifySealedSegmentAsync(segment.Path, cancellationToken)
                        .ConfigureAwait(false);

                await using var stream = new FileStream(
                    segment.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var header = await ReadHeaderAsync(stream, cancellationToken)
                    .ConfigureAwait(false);

                while (stream.Position < stream.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frameLength = await ReadInt32Async(stream, cancellationToken)
                        .ConfigureAwait(false);
                    if (frameLength == FooterMarker)
                        break;
                    ValidateFrameLength(frameLength);
                    var frame = new byte[frameLength];
                    await ReadExactlyAsync(stream, frame, cancellationToken)
                        .ConfigureAwait(false);
                    scannedBytes += sizeof(int) + frameLength;

                    var record = DecodeFrame(header.SegmentId, frame);
                    if (record.Sequence < nextSequence)
                        continue;
                    if (record.Sequence > snapshot)
                        return BuildPage(
                            state,
                            records,
                            returnedBytes,
                            null,
                            false,
                            snapshot);

                    continuation = record.Sequence + 1;
                    if (Matches(record, options))
                    {
                        var encodedBytes = JsonSerializer.SerializeToUtf8Bytes(record).Length;
                        if (returnedBytes + encodedBytes > options.MaxBytes)
                        {
                            if (records.Count == 0)
                            {
                                throw new InvalidOperationException(
                                    "The next record exceeds the requested page byte limit.");
                            }

                            return BuildPage(
                                state,
                                records,
                                returnedBytes,
                                record.Sequence,
                                true,
                                snapshot);
                        }

                        records.Add(record);
                        returnedBytes += encodedBytes;
                        if (records.Count == options.Take)
                        {
                            return BuildPage(
                                state,
                                records,
                                returnedBytes,
                                record.Sequence + 1,
                                record.Sequence < snapshot,
                                snapshot);
                        }
                    }

                    // A frame that crosses the scan budget is still evaluated.
                    // Advancing before evaluating it would permanently skip a
                    // matching record when the caller follows the continuation.
                    if (scannedBytes >= options.MaxScanBytes)
                    {
                        return BuildPage(
                            state,
                            records,
                            returnedBytes,
                            continuation,
                            continuation <= snapshot,
                            snapshot);
                    }
                }
            }

            var hasMore = continuation is { } next && next <= snapshot;
            return BuildPage(
                state,
                records,
                returnedBytes,
                hasMore ? continuation : null,
                hasMore,
                snapshot);
        }
        catch (Exception ex)
        {
            _degradedReason = ex.Message;
            throw;
        }
        finally
        {
            ReleaseState(state);
        }
    }

    public async ValueTask<DurableStreamSummary> GetSummaryAsync(
        DurableStreamKey key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var state = await AcquireStateAsync(key, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(state, cancellationToken).ConfigureAwait(false);
            return new DurableStreamSummary(
                state.RecordCount,
                state.NextSequence > 1 ? state.NextSequence - 1 : null,
                state.LastTimestamp,
                state.EncodedBytes,
                state.FirstAvailableSequence,
                state.ExpiredRecordCount);
        }
        finally
        {
            ReleaseState(state);
        }
    }

    public async Task<DurableOperationalStreamCatalog> EnumerateOperationalStreamsAsync(
        DurableOperationalStreamEnumerationOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);
        ValidateEnumerationOptions(options);

        var streams = new List<DurableOperationalStreamSummary>();
        var identityGaps = new List<DurableOperationalStreamIdentityGap>();
        using var budget = new EnumerationBudget(options, cancellationToken);
        var hasMore = false;
        var scannedDirectories = 0;

        try
        {
            foreach (var kind in new[]
                     {
                         DurableStreamKind.ProcessLog,
                         DurableStreamKind.RegistrationLog,
                     })
            {
                var kindDirectory = Path.Combine(
                    _options.RootDirectory,
                    "streams",
                    kind.ToString().ToLowerInvariant());
                if (!Directory.Exists(kindDirectory))
                    continue;

                foreach (var prefixDirectory in Directory.EnumerateDirectories(kindDirectory))
                {
                    budget.ThrowIfExpired();
                    if (IsReparsePoint(prefixDirectory))
                        continue;

                    foreach (var streamDirectory in Directory.EnumerateDirectories(prefixDirectory))
                    {
                        budget.ThrowIfExpired();
                        if (IsReparsePoint(streamDirectory))
                            continue;

                        if (streams.Count + identityGaps.Count >= options.MaxEntries)
                        {
                            hasMore = true;
                            return new DurableOperationalStreamCatalog(
                                streams,
                                identityGaps,
                                hasMore,
                                scannedDirectories,
                                budget.ScannedBytes);
                        }

                        scannedDirectories++;
                        var hash = Path.GetFileName(streamDirectory);
                        try
                        {
                            var identity = await ReadCatalogIdentityAsync(
                                    streamDirectory,
                                    kind,
                                    hash,
                                    budget,
                                    budget.Token)
                                .ConfigureAwait(false);
                            if (identity.Gap is not null)
                            {
                                identityGaps.Add(identity.Gap);
                                continue;
                            }

                            if (identity.Value is null)
                                continue;

                            var scan = await ReadCatalogStreamAsync(
                                    streamDirectory,
                                    budget,
                                    budget.Token)
                                .ConfigureAwait(false);
                            streams.Add(new DurableOperationalStreamSummary(
                                identity.Value.Stream,
                                identity.Value.AppName,
                                identity.Value.SourceId,
                                identity.Value.BootId,
                                scan.HasActiveSegment,
                                scan.HasSealedSegments,
                                scan.RecordCount,
                                scan.EncodedBytes,
                                scan.FirstSequence,
                                scan.LastSequence,
                                scan.FirstAvailableSequence,
                                scan.ExpiredRecordCount,
                                scan.LastTimestamp));
                        }
                        catch (Exception ex) when (
                            ex is IOException
                            or UnauthorizedAccessException
                            or InvalidDataException
                            or JsonException
                            or OverflowException
                            or ArgumentOutOfRangeException)
                        {
                            identityGaps.Add(new DurableOperationalStreamIdentityGap(
                                kind,
                                hash,
                                ex is InvalidDataException
                                    or JsonException
                                    or OverflowException
                                    or ArgumentOutOfRangeException
                                    ? "InvalidSegmentMetadata"
                                    : "UnreadableStreamMetadata"));
                        }
                    }
                }
            }
        }
        catch (CatalogBudgetExceededException)
        {
            hasMore = true;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && budget.IsExpired)
        {
            hasMore = true;
        }

        streams.Sort(static (left, right) =>
        {
            var kind = left.Stream.Kind.CompareTo(right.Stream.Kind);
            if (kind != 0)
                return kind;
            var boot = left.BootId.CompareTo(right.BootId);
            if (boot != 0)
                return boot;
            return string.Compare(
                left.AppName ?? left.SourceId,
                right.AppName ?? right.SourceId,
                StringComparison.Ordinal);
        });
        return new DurableOperationalStreamCatalog(
            streams,
            identityGaps,
            hasMore,
            scannedDirectories,
            budget.ScannedBytes);
    }

    public async ValueTask FlushAsync(
        DurableStreamKey key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var state = await AcquireStateAsync(key, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(state, cancellationToken).ConfigureAwait(false);
            if (state.Active is not null)
            {
                FlushToDisk(state.Active.Stream);
                _lastSuccessfulFlush = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            ReleaseState(state);
        }
    }

    public async ValueTask SealAsync(
        DurableStreamKey key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var state = await AcquireStateAsync(key, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(state, cancellationToken).ConfigureAwait(false);
            await SealActiveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseState(state);
        }
    }

    public async Task<DurableRetentionResult> ApplyRetentionAsync(
        DurableRetentionOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);
        ValidateRetentionOptions(options);

        var streamsRoot = Path.Combine(_options.RootDirectory, "streams");
        if (!Directory.Exists(streamsRoot))
        {
            return new DurableRetentionResult(
                0,
                0,
                0,
                GetAvailableFreeBytes(),
                true,
                DateTimeOffset.UtcNow);
        }

        await SealExpiredOpenSegmentsAsync(streamsRoot, cancellationToken)
            .ConfigureAwait(false);

        var candidates = EnumerateRetentionCandidates(streamsRoot).ToArray();
        var queues = candidates
            .GroupBy(candidate => candidate.DirectoryPath, PathComparer)
            .ToDictionary(
                group => group.Key,
                group => new Queue<RetentionCandidate>(
                    group.OrderBy(candidate => candidate.FirstSequence)),
                PathComparer);
        var selected = new List<RetentionCandidate>();
        var now = DateTimeOffset.UtcNow;

        foreach (var queue in queues.Values)
        {
            while (queue.Count > 0
                   && selected.Count < options.MaximumDeletesPerRun)
            {
                var head = queue.Peek();
                if (now - head.LastWriteTime < options.GetMaximumAge(head.Kind))
                    break;
                selected.Add(queue.Dequeue());
            }
        }

        var remainingBytes = GetStreamBytes(streamsRoot)
            - selected.Sum(candidate => candidate.EncodedBytes);
        var freeBytes = GetAvailableFreeBytes();
        while (selected.Count < options.MaximumDeletesPerRun
               && (remainingBytes > options.MaximumEncodedBytes
                   || freeBytes < options.MinimumFreeBytes))
        {
            var next = queues.Values
                .Where(queue => queue.Count > 0)
                .Select(queue => queue.Peek())
                .OrderBy(candidate => candidate.LastWriteTime)
                .ThenBy(candidate => candidate.FirstSequence)
                .FirstOrDefault();
            if (next is null)
                break;
            queues[next.DirectoryPath].Dequeue();
            selected.Add(next);
            remainingBytes -= next.EncodedBytes;
            freeBytes = checked(freeBytes + next.EncodedBytes);
        }

        var deleted = 0;
        long reclaimed = 0;
        foreach (var group in selected.GroupBy(
                     candidate => candidate.DirectoryPath,
                     PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await DeleteRetainedPrefixAsync(
                    group.Key,
                    group.OrderBy(candidate => candidate.FirstSequence).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            deleted += result.DeletedSegments;
            reclaimed += result.ReclaimedBytes;
        }

        remainingBytes = GetStreamBytes(streamsRoot);
        freeBytes = GetAvailableFreeBytes();
        return new DurableRetentionResult(
            deleted,
            reclaimed,
            remainingBytes,
            freeBytes,
            remainingBytes <= options.MaximumEncodedBytes
                && freeBytes >= options.MinimumFreeBytes,
            DateTimeOffset.UtcNow);
    }

    public DurableStorageSnapshot GetSnapshot()
    {
        var root = new DirectoryInfo(
            Path.Combine(_options.RootDirectory, "streams"));
        var bytes = root.Exists
            ? root.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
            : 0;
        var sealedSegments = root.Exists
            ? root.EnumerateFiles("*.scseg", SearchOption.AllDirectories).LongCount()
            : 0;
        return new DurableStorageSnapshot(
            _degradedReason is null,
            _degradedReason,
            bytes,
            _states.Values.Count(state => state.Active is not null),
            _states.Count,
            sealedSegments,
            _lastSuccessfulFlush);
    }

    public async Task<HashSet<Guid>> ReadArtifactReferencesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = new HashSet<Guid>();
        var streamsRoot = Path.Combine(_options.RootDirectory, "streams");
        if (!Directory.Exists(streamsRoot))
            return result;

        foreach (var path in Directory.EnumerateFiles(
                     streamsRoot,
                     ArtifactReferenceFileName,
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(path)!;
            var state = await TryAcquireStateByDirectoryAsync(
                    directory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (state is null)
            {
                await ReadArtifactReferenceEntriesAsync(
                        path,
                        entry =>
                        {
                            result.Add(entry.ArtifactId);
                            return ValueTask.CompletedTask;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                await ReadArtifactReferenceEntriesAsync(
                        path,
                        entry =>
                        {
                            result.Add(entry.ArtifactId);
                            return ValueTask.CompletedTask;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                ReleaseState(state);
            }
        }

        return result;
    }

    private IEnumerable<RetentionCandidate> EnumerateRetentionCandidates(
        string streamsRoot)
    {
        foreach (var kindDirectory in Directory.EnumerateDirectories(streamsRoot))
        {
            if (!Enum.TryParse<DurableStreamKind>(
                    Path.GetFileName(kindDirectory),
                    ignoreCase: true,
                    out var kind))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(
                         kindDirectory,
                         "*.scseg",
                         SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var pieces = name.Split('-');
                if (pieces.Length < 3
                    || !long.TryParse(pieces[0], out var first)
                    || !long.TryParse(pieces[1], out var last))
                {
                    throw new InvalidDataException(
                        $"Invalid sealed segment name '{name}'.");
                }
                var info = new FileInfo(path);
                yield return new RetentionCandidate(
                    kind,
                    path,
                    info.DirectoryName!,
                    first,
                    last,
                    info.Length,
                    info.LastWriteTimeUtc);
            }
        }
    }

    private async Task<RetentionDeleteResult> DeleteRetainedPrefixAsync(
        string directoryPath,
        IReadOnlyList<RetentionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
            return new RetentionDeleteResult(0, 0);

        var state = await TryAcquireStateByDirectoryAsync(
                directoryPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (state is not null)
        {
            try
            {
                await EnsureInitializedAsync(state, cancellationToken)
                    .ConfigureAwait(false);
                VerifyRetentionPrefix(directoryPath, candidates);
                var result = await DeleteSegmentFilesAsync(
                        candidates,
                        cancellationToken)
                    .ConfigureAwait(false);
                state.ExpiredRecordCount = checked(
                    state.ExpiredRecordCount + result.ExpiredRecords);
                state.EncodedBytes = Math.Max(
                    0,
                    state.EncodedBytes - result.ReclaimedBytes);
                state.FirstAvailableSequence = FindFirstAvailableSequence(state);
                await PruneArtifactReferencesAsync(
                        directoryPath,
                        candidates.Max(candidate => candidate.LastSequence),
                        cancellationToken)
                    .ConfigureAwait(false);
                await WriteManifestAsync(state, cancellationToken)
                    .ConfigureAwait(false);
                return new RetentionDeleteResult(
                    result.DeletedSegments,
                    result.ReclaimedBytes);
            }
            finally
            {
                ReleaseState(state);
            }
        }

        if (Directory.EnumerateFiles(directoryPath, "*.open").Any())
            return new RetentionDeleteResult(0, 0);

        VerifyRetentionPrefix(directoryPath, candidates);
        var manifest = await ReadManifestAsync(directoryPath, cancellationToken)
            .ConfigureAwait(false);
        var nextSequence = manifest?.NextSequence ?? 1;
        var expired = manifest?.ExpiredRecordCount ?? 0;
        DateTimeOffset? lastTimestamp = manifest?.LastTimestamp;
        var remaining = Directory.EnumerateFiles(directoryPath, "*.scseg")
            .OrderBy(ParseFirstSequence)
            .ToArray();
        foreach (var path in remaining)
        {
            var footer = await ReadSealedFooterAsync(path, cancellationToken)
                .ConfigureAwait(false);
            nextSequence = Math.Max(nextSequence, footer.LastSequence + 1);
        }
        if (manifest is null && remaining.Length > 0)
            expired = Math.Max(0, ParseFirstSequence(remaining[0]) - 1);

        var deleted = await DeleteSegmentFilesAsync(candidates, cancellationToken)
            .ConfigureAwait(false);
        expired = checked(expired + deleted.ExpiredRecords);
        await PruneArtifactReferencesAsync(
                directoryPath,
                candidates.Max(candidate => candidate.LastSequence),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteManifestAsync(
                directoryPath,
                new StreamManifest(1, nextSequence, expired, lastTimestamp),
                cancellationToken)
            .ConfigureAwait(false);
        return new RetentionDeleteResult(
            deleted.DeletedSegments,
            deleted.ReclaimedBytes);
    }

    private static void VerifyRetentionPrefix(
        string directoryPath,
        IReadOnlyList<RetentionCandidate> candidates)
    {
        var currentPrefix = Directory.EnumerateFiles(directoryPath, "*.scseg")
            .OrderBy(ParseFirstSequence)
            .Take(candidates.Count)
            .ToArray();
        if (currentPrefix.Length != candidates.Count
            || !currentPrefix.Zip(
                    candidates,
                    (path, candidate) => PathComparer.Equals(path, candidate.Path))
                .All(static matches => matches))
        {
            throw new InvalidOperationException(
                "Retention selection is no longer the sealed stream prefix.");
        }
    }

    private static async Task<SegmentDeleteResult> DeleteSegmentFilesAsync(
        IReadOnlyList<RetentionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        long reclaimed = 0;
        long expired = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate.Path))
                continue;
            var footer = await ReadSealedFooterAsync(
                    candidate.Path,
                    cancellationToken)
                .ConfigureAwait(false);
            var length = new FileInfo(candidate.Path).Length;
            File.Delete(candidate.Path);
            deleted++;
            reclaimed = checked(reclaimed + length);
            expired = checked(expired + footer.RecordCount);
        }
        return new SegmentDeleteResult(deleted, reclaimed, expired);
    }

    private static long GetStreamBytes(string streamsRoot) =>
        Directory.Exists(streamsRoot)
            ? new DirectoryInfo(streamsRoot)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length)
            : 0;

    private async Task AppendArtifactReferenceAsync(
        StreamState state,
        long sequence,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        if (artifactId == Guid.Empty)
            throw new InvalidDataException("Artifact reference ID is required.");
        var payload = new byte[sizeof(long) + 16];
        BitConverter.TryWriteBytes(payload.AsSpan(0, sizeof(long)), sequence);
        artifactId.TryWriteBytes(payload.AsSpan(sizeof(long), 16));
        var authentication = ComputeArtifactReferenceAuthentication(payload);
        var path = Path.Combine(state.DirectoryPath, ArtifactReferenceFileName);
        await using var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        stream.Position = stream.Length;
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(authentication, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async Task ReadArtifactReferenceEntriesAsync(
        string path,
        Func<ArtifactReferenceEntry, ValueTask> visit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(visit);
        if (!File.Exists(path))
            return;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length % ArtifactReferenceEntryBytes != 0)
        {
            throw new InvalidDataException(
                "Durable artifact-reference index is truncated.");
        }

        var bytes = new byte[ArtifactReferenceEntryBytes];
        while (stream.Position < stream.Length)
        {
            await ReadExactlyAsync(stream, bytes, cancellationToken)
                .ConfigureAwait(false);
            var payload = bytes.AsSpan(0, sizeof(long) + 16);
            var expected = bytes.AsSpan(payload.Length, 32);
            var actual = ComputeArtifactReferenceAuthentication(payload);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException(
                    "Durable artifact-reference index authentication failed.");
            }

            var sequence = BitConverter.ToInt64(payload[..sizeof(long)]);
            var artifactId = new Guid(payload.Slice(sizeof(long), 16));
            if (sequence < 1 || artifactId == Guid.Empty)
            {
                throw new InvalidDataException(
                    "Durable artifact-reference index values are invalid.");
            }
            await visit(new ArtifactReferenceEntry(sequence, artifactId))
                .ConfigureAwait(false);
        }
    }

    private async Task PruneArtifactReferencesAsync(
        string directoryPath,
        long throughSequence,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directoryPath, ArtifactReferenceFileName);
        if (!File.Exists(path))
            return;
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await ReadArtifactReferenceEntriesAsync(
                        path,
                        async entry =>
                        {
                            if (entry.Sequence <= throughSequence)
                                return;
                            var payload = new byte[sizeof(long) + 16];
                            BitConverter.TryWriteBytes(
                                payload.AsSpan(0, sizeof(long)),
                                entry.Sequence);
                            entry.ArtifactId.TryWriteBytes(
                                payload.AsSpan(sizeof(long), 16));
                            await stream.WriteAsync(payload, cancellationToken)
                                .ConfigureAwait(false);
                            await stream.WriteAsync(
                                    ComputeArtifactReferenceAuthentication(payload),
                                    cancellationToken)
                                .ConfigureAwait(false);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private byte[] ComputeArtifactReferenceAuthentication(
        ReadOnlySpan<byte> payload)
    {
        if (_options.EncryptionKey is { } key)
        {
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(payload.ToArray());
        }
        return SHA256.HashData(payload);
    }

    private long GetAvailableFreeBytes()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_options.RootDirectory));
        return string.IsNullOrWhiteSpace(root)
            ? 0
            : new DriveInfo(root).AvailableFreeSpace;
    }

    private static void ValidateEnumerationOptions(
        DurableOperationalStreamEnumerationOptions options)
    {
        if (options.MaxEntries is < 1
            or > DurableOperationalStreamEnumerationOptions.HardMaximumEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxEntries));
        }

        if (options.MaxScanBytes <= 0
            || options.MaxScanBytes
                > DurableOperationalStreamEnumerationOptions.HardMaximumScanBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxScanBytes));
        }

        if (options.MaxDuration <= TimeSpan.Zero
            || options.MaxDuration
                > DurableOperationalStreamEnumerationOptions.HardMaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxDuration));
        }
    }

    private static void ValidateRetentionOptions(DurableRetentionOptions options)
    {
        foreach (var kind in Enum.GetValues<DurableStreamKind>())
        {
            if (options.GetMaximumAge(kind) <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (options.MaximumEncodedBytes < 0
            || options.MinimumFreeBytes < 0
            || options.MaximumDeletesPerRun < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        foreach (var state in _states.Values)
        {
            await state.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (state.Initialized)
                    await SealActiveAsync(state, CancellationToken.None).ConfigureAwait(false);
                if (state.IdempotencyIndex is not null)
                    await state.IdempotencyIndex.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                state.Gate.Release();
                state.Gate.Dispose();
            }
        }

        if (_writerLease is not null)
        {
            await _writerLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<CatalogIdentityRead> ReadCatalogIdentityAsync(
        string directoryPath,
        DurableStreamKind expectedKind,
        string hash,
        EnumerationBudget budget,
        CancellationToken cancellationToken)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            return HasAnySegment(directoryPath)
                ? new(
                    null,
                    new DurableOperationalStreamIdentityGap(
                        expectedKind,
                        hash,
                        "InvalidStreamHash"))
                : new(null, null);
        }

        var identityPath = Path.Combine(directoryPath, StreamIdentityFileName);
        if (!File.Exists(identityPath))
        {
            return HasAnySegment(directoryPath)
                ? new(
                    null,
                    new DurableOperationalStreamIdentityGap(
                        expectedKind,
                        hash,
                        "IdentityUnavailableWithoutBodyScan"))
                : new(null, null);
        }
        if (IsReparsePoint(identityPath))
        {
            return new(
                null,
                new DurableOperationalStreamIdentityGap(
                    expectedKind,
                    hash,
                    "InvalidIdentityMetadata"));
        }

        var length = new FileInfo(identityPath).Length;
        if (length > MaxStreamIdentityBytes)
        {
            return new(
                null,
                new DurableOperationalStreamIdentityGap(
                    expectedKind,
                    hash,
                    "InvalidIdentityMetadata"));
        }

        budget.Consume(length);
        var bytes = await File.ReadAllBytesAsync(identityPath, cancellationToken)
            .ConfigureAwait(false);
        StreamIdentity? identity;
        try
        {
            identity = JsonSerializer.Deserialize<StreamIdentity>(bytes);
        }
        catch (JsonException)
        {
            identity = null;
        }

        if (identity is null
            || identity.Version != 1
            || string.IsNullOrWhiteSpace(identity.CanonicalValue)
            || identity.CanonicalValue.Length > 1024
            || !DurableStreamKey.TryParseOperational(
                identity.CanonicalValue,
                out var key,
                out var appName,
                out var SourceId,
                out var bootId)
            || key.Kind != expectedKind
            || !string.Equals(
                _paths.GetStreamHash(key),
                hash,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                null,
                new DurableOperationalStreamIdentityGap(
                    expectedKind,
                    hash,
                    "InvalidIdentityMetadata"));
        }

        return new(
            new CatalogIdentity(key, appName, SourceId, bootId),
            null);
    }

    private async Task<CatalogStreamScan> ReadCatalogStreamAsync(
        string directoryPath,
        EnumerationBudget budget,
        CancellationToken cancellationToken)
    {
        StreamManifest? manifest = null;
        var manifestPath = Path.Combine(directoryPath, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            if (IsReparsePoint(manifestPath))
                throw new InvalidDataException("Stream manifest is a reparse point.");
            var manifestLength = new FileInfo(manifestPath).Length;
            budget.Consume(manifestLength);
            manifest = await ReadManifestAsync(directoryPath, cancellationToken)
                .ConfigureAwait(false);
        }

        var hasSealedSegments = false;
        var hasActiveSegment = false;
        var physicalRecordCount = 0L;
        var encodedBytes = 0L;
        var firstSequence = long.MaxValue;
        long? lastSequence = null;

        foreach (var path in Directory.EnumerateFiles(directoryPath, "*.scseg"))
        {
            budget.ThrowIfExpired();
            if (IsReparsePoint(path))
                continue;

            var info = new FileInfo(path);
            if (info.Length < SegmentHeaderBytes + FooterBytes)
                throw new InvalidDataException("Sealed segment is too short.");

            budget.Consume(SegmentHeaderBytes);
            SegmentHeader header;
            await using (var stream = new FileStream(
                             path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite,
                             4096,
                             FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                header = await ReadHeaderAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
            }

            budget.Consume(FooterBytes);
            var footer = await ReadSealedFooterAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (footer.RecordCount < 0)
            {
                throw new InvalidDataException("Sealed segment metadata is invalid.");
            }

            long expectedLastSequence;
            try
            {
                expectedLastSequence = footer.RecordCount == 0
                    ? checked(header.FirstSequence - 1)
                    : checked(header.FirstSequence + footer.RecordCount - 1);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "Sealed segment metadata is invalid.",
                    exception);
            }

            if (footer.LastSequence != expectedLastSequence)
                throw new InvalidDataException("Sealed segment metadata is invalid.");

            hasSealedSegments = true;
            physicalRecordCount = checked(physicalRecordCount + footer.RecordCount);
            encodedBytes = checked(encodedBytes + info.Length);
            if (footer.RecordCount > 0)
            {
                firstSequence = Math.Min(firstSequence, header.FirstSequence);
                lastSequence = lastSequence is null
                    ? footer.LastSequence
                    : Math.Max(lastSequence.Value, footer.LastSequence);
            }
        }

        var openPaths = Directory.EnumerateFiles(directoryPath, "*.open")
            .Where(path => !IsReparsePoint(path))
            .Take(2)
            .ToArray();
        if (openPaths.Length > 1)
            throw new InvalidDataException("A durable stream has multiple active segments.");
        if (openPaths.Length == 1)
        {
            var path = openPaths[0];
            var info = new FileInfo(path);
            if (info.Length < SegmentHeaderBytes)
                throw new InvalidDataException("Active segment is too short.");

            hasActiveSegment = true;
            budget.Consume(SegmentHeaderBytes);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = await ReadHeaderAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            var activeCount = 0L;
            while (stream.Position < stream.Length)
            {
                budget.ThrowIfExpired();
                if (stream.Length - stream.Position < sizeof(int))
                    throw new InvalidDataException("Active segment frame header is truncated.");

                budget.Consume(sizeof(int));
                var frameLength = await ReadInt32Async(stream, cancellationToken)
                    .ConfigureAwait(false);
                if (frameLength == FooterMarker)
                    throw new InvalidDataException("Active segment contains a footer.");
                ValidateFrameLength(frameLength);
                if (frameLength > stream.Length - stream.Position)
                    throw new InvalidDataException("Active segment frame is truncated.");

                stream.Position += frameLength;
                activeCount = checked(activeCount + 1);
            }

            encodedBytes = checked(encodedBytes + info.Length);
            if (activeCount > 0)
            {
                firstSequence = Math.Min(firstSequence, header.FirstSequence);
                var activeLast = checked(header.FirstSequence + activeCount - 1);
                lastSequence = lastSequence is null
                    ? activeLast
                    : Math.Max(lastSequence.Value, activeLast);
                physicalRecordCount = checked(physicalRecordCount + activeCount);
            }
        }

        var expired = manifest?.ExpiredRecordCount ?? 0;
        var firstAvailable = Math.Max(1, checked(expired + 1));
        if (firstSequence != long.MaxValue)
            firstAvailable = Math.Max(firstAvailable, firstSequence);

        var logicalLast = manifest is { NextSequence: > 1 }
            ? manifest.NextSequence - 1
            : lastSequence ?? 0;
        if (lastSequence is { } physicalLast)
            logicalLast = Math.Max(logicalLast, physicalLast);
        var recordCount = Math.Max(
            logicalLast,
            checked(physicalRecordCount + expired));

        return new CatalogStreamScan(
            hasActiveSegment,
            hasSealedSegments,
            recordCount,
            encodedBytes,
            firstSequence == long.MaxValue ? firstAvailable : firstSequence,
            logicalLast > 0 ? logicalLast : null,
            firstAvailable,
            expired,
            manifest?.LastTimestamp);
    }

    private static bool HasAnySegment(string directoryPath) =>
        Directory.EnumerateFiles(directoryPath, "*.scseg")
            .Any(path => !IsReparsePoint(path))
        || Directory.EnumerateFiles(directoryPath, "*.open")
            .Any(path => !IsReparsePoint(path));

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private async Task EnsureStreamIdentityAsync(
        StreamState state,
        CancellationToken cancellationToken)
    {
        if (state.Key.Kind is not DurableStreamKind.ProcessLog
            and not DurableStreamKind.RegistrationLog)
        {
            return;
        }

        var path = Path.Combine(state.DirectoryPath, StreamIdentityFileName);
        if (File.Exists(path))
        {
            var length = new FileInfo(path).Length;
            if (length > MaxStreamIdentityBytes)
                throw new InvalidDataException("Durable stream identity is too large.");
            var existing = JsonSerializer.Deserialize<StreamIdentity>(
                await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            if (existing is null
                || existing.Version != 1
                || !string.Equals(
                    existing.CanonicalValue,
                    state.Key.CanonicalValue,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Durable stream identity does not match its key.");
            }

            return;
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new StreamIdentity(1, state.Key.CanonicalValue));
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    private async ValueTask<StreamState> AcquireStateAsync(
        DurableStreamKey key,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key.CanonicalValue))
            throw new ArgumentException("A typed stream key is required.", nameof(key));
        while (true)
        {
            var state = _states.GetOrAdd(
                key.CanonicalValue,
                _ => new StreamState(key, _paths.GetStreamDirectory(key)));
            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_states.TryGetValue(key.CanonicalValue, out var current)
                && ReferenceEquals(current, state))
            {
                return state;
            }

            state.Gate.Release();
        }
    }

    private async ValueTask<StreamState?> TryAcquireStateByDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = _states.Values.FirstOrDefault(candidate =>
                PathComparer.Equals(candidate.DirectoryPath, directoryPath));
            if (state is null)
                return null;

            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_states.TryGetValue(state.Key.CanonicalValue, out var current)
                && ReferenceEquals(current, state))
            {
                return state;
            }

            state.Gate.Release();
        }
    }

    private void ReleaseState(StreamState state)
    {
        if (state.Active is null
            && ((ICollection<KeyValuePair<string, StreamState>>)_states).Remove(
                new KeyValuePair<string, StreamState>(
                    state.Key.CanonicalValue,
                    state)))
        {
            state.IdempotencyIndex?.Dispose();
            state.IdempotencyIndex = null;
            state.IdempotentRecords.Clear();
        }

        state.Gate.Release();
    }

    private async Task EnsureInitializedAsync(
        StreamState state,
        CancellationToken cancellationToken)
    {
        if (state.Initialized)
            return;

        Directory.CreateDirectory(state.DirectoryPath);
        await EnsureStreamIdentityAsync(state, cancellationToken)
            .ConfigureAwait(false);
        var manifest = await ReadManifestAsync(
            state.DirectoryPath,
            cancellationToken).ConfigureAwait(false);
        if (manifest is not null)
        {
            state.NextSequence = manifest.NextSequence;
            state.ExpiredRecordCount = manifest.ExpiredRecordCount;
            state.LastTimestamp = manifest.LastTimestamp;
        }
        var openFiles = Directory.EnumerateFiles(state.DirectoryPath, "*.open").ToArray();
        if (openFiles.Length > 1)
            throw new InvalidDataException("A durable stream has multiple active segments.");

        foreach (var sealedPath in Directory.EnumerateFiles(state.DirectoryPath, "*.scseg"))
        {
            var footer = await ReadSealedFooterAsync(sealedPath, cancellationToken)
                .ConfigureAwait(false);
            state.NextSequence = Math.Max(state.NextSequence, footer.LastSequence + 1);
            state.EncodedBytes += new FileInfo(sealedPath).Length;
        }

        if (openFiles.Length == 1)
        {
            var recovered = await RecoverOpenSegmentAsync(
                openFiles[0],
                cancellationToken).ConfigureAwait(false);
            state.Active = recovered.Active;
            state.NextSequence = Math.Max(
                state.NextSequence,
                recovered.Active.LastSequence + 1);
            state.LastTimestamp = recovered.LastTimestamp;
            state.EncodedBytes += recovered.Active.Stream.Length;
        }

        state.RecordCount = state.NextSequence - 1;
        state.FirstAvailableSequence = FindFirstAvailableSequence(state);
        if (manifest is null && state.FirstAvailableSequence > 1)
            state.ExpiredRecordCount = state.FirstAvailableSequence - 1;
        if (File.Exists(Path.Combine(state.DirectoryPath, IdempotencyFileName)))
            await EnsureIdempotencyReadyAsync(state, cancellationToken)
                .ConfigureAwait(false);
        state.Initialized = true;
    }

    private async Task<ActiveSegment> EnsureActiveSegmentAsync(
        StreamState state,
        long firstSequence,
        CancellationToken cancellationToken)
    {
        if (state.Active is not null)
            return state.Active;

        var segmentId = Guid.NewGuid();
        var path = Path.Combine(
            state.DirectoryPath,
            $"{firstSequence:D20}-{segmentId:N}.open");
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var created = DateTimeOffset.UtcNow;
        await WriteHeaderAsync(
            stream,
            segmentId,
            firstSequence,
            created,
            cancellationToken).ConfigureAwait(false);
        state.EncodedBytes += SegmentHeaderBytes;
        state.Active = new ActiveSegment(
            path,
            stream,
            segmentId,
            firstSequence,
            created);
        return state.Active;
    }

    private bool ShouldRotate(ActiveSegment active, int nextFrameBytes) =>
        active.Count > 0
        && (active.Stream.Length + nextFrameBytes > _options.SegmentMaxBytes
            || DateTimeOffset.UtcNow - active.CreatedAt >= _options.SegmentMaxAge);

    private async Task SealActiveAsync(
        StreamState state,
        CancellationToken cancellationToken)
    {
        var active = state.Active;
        if (active is null)
            return;
        var sealedPath = await SealSegmentFileAsync(
                active,
                state.DirectoryPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (sealedPath is not null)
            state.EncodedBytes += FooterBytes;
        state.Active = null;
        await WriteManifestAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> SealSegmentFileAsync(
        ActiveSegment active,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        if (active.Count == 0)
        {
            await active.Stream.DisposeAsync().ConfigureAwait(false);
            File.Delete(active.Path);
            return null;
        }

        active.Stream.Flush(flushToDisk: false);
        var digest = await ComputePrefixDigestAsync(
            active.Path,
            active.Stream.Length,
            cancellationToken).ConfigureAwait(false);
        active.Stream.Position = active.Stream.Length;
        await WriteInt32Async(active.Stream, FooterMarker, cancellationToken)
            .ConfigureAwait(false);
        await WriteInt64Async(active.Stream, active.LastSequence, cancellationToken)
            .ConfigureAwait(false);
        await WriteInt64Async(active.Stream, active.Count, cancellationToken)
            .ConfigureAwait(false);
        await active.Stream.WriteAsync(digest, cancellationToken).ConfigureAwait(false);
        FlushToDisk(active.Stream);
        await active.Stream.DisposeAsync().ConfigureAwait(false);

        var sealedPath = Path.Combine(
            directoryPath,
            $"{active.FirstSequence:D20}-{active.LastSequence:D20}-{active.SegmentId:N}.scseg");
        File.Move(active.Path, sealedPath);
        _verifiedSegments.TryAdd(sealedPath, 0);
        _lastSuccessfulFlush = DateTimeOffset.UtcNow;
        return sealedPath;
    }

    private async Task SealExpiredOpenSegmentsAsync(
        string streamsRoot,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var trackedDirectories = _states.Values
            .Select(state => state.DirectoryPath)
            .Distinct(PathComparer)
            .ToArray();
        foreach (var directoryPath in trackedDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await TryAcquireStateByDirectoryAsync(
                    directoryPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (state is null)
                continue;
            try
            {
                await EnsureInitializedAsync(state, cancellationToken)
                    .ConfigureAwait(false);
                if (state.Active is { } active
                    && now - active.CreatedAt >= _options.SegmentMaxAge)
                {
                    await SealActiveAsync(state, cancellationToken)
                        .ConfigureAwait(false);
                    var sealedPath = Path.Combine(
                        state.DirectoryPath,
                        $"{active.FirstSequence:D20}-{active.LastSequence:D20}-{active.SegmentId:N}.scseg");
                    if (File.Exists(sealedPath))
                        File.SetLastWriteTimeUtc(sealedPath, active.CreatedAt.UtcDateTime);
                }
            }
            finally
            {
                ReleaseState(state);
            }
        }

        var trackedOpenPaths = _states.Values
            .Select(state => state.Active?.Path)
            .Where(path => path is not null)
            .ToHashSet(PathComparer);
        foreach (var path in Directory.EnumerateFiles(
                     streamsRoot,
                     "*.open",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (trackedOpenPaths.Contains(path)
                || now - File.GetLastWriteTimeUtc(path) < _options.SegmentMaxAge)
            {
                continue;
            }

            RecoveredOpenSegment recovered;
            var retainedLastWriteTime = File.GetLastWriteTimeUtc(path);
            try
            {
                recovered = await RecoverOpenSegmentAsync(path, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            var directoryPath = Path.GetDirectoryName(path)!;
            var sealedPath = await SealSegmentFileAsync(
                    recovered.Active,
                    directoryPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (sealedPath is not null)
                File.SetLastWriteTimeUtc(sealedPath, retainedLastWriteTime);
            var manifest = await ReadManifestAsync(directoryPath, cancellationToken)
                .ConfigureAwait(false);
            var nextSequence = Math.Max(
                manifest?.NextSequence ?? 1,
                recovered.Active.LastSequence + 1);
            var expired = manifest?.ExpiredRecordCount ?? Math.Max(
                0,
                recovered.Active.FirstSequence - 1);
            await WriteManifestAsync(
                    directoryPath,
                    new StreamManifest(
                        1,
                        nextSequence,
                        expired,
                        recovered.LastTimestamp ?? manifest?.LastTimestamp),
                    cancellationToken)
                .ConfigureAwait(false);
            if (sealedPath is not null)
                _verifiedSegments.TryAdd(sealedPath, 0);
        }
    }

    private byte[] BuildFrame(
        Guid segmentId,
        long sequence,
        DurableRecordWrite record)
    {
        var body = SerializeRecordBody(record);
        if (body.Length > _options.MaxRecordBytes)
        {
            throw new ArgumentException(
                $"Encoded record exceeds the {_options.MaxRecordBytes}-byte limit.",
                nameof(record));
        }

        var compressed = Compress(body);
        byte flags = 2;
        if (record.Idempotent)
            flags |= 4;
        if (_options.EncryptionKey is not null)
            flags |= 1;
        var timestamp = record.Timestamp.ToUnixTimeMilliseconds();
        var associatedData = BuildAssociatedData(
            segmentId,
            sequence,
            record.RecordId,
            timestamp,
            flags,
            body.Length);
        var digest = ComputeFrameDigest(associatedData, compressed);
        var nonce = new byte[12];
        var tag = new byte[16];
        byte[] payload;
        if (_options.EncryptionKey is { } key)
        {
            RandomNumberGenerator.Fill(nonce);
            payload = new byte[compressed.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(
                nonce,
                compressed,
                payload,
                tag,
                associatedData);
        }
        else
        {
            payload = compressed;
        }

        using var stream = new MemoryStream(MaxFrameOverheadBytes + payload.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write(sequence);
        writer.Write(record.RecordId.ToByteArray());
        writer.Write(timestamp);
        writer.Write(flags);
        writer.Write(body.Length);
        writer.Write(nonce);
        writer.Write(tag);
        writer.Write(digest);
        writer.Write(payload.Length);
        writer.Write(payload);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] SerializeRecordBody(DurableRecordWrite record) =>
        JsonSerializer.SerializeToUtf8Bytes(new RecordBody(
            record.Level,
            record.EventName,
            record.Message,
            record.ExceptionType,
            record.CorrelationId,
            record.Artifact,
            record.ExceptionText,
            record.MessageTemplate,
            record.Category,
            record.EventIdId,
            record.EventIdName,
            record.TraceId,
            record.SpanId,
            record.Properties));

    private DurableRecord DecodeFrame(Guid segmentId, byte[] frame) =>
        DecodeFrameEnvelope(segmentId, frame).Record;

    private DecodedFrame DecodeFrameEnvelope(Guid segmentId, byte[] frame)
    {
        using var stream = new MemoryStream(frame, writable: false);
        using var reader = new BinaryReader(stream);
        var sequence = reader.ReadInt64();
        var recordId = new Guid(ReadExactly(reader, 16));
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());
        var flags = reader.ReadByte();
        var uncompressedLength = reader.ReadInt32();
        if ((flags & 2) == 0 || (flags & ~7) != 0)
            throw new InvalidDataException("Durable frame flags are invalid.");
        if (uncompressedLength < 0 || uncompressedLength > _options.MaxRecordBytes)
            throw new InvalidDataException("Durable frame decoded length is invalid.");
        var nonce = ReadExactly(reader, 12);
        var tag = ReadExactly(reader, 16);
        var digest = ReadExactly(reader, 32);
        var payloadLength = reader.ReadInt32();
        if (payloadLength < 0 || payloadLength > frame.Length)
            throw new InvalidDataException("Durable frame payload length is invalid.");
        var payload = ReadExactly(reader, payloadLength);
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Durable frame contains trailing bytes.");
        var associatedData = BuildAssociatedData(
            segmentId,
            sequence,
            recordId,
            timestamp.ToUnixTimeMilliseconds(),
            flags,
            uncompressedLength);

        byte[] compressed;
        if ((flags & 1) != 0)
        {
            var key = _options.EncryptionKey
                ?? throw new CryptographicException(
                    "Encrypted durable data cannot be read without the instance key.");
            compressed = new byte[payload.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(
                nonce,
                payload,
                tag,
                compressed,
                associatedData);
        }
        else
        {
            compressed = payload;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                ComputeFrameDigest(associatedData, compressed),
                digest))
        {
            throw new InvalidDataException("Durable frame digest mismatch.");
        }

        var bodyBytes = (flags & 2) != 0
            ? Decompress(compressed, uncompressedLength)
            : compressed;
        if (bodyBytes.Length != uncompressedLength)
            throw new InvalidDataException("Durable frame decoded length mismatch.");
        var body = JsonSerializer.Deserialize<RecordBody>(bodyBytes)
            ?? throw new InvalidDataException("Durable frame body is invalid.");
        return new DecodedFrame(
            new DurableRecord(
                sequence,
                recordId,
                timestamp,
                body.Level,
                body.EventName,
                body.Message,
                body.ExceptionType,
                body.CorrelationId,
                body.Artifact,
                body.ExceptionText,
                body.MessageTemplate,
                body.Category,
                body.EventIdId,
                body.EventIdName,
                body.TraceId,
                body.SpanId,
                body.Properties),
            (flags & 4) != 0);
    }

    private async Task<RecoveredOpenSegment> RecoverOpenSegmentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        long count = 0;
        var lastSequence = header.FirstSequence - 1;
        DateTimeOffset? lastTimestamp = null;
        while (stream.Position < stream.Length)
        {
            var frameStart = stream.Position;
            if (stream.Length - stream.Position < sizeof(int))
            {
                stream.SetLength(frameStart);
                break;
            }

            var frameLength = await ReadInt32Async(stream, cancellationToken)
                .ConfigureAwait(false);
            if (frameLength == FooterMarker)
            {
                var footerRemainder = stream.Length - stream.Position;
                if (footerRemainder > FooterBodyBytes)
                {
                    throw new InvalidDataException(
                        "Recovered durable segment has bytes after its footer.");
                }
                if (footerRemainder == FooterBodyBytes)
                {
                    var footerLastSequence = await ReadInt64Async(
                        stream,
                        cancellationToken).ConfigureAwait(false);
                    var footerCount = await ReadInt64Async(
                        stream,
                        cancellationToken).ConfigureAwait(false);
                    var footerDigest = new byte[32];
                    await ReadExactlyAsync(stream, footerDigest, cancellationToken)
                        .ConfigureAwait(false);
                    var actualDigest = await ComputePrefixDigestAsync(
                        path,
                        frameStart,
                        cancellationToken).ConfigureAwait(false);
                    if (footerLastSequence != lastSequence
                        || footerCount != count
                        || !CryptographicOperations.FixedTimeEquals(
                            footerDigest,
                            actualDigest))
                    {
                        throw new InvalidDataException(
                            "Recovered durable segment footer is inconsistent.");
                    }
                }

                // A crash may occur after the footer is flushed but before the
                // atomic rename. The records are already durable, so discard
                // either the complete footer or its incomplete tail and resume
                // the same segment as the active append target.
                stream.SetLength(frameStart);
                break;
            }
            ValidateFrameLength(frameLength);
            if (stream.Length - stream.Position < frameLength)
            {
                stream.SetLength(frameStart);
                break;
            }

            var frame = new byte[frameLength];
            await ReadExactlyAsync(stream, frame, cancellationToken).ConfigureAwait(false);
            var record = DecodeFrame(header.SegmentId, frame);
            if (record.Sequence != lastSequence + 1)
                throw new InvalidDataException("Durable stream sequence is not contiguous.");
            count++;
            lastSequence = record.Sequence;
            lastTimestamp = record.Timestamp;
        }

        stream.Position = stream.Length;
        return new RecoveredOpenSegment(
            new ActiveSegment(
                path,
                stream,
                header.SegmentId,
                header.FirstSequence,
                header.CreatedAt)
            {
                Count = count,
                LastSequence = lastSequence
            },
            lastTimestamp);
    }

    private IEnumerable<SegmentPath> EnumerateSegments(StreamState state)
    {
        foreach (var path in Directory.EnumerateFiles(state.DirectoryPath, "*.scseg")
                     .OrderBy(ParseFirstSequence))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var pieces = name.Split('-');
            if (pieces.Length < 3
                || !long.TryParse(pieces[0], out var first)
                || !long.TryParse(pieces[1], out var last))
            {
                throw new InvalidDataException($"Invalid sealed segment name '{name}'.");
            }
            yield return new SegmentPath(path, first, last, true);
        }

        if (state.Active is { } active)
        {
            yield return new SegmentPath(
                active.Path,
                active.FirstSequence,
                active.LastSequence,
                false);
        }
    }

    private static long FindFirstAvailableSequence(StreamState state)
    {
        var first = state.Active?.FirstSequence;
        foreach (var path in Directory.EnumerateFiles(
                     state.DirectoryPath,
                     "*.scseg"))
        {
            var candidate = ParseFirstSequence(path);
            first = first is null ? candidate : Math.Min(first.Value, candidate);
        }
        return first ?? state.NextSequence;
    }

    private static Task EnsureIdempotencyIndexAsync(
        StreamState state,
        CancellationToken cancellationToken)
    {
        if (state.IdempotencyIndex is not null)
            return Task.CompletedTask;
        var path = Path.Combine(state.DirectoryPath, IdempotencyFileName);
        state.IdempotencyIndex = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
        state.IdempotencyIndex.Position = state.IdempotencyIndex.Length;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static async Task<long> LoadIdempotencyIndexAsync(
        StreamState state,
        CancellationToken cancellationToken)
    {
        var stream = state.IdempotencyIndex
            ?? throw new InvalidOperationException("Idempotency index is not open.");
        var completeLength = stream.Length
            - stream.Length % IdempotencyEntryBytes;
        if (completeLength != stream.Length)
        {
            stream.SetLength(completeLength);
            stream.Flush(flushToDisk: true);
        }
        stream.Position = 0;
        long lastSequence = 0;
        var entry = new byte[IdempotencyEntryBytes];
        while (stream.Position < stream.Length)
        {
            var entryOffset = stream.Position;
            await ReadExactlyAsync(stream, entry, cancellationToken)
                .ConfigureAwait(false);
            var sequence = BitConverter.ToInt64(entry, 0);
            var recordId = new Guid(entry.AsSpan(sizeof(long), 16));
            if (sequence >= state.NextSequence)
            {
                stream.SetLength(entryOffset);
                stream.Flush(flushToDisk: true);
                break;
            }
            if (sequence <= lastSequence
                || recordId == Guid.Empty
                || state.IdempotentRecords.ContainsKey(recordId))
            {
                throw new InvalidDataException(
                    "Durable idempotency index is inconsistent.");
            }
            state.IdempotentRecords.Add(recordId, sequence);
            lastSequence = sequence;
        }
        stream.Position = stream.Length;
        return lastSequence;
    }

    private async Task EnsureIdempotencyReadyAsync(
        StreamState state,
        CancellationToken cancellationToken)
    {
        if (state.IdempotencyReady)
            return;
        await EnsureIdempotencyIndexAsync(state, cancellationToken)
            .ConfigureAwait(false);
        var lastIndexed = await LoadIdempotencyIndexAsync(
                state,
                cancellationToken)
            .ConfigureAwait(false);
        await ReconcileIdempotencyIndexAsync(
                state,
                lastIndexed,
                cancellationToken)
            .ConfigureAwait(false);
        state.IdempotencyReady = true;
    }

    private async Task ReconcileIdempotencyIndexAsync(
        StreamState state,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        foreach (var segment in EnumerateSegments(state))
        {
            if (segment.LastSequence is { } last && last <= afterSequence)
                continue;
            if (segment.IsSealed)
            {
                await VerifySealedSegmentAsync(segment.Path, cancellationToken)
                    .ConfigureAwait(false);
            }

            await using var stream = new FileStream(
                segment.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = await ReadHeaderAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            while (stream.Position < stream.Length)
            {
                var frameLength = await ReadInt32Async(stream, cancellationToken)
                    .ConfigureAwait(false);
                if (frameLength == FooterMarker)
                    break;
                ValidateFrameLength(frameLength);
                var frame = new byte[frameLength];
                await ReadExactlyAsync(stream, frame, cancellationToken)
                    .ConfigureAwait(false);
                var decoded = DecodeFrameEnvelope(header.SegmentId, frame);
                if (decoded.Record.Sequence <= afterSequence
                    || !decoded.IsIdempotent)
                {
                    continue;
                }
                if (state.IdempotentRecords.TryGetValue(
                        decoded.Record.RecordId,
                        out var existing))
                {
                    if (existing != decoded.Record.Sequence)
                    {
                        throw new InvalidDataException(
                            "An idempotent record ID has multiple sequences.");
                    }
                    continue;
                }
                state.IdempotentRecords.Add(
                    decoded.Record.RecordId,
                    decoded.Record.Sequence);
                await AppendIdempotencyEntryAsync(
                        state,
                        decoded.Record.Sequence,
                        decoded.Record.RecordId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task AppendIdempotencyEntryAsync(
        StreamState state,
        long sequence,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        var stream = state.IdempotencyIndex
            ?? throw new InvalidOperationException("Idempotency index is not open.");
        stream.Position = stream.Length;
        await stream.WriteAsync(BitConverter.GetBytes(sequence), cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(recordId.ToByteArray(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<StreamManifest?> ReadManifestAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directoryPath, ManifestFileName);
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<StreamManifest>(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Durable stream manifest is invalid.");
        if (manifest.Version != 1
            || manifest.NextSequence < 1
            || manifest.ExpiredRecordCount < 0
            || manifest.ExpiredRecordCount >= manifest.NextSequence)
        {
            throw new InvalidDataException("Durable stream manifest values are invalid.");
        }
        return manifest;
    }

    private static async Task WriteManifestAsync(
        StreamState state,
        CancellationToken cancellationToken)
    {
        await WriteManifestAsync(
            state.DirectoryPath,
            new StreamManifest(
                1,
                state.NextSequence,
                state.ExpiredRecordCount,
                state.LastTimestamp),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteManifestAsync(
        string directoryPath,
        StreamManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directoryPath);
        var path = Path.Combine(directoryPath, ManifestFileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        manifest,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    private async Task VerifySealedSegmentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (_verifiedSegments.ContainsKey(path))
            return;
        var footer = await ReadSealedFooterAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var prefixLength = new FileInfo(path).Length - FooterBytes;
        var actual = await ComputePrefixDigestAsync(
            path,
            prefixLength,
            cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, footer.Digest))
            throw new InvalidDataException("Sealed segment digest mismatch.");
        _verifiedSegments.TryAdd(path, 0);
    }

    private static async Task<SealedFooter> ReadSealedFooterAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length < SegmentHeaderBytes + FooterBytes)
            throw new InvalidDataException("Sealed segment is too short.");
        stream.Position = stream.Length - FooterBytes;
        var marker = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (marker != FooterMarker)
            throw new InvalidDataException("Sealed segment footer is missing.");
        var lastSequence = await ReadInt64Async(stream, cancellationToken)
            .ConfigureAwait(false);
        var count = await ReadInt64Async(stream, cancellationToken).ConfigureAwait(false);
        var digest = new byte[32];
        await ReadExactlyAsync(stream, digest, cancellationToken).ConfigureAwait(false);
        return new SealedFooter(lastSequence, count, digest);
    }

    private static async Task<byte[]> ComputePrefixDigestAsync(
        string path,
        long length,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return hash.GetHashAndReset();
    }

    private static async Task WriteHeaderAsync(
        Stream stream,
        Guid segmentId,
        long firstSequence,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(SegmentMagic, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(segmentId.ToByteArray(), cancellationToken).ConfigureAwait(false);
        await WriteInt64Async(stream, firstSequence, cancellationToken).ConfigureAwait(false);
        await WriteInt64Async(
            stream,
            createdAt.ToUnixTimeMilliseconds(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SegmentHeader> ReadHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var magic = new byte[SegmentMagic.Length];
        await ReadExactlyAsync(stream, magic, cancellationToken).ConfigureAwait(false);
        if (!magic.SequenceEqual(SegmentMagic))
            throw new InvalidDataException("Unknown durable segment version.");
        var id = new byte[16];
        await ReadExactlyAsync(stream, id, cancellationToken).ConfigureAwait(false);
        var first = await ReadInt64Async(stream, cancellationToken).ConfigureAwait(false);
        var created = await ReadInt64Async(stream, cancellationToken).ConfigureAwait(false);
        return new SegmentHeader(
            new Guid(id),
            first,
            DateTimeOffset.FromUnixTimeMilliseconds(created));
    }

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(source);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] source, int expectedLength)
    {
        using var input = new MemoryStream(source, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        var output = new byte[expectedLength];
        var offset = 0;
        while (offset < output.Length)
        {
            var read = brotli.Read(output, offset, output.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }
        if (offset != expectedLength || brotli.ReadByte() != -1)
            throw new InvalidDataException("Durable frame expansion length is invalid.");
        return output;
    }

    private static byte[] BuildAssociatedData(
        Guid segmentId,
        long sequence,
        Guid recordId,
        long timestamp,
        byte flags,
        int uncompressedLength)
    {
        var data = new byte[53];
        segmentId.TryWriteBytes(data);
        BitConverter.TryWriteBytes(data.AsSpan(16), sequence);
        recordId.TryWriteBytes(data.AsSpan(24));
        BitConverter.TryWriteBytes(data.AsSpan(40), timestamp);
        data[48] = flags;
        BitConverter.TryWriteBytes(data.AsSpan(49), uncompressedLength);
        return data;
    }

    private static byte[] ComputeFrameDigest(
        ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> compressed)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(associatedData);
        hash.AppendData(compressed);
        return hash.GetHashAndReset();
    }

    private static bool Matches(DurableRecord record, DurableReadOptions options)
    {
        if (options.From is { } from && record.Timestamp < from)
            return false;
        if (options.To is { } to && record.Timestamp > to)
            return false;
        if (options.MinimumLevel is { } minimum
            && GetLevelRank(record.Level) < GetLevelRank(minimum))
        {
            return false;
        }
        return options.Contains is null
            || record.Message.Contains(options.Contains, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetLevelRank(string level) => level.ToLowerInvariant() switch
    {
        "trace" => 0,
        "debug" => 1,
        "information" or "info" => 2,
        "warning" or "warn" => 3,
        "error" => 4,
        "critical" or "fatal" => 5,
        _ => 2,
    };

    private static DurableRecordPage BuildPage(
        StreamState state,
        IReadOnlyList<DurableRecord> records,
        int returnedBytes,
        long? nextSequence,
        bool hasMore,
        long snapshot) =>
        new(
            records,
            hasMore ? nextSequence : null,
            hasMore,
            returnedBytes,
            snapshot,
            state.FirstAvailableSequence,
            state.ExpiredRecordCount);

    private void ValidateRecord(DurableRecordWrite record)
    {
        if (record.RecordId == Guid.Empty)
            throw new ArgumentException("Record ID is required.", nameof(record));
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Level);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EventName);
        ArgumentNullException.ThrowIfNull(record.Message);
    }

    private void ValidateReadOptions(DurableReadOptions options)
    {
        if (options.Take is < 1 || options.Take > _options.MaxPageRecords)
            throw new ArgumentOutOfRangeException(nameof(options.Take));
        if (options.MaxBytes is < 1 || options.MaxBytes > _options.MaxPageBytes)
            throw new ArgumentOutOfRangeException(nameof(options.MaxBytes));
        if (options.MaxScanBytes < options.MaxBytes
            || options.MaxScanBytes > _options.MaxReadScanBytes)
            throw new ArgumentOutOfRangeException(nameof(options.MaxScanBytes));
        if (options.Contains is { Length: > 4096 })
            throw new ArgumentOutOfRangeException(nameof(options.Contains));
        if (options.From is { } from
            && options.To is { } to
            && from > to)
        {
            throw new ArgumentException(
                "The durable read start time must not follow its end time.");
        }
    }

    private static void ValidateOptions(DurableStorageOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        if (options.EncryptionKey is { Length: not 32 })
            throw new ArgumentException("Durable encryption key must contain 256 bits.");
        if (options.SegmentMaxBytes < 64 * 1024
            || options.SegmentMaxBytes > options.MaxReadScanBytes)
            throw new ArgumentOutOfRangeException(nameof(options.SegmentMaxBytes));
        if (options.SegmentMaxAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.SegmentMaxAge));
        if (options.MaxRecordBytes is < 1024 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.MaxRecordBytes));
        if (options.MaxPageRecords < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaxPageRecords));
        if (options.MaxPageBytes < 1024
            || options.MaxPageBytes > DurableStorageOptions.HardMaximumPageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxPageBytes));
        }
        if (options.MaxPageBytes < options.MaxRecordBytes
                + MaxFrameOverheadBytes
                + SegmentHeaderBytes
                + sizeof(int))
            throw new ArgumentOutOfRangeException(nameof(options.MaxPageBytes));
        if (options.MaxReadScanBytes < options.MaxPageBytes
            || options.MaxReadScanBytes
                > DurableStorageOptions.HardMaximumReadScanBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxReadScanBytes));
        }
    }

    private void ValidateFrameLength(int frameLength)
    {
        if (frameLength < MaxFrameOverheadBytes
            || frameLength > _options.MaxRecordBytes + MaxFrameOverheadBytes)
        {
            throw new InvalidDataException("Durable frame length is invalid.");
        }
    }

    private static long ParseFirstSequence(string path)
    {
        var name = Path.GetFileName(path);
        var separator = name.IndexOf('-');
        return separator > 0 && long.TryParse(name[..separator], out var value)
            ? value
            : throw new InvalidDataException($"Invalid segment name '{name}'.");
    }

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return bytes;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await stream.ReadAsync(destination[read..], cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
                throw new EndOfStreamException();
            read += count;
        }
    }

    private static async Task<int> ReadInt32Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return BitConverter.ToInt32(bytes);
    }

    private static async Task<long> ReadInt64Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[sizeof(long)];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return BitConverter.ToInt64(bytes);
    }

    private static Task WriteInt32Async(
        Stream stream,
        int value,
        CancellationToken cancellationToken) =>
        stream.WriteAsync(BitConverter.GetBytes(value), cancellationToken).AsTask();

    private static Task WriteInt64Async(
        Stream stream,
        long value,
        CancellationToken cancellationToken) =>
        stream.WriteAsync(BitConverter.GetBytes(value), cancellationToken).AsTask();

    private static void FlushToDisk(FileStream stream)
    {
        stream.Flush(flushToDisk: true);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposeState != 0, this);

    private sealed class EnumerationBudget : IDisposable
    {
        private readonly CancellationToken _callerToken;
        private readonly CancellationTokenSource _timeoutSource;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly TimeSpan _maximumDuration;
        private readonly long _maximumBytes;

        public EnumerationBudget(
            DurableOperationalStreamEnumerationOptions options,
            CancellationToken callerToken)
        {
            _callerToken = callerToken;
            _maximumDuration = options.MaxDuration;
            _maximumBytes = options.MaxScanBytes;
            _timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            _timeoutSource.CancelAfter(options.MaxDuration);
        }

        public CancellationToken Token => _timeoutSource.Token;
        public long ScannedBytes { get; private set; }
        public bool IsExpired =>
            Stopwatch.GetElapsedTime(_startedAt) >= _maximumDuration
            || (_timeoutSource.IsCancellationRequested && !_callerToken.IsCancellationRequested);

        public void Consume(long bytes)
        {
            if (bytes < 0 || bytes > _maximumBytes - ScannedBytes)
                throw new CatalogBudgetExceededException();
            ThrowIfExpired();
            ScannedBytes = checked(ScannedBytes + bytes);
            ThrowIfExpired();
        }

        public void ThrowIfExpired()
        {
            Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(_startedAt) >= _maximumDuration)
                throw new CatalogBudgetExceededException();
        }

        public void Dispose() => _timeoutSource.Dispose();
    }

    private sealed class CatalogBudgetExceededException : Exception
    {
    }

    private sealed record CatalogIdentity(
        DurableStreamKey Stream,
        string? AppName,
        string? SourceId,
        Guid BootId);

    private sealed record CatalogIdentityRead(
        CatalogIdentity? Value,
        DurableOperationalStreamIdentityGap? Gap);

    private sealed record CatalogStreamScan(
        bool HasActiveSegment,
        bool HasSealedSegments,
        long RecordCount,
        long EncodedBytes,
        long FirstSequence,
        long? LastSequence,
        long FirstAvailableSequence,
        long ExpiredRecordCount,
        DateTimeOffset? LastTimestamp);

    private sealed record StreamIdentity(
        int Version,
        string CanonicalValue);

    private sealed class StreamState(DurableStreamKey key, string directoryPath)
    {
        public DurableStreamKey Key { get; } = key;
        public string DirectoryPath { get; } = directoryPath;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Initialized { get; set; }
        public long NextSequence { get; set; } = 1;
        public long RecordCount { get; set; }
        public long EncodedBytes { get; set; }
        public long FirstAvailableSequence { get; set; } = 1;
        public long ExpiredRecordCount { get; set; }
        public DateTimeOffset? LastTimestamp { get; set; }
        public ActiveSegment? Active { get; set; }
        public FileStream? IdempotencyIndex { get; set; }
        public bool IdempotencyReady { get; set; }
        public Dictionary<Guid, long> IdempotentRecords { get; } = [];
    }

    private sealed class ActiveSegment(
        string path,
        FileStream stream,
        Guid segmentId,
        long firstSequence,
        DateTimeOffset createdAt)
    {
        public string Path { get; } = path;
        public FileStream Stream { get; } = stream;
        public Guid SegmentId { get; } = segmentId;
        public long FirstSequence { get; } = firstSequence;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public long Count { get; set; }
        public long LastSequence { get; set; } = firstSequence - 1;
    }

    private sealed record RecordBody(
        string Level,
        string EventName,
        string Message,
        string? ExceptionType,
        string? CorrelationId,
        DurableArtifactReference? Artifact,
        string? ExceptionText = null,
        string? MessageTemplate = null,
        string? Category = null,
        int? EventIdId = null,
        string? EventIdName = null,
        string? TraceId = null,
        string? SpanId = null,
        IReadOnlyDictionary<string, string>? Properties = null);

    private sealed record DecodedFrame(
        DurableRecord Record,
        bool IsIdempotent);

    private sealed record SegmentHeader(
        Guid SegmentId,
        long FirstSequence,
        DateTimeOffset CreatedAt);

    private sealed record SegmentPath(
        string Path,
        long FirstSequence,
        long? LastSequence,
        bool IsSealed);

    private sealed record SealedFooter(
        long LastSequence,
        long RecordCount,
        byte[] Digest);

    private sealed record RecoveredOpenSegment(
        ActiveSegment Active,
        DateTimeOffset? LastTimestamp);

    private sealed record StreamManifest(
        int Version,
        long NextSequence,
        long ExpiredRecordCount,
        DateTimeOffset? LastTimestamp);

    private sealed record ArtifactReferenceEntry(
        long Sequence,
        Guid ArtifactId);

    private sealed record RetentionCandidate(
        DurableStreamKind Kind,
        string Path,
        string DirectoryPath,
        long FirstSequence,
        long LastSequence,
        long EncodedBytes,
        DateTimeOffset LastWriteTime);

    private sealed record RetentionDeleteResult(
        int DeletedSegments,
        long ReclaimedBytes);

    private sealed record SegmentDeleteResult(
        int DeletedSegments,
        long ReclaimedBytes,
        long ExpiredRecords);
}
