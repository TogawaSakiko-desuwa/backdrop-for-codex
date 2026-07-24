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
        string applicationId,
        string? packageFullName = null,
        string? packageRoot = null)
    {
        Name = RequireValue(name, nameof(name));
        FamilyName = RequireValue(familyName, nameof(familyName));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Architecture = architecture;
        ApplicationId = RequireValue(applicationId, nameof(applicationId));
        PackageFullName = packageFullName is null
            ? null
            : RequireValue(packageFullName, nameof(packageFullName));
        PackageRoot = packageRoot is null
            ? null
            : NormalizeAbsolutePath(packageRoot, nameof(packageRoot));
    }

    public string Name { get; }

    public string FamilyName { get; }

    public Version Version { get; }

    public CodexPackageArchitecture Architecture { get; }

    public string ApplicationId { get; }

    /// <summary>
    /// The installed package full name, when package discovery supplied it. A descriptor without
    /// this observed value cannot pass compatibility evaluation; it is retained as nullable only
    /// so discovery failures can be represented without inventing identity data.
    /// </summary>
    public string? PackageFullName { get; }

    /// <summary>
    /// The package installation root returned by Windows AppModel discovery. Packaged file targets
    /// are injectable only when they resolve to the exact reviewed entry point below this root.
    /// </summary>
    public string? PackageRoot { get; }

    public string AppUserModelId => string.Create(
        CultureInfo.InvariantCulture,
        $"{FamilyName}!{ApplicationId}");

    private static string RequireValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static string NormalizeAbsolutePath(string value, string parameterName)
    {
        var path = RequireValue(value, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "The package root must be an absolute path.",
                parameterName);
        }

        return Path.GetFullPath(path);
    }
}
