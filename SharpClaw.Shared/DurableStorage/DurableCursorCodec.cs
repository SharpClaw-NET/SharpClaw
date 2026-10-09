using System.Security.Cryptography;
using System.Text.Json;

namespace SharpClaw.Shared.DurableStorage;

/// <summary>Produces opaque authenticated cursors bound to one stream and filter set.</summary>
public sealed class DurableCursorCodec(byte[] key, DurableStreamPathEncoder paths)
{
    private readonly byte[] _key = key is { Length: 32 }
        ? key.ToArray()
        : throw new ArgumentException("Cursor key must contain 256 bits.", nameof(key));
    private readonly DurableStreamPathEncoder _paths = paths
        ?? throw new ArgumentNullException(nameof(paths));

    public string Encode(
        DurableStreamKey stream,
        long nextSequence,
        long snapshotLastSequence,
        string filterFingerprint)
    {
        if (nextSequence < 1)
            throw new ArgumentOutOfRangeException(nameof(nextSequence));
        ArgumentNullException.ThrowIfNull(filterFingerprint);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new CursorPayload(
            1,
            _paths.GetStreamHash(stream),
            nextSequence,
            snapshotLastSequence,
            filterFingerprint));
        var signature = HMACSHA256.HashData(_key, payload);
        var token = new byte[payload.Length + signature.Length];
        payload.CopyTo(token, 0);
        signature.CopyTo(token, payload.Length);
        return Base64UrlEncode(token);
    }

    public DurableCursor Decode(
        string cursor,
        DurableStreamKey expectedStream,
        string expectedFilterFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ArgumentNullException.ThrowIfNull(expectedFilterFingerprint);
        byte[] token;
        try
        {
            token = Base64UrlDecode(cursor);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Durable cursor is malformed.", ex);
        }

        if (token.Length <= 32)
            throw new InvalidDataException("Durable cursor is truncated.");
        var payload = token.AsSpan(0, token.Length - 32);
        var signature = token.AsSpan(token.Length - 32);
        var expectedSignature = HMACSHA256.HashData(_key, payload);
        if (!CryptographicOperations.FixedTimeEquals(signature, expectedSignature))
            throw new InvalidDataException("Durable cursor authentication failed.");

        var decoded = JsonSerializer.Deserialize<CursorPayload>(payload)
            ?? throw new InvalidDataException("Durable cursor payload is invalid.");
        if (decoded.Version != 1
            || !string.Equals(
                decoded.StreamHash,
                _paths.GetStreamHash(expectedStream),
                StringComparison.Ordinal)
            || !string.Equals(
                decoded.FilterFingerprint,
                expectedFilterFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Durable cursor does not match this stream or filter.");
        }
        if (decoded.NextSequence < 1
            || decoded.SnapshotLastSequence < decoded.NextSequence - 1)
        {
            throw new InvalidDataException("Durable cursor sequence range is invalid.");
        }

        return new DurableCursor(
            decoded.NextSequence,
            decoded.SnapshotLastSequence);
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + ((4 - normalized.Length % 4) % 4),
            '=');
        return Convert.FromBase64String(normalized);
    }

    private sealed record CursorPayload(
        int Version,
        string StreamHash,
        long NextSequence,
        long SnapshotLastSequence,
        string FilterFingerprint);
}
