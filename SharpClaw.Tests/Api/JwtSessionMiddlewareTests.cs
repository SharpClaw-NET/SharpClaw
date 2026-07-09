using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.API.Api;
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Instances;

namespace SharpClaw.Tests.Api;

[TestFixture]
public sealed class JwtSessionMiddlewareTests
{
    [Test]
    public async Task InvokeAsync_WhenAccessTokenCheckDisabled_PopulatesConfiguredAdminSession()
    {
        var instanceRoot = CreateTempDirectory();
        var adminId = Guid.NewGuid();

        try
        {
            await using var provider = CreateProvider(instanceRoot);
            await SeedUserAsync(provider, adminId, "admin", isAdmin: true);

            await using var scope = provider.CreateAsyncScope();
            var calledNext = false;
            var middleware = new JwtSessionMiddleware(
                context =>
                {
                    var session = context.RequestServices.GetRequiredService<SessionService>();

                    session.UserId.Should().Be(adminId);
                    calledNext = true;
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                },
                provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<ApiKeyProvider>());

            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider
            };
            context.Request.Path = "/auth/me";

            await middleware.InvokeAsync(context);

            calledNext.Should().BeTrue();
            context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public async Task InvokeAsync_WhenAccessTokenCheckDisabledAndBearerUserExists_DoesNotReplaceSessionWithAdmin()
    {
        var instanceRoot = CreateTempDirectory();
        var adminId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        try
        {
            await using var provider = CreateProvider(instanceRoot);
            await SeedUserAsync(provider, adminId, "admin", isAdmin: true);
            await SeedUserAsync(provider, userId, "regular", isAdmin: false);

            var token = provider.GetRequiredService<TokenService>().GenerateAccessToken(userId, "regular");
            await using var scope = provider.CreateAsyncScope();
            var calledNext = false;
            var middleware = new JwtSessionMiddleware(
                context =>
                {
                    var session = context.RequestServices.GetRequiredService<SessionService>();

                    session.UserId.Should().Be(userId);
                    session.UserId.Should().NotBe(adminId);
                    calledNext = true;
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                },
                provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<ApiKeyProvider>());

            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider
            };
            context.Request.Path = "/auth/me";
            context.Request.Headers.Authorization = $"Bearer {token}";

            await middleware.InvokeAsync(context);

            calledNext.Should().BeTrue();
            context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public async Task InvokeAsync_WhenAccessTokenCheckDisabledAndAnonymousUsernameConfigured_UsesAnonymousUser()
    {
        var instanceRoot = CreateTempDirectory();
        var adminId = Guid.NewGuid();
        var anonymousId = Guid.NewGuid();

        try
        {
            await using var provider = CreateProvider(
                instanceRoot,
                new Dictionary<string, string?>
                {
                    ["Auth:AnonymousUsername"] = "anonymous"
                });
            await SeedUserAsync(provider, adminId, "admin", isAdmin: true);
            await SeedUserAsync(provider, anonymousId, "anonymous", isAdmin: false);

            await using var scope = provider.CreateAsyncScope();
            var calledNext = false;
            var middleware = new JwtSessionMiddleware(
                context =>
                {
                    var session = context.RequestServices.GetRequiredService<SessionService>();

                    session.UserId.Should().Be(anonymousId);
                    session.UserId.Should().NotBe(adminId);
                    calledNext = true;
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                },
                provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<ApiKeyProvider>());

            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider
            };
            context.Request.Path = "/auth/me";

            await middleware.InvokeAsync(context);

            calledNext.Should().BeTrue();
            context.Response.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    [Test]
    public async Task InvokeAsync_WhenAccessTokenCheckDisabledAndNoLocalAdminExists_ReturnsResourceUnavailable()
    {
        var instanceRoot = CreateTempDirectory();

        try
        {
            await using var provider = CreateProvider(instanceRoot);
            await using var scope = provider.CreateAsyncScope();
            var middleware = new JwtSessionMiddleware(
                _ => throw new InvalidOperationException("Protected request should stop before next middleware."),
                provider.GetRequiredService<IConfiguration>(),
                provider.GetRequiredService<ApiKeyProvider>());

            await using var body = new MemoryStream();
            var context = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider,
                Response =
                {
                    Body = body
                }
            };
            context.Request.Path = "/auth/me";

            await middleware.InvokeAsync(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

            body.Position = 0;
            var payload = Encoding.UTF8.GetString(body.ToArray());
            payload.Should().Contain("\"error\":\"resource_unavailable\"");
            payload.Should().Contain("could not resolve a local session user");
        }
        finally
        {
            DeleteDirectoryIfExists(instanceRoot);
        }
    }

    private static ServiceProvider CreateProvider(
        string instanceRoot,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        var databaseName = $"JwtSessionMiddlewareTests-{Guid.NewGuid():N}";
        var configurationValues = new Dictionary<string, string?>
        {
            ["Auth:DisableAccessTokenCheck"] = "true",
            ["Admin:Username"] = "admin",
            ["ChatCache:MaxMegabytes"] = "0"
        };

        if (configurationOverrides is not null)
        {
            foreach (var pair in configurationOverrides)
                configurationValues[pair.Key] = pair.Value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(new JwtOptions
        {
            Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        });
        services.AddSingleton<TokenService>();
        services.AddSingleton<ChatCache>();
        services.AddSingleton(new SharpClawInstancePaths(SharpClawInstanceKind.Backend, instanceRoot));
        services.AddSingleton<ApiKeyProvider>();
        services.AddScoped<SessionService>();
        services.AddScoped<AuthService>();
        services.AddDbContext<SharpClawDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task SeedUserAsync(
        ServiceProvider provider,
        Guid userId,
        string username,
        bool isAdmin)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        db.Users.Add(new UserDB
        {
            Id = userId,
            Username = username,
            PasswordHash = [1],
            PasswordSalt = [2],
            IsUserAdmin = isAdmin
        });

        await db.SaveChangesAsync();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SharpClaw.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
