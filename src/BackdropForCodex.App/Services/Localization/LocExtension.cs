using System.Windows.Markup;

namespace BackdropForCodex.App.Services.Localization;

/// <summary>
/// Resolves localized application text for XAML and supplies an explicit fallback for missing keys.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    private static readonly IAppTextProvider Text = new AppTextProvider();

    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public string Fallback { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Key);
        return Text.GetStringOrFallback(Key, Fallback);
    }
}
