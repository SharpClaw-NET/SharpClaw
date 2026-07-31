using System.Threading.Channels;
using FluentAssertions;
using SharpClaw.Runtime.Host.Cli;

namespace SharpClaw.Tests.Cli;

[TestFixture]
public sealed class CliSessionContextTests
{
    [Test]
    public async Task Concurrent_sessions_keep_state_and_output_isolated()
    {
        using var firstOutput = new StringWriter();
        using var firstError = new StringWriter();
        using var secondOutput = new StringWriter();
        using var secondError = new StringWriter();

        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunSession(
            string user,
            StringWriter output,
            StringWriter error,
            TaskCompletionSource ready)
        {
            using var scope = CliDispatcher.BeginSession(output, error);
            CliDispatcher.CurrentSession!.CurrentUser = user;
            ready.SetResult();
            await release.Task;

            CliConsole.WriteLine($"user={CliDispatcher.CurrentSession.CurrentUser}");
            CliConsole.Error.WriteLine($"error={CliDispatcher.CurrentSession.CurrentUser}");
        }

        var first = Task.Run(() => RunSession("first", firstOutput, firstError, firstReady));
        var second = Task.Run(() => RunSession("second", secondOutput, secondError, secondReady));

        await Task.WhenAll(firstReady.Task, secondReady.Task).WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();
        await Task.WhenAll(first, second);

        firstOutput.ToString().Should().Be("user=first" + Environment.NewLine);
        firstError.ToString().Should().Be("error=first" + Environment.NewLine);
        secondOutput.ToString().Should().Be("user=second" + Environment.NewLine);
        secondError.ToString().Should().Be("error=second" + Environment.NewLine);
    }

    [Test]
    public void Session_input_is_read_from_the_session_channel()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var input = Channel.CreateUnbounded<string?>();
        input.Writer.TryWrite("approval");

        using var scope = CliDispatcher.BeginSession(output, error, input.Reader);

        CliConsole.ReadLine().Should().Be("approval");
    }
}
