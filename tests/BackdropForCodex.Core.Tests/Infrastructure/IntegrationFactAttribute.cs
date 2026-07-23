using Xunit;

namespace BackdropForCodex.Core.Tests.Infrastructure;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(string optInEnvironmentVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optInEnvironmentVariable);

        if (!string.Equals(
                Environment.GetEnvironmentVariable(optInEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Opt-in integration test. Set {optInEnvironmentVariable}=1 to run it.";
        }
    }
}
