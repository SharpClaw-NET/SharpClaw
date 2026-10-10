using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Runtime.Host;

internal static class RuntimeCliSession
{
    public static async ValueTask<int> RunAsync(
        IReadOnlyList<string> rawArguments,
        RuntimeKernelAdapter runtimeKernel,
        DirectChatKernel kernel,
        PackagedApplicationRegistry applications,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rawArguments);
        ArgumentNullException.ThrowIfNull(runtimeKernel);
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var context = RuntimeKernelAdapter.CreateCliExecutionContext(RequestPrincipal.Anonymous);
        return await RunWithFailureHandlingAsync(
            () => RunCommandAsync(rawArguments, runtimeKernel, kernel, applications,
                context, output, error, cancellationToken),
            runtimeKernel, context, error, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> RunWithFailureHandlingAsync(
        Func<ValueTask<int>> operation,
        RuntimeKernelAdapter runtimeKernel,
        KernelActionExecutionContext context,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (KernelActionCancelledException)
        {
            await RunCancellationAsync(runtimeKernel, context, error).ConfigureAwait(false);
            return 130;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RunCancellationAsync(runtimeKernel, context, error).ConfigureAwait(false);
            return 130;
        }
#pragma warning disable CA1031 // The CLI process boundary translates failures from arbitrary registration code into its contracted exit code and failure action.
        catch (Exception exception)
        {
            return await RunFailureAsync(runtimeKernel, context, error, exception).ConfigureAwait(false);
        }
#pragma warning restore CA1031
    }

    private static async ValueTask<int> RunCommandAsync(
        IReadOnlyList<string> rawArguments,
        RuntimeKernelAdapter runtimeKernel,
        DirectChatKernel kernel,
        PackagedApplicationRegistry applications,
        KernelActionExecutionContext context,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var command = await ParseAndSelectAsync(
            rawArguments, runtimeKernel, context, cancellationToken).ConfigureAwait(false);
        var result = await ExecuteCommandAsync(
            command, runtimeKernel, kernel, applications, context, cancellationToken).ConfigureAwait(false);
        await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.OutputWrite,
            new RuntimeCliActionInvocation("output-write", command.Name, command.Arguments.Count),
            _ => WriteOutputAsync(result, output, error),
            cancellationToken).ConfigureAwait(false);
        return await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.Complete,
            new RuntimeCliActionInvocation("complete", command.Name, command.Arguments.Count),
            _ => ValueTask.FromResult(result.ExitCode),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RuntimeCliCommand> ParseAndSelectAsync(
        IReadOnlyList<string> rawArguments,
        RuntimeKernelAdapter runtimeKernel,
        KernelActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var command = await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.Parse,
            new RuntimeCliActionInvocation("parse", null, rawArguments.Count),
            _ => ValueTask.FromResult(RuntimeCliCommandLine.Parse(rawArguments)),
            cancellationToken).ConfigureAwait(false);
        return await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.CommandSelect,
            new RuntimeCliActionInvocation("command-select", command.Name, command.Arguments.Count),
            _ => ValueTask.FromResult(command),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RuntimeCliResult> ExecuteCommandAsync(
        RuntimeCliCommand command,
        RuntimeKernelAdapter runtimeKernel,
        DirectChatKernel kernel,
        PackagedApplicationRegistry applications,
        KernelActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var result = await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.Execute,
            new RuntimeCliActionInvocation("execute", command.Name, command.Arguments.Count),
            cancellation => ExecuteAsync(command, runtimeKernel, kernel, applications, context, cancellation),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await runtimeKernel.RunCliActionAsync(
                context,
                RuntimeCliActionCatalog.Fail,
                new RuntimeCliActionInvocation("fail", command.Name, command.Arguments.Count),
                _ => ValueTask.FromResult(true),
                CancellationToken.None).ConfigureAwait(false);
        }
        return result;
    }

    private static async ValueTask<RuntimeCliResult> ExecuteAsync(
        RuntimeCliCommand command,
        RuntimeKernelAdapter runtimeKernel,
        DirectChatKernel kernel,
        PackagedApplicationRegistry applications,
        KernelActionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(command.Name, "help", StringComparison.Ordinal)
            || string.Equals(command.Name, "--help", StringComparison.Ordinal)
            || string.Equals(command.Name, "-h", StringComparison.Ordinal))
        {
            return RuntimeCliResult.Success(
                "SharpClaw Runtime CLI\n  --cli help\n  --cli chat <message>\n");
        }
        if (string.Equals(command.Name, "chat", StringComparison.Ordinal))
            return await ExecuteChatAsync(command, kernel, cancellationToken).ConfigureAwait(false);

        var registrationResult = await applications.TryInvokeCliAsync(
            command.Name,
            command.Arguments,
            runtimeKernel,
            executionContext,
            cancellationToken).ConfigureAwait(false);
        return registrationResult is null
            ? RuntimeCliResult.Failure(
                $"Unknown Runtime CLI command '{command.Name}'. Use '--cli help'.")
            : FromRegistrationResult(registrationResult);
    }

    private static RuntimeCliResult FromRegistrationResult(CliResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var output = string.Concat(result.Output
            .Where(item => !string.Equals(item.Stream, "stderr", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Text));
        var errors = result.Output
            .Where(item => string.Equals(item.Stream, "stderr", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Text)
            .ToList();
        if (result.Error is not null)
            errors.Add(result.Error.Message);
        return new RuntimeCliResult(
            result.Succeeded,
            output,
            errors.Count == 0 ? null : string.Concat(errors),
            result.Succeeded ? 0 : 1);
    }

    private static async ValueTask<RuntimeCliResult> ExecuteChatAsync(
        RuntimeCliCommand command,
        DirectChatKernel kernel,
        CancellationToken cancellationToken)
    {
        if (command.Arguments.Count == 0)
            return RuntimeCliResult.Failure("The chat command requires a message.");

        var result = await kernel.RunAsync(
            new ChatTurnInput(string.Join(' ', command.Arguments)),
            cancellationToken).ConfigureAwait(false);
        return RuntimeCliResult.Success(
            result.Completion.Content ?? string.Empty);
    }

    private static async ValueTask<bool> WriteOutputAsync(
        RuntimeCliResult result,
        TextWriter output,
        TextWriter error)
    {
        if (result.Output.Length > 0)
            await output.WriteAsync(result.Output).ConfigureAwait(false);
        if (result.Error is not null)
            await error.WriteLineAsync(result.Error).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask<int> RunFailureAsync(
        RuntimeKernelAdapter runtimeKernel,
        KernelActionExecutionContext context,
        TextWriter error,
        Exception exception)
    {
        await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.Fail,
            new RuntimeCliActionInvocation("fail", null, 0, exception.GetType().Name),
            _ => ValueTask.FromResult(true),
            CancellationToken.None).ConfigureAwait(false);
        await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.OutputWrite,
            new RuntimeCliActionInvocation("output-write", null, 0),
            _ => WriteMessageAsync(error, "The Runtime CLI command failed."),
            CancellationToken.None).ConfigureAwait(false);
        return 1;
    }

    private static async ValueTask RunCancellationAsync(
        RuntimeKernelAdapter runtimeKernel,
        KernelActionExecutionContext context,
        TextWriter error)
    {
        await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.Cancel,
            new RuntimeCliActionInvocation("cancel", null, 0),
            _ => ValueTask.FromResult(true),
            CancellationToken.None).ConfigureAwait(false);
        await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.OutputWrite,
            new RuntimeCliActionInvocation("output-write", null, 0),
            _ => WriteMessageAsync(error, "The Runtime CLI command was cancelled."),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async ValueTask<bool> WriteMessageAsync(TextWriter writer, string message)
    {
        await writer.WriteLineAsync(message).ConfigureAwait(false);
        return true;
    }

    private sealed record RuntimeCliResult(
        bool Succeeded,
        string Output,
        string? Error,
        int ExitCode)
    {
        public static RuntimeCliResult Success(string output) =>
            new(true, output, null, 0);

        public static RuntimeCliResult Failure(string error) =>
            new(false, string.Empty, error, 1);
    }
}
