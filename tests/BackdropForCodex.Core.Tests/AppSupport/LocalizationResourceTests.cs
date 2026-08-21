using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using BackdropForCodex.App.Services.Localization;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class LocalizationResourceTests
{
    private const string ResourceBaseName =
        "BackdropForCodex.App.Resources.AppResources";
    private const string SourceResourcePrefix = "LocalizationSources/";

    private static readonly Regex CSharpKeyPattern = new(
        "\\b(?:Text|GetString|GetStringOrFallback|FormatLocalized)\\s*\\(\\s*\"(?<key>[A-Za-z][A-Za-z0-9_]*)\"",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex XamlKeyPattern = new(
        "\\{[^}\\r\\n]*?:Loc\\s+(?<key>[A-Za-z][A-Za-z0-9_]*)\\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex CSharpFallbackPattern = new(
        "\\b(?:GetStringOrFallback|FormatLocalized)\\s*\\(\\s*\"(?<key>[A-Za-z][A-Za-z0-9_]*)\"\\s*,\\s*\"(?<fallback>(?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex XamlFallbackPattern = new(
        """\{[^}\r\n]*?:Loc\s+(?<key>[A-Za-z][A-Za-z0-9_]*)\s*,\s*Fallback=(?:(?<single>'[^']*')|(?<double>"[^"]*")|(?<bare>[^,}\s]+))""",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Fact]
    public void NeutralAndSimplifiedChineseResourcesHaveMatchingKeySets()
    {
        var neutralKeys = LoadResourceKeys(CultureInfo.InvariantCulture);
        var chineseKeys = LoadResourceKeys(CultureInfo.GetCultureInfo("zh-Hans"));

        Assert.Equal(neutralKeys.Count, chineseKeys.Count);
        Assert.Empty(neutralKeys.Except(chineseKeys, StringComparer.Ordinal));
        Assert.Empty(chineseKeys.Except(neutralKeys, StringComparer.Ordinal));
    }

    [Fact]
    public void StaticLocalizationKeysUsedByAppExistInEveryResourceSet()
    {
        var referencedKeys = LoadStaticallyReferencedKeys();
        var neutralKeys = LoadResourceKeys(CultureInfo.InvariantCulture);
        var chineseKeys = LoadResourceKeys(CultureInfo.GetCultureInfo("zh-Hans"));
        var missingNeutral = referencedKeys.Except(neutralKeys, StringComparer.Ordinal);
        var missingChinese = referencedKeys.Except(chineseKeys, StringComparer.Ordinal);

        Assert.NotEmpty(referencedKeys);
        Assert.Contains("Profile_AutomationName", referencedKeys);
        Assert.Contains("Profile_ActionsAutomationName", referencedKeys);
        Assert.Empty(missingNeutral);
        Assert.Empty(missingChinese);
    }

    [Fact]
    public void MissingResourceUsesExplicitFallback()
    {
        IAppTextProvider text = new MissingResourceTextProvider();

        var result = text.GetStringOrFallback("Missing_Key", "Fallback text");

        Assert.Equal("Fallback text", result);
    }

    [Fact]
    public void ReusedResourceKeysHaveConsistentFallbacks()
    {
        var fallbacksByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var source in LoadEmbeddedSources(".cs"))
        {
            foreach (Match match in CSharpFallbackPattern.Matches(source))
            {
                AddFallback(
                    fallbacksByKey,
                    match.Groups["key"].Value,
                    Regex.Unescape(match.Groups["fallback"].Value));
            }
        }

        foreach (var source in LoadEmbeddedSources(".xaml"))
        {
            foreach (Match match in XamlFallbackPattern.Matches(source))
            {
                AddFallback(
                    fallbacksByKey,
                    match.Groups["key"].Value,
                    ReadXamlFallback(match));
            }
        }

        Assert.NotEmpty(fallbacksByKey);
        var conflicts = fallbacksByKey
            .Where(pair => pair.Value.Count > 1)
            .Select(pair =>
                $"{pair.Key}: {string.Join(" | ", pair.Value.OrderBy(value => value, StringComparer.Ordinal))}")
            .ToArray();
        Assert.True(
            conflicts.Length == 0,
            $"Conflicting localization fallbacks:{Environment.NewLine}{string.Join(Environment.NewLine, conflicts)}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    public void DynamicWallpaperPrivacyCopyDescribesCurrentAudioBehavior(
        string cultureName)
    {
        var text = new AppTextProvider(CultureInfo.GetCultureInfo(cultureName));
        var libraryHint = text.GetString("Source_LibraryHint");
        var webNotice = text.GetString("WebPrivacy_Message");
        var privacy = text.GetString("Settings_PrivacyDescription");
        var combined = $"{libraryHint} {webNotice} {privacy}";

        if (string.Equals(cultureName, "zh-Hans", StringComparison.Ordinal))
        {
            Assert.Contains("可能播放声音", combined, StringComparison.Ordinal);
            Assert.Contains("不会枚举、静音或更改", combined, StringComparison.Ordinal);
            Assert.Contains("不会把音频送入 Codex", combined, StringComparison.Ordinal);
            Assert.Contains("网络", webNotice, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("may play sound", combined, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "does not enumerate, mute, or change",
                combined,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sends no audio into Codex", combined, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("network", webNotice, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AddFallback(
        Dictionary<string, HashSet<string>> fallbacksByKey,
        string key,
        string fallback)
    {
        if (!fallbacksByKey.TryGetValue(key, out var fallbacks))
        {
            fallbacks = new HashSet<string>(StringComparer.Ordinal);
            fallbacksByKey.Add(key, fallbacks);
        }

        _ = fallbacks.Add(fallback);
    }

    private static string ReadXamlFallback(Match match)
    {
        var value = match.Groups["single"].Success
            ? match.Groups["single"].Value[1..^1]
            : match.Groups["double"].Success
                ? match.Groups["double"].Value[1..^1]
                : match.Groups["bare"].Value;
        return WebUtility.HtmlDecode(value);
    }

    private static HashSet<string> LoadResourceKeys(CultureInfo culture)
    {
        var manager = new ResourceManager(
            ResourceBaseName,
            typeof(AppTextProvider).Assembly);
        var resourceSet = manager.GetResourceSet(
            culture,
            createIfNotExists: true,
            tryParents: false);
        Assert.NotNull(resourceSet);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in resourceSet)
        {
            var key = Assert.IsType<string>(entry.Key);
            var value = Assert.IsType<string>(entry.Value);
            Assert.False(string.IsNullOrWhiteSpace(value), $"Resource '{key}' is empty.");
            Assert.True(keys.Add(key), $"Resource '{key}' is duplicated.");
        }

        return keys;
    }

    private static HashSet<string> LoadStaticallyReferencedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in new[] { ".cs", ".xaml" })
        {
            var pattern = extension == ".xaml"
                ? XamlKeyPattern
                : CSharpKeyPattern;
            foreach (var source in LoadEmbeddedSources(extension))
            {
                foreach (Match match in pattern.Matches(source))
                {
                    _ = keys.Add(match.Groups["key"].Value);
                }
            }
        }

        return keys;
    }

    private static List<string> LoadEmbeddedSources(string extension)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var sourceNames = assembly
            .GetManifestResourceNames()
            .Where(name =>
                name.StartsWith(SourceResourcePrefix, StringComparison.Ordinal) &&
                name.EndsWith(extension, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(sourceNames);

        var sources = new List<string>(sourceNames.Length);
        foreach (var sourceName in sourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(sourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream);
            sources.Add(reader.ReadToEnd());
        }

        return sources;
    }

    private sealed class MissingResourceTextProvider : IAppTextProvider
    {
        public string GetString(string key) => key;
    }
}
