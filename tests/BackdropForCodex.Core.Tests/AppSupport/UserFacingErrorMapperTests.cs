using System.Globalization;
using BackdropForCodex.App.Models;
using BackdropForCodex.App.Services.Errors;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class UserFacingErrorMapperTests
{
    [Fact]
    public void MapUsesStableLocalizedContentWithoutExceptionDetails()
    {
        const string privateDetail =
            "C:\\Users\\person\\Pictures\\secret-project\\wallpaper.png";
        var mapper = new UserFacingErrorMapper(
            new AppTextProvider(CultureInfo.GetCultureInfo("en")));

        var result = mapper.Map(new MediaValidationException(privateDetail));

        Assert.Equal(UserFacingErrorCode.MediaInvalid, result.Code);
        Assert.NotEmpty(result.Title);
        Assert.NotEmpty(result.Message);
        Assert.NotEmpty(result.Recovery);
        Assert.True(result.CanRetry);
        Assert.DoesNotContain(
            privateDetail,
            $"{result.Title} {result.Message} {result.Recovery}",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapPreservesForegroundFailureWhenCleanupIsAggregated()
    {
        var mapper = new UserFacingErrorMapper(
            new AppTextProvider(CultureInfo.GetCultureInfo("en")));
        var aggregate = new AggregateException(
            new MediaValidationException("sensitive detail"),
            new IOException("cleanup detail"));

        var result = mapper.Map(
            aggregate,
            UserFacingOperation.ApplyWallpaper);

        Assert.Equal(UserFacingErrorCode.MediaInvalid, result.Code);
    }

    [Fact]
    public void RuntimeAlreadyRunningErrorUsesLocalizedRecoveryInsteadOfCoreMessage()
    {
        const string RawRuntimeMessage =
            "Codex is already running and was not launched by this coordinator.";
        var runtimeError = new WallpaperRuntimeError(
            "activation-failed",
            RawRuntimeMessage,
            typeof(CodexAlreadyRunningException).FullName);
        var english = new UserFacingErrorMapper(
            new AppTextProvider(CultureInfo.GetCultureInfo("en")));
        var chinese = new UserFacingErrorMapper(
            new AppTextProvider(CultureInfo.GetCultureInfo("zh-Hans")));

        var englishResult = english.Map(
            runtimeError,
            UserFacingOperation.ApplyWallpaper);
        var chineseResult = chinese.Map(
            runtimeError,
            UserFacingOperation.ApplyWallpaper);

        Assert.Equal(UserFacingErrorCode.CodexAlreadyRunning, englishResult.Code);
        Assert.Equal("Cannot start Codex safely", englishResult.Title);
        Assert.Equal("无法安全启动 Codex", chineseResult.Title);
        Assert.True(englishResult.CanRetry);
        Assert.DoesNotContain(
            RawRuntimeMessage,
            $"{englishResult.Title} {englishResult.Message} {englishResult.Recovery}",
            StringComparison.Ordinal);
    }

    [Fact]
    public void SecurityValidationFailureMapsToNonRetryableSafeContent()
    {
        const string privateDetail =
            "Package discovery failed at C:\\Users\\person\\AppData\\secret";
        var mapper = new UserFacingErrorMapper(
            new AppTextProvider(CultureInfo.GetCultureInfo("en")));
        var security = CodexSecurityResult.Rejected(
            CodexSecurityStage.PackageIdentity,
            CodexSecurityFailureCode.PackageDiscoveryFailed,
            privateDetail);

        var result = mapper.Map(new CodexSecurityValidationException(security));

        Assert.Equal(UserFacingErrorCode.CodexSecurityRejected, result.Code);
        Assert.False(result.CanRetry);
        Assert.DoesNotContain(
            privateDetail,
            $"{result.Title} {result.Message} {result.Recovery}",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinalPageApplyDeadlineMapsToWallpaperApplyInsteadOfBrowserHandshake()
    {
        var mapper = new UserFacingErrorMapper(
            new AppTextProvider(CultureInfo.GetCultureInfo("en")));
        var exception = new FinalPageApplyTimeoutException(
            "bounded final apply deadline elapsed",
            new TimeoutException());

        var result = mapper.Map(exception, UserFacingOperation.ApplyWallpaper);

        Assert.Equal(UserFacingErrorCode.WallpaperApplyFailed, result.Code);
        Assert.True(result.CanRetry);
    }

    [Fact]
    public void SimplifiedChineseResourcesFollowCultureAndEnglishIsTheFallback()
    {
        var chinese = new AppTextProvider(
            CultureInfo.GetCultureInfo("zh-CN"));
        var unsupportedCulture = new AppTextProvider(
            CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal("应用更改", chinese.GetString("Action_ApplyChanges"));
        Assert.Equal(
            "Apply changes",
            unsupportedCulture.GetString("Action_ApplyChanges"));
    }

    [Fact]
    public void EveryErrorAndOperationStageHasLocalizedResourceContent()
    {
        var cultures = new[]
        {
            CultureInfo.GetCultureInfo("en"),
            CultureInfo.GetCultureInfo("zh-Hans"),
        };

        foreach (var culture in cultures)
        {
            var text = new AppTextProvider(culture);
            foreach (var code in Enum.GetValues<UserFacingErrorCode>())
            {
                AssertResourceExists(text, $"Error_{code}_Message");
                AssertResourceExists(text, $"Error_{code}_Recovery");
            }

            foreach (var stage in Enum.GetValues<WallpaperOperationStage>())
            {
                if (stage != WallpaperOperationStage.Idle)
                {
                    AssertResourceExists(text, $"OperationStage_{stage}");
                }
            }
        }
    }

    private static void AssertResourceExists(
        AppTextProvider text,
        string key)
    {
        var value = text.GetString(key);
        Assert.NotEqual(key, value);
        Assert.False(string.IsNullOrWhiteSpace(value));
    }
}
