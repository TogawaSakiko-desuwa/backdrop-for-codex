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
        cancellationToken.ThrowIfCancellationRequested();
        using var deadlineCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCancellation.CancelAfter(_timeout);
        var deadlineToken = deadlineCancellation.Token;
        var latestResult = default(PageApplyResult);
        var hasResult = false;
        var ambiguousTargetsObserved = false;
        while (true)
        {
            PageApplyResult result;
            try
            {
                result = await applyAttempt(deadlineToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CreateTerminalResult(
                    latestResult,
                    hasResult,
                    ambiguousTargetsObserved);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (deadlineToken.IsCancellationRequested)
            {
                return CreateTerminalResult(
                    latestResult,
                    hasResult,
                    ambiguousTargetsObserved);
            }

            latestResult = result;
            hasResult = true;
            ambiguousTargetsObserved |=
                result.IsAmbiguous || result.AmbiguousTargetsObserved;
            if (result.AppliedCount != 0)
            {
                return CreateTerminalResult(
                    latestResult,
                    hasResult,
                    ambiguousTargetsObserved);
            }

            if (deadlineToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CreateTerminalResult(
                    latestResult,
                    hasResult,
                    ambiguousTargetsObserved);
            }

            try
            {
                await Task.Delay(_pollInterval, deadlineToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadlineToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CreateTerminalResult(
                    latestResult,
                    hasResult,
                    ambiguousTargetsObserved);
            }
        }
    }

    public async Task<PageApplyResult> RunFinalAttemptAsync(
        Func<CancellationToken, Task<PageApplyResult>> applyAttempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyAttempt);
        cancellationToken.ThrowIfCancellationRequested();
        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationCancellation.CancelAfter(_timeout);
        try
        {
            var result = await applyAttempt(operationCancellation.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            operationCancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException exception)
            when (operationCancellation.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new FinalPageApplyTimeoutException(
                "The final Global presentation fallback exceeded its bounded operation deadline.",
                exception);
        }
    }

    private static PageApplyResult CreateTerminalResult(
        PageApplyResult latestResult,
        bool hasResult,
        bool ambiguousTargetsObserved)
    {
        return hasResult
            ? new PageApplyResult(
                latestResult.EligibleCount,
                latestResult.AppliedCount,
                latestResult.IsAmbiguous,
                AmbiguousTargetsObserved: ambiguousTargetsObserved)
            : default;
    }
}

internal sealed class FinalPageApplyTimeoutException : WallpaperInjectionException
{
    public FinalPageApplyTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal readonly record struct PageApplyResult(
    int EligibleCount,
    int AppliedCount,
    bool IsAmbiguous,
    bool AmbiguousTargetsObserved);
