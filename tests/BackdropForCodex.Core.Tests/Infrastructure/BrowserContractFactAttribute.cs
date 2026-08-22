using Xunit;

namespace BackdropForCodex.Core.Tests.Infrastructure;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BrowserContractFactAttribute : FactAttribute
{
    internal const string OptInEnvironmentVariable =
        "BACKDROP_FOR_CODEX_RUN_BROWSER_CONTRACTS";

    public BrowserContractFactAttribute()
    {
        var isExplicitlyEnabled = string.Equals(
            Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        var isContinuousIntegration = string.Equals(
            Environment.GetEnvironmentVariable("CI"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (!isExplicitlyEnabled && !isContinuousIntegration)
        {
            Skip =
                $"Opt-in browser contract. Set {OptInEnvironmentVariable}=1 to run it.";
        }
    }
}
