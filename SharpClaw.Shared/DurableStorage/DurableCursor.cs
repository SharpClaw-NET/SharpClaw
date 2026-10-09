using System.Security.Cryptography;
using System.Text.Json;

namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableCursor(long NextSequence, long SnapshotLastSequence);
