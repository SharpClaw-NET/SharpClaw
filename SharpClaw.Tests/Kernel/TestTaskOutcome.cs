using System.Diagnostics.CodeAnalysis;

namespace SharpClaw.Tests;

internal static class TestTaskOutcome
{
    [SuppressMessage("Design", "CA1031", Justification =
        "These tests capture an awaited operation's actual failure for identity assertions; timeout still fails verification and is never swallowed.")]
    public static async Task<Exception?> CaptureAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            return exception;
        }
    }

    public static Task<Exception?[]> JoinAsync(params Task[] operations) =>
        Task.WhenAll(operations.Select(CaptureAsync));
}
