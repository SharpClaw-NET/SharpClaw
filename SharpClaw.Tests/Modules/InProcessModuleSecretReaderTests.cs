using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Modules.TestHarness;
using SharpClaw.Utils.Security;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class InProcessModuleSecretReaderTests
{
    [Test]
    public async Task GetProviderApiKeyAsync_ReturnsDecryptedProviderKey()
    {
        await using var services = CreateServices(out var encryptionOptions);
        var provider = await SeedProviderAsync(
            services,
            encryptionOptions,
            providerKey: "elevenlabs",
            apiKey: "xi-secret");

        var reader = services.GetRequiredService<IInProcessModuleSecretReader>();

        var apiKey = await reader.GetProviderApiKeyAsync(provider.ProviderKey);

        apiKey.Should().Be("xi-secret");
    }

    [Test]
    public async Task GetModelProviderApiKeyAsync_ReturnsDecryptedProviderKey()
    {
        await using var services = CreateServices(out var encryptionOptions);
        var provider = await SeedProviderAsync(
            services,
            encryptionOptions,
            providerKey: "elevenlabs",
            apiKey: "xi-model-secret");
        var model = await SeedModelAsync(services, provider);

        var reader = services.GetRequiredService<IInProcessModuleSecretReader>();

        var apiKey = await reader.GetModelProviderApiKeyAsync(model.Id);

        apiKey.Should().Be("xi-model-secret");
    }

    [Test]
    public async Task SecretReader_ReturnsNullForMissingOrUnsetSecrets()
    {
        await using var services = CreateServices(out var encryptionOptions);
        var provider = await SeedProviderAsync(
            services,
            encryptionOptions,
            providerKey: "unset",
            apiKey: null);
        var model = await SeedModelAsync(services, provider);
        var reader = services.GetRequiredService<IInProcessModuleSecretReader>();

        (await reader.GetProviderApiKeyAsync("missing")).Should().BeNull();
        (await reader.GetProviderApiKeyAsync(provider.ProviderKey)).Should().BeNull();
        (await reader.GetModelProviderApiKeyAsync(Guid.NewGuid())).Should().BeNull();
        (await reader.GetModelProviderApiKeyAsync(model.Id)).Should().BeNull();
    }

    [Test]
    public async Task GetProviderApiKeyAsync_RejectsBlankProviderKey()
    {
        await using var services = CreateServices(out _);
        var reader = services.GetRequiredService<IInProcessModuleSecretReader>();

        var act = async () => await reader.GetProviderApiKeyAsync(" ");

        await act.Should()
            .ThrowAsync<ArgumentException>()
            .WithParameterName("providerKey");
    }

    [Test]
    public async Task InProcessModuleScope_CanResolveSecretReader()
    {
        await using var services = CreateServices(out _);
        var moduleDir = Path.GetDirectoryName(typeof(TestHarnessModule).Assembly.Location)!;
        var manifest = new ModuleManifest(
            TestHarnessConstants.ModuleId,
            "Test Harness",
            "1.0.0",
            TestHarnessConstants.ToolPrefix,
            Path.GetFileName(typeof(TestHarnessModule).Assembly.Location),
            "0.0.0");

        await using var host = ExternalModuleHost.Load(
            moduleDir,
            manifest,
            services,
            services.GetRequiredService<ILoggerFactory>());
        using var scope = host.CreateScope();

        scope.ServiceProvider
            .GetRequiredService<IInProcessModuleSecretReader>()
            .Should()
            .BeSameAs(services.GetRequiredService<IInProcessModuleSecretReader>());
    }

    [Test]
    public void ModelInfoProviderContract_RemainsNonSecret()
    {
        var infoProperties = typeof(ModelProviderInfo)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        infoProperties.Should().BeEquivalentTo(
            "ModelName",
            "ProviderKey",
            "RequiresApiKey",
            "HasApiKey");
        infoProperties.Should().NotContain(property =>
            property.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || property.Contains("Decrypted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(property, "ApiKey", StringComparison.OrdinalIgnoreCase));
        typeof(IModelInfoProvider)
            .GetMethods()
            .Select(method => method.Name)
            .Should()
            .NotContain(name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void EmbeddedContracts_ExposePublishedProviderCompatibilityMembers()
    {
        var providerClientOptionsConstructors = typeof(ProviderClientOptions)
            .GetConstructors()
            .Where(constructor =>
            {
                var parameters = constructor.GetParameters();
                return parameters.Length == 1
                    && parameters[0].ParameterType == typeof(string);
            })
            .ToArray();

        providerClientOptionsConstructors
            .Should()
            .ContainSingle();

        typeof(IProviderPlugin)
            .GetMethod(nameof(IProviderPlugin.CreateClient), [typeof(ProviderClientOptions)])
            .Should()
            .NotBeNull();
        typeof(IProviderPlugin)
            .GetMethod(nameof(IProviderPlugin.CreateCostFeed), [typeof(ProviderClientOptions)])
            .Should()
            .NotBeNull();
        typeof(IProviderPlugin).GetProperty(nameof(IProviderPlugin.SupportsCostFeed)).Should().NotBeNull();
        typeof(IProviderPlugin).GetProperty(nameof(IProviderPlugin.CostFeedPermissionDeniedNote)).Should().NotBeNull();

        typeof(IProviderApiClient)
            .GetMethod(nameof(IProviderApiClient.ListModelIdsAsync), [typeof(CancellationToken)])
            .Should()
            .NotBeNull();
        var stringFirstChatCompletionMethods = typeof(IProviderApiClient)
            .GetMethods()
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == nameof(IProviderApiClient.ChatCompletionAsync)
                    && parameters.Length > 0
                    && parameters[0].ParameterType == typeof(string);
            })
            .ToArray();

        stringFirstChatCompletionMethods.Should().NotBeEmpty();
    }

    [Test]
    public void ForeignModuleProtocol_DoesNotExposeSecretReadRoute()
    {
        var protocolConstants = typeof(ForeignModuleProtocol)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string)field.GetValue(null)!)
            .ToArray();

        protocolConstants.Should().NotContain(value =>
            value.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || value.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api-key", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void ContractsAssemblyIdentity_MatchesPublishedSecretReaderPackageIdentity()
    {
        var assemblyName = typeof(IInProcessModuleSecretReader).Assembly.GetName();

        assemblyName.Name.Should().Be("SharpClaw.Contracts");
        assemblyName.Version.Should().Be(new Version(0, 3, 17, 0));
        assemblyName.GetPublicKeyToken().Should().BeEmpty();
    }

    private static ServiceProvider CreateServices(out EncryptionOptions encryptionOptions)
    {
        encryptionOptions = new EncryptionOptions
        {
            Key = ApiKeyEncryptor.GenerateKey(),
            EncryptProviderKeys = true,
        };

        var databaseName = "InProcessSecretReader_" + Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(encryptionOptions);
        services.AddDbContext<SharpClawDbContext>(options =>
            options.UseInMemoryDatabase(databaseName, databaseRoot));
        services.AddSingleton<IInProcessModuleSecretReader, HostInProcessModuleSecretReader>();

        return services.BuildServiceProvider();
    }

    private static async Task<ProviderDB> SeedProviderAsync(
        IServiceProvider services,
        EncryptionOptions encryptionOptions,
        string providerKey,
        string? apiKey)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var provider = new ProviderDB
        {
            Id = Guid.NewGuid(),
            Name = providerKey,
            ProviderKey = providerKey,
            EncryptedApiKey = apiKey is null
                ? null
                : ApiKeyEncryptor.Encrypt(apiKey, encryptionOptions.Key),
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    private static async Task<ModelDB> SeedModelAsync(
        IServiceProvider services,
        ProviderDB provider)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var attachedProvider = await db.Providers.FirstAsync(
            item => item.Id == provider.Id);
        var model = new ModelDB
        {
            Id = Guid.NewGuid(),
            Name = "elevenlabs-stt",
            ProviderId = attachedProvider.Id,
            Provider = attachedProvider,
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return model;
    }
}
