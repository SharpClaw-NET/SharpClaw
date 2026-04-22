using Microsoft.Extensions.Configuration;

namespace SharpClaw.Utils.Logging;

/// <summary>
/// Environment-backed Serilog options shared by the core API, gateway, and
/// Uno frontend.
/// </summary>
public sealed record SerilogEnvironmentOptions(
    bool Enabled,
    bool ConsoleEnabled,
    bool FileEnabled,
    bool RequestLoggingEnabled,
    string MinimumLevel,
    string MicrosoftMinimumLevel,
    string AspNetCoreMinimumLevel,
    string EntityFrameworkCoreMinimumLevel,
    string UnoMinimumLevel)
{
    /// <summary>
    /// The shared configuration section used by all apps.
    /// </summary>
    public const string SectionPath = "Logging:Serilog";

    /// <summary>
    /// Reads Serilog options from configuration using safe defaults.
    /// </summary>
    public static SerilogEnvironmentOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new SerilogEnvironmentOptions(
            Enabled: GetBool(configuration, $"{SectionPath}:Enabled", defaultValue: true),
            ConsoleEnabled: GetBool(configuration, $"{SectionPath}:ConsoleEnabled", defaultValue: true),
            FileEnabled: GetBool(configuration, $"{SectionPath}:FileEnabled", defaultValue: true),
            RequestLoggingEnabled: GetBool(configuration, $"{SectionPath}:RequestLoggingEnabled", defaultValue: true),
            MinimumLevel: GetString(configuration, $"{SectionPath}:MinimumLevel", "Information"),
            MicrosoftMinimumLevel: GetString(configuration, $"{SectionPath}:MicrosoftMinimumLevel", "Warning"),
            AspNetCoreMinimumLevel: GetString(configuration, $"{SectionPath}:AspNetCoreMinimumLevel", "Warning"),
            EntityFrameworkCoreMinimumLevel: GetString(configuration, $"{SectionPath}:EntityFrameworkCoreMinimumLevel", "Warning"),
            UnoMinimumLevel: GetString(configuration, $"{SectionPath}:UnoMinimumLevel", "Warning"));
    }

    /// <summary>
    /// Parses an enum value from configuration text, returning a fallback when
    /// the configured value is missing or invalid.
    /// </summary>
    public static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private static bool GetBool(IConfiguration configuration, string key, bool defaultValue)
    {
        var raw = configuration[key];
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static string GetString(IConfiguration configuration, string key, string defaultValue)
    {
        var raw = configuration[key];
        return string.IsNullOrWhiteSpace(raw) ? defaultValue : raw;
    }
}
