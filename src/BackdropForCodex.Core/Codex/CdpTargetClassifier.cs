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

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!profile.AllowedRemotePageHosts.Contains(uri.IdnHost))
        {
            return false;
        }

        return IsReviewedRemoteWorkspacePage(uri);
    }

    private static bool IsReviewedRemoteWorkspacePage(Uri uri)
    {
        if (!TryNormalizeRemotePath(uri.AbsolutePath, out var path) ||
            ContainsNonWorkspacePathSegment(path))
        {
            return false;
        }

        if (string.Equals(uri.IdnHost, "chatgpt.com", StringComparison.OrdinalIgnoreCase))
        {
            return IsWithinRouteBoundary(path, "/codex");
        }

        if (string.Equals(uri.IdnHost, "codex.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(path, "/", StringComparison.Ordinal) ||
                   IsWithinRouteBoundary(path, "/codex");
        }

        return false;
    }

    private static bool TryNormalizeRemotePath(string path, out string normalizedPath)
    {
        normalizedPath = path;

        try
        {
            for (var pass = 0; pass < 4; pass++)
            {
                var decodedPath = Uri.UnescapeDataString(normalizedPath);
                if (string.Equals(decodedPath, normalizedPath, StringComparison.Ordinal))
                {
                    return !normalizedPath.Contains('\\');
                }

                normalizedPath = decodedPath;
            }

            return string.Equals(
                       Uri.UnescapeDataString(normalizedPath),
                       normalizedPath,
                       StringComparison.Ordinal) &&
                   !normalizedPath.Contains('\\');
        }
        catch (UriFormatException)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }

    private static bool IsWithinRouteBoundary(string path, string boundary) =>
        string.Equals(path, boundary, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith($"{boundary}/", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsNonWorkspacePathSegment(string path)
    {
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal) ||
                string.Equals(segment, "..", StringComparison.Ordinal) ||
                string.Equals(segment, "auth", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "login", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "oauth", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReviewedPackagedFilePage(
        Uri uri,
        CodexCompatibilityProfile profile)
    {
        if (profile.PackageRoot is null || uri.IsUnc)
        {
            return false;
        }

        try
        {
            var candidatePath = Path.GetFullPath(uri.LocalPath);
            var expectedPath = Path.GetFullPath(
                Path.Combine(profile.PackageRoot, "app", "index.html"));
            return string.Equals(
                candidatePath,
                expectedPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UriFormatException)
        {
            return false;
        }
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
