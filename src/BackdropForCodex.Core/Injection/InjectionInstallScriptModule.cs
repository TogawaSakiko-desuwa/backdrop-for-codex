using System.Globalization;
using System.Text.Json;
using BackdropForCodex.Core.Codex;

namespace BackdropForCodex.Core.Injection;

internal static class InjectionInstallScriptModule
{
    internal const string PayloadToken = "__BACKDROP_FOR_CODEX_PAYLOAD_JSON__";

    private const string UnresolvedStyleTokenPrefix = "__BFC_";
    private const string UnresolvedInstallTokenPrefix = "__BACKDROP_FOR_CODEX_";

    private static readonly TimeSpan MediaLoadTimeout =
        InjectionLifecycleScriptModule.LeaseTimeout;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string Build(
        WallpaperInjectionOptions options,
        CompatibilityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);
        var styleCapabilities = InjectionStyleScriptModule.Resolve(capabilities);
        var styleSheet = BuildStyleSheet(options, styleCapabilities);
        return BuildWithStyleSheet(options, styleCapabilities, styleSheet);
    }

    internal static string BuildWithStyleSheet(
        WallpaperInjectionOptions options,
        CompatibilityCapabilities capabilities,
        string styleSheet)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(styleSheet);
        return BuildWithStyleSheet(
            options,
            InjectionStyleScriptModule.Resolve(capabilities),
            styleSheet);
    }

    private static string BuildWithStyleSheet(
        WallpaperInjectionOptions options,
        InjectionStyleCapabilities styleCapabilities,
        string styleSheet)
    {
        var payload = JsonSerializer.Serialize(
            new ScriptPayload(
                InjectionOwnershipContract.Owner,
                InjectionOwnershipContract.RootElementId,
                InjectionOwnershipContract.StyleElementId,
                InjectionOwnershipContract.FileInputElementId,
                InjectionOwnershipContract.StateProperty,
                options.Generation,
                options.ExpectedContentLength,
                options.MediaKind == WallpaperMediaKind.Video ? "video" : "image",
                checked((int)InjectionLifecycleScriptModule.HeartbeatInterval.TotalMilliseconds),
                checked((int)InjectionLifecycleScriptModule.LeaseTimeout.TotalMilliseconds),
                checked((int)MediaLoadTimeout.TotalMilliseconds),
                styleCapabilities.GlassEnabled,
                styleCapabilities.AdvancedSurfacesEnabled,
                styleSheet),
            SerializerOptions);

        var script = ReplaceSingleRequiredToken(
            InjectionTemplateResources.InstallScriptTemplate,
            PayloadToken,
            payload);
        if (script.Contains(UnresolvedInstallTokenPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The install template contains an unresolved token.");
        }

        return script;
    }

    internal static string BuildStyleSheet(
        WallpaperInjectionOptions options,
        CompatibilityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);
        return BuildStyleSheet(options, InjectionStyleScriptModule.Resolve(capabilities));
    }

    private static string BuildStyleSheet(
        WallpaperInjectionOptions options,
        InjectionStyleCapabilities capabilities)
    {
        var objectFit = InjectionMediaScriptModule.ToCss(options.ObjectFit);
        var focusX = options.ObjectFit == WallpaperObjectFit.Cover
            ? options.Composition.FocusX * 100
            : 50;
        var focusY = options.ObjectFit == WallpaperObjectFit.Cover
            ? options.Composition.FocusY * 100
            : 50;
        var glassBodySelector = capabilities.GlassEnabled
            ? "body"
            : "body[data-codex-wallpaper-glass-disabled]";
        var advancedBodySelector = capabilities.AdvancedSurfacesEnabled
            ? "body"
            : "body[data-codex-wallpaper-advanced-disabled]";

        var replacements = new (string Token, string Value)[]
        {
            ("__BFC_ROOT_ID__", InjectionOwnershipContract.RootElementId),
            ("__BFC_OBJECT_FIT__", objectFit),
            ("__BFC_FOCUS_X_PERCENT__", Format(focusX)),
            ("__BFC_FOCUS_Y_PERCENT__", Format(focusY)),
            ("__BFC_MEDIA_OPACITY__", Format(options.MediaOpacity)),
            ("__BFC_DARK_OVERLAY__", Format(options.Composition.DarkOverlay)),
            ("__BFC_LIGHT_OVERLAY__", Format(options.Composition.LightOverlay)),
            ("__BFC_GLASS_RED__", options.Glass.Red.ToString(CultureInfo.InvariantCulture)),
            ("__BFC_GLASS_GREEN__", options.Glass.Green.ToString(CultureInfo.InvariantCulture)),
            ("__BFC_GLASS_BLUE__", options.Glass.Blue.ToString(CultureInfo.InvariantCulture)),
            ("__BFC_GLASS_OPACITY__", Format(options.Glass.Opacity)),
            ("__BFC_GLASS_OPACITY_PERCENT__", Format(options.Glass.Opacity * 100)),
            ("__BFC_HOME_HOVER_OPACITY_PERCENT__", Format(
                Math.Min(options.Glass.Opacity + 0.08, 1) * 100)),
            ("__BFC_GLASS_BLUR_PIXELS__", Format(options.Glass.BlurPixels)),
            ("__BFC_GLASS_SATURATION__", Format(options.Glass.Saturation)),
            ("__BFC_GLASS_BODY_SELECTOR__", glassBodySelector),
            ("__BFC_ADVANCED_BODY_SELECTOR__", advancedBodySelector),
        };

        var styleSheet = InjectionTemplateResources.WallpaperStyleTemplate;
        foreach (var (token, value) in replacements)
        {
            if (!styleSheet.Contains(token, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The wallpaper stylesheet is missing required token '{token}'.");
            }

            styleSheet = styleSheet.Replace(token, value, StringComparison.Ordinal);
        }

        if (styleSheet.Contains(UnresolvedStyleTokenPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The wallpaper stylesheet contains an unresolved token.");
        }

        return styleSheet;
    }

    private static string ReplaceSingleRequiredToken(
        string template,
        string token,
        string value)
    {
        var tokenIndex = template.IndexOf(token, StringComparison.Ordinal);
        if (tokenIndex < 0 ||
            template.IndexOf(token, tokenIndex + token.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"The injection template must contain token '{token}' exactly once.");
        }

        return string.Concat(
            template.AsSpan(0, tokenIndex),
            value,
            template.AsSpan(tokenIndex + token.Length));
    }

    private static string Format(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private sealed record ScriptPayload(
        string Owner,
        string RootId,
        string StyleId,
        string FileInputId,
        string StateProperty,
        long Generation,
        long ExpectedContentLength,
        string MediaKind,
        int HeartbeatIntervalMs,
        int LeaseTimeoutMs,
        int MediaLoadTimeoutMs,
        bool GlassEnabled,
        bool AdvancedSurfacesEnabled,
        string StyleSheet);
}
