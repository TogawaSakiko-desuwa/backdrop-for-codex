using System.Reflection;
using System.Text;

namespace BackdropForCodex.Core.Injection;

internal static class InjectionTemplateResources
{
    internal const string InstallScriptResourceName =
        "BackdropForCodex.Core.Injection.Templates.Install.js";
    internal const string WallpaperStyleResourceName =
        "BackdropForCodex.Core.Injection.Templates.Wallpaper.css";

    private static readonly Lazy<string> InstallScriptTemplateValue =
        new(() => ReadRequired(InstallScriptResourceName));
    private static readonly Lazy<string> WallpaperStyleTemplateValue =
        new(() => ReadRequired(WallpaperStyleResourceName));

    internal static string InstallScriptTemplate => InstallScriptTemplateValue.Value;

    internal static string WallpaperStyleTemplate => WallpaperStyleTemplateValue.Value;

    private static string ReadRequired(string resourceName)
    {
        var assembly = typeof(InjectionTemplateResources).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException(
                $"Required injection resource '{resourceName}' is missing from '{assembly.GetName().Name}'.");
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        var value = reader.ReadToEnd().ReplaceLineEndings("\n");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required injection resource '{resourceName}' is empty.");
        }

        return value;
    }
}
