using System.Security.Cryptography;
using SharpClaw.Contracts.Enums;
using SharpClaw.Runtime.INF.DurableStorage;

namespace SharpClaw.Tests.DurableStorage;

[TestFixture]
public sealed class ExecutionArtifactStoreTests
{
    private static readonly byte[] TestKey =
        SHA256.HashData("SharpClaw artifact tests"u8);
    private readonly List<string> _roots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var root in _roots)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        _roots.Clear();
    }

    [Test]
    public async Task PutAndOpenRead_RoundTripsChunksAndExactRanges()
    {
        var root = CreateRoot();
        var store = new ExecutionArtifactStore(root, TestKey);
        var ownerId = Guid.NewGuid();
        var payload = RandomNumberGenerator.GetBytes(150_000);

        var descriptor = await store.PutAsync(
            new MemoryStream(payload, writable: false),
            new ArtifactWriteRequest(
                ExecutionOwnerKind.AgentJob,
                ownerId,
                "application/octet-stream",
                "binary result"));

        descriptor.Length.Should().Be(payload.Length);
        descriptor.Sha256.Should().Be(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        await using (var handle = await store.OpenReadAsync(
                         descriptor.Id,
                         ExecutionOwnerKind.AgentJob,
                         ownerId))
        {
            handle.Should().NotBeNull();
            var roundTrip = await ReadAllAsync(handle!.Content);
            roundTrip.Should().Equal(payload);
        }

        const int offset = 65_000;
        const int length = 5_000;
        await using (var range = await store.OpenReadAsync(
                         descriptor.Id,
                         ExecutionOwnerKind.AgentJob,
                         ownerId,
                         new ArtifactRange(offset, length)))
        {
            range.Should().NotBeNull();
            var bytes = await ReadAllAsync(range!.Content);
            bytes.Should().Equal(payload.AsSpan(offset, length).ToArray());
        }
    }

    [Test]
    public async Task OpenRead_EnforcesOwnerAndKeyBinding()
    {
        var root = CreateRoot();
        var ownerId = Guid.NewGuid();
        var store = new ExecutionArtifactStore(root, TestKey);
        var descriptor = await PutAsync(store, ownerId, "secret");

        Func<Task> wrongOwner = async () =>
            _ = await store.OpenReadAsync(
                descriptor.Id,
                ExecutionOwnerKind.TaskInstance,
                ownerId);
        await wrongOwner.Should().ThrowAsync<UnauthorizedAccessException>();

        var wrongKeyStore = new ExecutionArtifactStore(
            root,
            RandomNumberGenerator.GetBytes(32));
        Func<Task> wrongKey = async () =>
            _ = await wrongKeyStore.OpenReadAsync(
                descriptor.Id,
                ExecutionOwnerKind.AgentJob,
                ownerId);
        await wrongKey.Should().ThrowAsync<CryptographicException>();
    }

    [Test]
    public async Task OpenRead_RejectsRangeLengthThatOverflowsTheArtifactEnd()
    {
        var root = CreateRoot();
        var ownerId = Guid.NewGuid();
        var store = new ExecutionArtifactStore(root, TestKey);
        var descriptor = await PutAsync(store, ownerId, "range");

        Func<Task> overflowingRange = async () =>
        {
            await using var handle = await store.OpenReadAsync(
                descriptor.Id,
                ExecutionOwnerKind.AgentJob,
                ownerId,
                new ArtifactRange(1, long.MaxValue));
        };

        await overflowingRange.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task OpenRead_RejectsAuthenticatedChunkCountBeyondEncodedFileBounds()
    {
        var root = CreateRoot();
        var ownerId = Guid.NewGuid();
        var store = new ExecutionArtifactStore(root, TestKey);
        var descriptor = await PutAsync(store, ownerId, "chunk-count");
        var path = FindPath(root, descriptor.Id);
        var encoded = await File.ReadAllBytesAsync(path);
        var offsets = LocateMetadataOffsets(encoded);
        BitConverter.GetBytes(100_000).CopyTo(encoded, offsets.ChunkCount);
        AuthenticateHeader(encoded, offsets.Authentication);
        await File.WriteAllBytesAsync(path, encoded);

        Func<Task> excessiveChunkCount = async () =>
        {
            await using var handle = await store.OpenReadAsync(
                descriptor.Id,
                ExecutionOwnerKind.AgentJob,
                ownerId);
        };

        await excessiveChunkCount.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("Artifact chunk count exceeds configured or encoded bounds.");
    }

    [Test]
    public async Task OpenRead_RejectsAuthenticatedLengthBeyondConfiguredMaximum()
    {
        var root = CreateRoot();
        var ownerId = Guid.NewGuid();
        const int maximumBytes = 64 * 1024;
        var store = new ExecutionArtifactStore(root, TestKey, maximumBytes);
        var descriptor = await PutAsync(store, ownerId, "configured-bound");
        var path = FindPath(root, descriptor.Id);
        var encoded = await File.ReadAllBytesAsync(path);
        var offsets = LocateMetadataOffsets(encoded);
        BitConverter.GetBytes((long)maximumBytes + 1).CopyTo(encoded, offsets.Length);
        AuthenticateHeader(encoded, offsets.Authentication);
        await File.WriteAllBytesAsync(path, encoded);

        Func<Task> excessiveLength = async () =>
        {
            await using var handle = await store.OpenReadAsync(
                descriptor.Id,
                ExecutionOwnerKind.AgentJob,
                ownerId);
        };

        await excessiveLength.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("Artifact metadata is invalid.");
    }

    [Test]
    public async Task PutAndOpenRead_FillsCanonicalChunksAcrossShortSourceReads()
    {
        var root = CreateRoot();
        var ownerId = Guid.NewGuid();
        var store = new ExecutionArtifactStore(root, TestKey);
        var payload = RandomNumberGenerator.GetBytes(150_000);
        await using var source = new ShortReadStream(payload, maximumReadBytes: 17);

        var descriptor = await store.PutAsync(
            source,
            new ArtifactWriteRequest(
                ExecutionOwnerKind.AgentJob,
                ownerId,
                "application/octet-stream"));
        await using var handle = await store.OpenReadAsync(
            descriptor.Id,
            ExecutionOwnerKind.AgentJob,
            ownerId);

        handle.Should().NotBeNull();
        (await ReadAllAsync(handle!.Content)).Should().Equal(payload);
    }

    [Test]
    public async Task OpenRead_AuthenticatesHeaderOwnershipAndCiphertext()
    {
        var root = CreateRoot();
        var store = new ExecutionArtifactStore(root, TestKey);
        var ownerId = Guid.NewGuid();
        var headerDescriptor = await PutAsync(store, ownerId, "header");
        var headerPath = FindPath(root, headerDescriptor.Id);
        await using (var file = new FileStream(
                         headerPath,
                         FileMode.Open,
                         FileAccess.Write,
                         FileShare.None))
        {
            file.Position = 24;
            await file.WriteAsync(BitConverter.GetBytes(
                (int)ExecutionOwnerKind.TaskInstance));
        }

        Func<Task> tamperedHeader = async () =>
            _ = await store.OpenReadAsync(
                headerDescriptor.Id,
                ExecutionOwnerKind.TaskInstance,
                ownerId);
        await tamperedHeader.Should().ThrowAsync<CryptographicException>();

        var cipherDescriptor = await PutAsync(store, ownerId, "ciphertext");
        var cipherPath = FindPath(root, cipherDescriptor.Id);
        var encoded = await File.ReadAllBytesAsync(cipherPath);
        encoded[^1] ^= 0x20;
        await File.WriteAllBytesAsync(cipherPath, encoded);

        await using var handle = await store.OpenReadAsync(
            cipherDescriptor.Id,
            ExecutionOwnerKind.AgentJob,
            ownerId);
        handle.Should().NotBeNull();
        Func<Task> tamperedCiphertext = async () =>
            _ = await ReadAllAsync(handle!.Content);
        await tamperedCiphertext.Should().ThrowAsync<CryptographicException>();
    }

    [Test]
    public async Task Retention_PreservesProtectedArtifactsAndDeletesEligibleOrphans()
    {
        var root = CreateRoot();
        var store = new ExecutionArtifactStore(root, TestKey);
        var ownerId = Guid.NewGuid();
        var protectedArtifact = await PutAsync(store, ownerId, "protected");
        var orphan = await PutAsync(store, ownerId, "orphan");

        var result = await store.ApplyRetentionAsync(
            new HashSet<Guid> { protectedArtifact.Id },
            TimeSpan.FromTicks(1),
            TimeSpan.FromTicks(1),
            TimeSpan.FromTicks(1),
            long.MaxValue,
            0,
            maximumDeletes: 10);

        result.DeletedArtifacts.Should().Be(1);
        await using var protectedHandle = await store.OpenReadAsync(
            protectedArtifact.Id,
            ExecutionOwnerKind.AgentJob,
            ownerId);
        protectedHandle.Should().NotBeNull();
        var orphanHandle = await store.OpenReadAsync(
            orphan.Id,
            ExecutionOwnerKind.AgentJob,
            ownerId);
        orphanHandle.Should().BeNull();
    }

    [Test]
    public async Task Retention_DoesNotDeleteYoungArtifactsToPretendQuotaSuccess()
    {
        var root = CreateRoot();
        var store = new ExecutionArtifactStore(root, TestKey);
        var ownerId = Guid.NewGuid();
        var descriptor = await PutAsync(store, ownerId, "young");

        var result = await store.ApplyRetentionAsync(
            new HashSet<Guid>(),
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(1),
            maximumEncodedBytes: 0,
            minimumFreeBytes: 0,
            maximumDeletes: 10);

        result.DeletedArtifacts.Should().Be(0);
        result.QuotaSatisfied.Should().BeFalse();
        await using var handle = await store.OpenReadAsync(
            descriptor.Id,
            ExecutionOwnerKind.AgentJob,
            ownerId);
        handle.Should().NotBeNull();
    }

    [Test]
    public async Task Retention_UsesPressureOnlyAfterTheOrphanGracePeriod()
    {
        var root = CreateRoot();
        var store = new ExecutionArtifactStore(root, TestKey);
        var ownerId = Guid.NewGuid();
        var descriptor = await PutAsync(store, ownerId, "old enough orphan");
        await Task.Delay(10);

        var result = await store.ApplyRetentionAsync(
            new HashSet<Guid>(),
            TimeSpan.FromDays(30),
            TimeSpan.FromDays(30),
            TimeSpan.FromMilliseconds(1),
            maximumEncodedBytes: 0,
            minimumFreeBytes: 0,
            maximumDeletes: 10);

        result.DeletedArtifacts.Should().Be(1);
        result.QuotaSatisfied.Should().BeTrue();
        var handle = await store.OpenReadAsync(
            descriptor.Id,
            ExecutionOwnerKind.AgentJob,
            ownerId);
        handle.Should().BeNull();
    }

    private async Task<ExecutionArtifactDescriptor> PutAsync(
        ExecutionArtifactStore store,
        Guid ownerId,
        string text) =>
        await store.PutAsync(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)),
            new ArtifactWriteRequest(
                ExecutionOwnerKind.AgentJob,
                ownerId,
                "text/plain",
                text));

    private string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.Tests",
            "artifacts",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static string FindPath(string root, Guid artifactId) =>
        Directory.GetFiles(root, artifactId.ToString("N") + ".scartifact", SearchOption.AllDirectories)
            .Single();

    private static (int Length, int ChunkCount, int Authentication) LocateMetadataOffsets(
        ReadOnlySpan<byte> encoded)
    {
        var offset = 8 + 16 + sizeof(int) + 16 + sizeof(long);
        var mediaTypeLength = BitConverter.ToInt32(encoded.Slice(offset, sizeof(int)));
        offset += sizeof(int) + mediaTypeLength;
        var previewLength = BitConverter.ToInt32(encoded.Slice(offset, sizeof(int)));
        offset += sizeof(int) + previewLength;
        var lengthOffset = offset;
        var chunkCountOffset = lengthOffset + sizeof(long) + 32;
        return (lengthOffset, chunkCountOffset, chunkCountOffset + sizeof(int));
    }

    private static void AuthenticateHeader(byte[] encoded, int authenticationOffset)
    {
        using var hmac = new HMACSHA256(TestKey);
        var authentication = hmac.ComputeHash(encoded, 0, authenticationOffset);
        authentication.CopyTo(encoded, authenticationOffset);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private sealed class ShortReadStream(byte[] payload, int maximumReadBytes)
        : MemoryStream(payload, writable: false)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(
                buffer[..Math.Min(buffer.Length, maximumReadBytes)],
                cancellationToken);
        }
    }
}
