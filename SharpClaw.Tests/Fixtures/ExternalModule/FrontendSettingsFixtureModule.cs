using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.ExternalRegistration;

public sealed class FrontendSettingsFixtureModule : ISharpClawModule
{
    public const string SourceId = "synthetic_frontend_settings";
    public ModuleIdentity Identity { get; } = new(SourceId, "External frontend settings", "sfs");
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SettingsFixtureState>();
        services.AddHttpEndpoint<SettingsFixtureEndpoint>(new("fixture.settings.read", "/fixture/settings", "GET", HostEndpointTransport.Http));
        services.AddHttpEndpoint<SettingsFixtureEndpoint>(new("fixture.settings.save", "/fixture/settings", "POST", HostEndpointTransport.Http));
    }
}

public sealed class EmptyFrontendSettingsFixtureModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new("empty_frontend_settings", "Empty frontend settings", "efs");
    public void ConfigureServices(IServiceCollection services) { }
}

internal sealed class SettingsFixtureState
{
    public string Message { get; set; } = "original";
}

internal sealed class SettingsFixtureEndpoint(SettingsFixtureState state) : IHttpEndpointHandler
{
    public ValueTask<HttpEndpointResponse> InvokeAsync(HostEndpointRouteRequest request,
        IHostActionEntry hostActionEntry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Route.Method == "POST")
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
