using System.Text.RegularExpressions;
using System.Xml.Linq;
using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed partial class ReviewedCodexRightPanelSelectorTests
{
    private static readonly string[] ProtectedSurfaceIds =
    [
        "code-surface",
        "diff-surface",
        "editor-surface",
        "popcorn-surface",
        "table-surface",
    ];

    [Fact]
    public void ReviewedSelectors_MatchOnlyTheIntendedRightPanelShells()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(ReviewedRightPanelFixture);

        var glassRule = Assert.Single(rules, IsReviewedRightPanelGlassRule);
        var clearRule = Assert.Single(rules, IsReviewedRightPanelClearRule);
        var generalGlassRule = Assert.Single(rules, IsGeneralGlassRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body aside[data-app-shell-focus-area="right-panel"]
                      > div:has([role="tabpanel"][data-app-shell-tab-panel-controller="right"])
                      > div[class~="bg-token-main-surface-primary"]
                    """),
            ],
            glassRule.Selectors);
        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body [role="tabpanel"][data-app-shell-tab-panel-controller="right"]
                      > [class~="bg-token-main-surface-primary"]
                    """),
                CanonicalizeSelector(
                    """
                    body [role="tabpanel"][data-app-shell-tab-panel-controller="right"]
                      [class~="relative"][class~="rounded-lg"][class~="bg-token-main-surface-primary"]:has(.markdown)
                    """),
            ],
            clearRule.Selectors);

        Assert.Equal(
            ["right-panel-glass-shell"],
            SelectFixtureIds(fixture, glassRule.Selectors));
        Assert.Equal(
            ["file-layout-shell"],
            SelectFixtureIds(fixture, [clearRule.Selectors[0]]));
        Assert.Equal(
            ["markdown-shell"],
            SelectFixtureIds(fixture, [clearRule.Selectors[1]]));
        Assert.Equal(
            ["file-layout-shell", "markdown-shell"],
            SelectFixtureIds(fixture, clearRule.Selectors));
        Assert.Equal(
            ["left-panel-lookalike"],
            SelectFixtureIds(fixture, generalGlassRule.Selectors));
    }

    [Fact]
    public void ReviewedSelectors_KeepContentSurfacesOutOfGlassAndClearRules()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(ReviewedRightPanelFixture);
        var glassRule = Assert.Single(rules, IsReviewedRightPanelGlassRule);
        var clearRule = Assert.Single(rules, IsReviewedRightPanelClearRule);
        var modifiedIds = SelectFixtureIds(
            fixture,
            [.. glassRule.Selectors, .. clearRule.Selectors]);

        Assert.All(
            ProtectedSurfaceIds,
            protectedId =>
            {
                Assert.NotNull(FindFixtureNode(fixture, protectedId));
                Assert.DoesNotContain(protectedId, modifiedIds);
            });
        Assert.DoesNotContain("left-panel-lookalike", modifiedIds);
        Assert.DoesNotContain("right-panel-near-miss", modifiedIds);
        Assert.DoesNotContain("rounded-surface-without-markdown", modifiedIds);
    }

    private static bool IsReviewedRightPanelGlassRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: var(--codex-wallpaper-glass) !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: blur(var(--codex-wallpaper-blur))",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector => selector.Contains(
                "aside[data-app-shell-focus-area=\"right-panel\"]",
                StringComparison.Ordinal) &&
                selector.Contains(":has(", StringComparison.Ordinal) &&
                selector.Contains('>'));

    private static bool IsReviewedRightPanelClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background-color: transparent !important;" &&
        rule.Selectors.Any(
            selector => selector.Contains(
                "[data-app-shell-tab-panel-controller=\"right\"]",
                StringComparison.Ordinal));

    private static bool IsGeneralGlassRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: var(--codex-wallpaper-glass) !important",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector => selector.Contains(
                "aside:not([data-app-shell-focus-area=\"right-panel\"])",
                StringComparison.Ordinal));

    private static string ExtractGeneratedStyleSheet()
    {
        var script = InjectionScriptBuilder.BuildInstall(
            new WallpaperInjectionOptions(
                1,
                new Uri("https://127.0.0.1:49152/media/wallpaper"),
                @"C:\Wallpapers\wallpaper.png",
                1234,
                WallpaperMediaKind.Image));
        const string StartMarker = "style.textContent = `";
        var start = script.IndexOf(StartMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += StartMarker.Length;
        var end = script.IndexOf("`;", start, StringComparison.Ordinal);
        Assert.True(end > start);

        return script[start..end];
    }

    private static string ExtractBlock(string source, string blockHeader)
    {
        var withoutRuntimeExpressions = JavaScriptInterpolationRegex().Replace(
            source,
            "runtime-value");
        var header = withoutRuntimeExpressions.IndexOf(blockHeader, StringComparison.Ordinal);
        Assert.True(header >= 0);
        var openingBrace = withoutRuntimeExpressions.IndexOf('{', header);
        Assert.True(openingBrace > header);

        var depth = 0;
        for (var index = openingBrace; index < withoutRuntimeExpressions.Length; index++)
        {
            depth += withoutRuntimeExpressions[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return withoutRuntimeExpressions[(openingBrace + 1)..index];
            }
        }

        throw new InvalidOperationException($"CSS block '{blockHeader}' is not closed.");
    }

    private static CssRule[] ParseLeafRules(string css)
    {
        var withoutComments = CssCommentRegex().Replace(css, string.Empty);

        return CssLeafRuleRegex()
            .Matches(withoutComments)
            .Select(match => new CssRule(
                SplitTopLevel(
                        match.Groups["selectors"].Value,
                        ',')
                    .Select(CanonicalizeSelector)
                    .ToArray(),
                match.Groups["declarations"].Value.Trim()))
            .ToArray();
    }

    private static string[] SelectFixtureIds(
        XDocument fixture,
        IReadOnlyCollection<string> selectors) =>
        fixture
            .Descendants()
            .Where(element => selectors.Any(selector => MatchesSelector(element, selector)))
            .Select(element => (string?)element.Attribute("data-fixture-id"))
            .Where(id => id is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static XElement? FindFixtureNode(XDocument fixture, string fixtureId) =>
        fixture
            .Descendants()
            .SingleOrDefault(
                element => (string?)element.Attribute("data-fixture-id") == fixtureId);

    private static bool MatchesSelector(XElement element, string selector)
    {
        var compactSelector = ChildCombinatorWhitespaceRegex().Replace(
            CanonicalizeWhitespace(selector),
            ">");
        var split = FindLastCombinator(compactSelector);
        if (split is null)
        {
            return MatchesSimpleSelector(element, compactSelector);
        }

        var (index, combinator) = split.Value;
        var left = compactSelector[..index].Trim();
        var right = compactSelector[(index + 1)..].Trim();
        if (!MatchesSimpleSelector(element, right))
        {
            return false;
        }

        return combinator == '>'
            ? element.Parent is not null && MatchesSelector(element.Parent, left)
            : element.Ancestors().Any(ancestor => MatchesSelector(ancestor, left));
    }

    private static (int Index, char Combinator)? FindLastCombinator(string selector)
    {
        var parentheses = 0;
        var brackets = 0;
        var quote = '\0';

        for (var index = selector.Length - 1; index >= 0; index--)
        {
            var character = selector[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            switch (character)
            {
                case ')':
                    parentheses++;
                    break;
                case '(':
                    parentheses--;
                    break;
                case ']':
                    brackets++;
                    break;
                case '[':
                    brackets--;
                    break;
                case '>' when parentheses == 0 && brackets == 0:
                    return (index, character);
                case ' ' when parentheses == 0 && brackets == 0:
                    return (index, character);
            }
        }

        return null;
    }

    private static bool MatchesSimpleSelector(XElement element, string selector)
    {
        if (selector.StartsWith(":is(", StringComparison.Ordinal))
        {
            var closingParenthesis = FindMatchingParenthesis(selector, 3);
            if (closingParenthesis != selector.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Unsupported selector after :is(): '{selector}'.");
            }

            return SplitTopLevel(selector[4..closingParenthesis], ',')
                .Any(alternative => MatchesSimpleSelector(element, alternative));
        }

        var notIndex = selector.IndexOf(":not(", StringComparison.Ordinal);
        if (notIndex >= 0)
        {
            var closingParenthesis = FindMatchingParenthesis(selector, notIndex + 4);
            if (closingParenthesis != selector.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Unsupported selector after :not(): '{selector}'.");
            }

            var excludedSelector = selector[(notIndex + 5)..closingParenthesis];
            if (MatchesSimpleSelector(element, excludedSelector))
            {
                return false;
            }

            selector = selector[..notIndex];
        }

        var hasIndex = selector.IndexOf(":has(", StringComparison.Ordinal);
        if (hasIndex >= 0)
        {
            var closingParenthesis = FindMatchingParenthesis(selector, hasIndex + 4);
            if (closingParenthesis != selector.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Unsupported selector after :has(): '{selector}'.");
            }

            var relativeSelector = selector[(hasIndex + 5)..closingParenthesis];
            if (!element.Descendants().Any(
                    descendant => MatchesSelector(descendant, relativeSelector)))
            {
                return false;
            }

            selector = selector[..hasIndex];
        }

        var attributes = CssAttributeRegex().Matches(selector);
        foreach (Match attributeMatch in attributes)
        {
            var name = attributeMatch.Groups["name"].Value;
            var attribute = element.Attribute(name);
            if (attribute is null)
            {
                return false;
            }

            var operation = attributeMatch.Groups["operation"].Value;
            var expected = attributeMatch.Groups["value"].Value;
            if (operation == "=" && attribute.Value != expected)
            {
                return false;
            }

            if (operation == "~=" &&
                !attribute.Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(expected, StringComparer.Ordinal))
            {
                return false;
            }
        }

        var selectorWithoutAttributes = CssAttributeRegex().Replace(selector, string.Empty);
        var classes = CssClassRegex().Matches(selectorWithoutAttributes);
        var actualClasses = ((string?)element.Attribute("class") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (classes.Any(
                classMatch => !actualClasses.Contains(
                    classMatch.Groups["name"].Value,
                    StringComparer.Ordinal)))
        {
            return false;
        }

        var typeSelector = CssClassRegex()
            .Replace(selectorWithoutAttributes, string.Empty)
            .Trim();

        return typeSelector.Length == 0 ||
            typeSelector == "*" ||
            element.Name.LocalName.Equals(typeSelector, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindMatchingParenthesis(string source, int openingParenthesis)
    {
        var depth = 0;
        for (var index = openingParenthesis; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Selector has an unclosed parenthesis: '{source}'.");
    }

    private static List<string> SplitTopLevel(string value, char separator)
    {
        var result = new List<string>();
        var start = 0;
        var parentheses = 0;
        var brackets = 0;
        var quote = '\0';

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            switch (character)
            {
                case '(':
                    parentheses++;
                    break;
                case ')':
                    parentheses--;
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets--;
                    break;
                default:
                    if (character == separator && parentheses == 0 && brackets == 0)
                    {
                        result.Add(value[start..index].Trim());
                        start = index + 1;
                    }

                    break;
            }
        }

        result.Add(value[start..].Trim());
        return result;
    }

    private static string CanonicalizeSelector(string selector) =>
        ChildCombinatorWhitespaceRegex().Replace(
            CanonicalizeWhitespace(selector),
            " > ");

    private static string CanonicalizeWhitespace(string value) =>
        WhitespaceRegex().Replace(value, " ").Trim();

    private sealed record CssRule(
        IReadOnlyList<string> Selectors,
        string Declarations);

    [GeneratedRegex(@"\$\{[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptInterpolationRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CssCommentRegex();

    [GeneratedRegex(
        @"(?<selectors>[^{}]+)\{(?<declarations>[^{}]*)\}",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CssLeafRuleRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s*>\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ChildCombinatorWhitespaceRegex();

    [GeneratedRegex(
        @"\[(?<name>[\w-]+)(?:(?<operation>~=|=)""(?<value>[^""]*)"")?\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex CssAttributeRegex();

    [GeneratedRegex(@"\.(?<name>[\w-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CssClassRegex();

    private const string ReviewedRightPanelFixture =
        """
        <html class="electron-dark">
          <body>
            <aside data-app-shell-focus-area="left-panel"
                   data-fixture-id="left-panel-lookalike">
              <div>
                <div class="bg-token-main-surface-primary">
                  <div role="tabpanel"
                       data-app-shell-tab-panel-controller="left" />
                </div>
              </div>
            </aside>

            <aside data-app-shell-focus-area="right-panel"
                   data-fixture-id="reviewed-right-panel">
              <div data-fixture-id="right-panel-controller">
                <div class="bg-token-main-surface-primary"
                     data-fixture-id="right-panel-glass-shell">
                  <header class="bg-token-main-surface-primary"
                          data-fixture-id="right-panel-tab-strip" />
                  <div role="tabpanel"
                       data-app-shell-tab-panel-controller="right"
                       data-fixture-id="right-tabpanel">
                    <div class="bg-token-main-surface-primary"
                         data-fixture-id="file-layout-shell">
                      <div class="monaco-editor bg-token-main-surface-primary"
                           data-fixture-id="editor-surface" />
                      <div class="bg-token-main-surface-primary"
                           data-diff-view="unified"
                           data-fixture-id="diff-surface" />
                      <pre class="bg-token-main-surface-primary"
                           data-fixture-id="code-surface"><code>const answer = 42;</code></pre>
                      <table class="bg-token-main-surface-primary"
                             data-fixture-id="table-surface">
                        <tbody><tr><td>preserve table background</td></tr></tbody>
                      </table>
                      <div class="bg-token-main-surface-primary"
                           data-popcorn-root=""
                           data-fixture-id="popcorn-surface" />
                    </div>

                    <section>
                      <div class="relative rounded-lg bg-token-main-surface-primary"
                           data-fixture-id="markdown-shell">
                        <article class="markdown">
                          <p>Reviewed file details</p>
                        </article>
                      </div>

                      <div class="relative rounded-lg bg-token-main-surface-primary"
                           data-fixture-id="rounded-surface-without-markdown">
                        <article>Not Markdown</article>
                      </div>
                    </section>
                  </div>
                </div>
              </div>
            </aside>

            <aside data-app-shell-focus-area="right-panel">
              <div>
                <div>
                  <div class="bg-token-main-surface-primary"
                       data-fixture-id="right-panel-near-miss">
                    <div>
                      <div role="tabpanel"
                           data-app-shell-tab-panel-controller="right" />
                    </div>
                  </div>
                </div>
              </div>
            </aside>
          </body>
        </html>
        """;
}
