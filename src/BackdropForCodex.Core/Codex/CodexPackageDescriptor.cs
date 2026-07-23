using System.Globalization;

namespace BackdropForCodex.Core.Codex;

/// <summary>
/// Architectures reported by an MSIX package manifest.
/// </summary>
public enum CodexPackageArchitecture
{
    Unknown = 0,
    X86,
    X64,
    Arm64,
    Neutral,
}

/// <summary>
/// The package identity fields used by the compatibility gate. The descriptor deliberately
/// contains no "best effort" defaults: every value must come from the installed package.
/// </summary>
public sealed record CodexPackageDescriptor
{
    public CodexPackageDescriptor(
        string name,
        string familyName,
        Version version,
        CodexPackageArchitecture architecture,
        string applicationId)
    {
        Name = RequireValue(name, nameof(name));
        FamilyName = RequireValue(familyName, nameof(familyName));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Architecture = architecture;
        ApplicationId = RequireValue(applicationId, nameof(applicationId));
    }

    public string Name { get; }

    public string FamilyName { get; }

    public Version Version { get; }

    public CodexPackageArchitecture Architecture { get; }

    public string ApplicationId { get; }

    public string AppUserModelId => string.Create(
        CultureInfo.InvariantCulture,
        $"{FamilyName}!{ApplicationId}");

    private static string RequireValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
