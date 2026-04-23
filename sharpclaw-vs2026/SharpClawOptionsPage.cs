using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SharpClaw.VS2026Extension;

/// <summary>
/// Configurable options exposed under <c>Tools &gt; Options &gt; SharpClaw</c>.
/// Persisted automatically in the VS private registry by the <see cref="DialogPage"/> infrastructure.
/// </summary>
[Guid("c8e7f2b1-3a4d-4e6f-9b2c-1d5e8f0a3b7c")]
public sealed class SharpClawOptionsPage : DialogPage
{
    private const int DefaultPort = 48923;
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultAutoConnectDelaySeconds = 3;
    private const int DefaultConnectionTimeoutSeconds = 10;

    // ═══════════════════════════════════════════════════════════════
    // Connection
    // ═══════════════════════════════════════════════════════════════

    [Category("Connection")]
    [DisplayName("Host")]
    [Description("Hostname or IP address of the SharpClaw backend (default: 127.0.0.1).")]
    [DefaultValue(DefaultHost)]
    public string Host { get; set; } = DefaultHost;

    [Category("Connection")]
    [DisplayName("Port")]
    [Description("TCP port the SharpClaw backend listens on (default: 48923).")]
    [DefaultValue(DefaultPort)]
    public int Port { get; set; } = DefaultPort;

    [Category("Connection")]
    [DisplayName("API Key File Path")]
    [Description(
        "Explicit path to the backend runtime API key file. Leave empty to resolve " +
        "through backend discovery using Backend Instance Id first, then Host and Port. " +
        "Environment variables are expanded at connect time.")]
    [DefaultValue("")]
    public string ApiKeyFilePath { get; set; } = string.Empty;

    [Category("Connection")]
    [DisplayName("Backend Instance Id")]
    [Description("Optional backend instance id to attach to explicitly when resolving discovery metadata and runtime auth files.")]
    [DefaultValue("")]
    public string BackendInstanceId { get; set; } = string.Empty;

    // ═══════════════════════════════════════════════════════════════
    // Auto-connect
    // ═══════════════════════════════════════════════════════════════

    [Category("Auto-Connect")]
    [DisplayName("Auto-Connect on Startup")]
    [Description("Whether to automatically connect to the SharpClaw backend when VS starts.")]
    [DefaultValue(true)]
    public bool AutoConnect { get; set; } = true;

    [Category("Auto-Connect")]
    [DisplayName("Auto-Connect Delay (seconds)")]
    [Description(
        "Seconds to wait after VS startup before attempting auto-connect. " +
        "Gives VS time to finish loading (default: 3).")]
    [DefaultValue(DefaultAutoConnectDelaySeconds)]
    public int AutoConnectDelaySeconds { get; set; } = DefaultAutoConnectDelaySeconds;

    [Category("Auto-Connect")]
    [DisplayName("Connection Timeout (seconds)")]
    [Description(
        "Maximum seconds to wait for a connection attempt before giving up " +
        "(applies to both auto-connect and manual connect, default: 10).")]
    [DefaultValue(DefaultConnectionTimeoutSeconds)]
    public int ConnectionTimeoutSeconds { get; set; } = DefaultConnectionTimeoutSeconds;

    // ═══════════════════════════════════════════════════════════════
    // Resolved helpers (not displayed in the property grid)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the WebSocket URI from the current <see cref="Host"/> and <see cref="Port"/>.
    /// </summary>
    [Browsable(false)]
    public Uri BridgeUri => new($"ws://{Host}:{Port}/editor/ws");

    /// <summary>
    /// Returns the explicitly configured API key file path, if any.
    /// Environment variables in the path are expanded.
    /// </summary>
    [Browsable(false)]
    public string ResolvedApiKeyFilePath
    {
        get
        {
            return Environment.ExpandEnvironmentVariables(ApiKeyFilePath);
        }
    }
}
