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

        var context = runtimeKernel.CreateCliExecutionContext(RequestPrincipal.Anonymous);
        RuntimeCliCommand command;
        try
        {
            command = await runtimeKernel.RunCliActionAsync(
                context,
                RuntimeCliActionCatalog.Parse,
                new RuntimeCliActionInvocation("parse", null, rawArguments.Count),
                _ => ValueTask.FromResult(RuntimeCliCommandLine.Parse(rawArguments)),
                cancellationToken);
            command = await runtimeKernel.RunCliActionAsync(
                context,
                RuntimeCliActionCatalog.CommandSelect,
                new RuntimeCliActionInvocation("command-select", command.Name, command.Arguments.Count),
                _ => ValueTask.FromResult(command),
                cancellationToken);
        }
        catch (KernelActionCancelledException)
        {
            await RunCancellationAsync(runtimeKernel, context, error);
            return 130;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RunCancellationAsync(runtimeKernel, context, error);
            return 130;
        }
        catch (Exception exception)
        {
            return await RunFailureAsync(
                runtimeKernel,
                context,
                error,
                exception);
        }

        RuntimeCliResult result;
        try
        {
            result = await runtimeKernel.RunCliActionAsync(
                context,
                RuntimeCliActionCatalog.Execute,
                new RuntimeCliActionInvocation("execute", command.Name, command.Arguments.Count),
                cancellation => ExecuteAsync(
                    command,
                    runtimeKernel,
                    kernel,
                    applications,
                    context,
                    cancellation),
                cancellationToken);

            if (!result.Succeeded)
            {
                await runtimeKernel.RunCliActionAsync(
                    context,
                    RuntimeCliActionCatalog.Fail,
                    new RuntimeCliActionInvocation("fail", command.Name, command.Arguments.Count),
                    _ =>
                    {
                        return ValueTask.FromResult(true);
                    },
                    CancellationToken.None);
            }
        }
        catch (KernelActionCancelledException)
        {
            await RunCancellationAsync(runtimeKernel, context, error);
            return 130;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RunCancellationAsync(runtimeKernel, context, error);
            return 130;
        }
        catch (Exception exception)
        {
            return await RunFailureAsync(
                runtimeKernel,
                context,
                error,
                exception);
        }

        try
        {
            await runtimeKernel.RunCliActionAsync(
                context,
                RuntimeCliActionCatalog.OutputWrite,
                new RuntimeCliActionInvocation("output-write", command.Name, command.Arguments.Count),
                _ => WriteOutputAsync(result, output, error),
                cancellationToken);

            return await runtimeKernel.RunCliActionAsync(
                context,
                RuntimeCliActionCatalog.Complete,
                new RuntimeCliActionInvocation("complete", command.Name, command.Arguments.Count),
                _ => ValueTask.FromResult(result.ExitCode),
                cancellationToken);
        }
        catch (KernelActionCancelledException)
        {
            await RunCancellationAsync(runtimeKernel, context, error);
            return 130;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RunCancellationAsync(runtimeKernel, context, error);
            return 130;
        }
        catch (Exception exception)
        {
            return await RunFailureAsync(
                runtimeKernel,
                context,
                error,
                exception);
        }
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
        if (command.Name is "help" or "--help" or "-h")
        {
            return RuntimeCliResult.Success(
                "SharpClaw Runtime CLI\n  --cli help\n  --cli chat <message>\n");
        }
        if (command.Name == "chat")
            return await ExecuteChatAsync(command, kernel, cancellationToken);

        var registrationResult = await applications.TryInvokeCliAsync(
            command.Name,
            command.Arguments,
            runtimeKernel,
            executionContext,
            cancellationToken);
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
            cancellationToken);
        return RuntimeCliResult.Success(
            result.Completion.Content ?? string.Empty);
    }

    private static async ValueTask<bool> WriteOutputAsync(
        RuntimeCliResult result,
        TextWriter output,
        TextWriter error)
    {
        if (result.Output.Length > 0)
            await output.WriteAsync(result.Output);
        if (result.Error is not null)
            await error.WriteLineAsync(result.Error);
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
            CancellationToken.None);
        await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.OutputWrite,
            new RuntimeCliActionInvocation("output-write", null, 0),
            _ =>
            {
                error.WriteLine("The Runtime CLI command failed.");
                return ValueTask.FromResult(true);
            },
            CancellationToken.None);
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
            CancellationToken.None);
        await runtimeKernel.RunCliActionAsync(
            context,
            RuntimeCliActionCatalog.OutputWrite,
            new RuntimeCliActionInvocation("output-write", null, 0),
            _ =>
            {
                error.WriteLine("The Runtime CLI command was cancelled.");
                return ValueTask.FromResult(true);
            },
            CancellationToken.None);
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
