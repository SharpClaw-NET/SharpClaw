using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Persistence;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.INF;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public sealed class ExternalPersistencePackageTests
{
    private const string FixturePackageId = "SharpClaw.TestFixtures.ExternalPersistence";
    private const string FixturePackageVersion = "1.0.0-test";
    private const string FixtureSourceId = "synthetic_external_persistence";
    private const string FixtureProviderKey = "ExternalInMemory";
    private readonly List<string> _temporaryDirectories = [];

    [TearDown]
    public async Task TearDown()
    {
        try
        {
            foreach (var directory in _temporaryDirectories)
                await DeleteModuleDirectoryAsync(directory);
        }
        finally
        {
            _temporaryDirectories.Clear();
        }
    }

    [Test]
    public async Task ProductionLoader_UsesIndependentlyPackagedExternalPersistenceProvider()
    {
        var packagePath = Path.Combine(
            FindSourceRoot(),
            "SharpClaw.Tests",
            "Fixtures",
            "ExternalModule",
            "obj",
            "persistence-package",
            $"{FixturePackageId}.{FixturePackageVersion}.nupkg");
        File.Exists(packagePath).Should().BeTrue(
            "the external provider fixture must be produced as an independent NuGet package");

        var root = CreateTemporaryDirectory();
        var bundledRoot = Path.Combine(root, "bundled");
        var externalRoot = Path.Combine(root, "external");
        Directory.CreateDirectory(bundledRoot);
        ExtractContribution(packagePath, externalRoot);

        var configuration = Configuration(
            ("Database:Provider", FixtureProviderKey),
            ("ExternalRegistrations:0:Path", externalRoot),
            ("ExternalRegistrations:0:Enabled", "true"));
        var roots = PackagedRegistrationRootResolver.Resolve(bundledRoot, configuration);
        roots.Should().Equal(Path.GetFullPath(bundledRoot), Path.GetFullPath(externalRoot));

        await VerifyPackagedProviderAsync(roots, configuration);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task VerifyPackagedProviderAsync(
        IReadOnlyList<string> roots,
        IConfiguration configuration)
    {
        await using var registrations = await PackagedDotNetRegistrationSet.LoadProductionAsync(
            roots,
            configuration);
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        foreach (var descriptor in registrations.Services)
            services.Add(descriptor);
        services.AddInfrastructure(
            configuration,
            SharpClawPersistenceOptions.FromConfiguration(configuration));
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var selection = serviceProvider.GetRequiredService<PersistenceProviderSelection>();
        var dbContext = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        registrations.SourceIds.Should().ContainSingle().Which.Should().Be(FixtureSourceId);
        selection.Provider.Key.Should().Be(FixtureProviderKey);
        dbContext.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.InMemory");
        AssemblyLoadContext.GetLoadContext(selection.Provider.GetType().Assembly)
            .Should().NotBeSameAs(AssemblyLoadContext.Default);
    }

    [Test]
    public void RootResolver_IgnoresDisabledExternalRegistrationWithoutASecondLoader()
    {
        var bundledRoot = CreateTemporaryDirectory();
        var configuration = Configuration(
            ("ExternalRegistrations:0:Path", Path.Combine(bundledRoot, "missing")),
            ("ExternalRegistrations:0:Enabled", "false"));

        PackagedRegistrationRootResolver.Resolve(bundledRoot, configuration)
            .Should().Equal(Path.GetFullPath(bundledRoot));
    }

    [Test]
    public void RootResolver_RejectsEnabledRelativeOrMissingRoots()
    {
        var bundledRoot = CreateTemporaryDirectory();

        var relative = () => PackagedRegistrationRootResolver.Resolve(
            bundledRoot,
            Configuration(("ExternalRegistrations:0:Path", "relative/module")));
        relative.Should().Throw<InvalidOperationException>().WithMessage("*must be absolute*");

        var missing = Path.Combine(bundledRoot, "missing");
        var absent = () => PackagedRegistrationRootResolver.Resolve(
            bundledRoot,
            Configuration(("ExternalRegistrations:0:Path", missing)));
        absent.Should().Throw<DirectoryNotFoundException>().WithMessage("*does not exist*");
    }

    [Test]
    public void RootResolver_RejectsIncompleteOrInvalidEnabledRegistrations()
    {
        var bundledRoot = CreateTemporaryDirectory();

        var missingPath = () => PackagedRegistrationRootResolver.Resolve(
            bundledRoot,
            Configuration(("ExternalRegistrations:0:Enabled", "true")));
        missingPath.Should().Throw<InvalidOperationException>().WithMessage("*must declare Path*");

        var invalidEnabled = () => PackagedRegistrationRootResolver.Resolve(
            bundledRoot,
            Configuration(
                ("ExternalRegistrations:0:Path", bundledRoot),
                ("ExternalRegistrations:0:Enabled", "sometimes")));
        invalidEnabled.Should().Throw<InvalidOperationException>().WithMessage("*must be true or false*");
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private static void ExtractContribution(string packagePath, string destinationRoot)
    {
        const string prefix = "contentFiles/any/net10.0/contributions/";
        Directory.CreateDirectory(destinationRoot);
        using var archive = ZipFile.OpenRead(packagePath);
        var entries = archive.Entries
            .Where(entry =>
                !string.IsNullOrEmpty(entry.Name) &&
                entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        entries.Should().NotBeEmpty();

        var containedRoot = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in entries)
        {
            var relative = entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            destination.Should().StartWith(containedRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.Tests",
            nameof(ExternalPersistencePackageTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return directory;
    }

    private static async Task DeleteModuleDirectoryAsync(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                await Task.Delay(50);
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(50);
            }
        }
    }

    private static string FindSourceRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) &&
            File.Exists(Path.Combine(configured, "SharpClaw.slnx")))
        {
            return Path.GetFullPath(configured);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpClaw.slnx")))
                return directory.FullName;
        }

        throw new AssertionException("The SharpClaw source root could not be located.");
    }
}
