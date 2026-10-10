using System.Diagnostics.CodeAnalysis;

namespace SharpClaw.Services;

/// <summary>Observes asynchronous Uno event work on its originating UI context.</summary>
internal static class ClientUiEvent
{
    [SuppressMessage("Usage", "VSTHRD100", Justification =
        "Uno events return void. This adapter awaits the complete supplied operation, preserves UI affinity and observes every failure before the async-void boundary returns.")]
    [SuppressMessage("Design", "CA1031", Justification =
        "The required void UI event boundary records any unexpected action or rendering fault; composable operations remain Tasks and their errors cannot escape into the dispatcher.")]
    internal static async void Observe(Func<Task> operation)
    {
        try { await operation().ConfigureAwait(true); }
        catch (OperationCanceledException) { /* Retired page/tab work has no remaining UI effect. */ }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
        }
    }
}
