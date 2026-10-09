using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Services;


internal sealed record ModulePackageSource(
    string Name, string? LocalPath = null, Uri? Download = null,
    string? PackageId = null, string? Version = null, string? GitHubUsername = null);
