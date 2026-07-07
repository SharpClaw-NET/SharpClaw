using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Modules.Foreign;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Security;
using SharpClaw.TestFixtures.ExternalModule;

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
        using var services = CreateServices(out _);
        var moduleDir = CreateInProcessFixtureDirectory();
        var assemblyPath = typeof(InProcessPerformanceFixtureModule).Assembly.Location;
        var manifest = new ModuleManifest(
            InProcessPerformanceFixtureModule.ModuleId,
            "Synthetic In-Process Performance",
            "1.0.0",
            InProcessPerformanceFixtureModule.ToolPrefixValue,
            Path.GetFileName(assemblyPath),
            "0.0.0");
        var runtimeInfo = new ModuleManifestRuntimeInfo(
            "dotnet",
            Path.GetFileName(assemblyPath),
            typeof(InProcessPerformanceFixtureModule).FullName,
            "in-process");

        await using var host = InProcessModuleHost.Load(
            moduleDir,
            moduleDir,
            manifest,
            runtimeInfo,
            services);
        using var scope = host.CreateScope();

        scope.ServiceProvider
            .GetRequiredService<IInProcessModuleSecretReader>()
            .Should()
            .BeSameAs(services.GetRequiredService<IInProcessModuleSecretReader>());
    }

    [Test]
    public async Task InProcessModuleScope_CanResolveModelRegistrar()
    {
        using var services = CreateServices(out _);
        var moduleDir = CreateInProcessFixtureDirectory();
        var assemblyPath = typeof(InProcessPerformanceFixtureModule).Assembly.Location;
        var manifest = new ModuleManifest(
            InProcessPerformanceFixtureModule.ModuleId,
            "Synthetic In-Process Performance",
            "1.0.0",
            InProcessPerformanceFixtureModule.ToolPrefixValue,
            Path.GetFileName(assemblyPath),
            "0.0.0");
        var runtimeInfo = new ModuleManifestRuntimeInfo(
            "dotnet",
            Path.GetFileName(assemblyPath),
            typeof(InProcessPerformanceFixtureModule).FullName,
            "in-process");

        await using var host = InProcessModuleHost.Load(
            moduleDir,
            moduleDir,
            manifest,
            runtimeInfo,
            services);
        using var scope = host.CreateScope();

        var registrar = scope.ServiceProvider.GetRequiredService<IModelRegistrar>();
        var providerId = await registrar.EnsureProviderAsync(
            "module-owned-provider",
            "Module Owned Provider");

        await using var dbScope = services.CreateAsyncScope();
        var db = dbScope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var provider = await db.Providers.SingleAsync(provider =>
            provider.Id == providerId
            && provider.ProviderKey == "module-owned-provider");

        provider.Name.Should().Be("Module Owned Provider");
    }

    [Test]
    public void ModelInfoProviderContract_RemainsNonSecret()
    {
        var disallowedProperties = typeof(ModelProviderInfo)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property =>
                property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Decrypted", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Name)
            .ToArray();

        disallowedProperties.Should().BeEmpty();
        typeof(IModelInfoProvider)
            .GetMethods()
            .Select(method => method.Name)
            .Should()
            .NotContain(name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void ForeignModuleProtocol_DoesNotExposeSecretReadRoute()
    {
        var protocolConstants = typeof(ForeignModuleHostCapabilityProtocol)
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
        assemblyName.GetPublicKeyToken().Should().BeNullOrEmpty();
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
        services.AddScoped<IModelRegistrar, HostModelRegistrar>();

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

    private static string CreateInProcessFixtureDirectory()
    {
        var assemblyPath = typeof(InProcessPerformanceFixtureModule).Assembly.Location;
        var sourceDir = Path.GetDirectoryName(assemblyPath)!;
        var moduleDir = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "inprocess-secret-reader-modules",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(moduleDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*.dll"))
            File.Copy(file, Path.Combine(moduleDir, Path.GetFileName(file)), overwrite: true);

        foreach (var file in Directory.GetFiles(sourceDir, "*.deps.json"))
            File.Copy(file, Path.Combine(moduleDir, Path.GetFileName(file)), overwrite: true);

        return moduleDir;
    }
}
