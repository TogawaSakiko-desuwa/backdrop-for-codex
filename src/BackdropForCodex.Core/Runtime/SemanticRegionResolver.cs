using System.Collections.ObjectModel;
using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.Core.Runtime;

/// <summary>
/// Selector-free output of a reviewed, versioned page probe. These tokens are runtime adapter
/// contracts and are never persisted in user settings.
/// </summary>
public sealed record SemanticRegionEvidence
{
    public SemanticRegionEvidence(
        string probePackageId,
        string routeFeature,
        IEnumerable<string>? domFeatures = null)
    {
        ProbePackageId = SemanticRegionToken.Normalize(probePackageId, nameof(probePackageId));
        RouteFeature = SemanticRegionToken.Normalize(routeFeature, nameof(routeFeature));
        DomFeatures = new ReadOnlySet<string>(
            new HashSet<string>(
                (domFeatures ?? [])
                    .Select(feature =>
                        SemanticRegionToken.Normalize(feature, nameof(domFeatures))),
                StringComparer.Ordinal));
    }

    public string ProbePackageId { get; }

    public string RouteFeature { get; }

    public IReadOnlySet<string> DomFeatures { get; }
}

public sealed record SemanticRegionRule
{
    public SemanticRegionRule(
        string probePackageId,
        string routeFeature,
        SemanticRegion region,
        IEnumerable<string>? requiredDomFeatures = null)
    {
        if (!Enum.IsDefined(region))
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        ProbePackageId = SemanticRegionToken.Normalize(probePackageId, nameof(probePackageId));
        RouteFeature = SemanticRegionToken.Normalize(routeFeature, nameof(routeFeature));
        Region = region;
        RequiredDomFeatures = new ReadOnlySet<string>(
            new HashSet<string>(
                (requiredDomFeatures ?? [])
                    .Select(feature =>
                        SemanticRegionToken.Normalize(
                            feature,
                            nameof(requiredDomFeatures))),
                StringComparer.Ordinal));
    }

    public string ProbePackageId { get; }

    public string RouteFeature { get; }

    public SemanticRegion Region { get; }

    public IReadOnlySet<string> RequiredDomFeatures { get; }
}

public interface ISemanticRegionResolver
{
    SemanticRegion Resolve(SemanticRegionEvidence evidence);
}

/// <summary>
/// Resolves only reviewed feature tokens. Unknown or ambiguous observations always fall back to
/// Global, so DOM drift cannot select a more privileged or more specific user binding.
/// </summary>
public sealed class ReviewedSemanticRegionResolver : ISemanticRegionResolver
{
    private readonly IReadOnlyList<SemanticRegionRule> _rules;

    public ReviewedSemanticRegionResolver(IEnumerable<SemanticRegionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = Array.AsReadOnly(rules.ToArray());
    }

    public SemanticRegion Resolve(SemanticRegionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var matches = _rules
            .Where(rule =>
                string.Equals(
                    rule.ProbePackageId,
                    evidence.ProbePackageId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    rule.RouteFeature,
                    evidence.RouteFeature,
                    StringComparison.Ordinal) &&
                rule.RequiredDomFeatures.IsSubsetOf(evidence.DomFeatures))
            .Select(rule => rule.Region)
            .Distinct()
            .Take(2)
            .ToArray();

        return matches.Length == 1
            ? matches[0]
            : SemanticRegion.Global;
    }
}

/// <summary>
/// The production policy for 1.3. Region-specific observation is deliberately not enabled yet.
/// </summary>
public sealed class GlobalSemanticRegionResolver : ISemanticRegionResolver
{
    public static GlobalSemanticRegionResolver Instance { get; } = new();

    private GlobalSemanticRegionResolver()
    {
    }

    public SemanticRegion Resolve(SemanticRegionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return SemanticRegion.Global;
    }
}

internal static class SemanticRegionToken
{
    private const int MaximumLength = 64;

    internal static string Normalize(string token, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token, parameterName);
        var normalized = token.Trim().ToLowerInvariant();
        if (normalized.Length > MaximumLength ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException(
                "Semantic region feature tokens must contain only ASCII letters, digits, '.', '_' or '-'.",
                parameterName);
        }

        return normalized;
    }
}
