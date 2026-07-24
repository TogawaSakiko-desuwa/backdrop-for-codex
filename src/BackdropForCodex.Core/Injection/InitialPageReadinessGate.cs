using System.Diagnostics;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Applies the bounded startup policy: retry transient absence or ambiguity, but never select
/// among multiple eligible work pages.
/// </summary>
internal sealed class InitialPageReadinessGate
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;

    public InitialPageReadinessGate()
        : this(DefaultTimeout, DefaultPollInterval)
    {
    }

    internal InitialPageReadinessGate(TimeSpan timeout, TimeSpan pollInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        _timeout = timeout;
        _pollInterval = pollInterval;
    }

    public async Task<PageApplyResult> WaitAsync(
        Func<CancellationToken, Task<PageApplyResult>> applyAttempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyAttempt);
        var elapsed = Stopwatch.StartNew();
        var greatestEligibleCount = 0;
        var ambiguousTargetsObserved = false;
        do
        {
            var result = await applyAttempt(cancellationToken).ConfigureAwait(false);
            greatestEligibleCount = Math.Max(greatestEligibleCount, result.EligibleCount);
            ambiguousTargetsObserved |= result.IsAmbiguous;
            if (result.AppliedCount != 0)
            {
                return new PageApplyResult(
                    greatestEligibleCount,
                    result.AppliedCount,
                    IsAmbiguous: false,
                    AmbiguousTargetsObserved: ambiguousTargetsObserved);
            }

            var remaining = _timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new PageApplyResult(
                    greatestEligibleCount,
                    AppliedCount: 0,
                    IsAmbiguous: greatestEligibleCount > 1,
                    AmbiguousTargetsObserved: ambiguousTargetsObserved);
            }

            await Task.Delay(
                    remaining < _pollInterval ? remaining : _pollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        while (true);
    }
}

internal readonly record struct PageApplyResult(
    int EligibleCount,
    int AppliedCount,
    bool IsAmbiguous,
    bool AmbiguousTargetsObserved);
