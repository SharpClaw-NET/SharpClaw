using Microsoft.Extensions.Configuration;

namespace SharpClaw.Runtime.Host;

internal static class PackagedRegistrationRootResolver
{
    public static IReadOnlyList<string> Resolve(
        string bundledRegistrationsRoot,
        IConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledRegistrationsRoot);
        ArgumentNullException.ThrowIfNull(configuration);

        var roots = new List<string>
        {
            Path.GetFullPath(bundledRegistrationsRoot),
        };
        var knownRoots = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);

        foreach (var registration in configuration
                     .GetSection("ExternalRegistrations")
                     .GetChildren())
        {
            var enabled = ReadEnabled(registration);
            if (!enabled)
                continue;

            var configuredPath = registration["Path"];
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                throw new InvalidOperationException(
                    $"The external registration '{registration.Path}' must declare Path.");
            }

            if (!Path.IsPathFullyQualified(configuredPath))
            {
                throw new InvalidOperationException(
                    $"The external registration '{registration.Path}' Path must be absolute.");
            }

            var root = Path.GetFullPath(configuredPath);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException(
                    $"The enabled external registration root '{root}' does not exist.");
            }

            if (knownRoots.Add(root))
                roots.Add(root);
        }

        return roots.AsReadOnly();
    }

    private static bool ReadEnabled(IConfigurationSection registration)
    {
        var configured = registration["Enabled"];
        if (string.IsNullOrWhiteSpace(configured))
            return true;
        if (bool.TryParse(configured, out var enabled))
            return enabled;

        throw new InvalidOperationException(
            $"The external registration setting '{registration.Path}:Enabled' must be true or false.");
    }
}
