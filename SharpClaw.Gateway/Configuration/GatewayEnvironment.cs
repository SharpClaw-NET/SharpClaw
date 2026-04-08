using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Loads gateway environment configuration from <c>Environment/.env</c>
/// (always) and <c>Environment/.dev.env</c> (development only) relative
/// to the assembly location.  Creates a default <c>.env</c> if one does
/// not exist.
/// <para>
/// This follows the same pattern used by the Core and Interface
/// environments — JSON-with-comments loaded via
/// <see cref="PhysicalFileProvider"/>.
/// </para>
/// </summary>
public static class GatewayEnvironment
{
    private const string DefaultEnvContent =
        """
        {
          // SharpClaw Gateway Environment Configuration
          // Values here are loaded for all environments.
          //
          // DEFAULT POSTURE: only bot integrations are enabled.
          // All public-facing REST/streaming endpoints are disabled.
          // To expose the full REST API, set the individual endpoint
          // toggles to "true" (or flip the master "Enabled" switch).

          // ── Internal API ───────────────────────────────────────────
          // Base URL + timeout for the internal SharpClaw Application API.
          // TimeoutSeconds should be generous — agent tool-call chains
          // (wait, screenshot, click, type, inference) can take minutes.
          "InternalApi": {
            "BaseUrl": "http://127.0.0.1:48923",
            "TimeoutSeconds": "300"
          },

          // ── Request Queue ──────────────────────────────────────────
          // Buffers mutation requests and forwards them to the core API
          // sequentially (or with bounded concurrency).
          "Gateway": {
            "RequestQueue": {
              "Enabled": "true",
              "MaxConcurrency": "1",
              "TimeoutSeconds": "30",
              "MaxRetries": "2",
              "RetryDelayMs": "500",
              "MaxQueueSize": "500"
            },

            // ── Endpoint Toggles ───────────────────────────────────────
            // Master kill-switch and per-group enable/disable.
            // By default everything except "Bots" is disabled so the
            // gateway acts purely as a bot relay. Enable individual
            // groups as needed to expose the public REST surface.
            "Endpoints": {
              "Enabled": "true",
              "Auth": "false",
              "Agents": "false",
              "Channels": "false",
              "ChannelContexts": "false",
              "Chat": "false",
              "ChatStream": "false",
              "Threads": "false",
              "ThreadChat": "false",
              "Jobs": "false",
              "Models": "false",
              "Providers": "false",
              "Roles": "false",
              "Users": "false",
              "InputAudios": "false",
              "Transcription": "false",
              "TranscriptionStreaming": "false",
              "Cost": "false",
              "Bots": "true"
            },

            // ── Bot Integrations ───────────────────────────────────────
            // Bots are enabled by default — configure tokens via the
            // Uno settings page or directly in this file.
            "Bots": {
              "Telegram": {
                "Enabled": "true",
                "BotToken": ""
              },
              "Discord": {
                "Enabled": "false",
                "BotToken": ""
              },
              "WhatsApp": {
                  "Enabled": "false",
                  "PhoneNumberId": "",
                  "VerifyToken": ""
                },
                "Slack": {
                  "Enabled": "false",
                  "SigningSecret": ""
                },
                "Matrix": {
                  "Enabled": "false",
                  "HomeserverUrl": ""
                },
                "Signal": {
                  "Enabled": "false",
                  "ApiUrl": "",
                  "PhoneNumber": ""
                },
                "Email": {
                  "Enabled": "false",
                  "ImapHost": "",
                  "ImapPort": "993",
                  "SmtpHost": "",
                  "SmtpPort": "587",
                  "Username": "",
                  "PollIntervalSeconds": "30"
                },
                "Teams": {
                  "Enabled": "false",
                  "AppId": ""
                }
              }
          }
        }
        """;

    public static IConfigurationBuilder AddGatewayEnvironment(
        this IConfigurationBuilder builder, bool isDevelopment = false)
    {
        var envDir = Path.Combine(
            Path.GetDirectoryName(typeof(GatewayEnvironment).Assembly.Location)!,
            "Environment");

        EnsureEnvironmentFile(envDir);

        if (!Directory.Exists(envDir))
            return builder;

        // PhysicalFileProvider defaults to ExclusionFilters.Sensitive which
        // excludes dot-prefixed files (.env, .dev.env). Use None so the
        // configuration system can see them.
        var fileProvider = new PhysicalFileProvider(envDir, ExclusionFilters.None);

        builder.AddJsonFile(fileProvider, ".env", optional: true, reloadOnChange: false);

        if (isDevelopment)
            builder.AddJsonFile(fileProvider, ".dev.env", optional: true, reloadOnChange: false);

        return builder;
    }

    private static void EnsureEnvironmentFile(string envDir)
    {
        var envFile = Path.Combine(envDir, ".env");
        if (File.Exists(envFile) && new FileInfo(envFile).Length > 0)
            return;

        try
        {
            Directory.CreateDirectory(envDir);
            File.WriteAllText(envFile, DefaultEnvContent);
        }
        catch
        {
            // Best-effort — read-only or restricted file system.
        }
    }
}
