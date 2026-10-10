using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.ExternalRegistration;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "The module registration supplies this type to Microsoft DI, which activates its constructor through reflection.")]
internal sealed class SettingsFixtureEndpoint(SettingsFixtureState state) : IHttpEndpointHandler
{
    public ValueTask<HttpEndpointResponse> InvokeAsync(HostEndpointRouteRequest request,
        IHostActionEntry hostActionEntry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(request.Route.Method, "POST", StringComparison.Ordinal))
        {
            using var json = JsonDocument.Parse(request.Body);
            state.Message = json.RootElement.GetProperty("values").GetProperty("message").GetString()!;
            return ValueTask.FromResult(HttpEndpointResponse.Empty(204));
        }
        return ValueTask.FromResult(HttpEndpointResponse.Json(200, JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            fields = new[] { new { key = "message", label = "Message", kind = "text", required = true } },
            values = new { message = state.Message },
        })));
    }
}
