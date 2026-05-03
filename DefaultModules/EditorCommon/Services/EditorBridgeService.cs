using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.Editor;
using SharpClaw.Modules.EditorCommon.Models;

namespace SharpClaw.Modules.EditorCommon.Services;

/// <summary>
/// Manages WebSocket connections to IDE extensions (VS 2026, VS Code).
/// Routes editor action requests from the agent to the appropriate
/// connected editor and waits for responses.
/// <para>
/// On connect the service auto-creates (or reuses) an
/// <see cref="SharpClaw.Contracts.Entities.Core.Resources.EditorSessionDB"/>
/// resource so agents can reference it immediately. On disconnect the
/// <c>ConnectionId</c> is cleared but the resource persists for
/// permission/default references.
/// </para>
/// </summary>
public sealed class EditorBridgeService(IServiceScopeFactory scopeFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, EditorConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<EditorActionResponse>> _pending = new();

    /// <summary>
    /// Returns all currently connected editors.
    /// </summary>
    public IReadOnlyList<EditorConnection> GetConnections() =>
        _connections.Values.ToList();

    /// <summary>
    /// Returns the first connected editor, or <c>null</c> if none.
    /// </summary>
    public EditorConnection? GetActiveConnection() =>
        _connections.Values.FirstOrDefault();

    /// <summary>
    /// Returns a connected editor by session ID, or <c>null</c>.
    /// </summary>
    public EditorConnection? GetConnection(Guid sessionId) =>
        _connections.Values.FirstOrDefault(c => c.SessionId == sessionId);

    /// <summary>
    /// Sends an action request to the connected editor identified by
    /// <paramref name="sessionId"/> and waits for the response.
    /// </summary>
    public async Task<EditorActionResponse> SendRequestAsync(
        Guid sessionId, string action,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        var conn = GetConnection(sessionId)
            ?? throw new InvalidOperationException(
                $"No editor connected with session {sessionId}.");

        var request = new EditorActionRequest(Guid.NewGuid(), action, parameters);
        var tcs = new TaskCompletionSource<EditorActionResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[request.RequestId] = tcs;

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                type = "request",
                requestId = request.RequestId,
                action = request.Action,
                @params = request.Params
            }, JsonOptions);

            await SendTextAsync(conn.Socket, json, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);

            using var reg = timeoutCts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException(
                    $"Editor did not respond to '{action}' within {RequestTimeout.TotalSeconds}s.")));

            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    /// <summary>
    /// Handles a new WebSocket connection from an editor extension.
    /// Auto-registers (or reuses) an <c>EditorSessionDB</c> resource,
    /// then reads messages in a loop until the connection closes.
    /// </summary>
    public async Task HandleConnectionAsync(
        WebSocket socket, CancellationToken ct)
    {
        string? connectionId = null;
        Guid? sessionId = null;

        try
        {
            // First message must be a registration
            var regJson = await ReceiveTextAsync(socket, ct);
            if (regJson is null) return;

            var regMsg = JsonSerializer.Deserialize<RegistrationEnvelope>(regJson, JsonOptions);
            if (regMsg?.Type != "register")
            {
                await socket.CloseAsync(WebSocketCloseStatus.ProtocolError,
                    "First message must be a registration", ct);
                return;
            }

            connectionId = Guid.NewGuid().ToString("N");

            // Parse the string key to the internal enum; fall back to Other for unknown keys.
            var editorType = Enum.TryParse<EditorType>(regMsg.EditorKey, ignoreCase: true, out var et)
                ? et
                : EditorType.Other;

            // Auto-register the session resource in the database
            using (var scope = scopeFactory.CreateScope())
            {
                var sessionSvc = scope.ServiceProvider.GetRequiredService<EditorSessionService>();
                var workspaceName = regMsg.WorkspacePath is not null
                    ? Path.GetFileName(regMsg.WorkspacePath)
                    : null;
                var name = $"{regMsg.EditorKey}"
                    + (workspaceName is not null ? $" — {workspaceName}" : "");

                var session = await sessionSvc.GetOrCreateAsync(
                    name, editorType, regMsg.EditorVersion,
                    regMsg.WorkspacePath, ct);

                session.ConnectionId = connectionId;
                sessionId = session.Id;

                var db = scope.ServiceProvider.GetRequiredService<EditorCommonDbContext>();
                await db.SaveChangesAsync(ct);
            }

            var conn = new EditorConnection(
                connectionId,
                sessionId.Value,
                editorType,
                regMsg.EditorVersion,
                regMsg.WorkspacePath,
                socket,
                DateTimeOffset.UtcNow);

            _connections[connectionId] = conn;

            // Send acknowledgement
            await SendTextAsync(socket, JsonSerializer.Serialize(new
            {
                type = "registered",
                sessionId = conn.SessionId,
                connectionId
            }, JsonOptions), ct);

            // Read loop
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(socket, ct);
                if (json is null) break;

                var envelope = JsonSerializer.Deserialize<ResponseEnvelope>(json, JsonOptions);
                if (envelope?.Type == "response" && envelope.RequestId.HasValue)
                {
                    if (_pending.TryGetValue(envelope.RequestId.Value, out var tcs))
                    {
                        tcs.TrySetResult(new EditorActionResponse(
                            envelope.RequestId.Value,
                            envelope.Success,
                            envelope.Data,
                            envelope.Error));
                    }
                }
            }
        }
        catch (WebSocketException) { /* connection dropped */ }
        catch (OperationCanceledException) { /* server shutting down */ }
        finally
        {
            if (connectionId is not null)
                _connections.TryRemove(connectionId, out _);

            // Clear the ConnectionId on the session so it shows as disconnected
            if (sessionId.HasValue)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<EditorCommonDbContext>();
                    var session = await db.EditorSessions.FindAsync(sessionId.Value);
                    if (session is not null)
                    {
                        session.ConnectionId = null;
                        await db.SaveChangesAsync();
                    }
                }
                catch { /* best effort cleanup */ }
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                catch { /* best effort */ }
            }
        }
    }

    // ── Internal message types ────────────────────────────────────

    private sealed class RegistrationEnvelope
    {
        public string? Type { get; set; }
        public string EditorKey { get; set; } = "";
        public string? EditorVersion { get; set; }
        public string? WorkspacePath { get; set; }
    }

    private sealed class ResponseEnvelope
    {
        public string? Type { get; set; }
        public Guid? RequestId { get; set; }
        public bool Success { get; set; }
        public string? Data { get; set; }
        public string? Error { get; set; }
    }

    // ── WebSocket helpers ─────────────────────────────────────────

    private static async Task SendTextAsync(
        WebSocket socket, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveTextAsync(
        WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, ct);

        if (result.MessageType == WebSocketMessageType.Close)
            return null;

        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}

/// <summary>
/// Represents an active WebSocket connection to an IDE extension.
/// </summary>
public sealed record EditorConnection(
    string ConnectionId,
    Guid SessionId,
    EditorType EditorType,
    string? EditorVersion,
    string? WorkspacePath,
    WebSocket Socket,
    DateTimeOffset ConnectedAt);
