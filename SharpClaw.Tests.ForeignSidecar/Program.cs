using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var mode = args.Length >= 2 && args[0] == "--mode" ? args[1] : "normal";

if (mode == "early-exit")
{
    Console.WriteLine("sidecar stdout before early exit");
    Console.Error.WriteLine("sidecar stderr before early exit");
    return 23;
}

var moduleDir = ReadEnv("SHARPCLAW_MODULE_DIR");
var dataDir = ReadEnv("SHARPCLAW_MODULE_DATA_DIR");
var controlAddress = ReadEnv("SHARPCLAW_CONTROL_ADDRESS");
var token = ReadEnv("SHARPCLAW_CONTROL_TOKEN");
var moduleId = ReadEnv("SHARPCLAW_MODULE_ID");
var runtime = ReadEnv("SHARPCLAW_MODULE_RUNTIME");
var toolPrefix = Environment.GetEnvironmentVariable("SHARPCLAW_TEST_TOOL_PREFIX") ?? "snm";

Console.WriteLine(
    $"ENV|moduleDir={moduleDir}|dataDir={dataDir}|control={controlAddress}|token={token}|moduleId={moduleId}|runtime={runtime}");
Console.Out.Flush();

if (mode == "never-ready")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

var uri = new Uri(controlAddress);
var listener = new TcpListener(IPAddress.Loopback, uri.Port);
listener.Start();

var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
_ = Task.Run(async () =>
{
    try
    {
        while (!stop.Task.IsCompleted)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleAsync(client));
        }
    }
    catch (SocketException)
    {
    }
    catch (ObjectDisposedException)
    {
    }
});

await stop.Task;
listener.Stop();
return 0;

async Task HandleAsync(TcpClient client)
{
    await using var stream = client.GetStream();
    using (client)
    {
        var request = await ReadRequestAsync(stream);
        if (!string.Equals(
                request.Headers.GetValueOrDefault("X-SharpClaw-Control-Token"),
                token,
                StringComparison.Ordinal))
        {
            await WriteTextAsync(stream, 401, "Unauthorized", "bad token", "text/plain");
            return;
        }

        switch (request.Path)
        {
            case "/.sharpclaw/handshake":
                await WriteJsonAsync(stream, new
                {
                    protocolVersion = 1,
                    moduleId,
                    toolPrefix,
                    runtime,
                    runtimeVersion = "test-runtime",
                    capabilities = new[] { "endpoints", "lifecycleHooks" },
                });
                break;

            case "/.sharpclaw/discovery":
                await WriteJsonAsync(stream, new
                {
                    endpoints = new[]
                    {
                        new
                        {
                            method = "GET",
                            routePattern = "/modules/sample/ping",
                            responseMode = "json",
                        },
                        new
                        {
                            method = "POST",
                            routePattern = "/modules/sample/echo",
                            responseMode = "json",
                        },
                    },
                });
                break;

            case "/.sharpclaw/health":
                await WriteJsonAsync(stream, new
                {
                    isHealthy = true,
                    message = "ready",
                });
                break;

            case "/.sharpclaw/initialize":
                await WriteJsonAsync(stream, new
                {
                    accepted = true,
                    message = "initialized",
                });
                break;

            case "/.sharpclaw/shutdown":
                await WriteJsonAsync(stream, new
                {
                    accepted = true,
                    message = "stopping",
                });
                if (mode != "ignore-shutdown")
                    stop.TrySetResult();
                break;

            case "/modules/sample/ping":
                await WriteJsonAsync(stream, new
                {
                    ok = true,
                    path = request.Path,
                    query = request.Query,
                    marker = request.Headers.GetValueOrDefault("X-Test-Marker"),
                }, ("X-Sidecar", "yes"));
                break;

            case "/modules/sample/echo":
                await WriteJsonAsync(stream, new
                {
                    method = request.Method,
                    path = request.Path,
                    query = request.Query,
                    body = request.Body,
                    contentType = request.Headers.GetValueOrDefault("Content-Type"),
                });
                break;

            default:
                await WriteTextAsync(stream, 404, "Not Found", "not found", "text/plain");
                break;
        }
    }
}

static string ReadEnv(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing required environment variable '{name}'.");

static async Task<SidecarRequest> ReadRequestAsync(NetworkStream stream)
{
    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
    var requestLine = await reader.ReadLineAsync() ?? string.Empty;
    var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var method = parts.Length >= 1 ? parts[0] : "GET";
    var requestUri = parts.Length >= 2 ? new Uri("http://127.0.0.1" + parts[1]) : new Uri("http://127.0.0.1/");
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    while (await reader.ReadLineAsync() is { } line && line.Length > 0)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0)
            continue;

        headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
    }

    var body = string.Empty;
    if (headers.TryGetValue("Content-Length", out var contentLengthText)
        && int.TryParse(contentLengthText, out var contentLength)
        && contentLength > 0)
    {
        var buffer = new char[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(offset, contentLength - offset));
            if (read == 0)
                break;

            offset += read;
        }

        body = new string(buffer, 0, offset);
    }

    return new SidecarRequest(
        method,
        requestUri.AbsolutePath,
        requestUri.Query,
        headers,
        body);
}

static Task WriteJsonAsync(
    NetworkStream stream,
    object value,
    params (string Name, string Value)[] headers) =>
    WriteTextAsync(
        stream,
        200,
        "OK",
        JsonSerializer.Serialize(value),
        "application/json",
        headers);

static async Task WriteTextAsync(
    NetworkStream stream,
    int statusCode,
    string reasonPhrase,
    string text,
    string contentType,
    params (string Name, string Value)[] extraHeaders)
{
    var bytes = Encoding.UTF8.GetBytes(text);
    var headerText =
        $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
        $"Content-Type: {contentType}; charset=utf-8\r\n" +
        $"Content-Length: {bytes.Length}\r\n" +
        "Connection: close\r\n";
    foreach (var (name, value) in extraHeaders)
        headerText += $"{name}: {value}\r\n";
    headerText += "\r\n";
    var headers = Encoding.ASCII.GetBytes(
        headerText);
    await stream.WriteAsync(headers);
    await stream.WriteAsync(bytes);
}

internal sealed record SidecarRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body);
