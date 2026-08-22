using Xunit;

namespace BackdropForCodex.Core.Tests.Infrastructure;

internal sealed class RealDynamicWallpaperFactAttribute : FactAttribute
{
    internal const string OptInVariable = "BACKDROP_RUN_REAL_WGC_MF";

    public RealDynamicWallpaperFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {OptInVariable}=1 to run the real WGC/MF/Edge machine test.";
        }
    }
}
