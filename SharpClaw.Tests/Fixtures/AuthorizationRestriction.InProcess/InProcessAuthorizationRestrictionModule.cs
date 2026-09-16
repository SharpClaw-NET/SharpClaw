using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using RestrictionDecision = SharpClaw.Contracts.Kernel.AuthorizationRestriction;

namespace SharpClaw.TestFixtures.AuthorizationRestriction.InProcess;

public sealed class InProcessAuthorizationRestrictionModule : ISharpClawModule
{
    public const string SourceId = "sharpclaw_test_authorization_restriction_in_process";
    public const string DenyRole = "test-authorization-restriction-in-process-deny";
    public const string DiagnosticsCommand = "test-authorization-restriction-state";

    private readonly AuthorizationRestrictionCapture _capture = new();

    public ModuleIdentity Identity { get; } = new(
        SourceId,
        "Tracked In-Process Authorization Restriction",
        "test_auth");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(_capture);
        services.AddAuthorizationRestriction<RoleAuthorizationRestriction>("tracked-in-process-role");
        services.AddCliCommand<AuthorizationRestrictionDiagnosticsHandler>(new CliCommandDescriptor(
            DiagnosticsCommand,
            [],
            "Reports authorization restriction lifecycle evidence.",
            new JsonSchemaReference("test.authorization.restriction.cli.input", 1, "none"),
            new JsonSchemaReference("test.authorization.restriction.cli.result", 1, "json")));
    }

    public ValueTask StartAsync(ServiceStartContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _capture.RecordStart();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _capture.RecordStop();
        return ValueTask.CompletedTask;
    }
}

public sealed class RoleAuthorizationRestriction : IAuthorizationRestriction, IDisposable
{
    private readonly AuthorizationRestrictionCapture _capture;
    private int _disposed;

    public RoleAuthorizationRestriction(AuthorizationRestrictionCapture capture)
    {
        _capture = capture;
        _capture.RecordConstruction();
    }

    public ValueTask<RestrictionDecision> EvaluateAsync(
        AuthorizationRestrictionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var denied = context.Caller.Roles?.Contains(
            InProcessAuthorizationRestrictionModule.DenyRole,
            StringComparer.Ordinal) == true;
        _capture.RecordEvaluation(context, denied);
        return ValueTask.FromResult(denied
            ? RestrictionDecision.Deny(
                "tracked_in_process_role_denied",
                "The tracked in-process restriction denies this caller.")
            : RestrictionDecision.Preserve());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _capture.RecordDisposal();
    }
}

public sealed class AuthorizationRestrictionDiagnosticsHandler(
    AuthorizationRestrictionCapture capture) : ICliHandler
{
    public ValueTask<CliResult> ExecuteAsync(CliInvocation invocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new CliResult(
            true,
            [new CliOutput("stdout", JsonSerializer.Serialize(capture.Snapshot()))]));
    }
}

public sealed class AuthorizationRestrictionCapture
{
    private readonly object _sync = new();
    private int _starts;
    private int _stops;
    private int _constructions;
    private int _evaluations;
    private int _denials;
    private int _disposals;
    private string? _lastSubjectId;
    private bool _lastAuthenticated;
    private Guid? _lastTraceId;
    private Guid? _lastIdempotencyKey;
    private string? _lastOperation;

    public void RecordStart() => Interlocked.Increment(ref _starts);

    public void RecordStop() => Interlocked.Increment(ref _stops);

    public void RecordConstruction() => Interlocked.Increment(ref _constructions);

    public void RecordEvaluation(AuthorizationRestrictionContext context, bool denied)
    {
        Interlocked.Increment(ref _evaluations);
        if (denied)
            Interlocked.Increment(ref _denials);
        lock (_sync)
        {
            _lastSubjectId = context.Caller.SubjectId;
            _lastAuthenticated = context.Caller.IsAuthenticated;
            _lastTraceId = context.TraceId;
            _lastIdempotencyKey = context.IdempotencyKey;
            _lastOperation = context.Request.Operation;
        }
    }

    public void RecordDisposal() => Interlocked.Increment(ref _disposals);

    public AuthorizationRestrictionSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new AuthorizationRestrictionSnapshot(
                Volatile.Read(ref _starts),
                Volatile.Read(ref _stops),
                Volatile.Read(ref _constructions),
                Volatile.Read(ref _evaluations),
                Volatile.Read(ref _denials),
                Volatile.Read(ref _disposals),
                Volatile.Read(ref _constructions) - Volatile.Read(ref _disposals),
                _lastSubjectId,
                _lastAuthenticated,
                _lastTraceId,
                _lastIdempotencyKey,
                _lastOperation);
        }
    }
}

public sealed record AuthorizationRestrictionSnapshot(
    int Starts,
    int Stops,
    int Constructions,
    int Evaluations,
    int Denials,
    int Disposals,
    int Active,
    string? LastSubjectId,
    bool LastAuthenticated,
    Guid? LastTraceId,
    Guid? LastIdempotencyKey,
    string? LastOperation);
