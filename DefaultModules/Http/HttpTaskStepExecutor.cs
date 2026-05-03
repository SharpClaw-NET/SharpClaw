using System.Net.Http;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.Http;

/// <summary>
/// Module-side executor for the HTTP request task step
/// (<see cref="HttpStepKeys.HttpRequest"/>).  Resolves
/// <see cref="IHttpClientFactory"/> from the running task's scoped
/// services and performs the request without any direct dependency on
/// Core or Infrastructure types.
/// </summary>
/// <remarks>
/// The HTTP step descriptors declare the verb via
/// <c>TaskStepDescriptor.PrefixArgument</c>; the parser prepends it to
/// the parsed step's argument list so the host-side step contract
/// stays free of HTTP-specific properties.
/// </remarks>
public sealed class HttpTaskStepExecutor : ITaskStepExecutorExtension
{
    public string ModuleId => "sharpclaw_http";

    public bool CanExecute(string moduleStepKey) =>
        moduleStepKey == HttpStepKeys.HttpRequest;

    public async Task<bool> ExecuteAsync(
        string moduleStepKey,
        ITaskStepExecutionContext context,
        IReadOnlyList<string>? arguments,
        string? expression,
        string? resultVariable)
    {
        if (moduleStepKey != HttpStepKeys.HttpRequest)
            return false;

        var url = expression ?? string.Empty;

        // The parser prepends descriptor.PrefixArgument (the HTTP verb) as
        // the first argument; arguments[1..] are the call's resolved
        // positional arguments (arg[1] = URL, arg[2] = body when present).
        var method = arguments is { Count: > 0 } ? arguments[0].ToUpperInvariant() : "GET";

        var factory = context.Services.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient("TaskOrchestrator");

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT")
        {
            var body = arguments is { Count: > 2 }
                ? arguments[2]
                : arguments is { Count: > 1 }
                    ? arguments[1]
                    : string.Empty;
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, context.CancellationToken);
        var content = await response.Content.ReadAsStringAsync(context.CancellationToken);

        await context.AppendLogAsync($"HTTP {method} {url} → {(int)response.StatusCode}");

        if (resultVariable is not null)
            context.Variables[resultVariable] = content;

        return true;
    }
}
