using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using SharpClaw.SidecarHost.OutOfProcess;

namespace SharpClaw.Runtime.Host;

/// <summary>A bounded registration bootstrap budget, separate from action deadlines.</summary>
internal static class PackagedSidecarReadiness
{
    internal const string TimeoutConfigurationKey = "Packages:OutOfProcessSidecarStartupTimeoutSeconds";
    internal const int DefaultTimeoutSeconds = 120;
    private const int MaximumTimeoutSeconds = 600;
    private static readonly Uri ReadinessAddress = new(
        OutOfProcessSidecarHostProtocol.ReadinessPath, UriKind.Relative);

    internal static TimeSpan ResolveTimeout(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[TimeoutConfigurationKey];
        if (string.IsNullOrEmpty(value))
            return TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 1 or > MaximumTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"{TimeoutConfigurationKey} must be an integer between 1 and {MaximumTimeoutSeconds}.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    internal static async Task WaitAsync(
        HttpClient http,
        Func<bool> hasExited,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, TimeSpan.FromSeconds(MaximumTimeoutSeconds));
        cancellationToken.ThrowIfCancellationRequested();
        var clock = timeProvider ?? TimeProvider.System;
        var started = clock.GetTimestamp();
        using var budget = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            while (clock.GetElapsedTime(started) < timeout)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (hasExited())
                    throw new InvalidOperationException("The sidecar exited before readiness.");
                try
                {
                    using var response = await http.GetAsync(
                        ReadinessAddress, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    if (clock.GetElapsedTime(started) >= timeout)
                        throw new TimeoutException("The sidecar readiness bootstrap budget expired.");
                    if (response.StatusCode == HttpStatusCode.OK)
                        return;
                }
                catch (HttpRequestException)
                {
                }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                {
                    // HttpClient's shorter individual probe timeout is retryable.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (
            budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The sidecar readiness bootstrap budget expired.", exception);
        }

        throw new TimeoutException("The sidecar readiness bootstrap budget expired.");
    }
}
