using System.Globalization;
using BackdropForCodex.App.Models;
using BackdropForCodex.App.Services.Errors;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.Core.Media;
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
