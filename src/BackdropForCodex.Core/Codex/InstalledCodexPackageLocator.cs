using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BackdropForCodex.Core.Codex;

public sealed record InstalledCodexPackage(
    CodexPackageDescriptor Descriptor,
    string PackageFullName,
    string PackageRoot,
    string ExecutableRelativePath)
{
    public string ExecutablePath => Path.GetFullPath(
        Path.Combine(PackageRoot, ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar)));
}

public interface IInstalledCodexPackageLocator
{
    InstalledCodexPackage Locate();
}

/// <summary>
/// Resolves the registered Store package through public Win32 AppModel APIs, then validates its
/// manifest. No PowerShell invocation or WindowsApps directory scan is used.
/// </summary>
public sealed class InstalledCodexPackageLocator : IInstalledCodexPackageLocator
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const string ExpectedPublisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";
    private const string ExpectedExecutable = "app/ChatGPT.exe";

    public InstalledCodexPackage Locate()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            throw new PlatformNotSupportedException("Backdrop for Codex requires Windows 11.");
        }

        var packages = GetRegisteredPackageFullNames(CodexCompatibilityCatalog.OfficialPackageFamilyName);
        var candidates = new List<InstalledCodexPackage>();

        foreach (var packageFullName in packages)
        {
            if (TryReadPackage(packageFullName, out var package))
            {
                candidates.Add(package);
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Descriptor.Version)
            .FirstOrDefault()
            ?? throw new CodexPackageDiscoveryException(
                "The reviewed official OpenAI Codex Store package is not installed.");
    }

    private static bool TryReadPackage(string packageFullName, out InstalledCodexPackage package)
    {
        package = null!;
        var familyName = GetPackageFamilyName(packageFullName);
        if (!string.Equals(
                familyName,
                CodexCompatibilityCatalog.OfficialPackageFamilyName,
                StringComparison.Ordinal))
        {
            return false;
        }

        var packageRoot = GetPackagePath(packageFullName);
        var manifestPath = Path.Combine(packageRoot, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root is null)
            {
                return false;
            }

            var ns = root.Name.Namespace;
            var identity = root.Element(ns + "Identity");
            var application = root
                .Element(ns + "Applications")?
                .Elements(ns + "Application")
                .SingleOrDefault(element =>
                    string.Equals((string?)element.Attribute("Id"), "App", StringComparison.Ordinal));

            var name = (string?)identity?.Attribute("Name");
            var versionValue = (string?)identity?.Attribute("Version");
            var architecture = (string?)identity?.Attribute("ProcessorArchitecture");
            var publisher = (string?)identity?.Attribute("Publisher");
            var applicationId = (string?)application?.Attribute("Id");
            var executable = ((string?)application?.Attribute("Executable"))?.Replace('\\', '/');

            if (name is null ||
                applicationId is null ||
                !string.Equals(name, CodexCompatibilityCatalog.OfficialPackageName, StringComparison.Ordinal) ||
                !Version.TryParse(versionValue, out var version) ||
                !string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(publisher, ExpectedPublisher, StringComparison.Ordinal) ||
                !string.Equals(applicationId, "App", StringComparison.Ordinal) ||
                !string.Equals(executable, ExpectedExecutable, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var descriptor = new CodexPackageDescriptor(
                name,
                familyName,
                version,
                CodexPackageArchitecture.X64,
                applicationId);
            package = new InstalledCodexPackage(
                descriptor,
                packageFullName,
                packageRoot,
                ExpectedExecutable);
            return File.Exists(package.ExecutablePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }

    private static string[] GetRegisteredPackageFullNames(string packageFamilyName)
    {
        uint count = 0;
        uint bufferLength = 0;
        var result = GetPackagesByPackageFamily(
            packageFamilyName,
            ref count,
            IntPtr.Zero,
            ref bufferLength,
            IntPtr.Zero);
        if (result != ErrorInsufficientBuffer || count == 0 || bufferLength == 0)
        {
            return Array.Empty<string>();
        }

        var namesBuffer = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var textBuffer = Marshal.AllocHGlobal(checked((int)bufferLength * sizeof(char)));
        try
        {
            result = GetPackagesByPackageFamily(
                packageFamilyName,
                ref count,
                namesBuffer,
                ref bufferLength,
                textBuffer);
            if (result != ErrorSuccess)
            {
                throw new CodexPackageDiscoveryException(
                    $"Windows AppModel package discovery failed with code {result}.");
            }

            var names = new string[count];
            for (var index = 0; index < count; index++)
            {
                var namePointer = Marshal.ReadIntPtr(namesBuffer, checked(index * IntPtr.Size));
                names[index] = Marshal.PtrToStringUni(namePointer)
                    ?? throw new CodexPackageDiscoveryException(
                        "Windows AppModel returned an invalid package name.");
            }

            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(textBuffer);
            Marshal.FreeHGlobal(namesBuffer);
        }
    }

    private static string GetPackagePath(string packageFullName)
    {
        uint length = 0;
        var result = GetPackagePathByFullName(packageFullName, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new CodexPackageDiscoveryException(
                $"Windows AppModel could not resolve the package path (code {result}).");
        }

        var builder = new StringBuilder(checked((int)length));
        result = GetPackagePathByFullName(packageFullName, ref length, builder);
        if (result != ErrorSuccess)
        {
            throw new CodexPackageDiscoveryException(
                $"Windows AppModel could not resolve the package path (code {result}).");
        }

        return Path.GetFullPath(builder.ToString());
    }

    private static string GetPackageFamilyName(string packageFullName)
    {
        uint length = 0;
        var result = PackageFamilyNameFromFullName(packageFullName, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new CodexPackageDiscoveryException(
                $"Windows AppModel could not resolve the package family (code {result}).");
        }

        var builder = new StringBuilder(checked((int)length));
        result = PackageFamilyNameFromFullName(packageFullName, ref length, builder);
        if (result != ErrorSuccess)
        {
            throw new CodexPackageDiscoveryException(
                $"Windows AppModel could not resolve the package family (code {result}).");
        }

        return builder.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(
        string packageFamilyName,
        ref uint count,
        IntPtr packageFullNames,
        ref uint bufferLength,
        IntPtr buffer);

    [SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "The Win32 AppModel API is a bounded two-call string-buffer contract.")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(
        string packageFullName,
        ref uint pathLength,
        StringBuilder? path);

    [SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "The Win32 AppModel API is a bounded two-call string-buffer contract.")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromFullName(
        string packageFullName,
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);
}

public sealed class CodexPackageDiscoveryException : InvalidOperationException
{
    public CodexPackageDiscoveryException(string message)
        : base(message)
    {
    }
}
