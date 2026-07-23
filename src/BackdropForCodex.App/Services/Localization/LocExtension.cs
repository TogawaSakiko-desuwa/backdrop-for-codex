using System.Windows.Markup;

namespace BackdropForCodex.App.Services.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    private static readonly AppTextProvider Text = new();

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
        var value = Text.GetString(Key);
        return string.Equals(value, Key, StringComparison.Ordinal)
            ? Fallback
            : value;
    }
}
