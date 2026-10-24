using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Weft.Cli.Configuration;

/// <summary>
/// The CLI's private directory and files: owner-only on every platform. Unix creates them with modes 0700 and
/// 0600; Windows creates them with an explicit access list granting only the current user. An existing directory
/// or file that other users can reach is refused, never changed.
/// </summary>
internal static class OwnerOnlyFiles
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OtherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Creates the directory owner-only when missing, and refuses an existing one others can reach.</summary>
    internal static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
                directory.Create(WindowsDirectorySecurity());
            else
                RequireWindowsPrivate(directory.GetAccessControl(), path, "configuration directory");
            return;
        }
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path, DirectoryMode);
            File.SetUnixFileMode(path, DirectoryMode);
        }
        RequireUnixPrivate(path, "configuration directory");
    }

    /// <summary>Refuses a file other users can reach. A missing file is accepted.</summary>
    internal static void RequirePrivateFile(string path)
    {
        if (!File.Exists(path))
            return;
        if (OperatingSystem.IsWindows())
            RequireWindowsPrivate(new FileInfo(path).GetAccessControl(), path, "configuration file");
        else
            RequireUnixPrivate(path, "configuration file");
    }

    /// <summary>Atomically replaces the file with owner-only content.</summary>
    internal static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The file has no directory.");
        EnsureDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = Create(temporary))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static FileStream Create(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileInfo(path).Create(System.IO.FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096,
                FileOptions.None, WindowsFileSecurity());
        return new FileStream(path, new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = FileMode
        });
    }

    [UnsupportedOSPlatform("windows")]
    private static void RequireUnixPrivate(string path, string kind)
    {
        if ((File.GetUnixFileMode(path) & OtherAccess) != 0)
            throw CliException.Usage($"The Weft {kind} {path} is accessible to other users. Restrict it to its owner (chmod go-rwx).");
    }

    // The account itself, the operating system and administrators may hold access; anyone else may not.
    [SupportedOSPlatform("windows")]
    private static void RequireWindowsPrivate(FileSystemSecurity security, string path, string kind)
    {
        var user = CurrentUser();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier holder &&
                holder != user && holder != system && holder != administrators)
                throw CliException.Usage($"The Weft {kind} {path} is accessible to other users. Restrict it to your account.");
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity WindowsDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity WindowsFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The current Windows user has no security identifier.");
    }
}
