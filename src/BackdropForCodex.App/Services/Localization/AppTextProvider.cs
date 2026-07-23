using System.Globalization;
using System.Resources;

namespace BackdropForCodex.App.Services.Localization;

public interface IAppTextProvider
{
    string GetString(string key);
}

/// <summary>
/// Reads neutral English resources by default and follows the current Windows UI culture.
/// </summary>
public sealed class AppTextProvider : IAppTextProvider
{
    private const string ResourceBaseName =
        "BackdropForCodex.App.Resources.AppResources";

    private static readonly ResourceManager ResourceManager =
        new(ResourceBaseName, typeof(AppTextProvider).Assembly);

    private readonly CultureInfo? _culture;

    public AppTextProvider(CultureInfo? culture = null)
    {
        _culture = culture;
    }

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var culture = NormalizeCulture(_culture ?? CultureInfo.CurrentUICulture);
        return ResourceManager.GetString(key, culture)
            ?? ResourceManager.GetString(key, CultureInfo.InvariantCulture)
            ?? key;
    }

    private static CultureInfo NormalizeCulture(CultureInfo culture)
    {
        var name = culture.Name;
        return string.Equals(name, "zh-CN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "zh-SG", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "zh-CHS", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "zh-Hans", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("zh-Hans-", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("zh-Hans")
            : culture;
    }
}
