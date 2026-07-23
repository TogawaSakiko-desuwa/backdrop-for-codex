namespace BackdropForCodex.Core.Codex;

public static class CdpTargetClassifier
{
    public static CdpTargetClassification Classify(
        CdpTargetDescriptor target,
        CodexCompatibilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(profile);

        if (string.Equals(target.Type, "service_worker", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Type, "worker", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target.Type, "shared_worker", StringComparison.OrdinalIgnoreCase))
        {
            return CdpTargetClassification.Worker;
        }

        if (!string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase))
        {
            return CdpTargetClassification.Unsupported;
        }

        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri))
        {
            return CdpTargetClassification.Unsupported;
        }

        if (string.Equals(uri.Scheme, "devtools", StringComparison.OrdinalIgnoreCase))
        {
            return CdpTargetClassification.DeveloperTools;
        }

        if (string.Equals(uri.Scheme, "chrome-extension", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, "extension", StringComparison.OrdinalIgnoreCase))
        {
            return CdpTargetClassification.Extension;
        }

        if (IsAuthenticationPage(uri))
        {
            return CdpTargetClassification.AuthenticationPage;
        }

        if (profile.IsKnownTitle(target.Title) && IsReviewedCodexPage(uri, profile))
        {
            return CdpTargetClassification.CodexPage;
        }

        return CdpTargetClassification.OtherPage;
    }

    private static bool IsReviewedCodexPage(Uri uri, CodexCompatibilityProfile profile)
    {
        if (string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return IsReviewedPackagedFilePage(uri, profile);
        }

        if (string.Equals(uri.Scheme, "app", StringComparison.OrdinalIgnoreCase))
        {
            return (string.Equals(uri.Host, "codex", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(uri.Host, "-", StringComparison.Ordinal)) &&
                   IsMainApplicationPath(uri.AbsolutePath);
        }

        if (string.Equals(uri.Scheme, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(uri.Host, "desktop", StringComparison.OrdinalIgnoreCase) &&
                   IsMainApplicationPath(uri.AbsolutePath);
        }

        if (string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return IsMainApplicationPath(uri.AbsolutePath);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!profile.AllowedRemotePageHosts.Contains(uri.IdnHost))
        {
            return false;
        }

        return !string.Equals(uri.IdnHost, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
               uri.AbsolutePath.StartsWith("/codex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReviewedPackagedFilePage(
        Uri uri,
        CodexCompatibilityProfile profile)
    {
        var publisherIdSeparator = profile.PackageFamilyName.LastIndexOf('_');
        if (publisherIdSeparator < 0 || publisherIdSeparator == profile.PackageFamilyName.Length - 1)
        {
            return false;
        }

        var publisherId = profile.PackageFamilyName[(publisherIdSeparator + 1)..];
        var packageDirectory =
            $"{profile.PackageName}_{profile.PackageVersion}_x64__{publisherId}";
        var normalizedPath = Uri.UnescapeDataString(uri.AbsolutePath).Replace('\\', '/');
        var expectedMarker = $"/Program Files/WindowsApps/{packageDirectory}/app/";
        return normalizedPath.Contains(expectedMarker, StringComparison.OrdinalIgnoreCase) &&
               IsMainApplicationPath(normalizedPath[(normalizedPath.IndexOf(
                   expectedMarker,
                   StringComparison.OrdinalIgnoreCase) + expectedMarker.Length - 1)..]);
    }

    private static bool IsMainApplicationPath(string path)
    {
        var normalized = path.TrimEnd('/');
        if (normalized.Length == 0)
        {
            return true;
        }

        return !normalized.Contains("/auth", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("/login", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("/oauth", StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(normalized, "/app", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "/index.html", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAuthenticationPage(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(uri.IdnHost, "auth.openai.com", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.IdnHost, "auth0.openai.com", StringComparison.OrdinalIgnoreCase));
}
