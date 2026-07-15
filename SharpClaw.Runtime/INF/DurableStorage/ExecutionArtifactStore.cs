using System.Security.Cryptography;
using System.Text;
using SharpClaw.Contracts.Enums;
using SharpClaw.Shared.DurableStorage;

namespace SharpClaw.Runtime.INF.DurableStorage;

public interface IExecutionArtifactStore
{
    ValueTask<ExecutionArtifactDescriptor> PutAsync(
        Stream content,
        ArtifactWriteRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ArtifactReadHandle?> OpenReadAsync(
        Guid artifactId,
        ExecutionOwnerKind expectedOwnerKind,
        Guid expectedOwnerId,
        ArtifactRange? range = null,
        CancellationToken cancellationToken = default);
}

public sealed record ArtifactWriteRequest(
    ExecutionOwnerKind OwnerKind,
    Guid OwnerId,
    string MediaType,
    string? Preview = null);

public sealed record ExecutionArtifactDescriptor(
    Guid Id,
    ExecutionOwnerKind OwnerKind,
    Guid OwnerId,
    string MediaType,
    long Length,
    string Sha256,
    DateTimeOffset CreatedAt,
    string? Preview);

public sealed record ArtifactRange(long Offset, long? Length = null);

public sealed class ArtifactReadHandle(
    ExecutionArtifactDescriptor descriptor,
    Stream content) : IAsyncDisposable
{
    public ExecutionArtifactDescriptor Descriptor { get; } = descriptor;
    public Stream Content { get; } = content;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record ArtifactStorageSnapshot(
    long EncodedBytes,
    long ArtifactCount);

public sealed record ArtifactRetentionResult(
    int DeletedArtifacts,
    long ReclaimedBytes,
    long RemainingEncodedBytes,
    bool QuotaSatisfied);

/// <summary>Chunked AES-GCM artifact storage with deterministic identifier lookup.</summary>
public sealed class ExecutionArtifactStore : IExecutionArtifactStore
{
    private static readonly byte[] Magic = "SCART001"u8.ToArray();
    private const int ChunkBytes = 64 * 1024;
    private const int ChunkMetadataBytes = sizeof(int) + 12 + 16 + sizeof(int);
    private const int HeaderAuthenticationBytes = 32;
    private const long MaximumRepresentableArtifactBytes =
        (long)int.MaxValue * ChunkBytes;
    private readonly string _root;
    private readonly byte[] _key;
    private readonly long _maxArtifactBytes;

    public ExecutionArtifactStore(
        string durableRoot,
        byte[] encryptionKey,
        long maxArtifactBytes = 10L * 1024 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(durableRoot);
        if (encryptionKey is not { Length: 32 })
            throw new ArgumentException(
                "Artifact encryption key must contain 256 bits.",
                nameof(encryptionKey));
        if (maxArtifactBytes < ChunkBytes
            || maxArtifactBytes > MaximumRepresentableArtifactBytes)
            throw new ArgumentOutOfRangeException(nameof(maxArtifactBytes));

        _root = Path.GetFullPath(Path.Combine(durableRoot, "artifacts"));
        _key = encryptionKey.ToArray();
        _maxArtifactBytes = maxArtifactBytes;
        Directory.CreateDirectory(_root);
    }

    public async ValueTask<ExecutionArtifactDescriptor> PutAsync(
        Stream content,
        ArtifactWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(request);
        if (!content.CanRead)
            throw new ArgumentException("Artifact stream must be readable.", nameof(content));
        if (request.OwnerId == Guid.Empty)
            throw new ArgumentException("Artifact owner ID is required.", nameof(request));
        if (!Enum.IsDefined(request.OwnerKind))
            throw new ArgumentException("Artifact owner kind is invalid.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        if (Encoding.UTF8.GetByteCount(request.MediaType) > 256)
            throw new ArgumentException("Artifact media type is too long.", nameof(request));
        if (request.Preview is not null
            && Encoding.UTF8.GetByteCount(request.Preview) > 4096)
        {
            throw new ArgumentException("Artifact preview is too long.", nameof(request));
        }

        var artifactId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var finalPath = GetPath(artifactId);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var temporaryPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            long length = 0;
            var chunkCount = 0;
            byte[] digest;
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             ChunkBytes,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var metadataOffset = await WriteHeaderAsync(
                    output,
                    artifactId,
                    request,
                    createdAt,
                    cancellationToken).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[ChunkBytes];
                while (true)
                {
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var current = await content.ReadAsync(
                                buffer.AsMemory(read),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (current == 0)
                            break;
                        read += current;
                    }
                    if (read == 0)
                        break;
                    length = checked(length + read);
                    if (length > _maxArtifactBytes)
                        throw new InvalidOperationException("Artifact exceeds the configured size limit.");

                    hash.AppendData(buffer, 0, read);
                    await WriteChunkAsync(
                        output,
                        artifactId,
                        chunkCount,
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);
                    chunkCount++;
                }

                digest = hash.GetHashAndReset();
                var end = output.Position;
                output.Position = metadataOffset;
                await WriteInt64Async(output, length, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(digest, cancellationToken).ConfigureAwait(false);
                await WriteInt32Async(output, chunkCount, cancellationToken).ConfigureAwait(false);
                var authenticationOffset = output.Position;
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                var authentication = await ComputePrefixHmacAsync(
                    output,
                    authenticationOffset,
                    _key,
                    cancellationToken).ConfigureAwait(false);
                output.Position = authenticationOffset;
                await output.WriteAsync(authentication, cancellationToken)
                    .ConfigureAwait(false);
                output.Position = end;
                output.Flush(flushToDisk: true);
            }

            await VerifyAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, finalPath);
            return new ExecutionArtifactDescriptor(
                artifactId,
                request.OwnerKind,
                request.OwnerId,
                request.MediaType,
                length,
                Convert.ToHexString(digest).ToLowerInvariant(),
                createdAt,
                request.Preview);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }

    public async ValueTask<ArtifactReadHandle?> OpenReadAsync(
        Guid artifactId,
        ExecutionOwnerKind expectedOwnerKind,
        Guid expectedOwnerId,
        ArtifactRange? range = null,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(artifactId);
        if (!File.Exists(path))
            return null;

        var stream = await ArtifactDecryptStream.OpenAsync(
                path,
                _key,
                _maxArtifactBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (stream.Descriptor.Id != artifactId)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException(
                "Artifact identifier does not match its durable path.");
        }
        if (stream.Descriptor.OwnerKind != expectedOwnerKind
            || stream.Descriptor.OwnerId != expectedOwnerId)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new UnauthorizedAccessException("Artifact does not belong to the authorized owner.");
        }

        Stream content = stream;
        if (range is not null)
        {
            if (range.Offset < 0 || range.Offset > stream.Length)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw new ArgumentOutOfRangeException(nameof(range));
            }
            var availableLength = stream.Length - range.Offset;
            var length = range.Length ?? availableLength;
            if (length < 0 || length > availableLength)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw new ArgumentOutOfRangeException(nameof(range));
            }
            stream.Position = range.Offset;
            content = new BoundedArtifactStream(stream, length);
        }

        return new ArtifactReadHandle(stream.Descriptor, content);
    }

    public ArtifactStorageSnapshot GetSnapshot()
    {
        var root = new DirectoryInfo(_root);
        if (!root.Exists)
            return new ArtifactStorageSnapshot(0, 0);
        var files = root.EnumerateFiles(
            "*.scartifact",
            SearchOption.AllDirectories);
        long bytes = 0;
        long count = 0;
        foreach (var file in files)
        {
            bytes = checked(bytes + file.Length);
            count++;
        }
        return new ArtifactStorageSnapshot(bytes, count);
    }

    public async Task<ArtifactRetentionResult> ApplyRetentionAsync(
        IReadOnlySet<Guid> protectedArtifactIds,
        TimeSpan jobMaximumAge,
        TimeSpan taskMaximumAge,
        TimeSpan orphanGraceAge,
        long maximumEncodedBytes,
        long minimumFreeBytes,
        int maximumDeletes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protectedArtifactIds);
        if (jobMaximumAge <= TimeSpan.Zero
            || taskMaximumAge <= TimeSpan.Zero
            || orphanGraceAge <= TimeSpan.Zero
            || maximumEncodedBytes < 0
            || minimumFreeBytes < 0
            || maximumDeletes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jobMaximumAge));
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = new List<ArtifactRetentionCandidate>();
        long totalBytes = 0;
        foreach (var path in Directory.EnumerateFiles(
                     _root,
                     "*.scartifact",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            totalBytes = checked(totalBytes + info.Length);
            var idText = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(idText, "N", out var id)
                || protectedArtifactIds.Contains(id))
            {
                continue;
            }

            await using var stream = await ArtifactDecryptStream.OpenAsync(
                    path,
                    _key,
                    _maxArtifactBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var maximumAge = stream.Descriptor.OwnerKind switch
            {
                ExecutionOwnerKind.AgentJob => jobMaximumAge,
                ExecutionOwnerKind.TaskInstance => taskMaximumAge,
                _ => throw new InvalidDataException(
                    "Artifact owner kind is not supported."),
            };
            candidates.Add(new ArtifactRetentionCandidate(
                id,
                path,
                info.Length,
                stream.Descriptor.CreatedAt,
                maximumAge));
        }

        var availableFreeBytes = GetAvailableFreeBytes();
        var selected = candidates
            .Where(candidate => now - candidate.CreatedAt >= candidate.MaximumAge)
            .OrderBy(candidate => candidate.CreatedAt)
            .Take(maximumDeletes)
            .ToList();
        var selectedIds = selected.Select(candidate => candidate.Id).ToHashSet();
        var projectedBytes = totalBytes
            - selected.Sum(candidate => candidate.EncodedBytes);
        var projectedFreeBytes = checked(
            availableFreeBytes
            + selected.Sum(candidate => candidate.EncodedBytes));
        foreach (var candidate in candidates
                     .Where(candidate => !selectedIds.Contains(candidate.Id))
                     .Where(candidate => now - candidate.CreatedAt >= orphanGraceAge)
                     .OrderBy(candidate => candidate.CreatedAt))
        {
            if (selected.Count >= maximumDeletes
                || (projectedBytes <= maximumEncodedBytes
                    && projectedFreeBytes >= minimumFreeBytes))
            {
                break;
            }

            selected.Add(candidate);
            selectedIds.Add(candidate.Id);
            projectedBytes -= candidate.EncodedBytes;
            projectedFreeBytes = checked(
                projectedFreeBytes + candidate.EncodedBytes);
        }
        var deleted = 0;
        long reclaimed = 0;
        foreach (var candidate in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(candidate.Path))
                    continue;
                var length = new FileInfo(candidate.Path).Length;
                File.Delete(candidate.Path);
                deleted++;
                reclaimed = checked(reclaimed + length);
            }
            catch (IOException)
            {
            }
        }

        var remaining = GetSnapshot().EncodedBytes;
        var free = GetAvailableFreeBytes();
        return new ArtifactRetentionResult(
            deleted,
            reclaimed,
            remaining,
            remaining <= maximumEncodedBytes && free >= minimumFreeBytes);
    }

    private string GetPath(Guid id)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Artifact ID is required.", nameof(id));
        var text = id.ToString("N");
        var candidate = Path.GetFullPath(
            Path.Combine(_root, text[..2], text + ".scartifact"));
        var boundary = _root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Artifact path escaped the durable root.");
        return candidate;
    }

    private long GetAvailableFreeBytes()
    {
        var root = Path.GetPathRoot(_root);
        return string.IsNullOrWhiteSpace(root)
            ? 0
            : new DriveInfo(root).AvailableFreeSpace;
    }

    private static async Task<long> WriteHeaderAsync(
        Stream output,
        Guid artifactId,
        ArtifactWriteRequest request,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(artifactId.ToByteArray(), cancellationToken).ConfigureAwait(false);
        await WriteInt32Async(output, (int)request.OwnerKind, cancellationToken)
            .ConfigureAwait(false);
        await output.WriteAsync(request.OwnerId.ToByteArray(), cancellationToken)
            .ConfigureAwait(false);
        await WriteInt64Async(
            output,
            createdAt.ToUnixTimeMilliseconds(),
            cancellationToken).ConfigureAwait(false);
        var mediaType = Encoding.UTF8.GetBytes(request.MediaType);
        await WriteInt32Async(output, mediaType.Length, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(mediaType, cancellationToken).ConfigureAwait(false);
        var preview = request.Preview is null
            ? []
            : Encoding.UTF8.GetBytes(request.Preview);
        await WriteInt32Async(output, preview.Length, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(preview, cancellationToken).ConfigureAwait(false);
        var metadataOffset = output.Position;
        await output.WriteAsync(
                new byte[sizeof(long) + 32 + sizeof(int) + HeaderAuthenticationBytes],
                cancellationToken)
            .ConfigureAwait(false);
        return metadataOffset;
    }

    private async Task WriteChunkAsync(
        Stream output,
        Guid artifactId,
        int chunkIndex,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(_key, tag.Length))
        {
            aes.Encrypt(
                nonce,
                plaintext.Span,
                ciphertext,
                tag,
                BuildAssociatedData(artifactId, chunkIndex));
        }
        await WriteInt32Async(output, plaintext.Length, cancellationToken)
            .ConfigureAwait(false);
        await output.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
        await WriteInt32Async(output, ciphertext.Length, cancellationToken)
            .ConfigureAwait(false);
        await output.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await ArtifactDecryptStream.OpenAsync(
            path,
            _key,
            _maxArtifactBytes,
            cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkBytes];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
            length += read;
        }
        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (length != stream.Descriptor.Length
            || !string.Equals(digest, stream.Descriptor.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Artifact verification failed.");
        }
    }

    private static byte[] BuildAssociatedData(Guid artifactId, int chunkIndex)
    {
        var data = new byte[20];
        artifactId.TryWriteBytes(data);
        BitConverter.TryWriteBytes(data.AsSpan(16), chunkIndex);
        return data;
    }

    private static int GetRequiredChunkCount(long length)
    {
        return length == 0
            ? 0
            : checked((int)(((length - 1) / ChunkBytes) + 1));
    }

    private sealed class ArtifactDecryptStream : Stream
    {
        private readonly FileStream _file;
        private readonly byte[] _key;
        private readonly Guid _artifactId;
        private readonly IReadOnlyList<ChunkInfo> _chunks;
        private byte[]? _currentChunk;
        private int _currentChunkIndex = -1;
        private long _position;

        private ArtifactDecryptStream(
            FileStream file,
            byte[] key,
            Guid artifactId,
            ExecutionArtifactDescriptor descriptor,
            IReadOnlyList<ChunkInfo> chunks)
        {
            _file = file;
            _key = key;
            _artifactId = artifactId;
            Descriptor = descriptor;
            _chunks = chunks;
        }

        public ExecutionArtifactDescriptor Descriptor { get; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => Descriptor.Length;
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public static async Task<ArtifactDecryptStream> OpenAsync(
            string path,
            byte[] key,
            long maxArtifactBytes,
            CancellationToken cancellationToken)
        {
            var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                ChunkBytes,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            {
                var magic = new byte[Magic.Length];
                await ExecutionArtifactStore.ReadExactlyAsync(
                    file,
                    magic,
                    cancellationToken).ConfigureAwait(false);
                if (!magic.SequenceEqual(Magic))
                    throw new InvalidDataException("Unknown artifact version.");
                var idBytes = new byte[16];
                await ExecutionArtifactStore.ReadExactlyAsync(
                    file,
                    idBytes,
                    cancellationToken).ConfigureAwait(false);
                var id = new Guid(idBytes);
                var ownerKind = (ExecutionOwnerKind)await ReadInt32Async(
                    file,
                    cancellationToken).ConfigureAwait(false);
                var ownerBytes = new byte[16];
                await ExecutionArtifactStore.ReadExactlyAsync(
                    file,
                    ownerBytes,
                    cancellationToken).ConfigureAwait(false);
                var ownerId = new Guid(ownerBytes);
                var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(
                    await ReadInt64Async(file, cancellationToken).ConfigureAwait(false));
                var mediaType = await ReadStringAsync(file, 256, cancellationToken)
                    .ConfigureAwait(false);
                var preview = await ReadStringAsync(file, 4096, cancellationToken)
                    .ConfigureAwait(false);
                var length = await ReadInt64Async(file, cancellationToken).ConfigureAwait(false);
                var digest = new byte[32];
                await ExecutionArtifactStore.ReadExactlyAsync(
                    file,
                    digest,
                    cancellationToken).ConfigureAwait(false);
                var chunkCount = await ReadInt32Async(file, cancellationToken)
                    .ConfigureAwait(false);
                if (length < 0
                    || length > maxArtifactBytes
                    || chunkCount < 0
                    || !Enum.IsDefined(ownerKind)
                    || id == Guid.Empty
                    || ownerId == Guid.Empty)
                    throw new InvalidDataException("Artifact metadata is invalid.");
                var authenticationOffset = file.Position;
                var encodedBytesAfterMetadata = file.Length - authenticationOffset;
                var expectedChunkCount = GetRequiredChunkCount(length);
                var minimumEncodedChunkBytes =
                    (long)chunkCount * (ChunkMetadataBytes + 1);
                if (chunkCount != expectedChunkCount
                    || encodedBytesAfterMetadata < HeaderAuthenticationBytes
                    || minimumEncodedChunkBytes
                    > encodedBytesAfterMetadata - HeaderAuthenticationBytes)
                {
                    throw new InvalidDataException(
                        "Artifact chunk count exceeds configured or encoded bounds.");
                }
                var authentication = new byte[HeaderAuthenticationBytes];
                await ExecutionArtifactStore.ReadExactlyAsync(
                    file,
                    authentication,
                    cancellationToken).ConfigureAwait(false);
                var expectedAuthentication = await ComputePrefixHmacAsync(
                    file,
                    authenticationOffset,
                    key,
                    cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(
                        authentication,
                        expectedAuthentication))
                {
                    throw new CryptographicException(
                        "Artifact metadata authentication failed.");
                }

                var chunks = new List<ChunkInfo>(chunkCount);
                long plaintextOffset = 0;
                for (var index = 0; index < chunkCount; index++)
                {
                    var plainLength = await ReadInt32Async(file, cancellationToken)
                        .ConfigureAwait(false);
                    var nonce = new byte[12];
                    await ExecutionArtifactStore.ReadExactlyAsync(
                        file,
                        nonce,
                        cancellationToken).ConfigureAwait(false);
                    var tag = new byte[16];
                    await ExecutionArtifactStore.ReadExactlyAsync(
                        file,
                        tag,
                        cancellationToken).ConfigureAwait(false);
                    var cipherLength = await ReadInt32Async(file, cancellationToken)
                        .ConfigureAwait(false);
                    var expectedPlainLength = checked((int)Math.Min(
                        ChunkBytes,
                        length - plaintextOffset));
                    if (plainLength != expectedPlainLength
                        || cipherLength != plainLength
                        || cipherLength > file.Length - file.Position)
                        throw new InvalidDataException("Artifact chunk metadata is invalid.");
                    chunks.Add(new ChunkInfo(
                        file.Position,
                        plaintextOffset,
                        plainLength,
                        nonce,
                        tag));
                    file.Position += cipherLength;
                    plaintextOffset = checked(plaintextOffset + plainLength);
                }
                if (plaintextOffset != length || file.Position != file.Length)
                    throw new InvalidDataException("Artifact chunk index is inconsistent.");

                return new ArtifactDecryptStream(
                    file,
                    key,
                    id,
                    new ExecutionArtifactDescriptor(
                        id,
                        ownerKind,
                        ownerId,
                        mediaType,
                        length,
                        Convert.ToHexString(digest).ToLowerInvariant(),
                        createdAt,
                        preview.Length == 0 ? null : preview),
                    chunks);
            }
            catch
            {
                await file.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position >= Length || buffer.Length == 0)
                return 0;
            var chunkIndex = FindChunk(_position);
            await LoadChunkAsync(chunkIndex, cancellationToken).ConfigureAwait(false);
            var chunk = _currentChunk!;
            var info = _chunks[chunkIndex];
            var inChunk = checked((int)(_position - info.PlaintextOffset));
            var count = Math.Min(buffer.Length, chunk.Length - inChunk);
            chunk.AsMemory(inChunk, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0 || target > Length)
                throw new IOException("Artifact seek is outside the content range.");
            _position = target;
            return target;
        }

        private int FindChunk(long position)
        {
            var low = 0;
            var high = _chunks.Count - 1;
            while (low <= high)
            {
                var mid = low + (high - low) / 2;
                var chunk = _chunks[mid];
                if (position < chunk.PlaintextOffset)
                    high = mid - 1;
                else if (position >= chunk.PlaintextOffset + chunk.PlainLength)
                    low = mid + 1;
                else
                    return mid;
            }
            throw new EndOfStreamException();
        }

        private async Task LoadChunkAsync(int index, CancellationToken cancellationToken)
        {
            if (_currentChunkIndex == index)
                return;
            var info = _chunks[index];
            _file.Position = info.CiphertextOffset;
            var ciphertext = new byte[info.PlainLength];
            await ExecutionArtifactStore.ReadExactlyAsync(
                _file,
                ciphertext,
                cancellationToken).ConfigureAwait(false);
            var plaintext = new byte[info.PlainLength];
            using var aes = new AesGcm(_key, info.Tag.Length);
            aes.Decrypt(
                info.Nonce,
                ciphertext,
                info.Tag,
                plaintext,
                BuildAssociatedData(_artifactId, index));
            _currentChunk = plaintext;
            _currentChunkIndex = index;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _file.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await _file.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private sealed record ChunkInfo(
            long CiphertextOffset,
            long PlaintextOffset,
            int PlainLength,
            byte[] Nonce,
            byte[] Tag);
    }

    private sealed class BoundedArtifactStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _remaining;

        public BoundedArtifactStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
                return 0;
            var read = await _inner.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, _remaining)],
                cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private static async Task<string> ReadStringAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var length = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (length < 0 || length > maxBytes)
            throw new InvalidDataException("Artifact string length is invalid.");
        var value = new byte[length];
        await ReadExactlyAsync(stream, value, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(value);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }

    private static async Task<byte[]> ComputePrefixHmacAsync(
        Stream stream,
        long length,
        byte[] key,
        CancellationToken cancellationToken)
    {
        if (!stream.CanSeek || !stream.CanRead)
            throw new ArgumentException("Authenticated stream must be seekable and readable.", nameof(stream));
        if (length < 0 || length > stream.Length)
            throw new ArgumentOutOfRangeException(nameof(length));

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            var buffer = new byte[64 * 1024];
            var remaining = length;
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
        finally
        {
            stream.Position = originalPosition;
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

    private sealed record ArtifactRetentionCandidate(
        Guid Id,
        string Path,
        long EncodedBytes,
        DateTimeOffset CreatedAt,
        TimeSpan MaximumAge);
}
