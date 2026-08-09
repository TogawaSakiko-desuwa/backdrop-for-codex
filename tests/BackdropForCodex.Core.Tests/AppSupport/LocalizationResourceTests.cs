using System.Collections;
using System.Globalization;
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
        "\\b(?:Text|GetString|GetStringOrFallback)\\s*\\(\\s*\"(?<key>[A-Za-z][A-Za-z0-9_]*)\"",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex XamlKeyPattern = new(
        "\\{[^}\\r\\n]*?:Loc\\s+(?<key>[A-Za-z][A-Za-z0-9_]*)\\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex CSharpFallbackPattern = new(
        "\\bGetStringOrFallback\\s*\\(\\s*\"(?<key>[A-Za-z][A-Za-z0-9_]*)\"\\s*,\\s*\"(?<fallback>(?:\\\\.|[^\"\\\\])*)\"",
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
    public void ReusedCSharpResourceKeysHaveConsistentFallbacks()
    {
        var fallbacksByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var source in LoadEmbeddedSources(".cs"))
        {
            foreach (Match match in CSharpFallbackPattern.Matches(source))
            {
                var key = match.Groups["key"].Value;
                if (!fallbacksByKey.TryGetValue(key, out var fallbacks))
                {
                    fallbacks = new HashSet<string>(StringComparer.Ordinal);
                    fallbacksByKey.Add(key, fallbacks);
                }

                _ = fallbacks.Add(match.Groups["fallback"].Value);
            }
        }

        Assert.NotEmpty(fallbacksByKey);
        var conflicts = fallbacksByKey
            .Where(pair => pair.Value.Count > 1)
            .Select(pair =>
                $"{pair.Key}: {string.Join(" | ", pair.Value.OrderBy(value => value, StringComparer.Ordinal))}")
            .ToArray();
        Assert.Empty(conflicts);
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
