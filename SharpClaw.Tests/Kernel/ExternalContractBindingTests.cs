using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Runtime.Host;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class ExternalContractBindingTests
{
    private const string ContractName = "test.authorization";

    [Test]
    public void ExternalExportWithoutServiceTypeIsRejected()
    {
        var services = new List<ServiceDescriptor>();
        var exports = Sources(new PackageContractReference(ContractName));

        var act = () => PackagedDotNetRegistrationSet.AddExternalContractExports(services, exports);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must declare a service type*");
        services.Should().BeEmpty();
    }

    [Test]
    public void ExternalExportWithChangedServiceTypeIsRejected()
    {
        var services = ServicesWithRequirement<ExpectedContract>();
        var exports = Sources(new PackageContractReference(ContractName, typeof(ChangedContract).FullName));

        var act = () => PackagedDotNetRegistrationSet.AddExternalContractExports(services, exports);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not match its local service type*");
        ExportBindings(services).Should().BeEmpty();
    }

    [Test]
    public void ExactExternalExportAddsTheRequiredBinding()
    {
        var services = ServicesWithRequirement<ExpectedContract>();
        var exports = Sources(new PackageContractReference(
            ContractName,
            typeof(ExpectedContract).FullName));

        PackagedDotNetRegistrationSet.AddExternalContractExports(services, exports);

        ExportBindings(services).Should().ContainSingle()
            .Which.Should().Be(new ServiceContractBinding(
                "external-provider",
                typeof(ExpectedContract),
                ContractName,
                1,
                65_536,
                IsExport: true,
                Optional: false));
    }

    [Test]
    public void DuplicateExternalProvidersAreRejected()
    {
        var services = ServicesWithRequirement<ExpectedContract>();
        var exports = new[]
        {
            Source("provider-a", typeof(ExpectedContract)),
            Source("provider-b", typeof(ExpectedContract)),
        };

        var act = () => PackagedDotNetRegistrationSet.AddExternalContractExports(services, exports);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*more than one external provider*");
        ExportBindings(services).Should().BeEmpty();
    }

    [Test]
    public void MixedLocalAndExternalProvidersAreRejected()
    {
        var services = ServicesWithRequirement<ExpectedContract>();
        services.Add(Binding(
            new ServiceContractBinding(
                "local-provider",
                typeof(ExpectedContract),
                ContractName,
                1,
                65_536,
                IsExport: true,
                Optional: false)));

        var act = () => PackagedDotNetRegistrationSet.AddExternalContractExports(
            services,
            [Source("external-provider", typeof(ExpectedContract))]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*both local and external providers*");
    }

    [Test]
    public void IncompatibleLocalRequirementsAreRejected()
    {
        var services = ServicesWithRequirement<ExpectedContract>();
        services.Add(Binding(new ServiceContractBinding(
            "consumer-b",
            typeof(ChangedContract),
            ContractName,
            1,
            65_536,
            IsExport: false,
            Optional: false)));

        var act = () => PackagedDotNetRegistrationSet.AddExternalContractExports(
            services,
            [Source("external-provider", typeof(ExpectedContract))]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*incompatible local requirements*");
        ExportBindings(services).Should().BeEmpty();
    }

    private static List<ServiceDescriptor> ServicesWithRequirement<TContract>() =>
    [
        Binding(new ServiceContractBinding(
            "consumer-a",
            typeof(TContract),
            ContractName,
            1,
            65_536,
            IsExport: false,
            Optional: false)),
    ];

    private static PackagedDotNetRegistrationSet.ExternalContractExportSource[] Sources(
        PackageContractReference export) =>
        [new("external-provider", [export])];

    private static PackagedDotNetRegistrationSet.ExternalContractExportSource Source(
        string sourceId,
        Type serviceType) =>
        new(sourceId, [new PackageContractReference(ContractName, serviceType.FullName)]);

    private static ServiceDescriptor Binding(ServiceContractBinding binding) =>
        ServiceDescriptor.Singleton(typeof(ServiceContractBinding), binding);

    private static IEnumerable<ServiceContractBinding> ExportBindings(
        IEnumerable<ServiceDescriptor> services) =>
        services
            .Where(value => value.ServiceType == typeof(ServiceContractBinding))
            .Select(value => value.ImplementationInstance)
            .OfType<ServiceContractBinding>()
            .Where(value => value.IsExport);

    private sealed record ExpectedContract;

    private sealed record ChangedContract;
}
