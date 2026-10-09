using Microsoft.AspNetCore.Http;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.Host.Routing;

namespace SharpClaw.Runtime.Host.Handlers;


internal sealed record KernelJobSubmissionRequest(
    string ActionKey,
    JobPayloadEnvelope Input,
    Guid? ConversationId = null,
    IReadOnlyList<ToolHoldRequirement>? Holds = null);
