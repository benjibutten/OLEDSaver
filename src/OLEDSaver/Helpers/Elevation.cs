using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OLEDSaver.Helpers;

/// <summary>
/// Whether this process runs as administrator, and whether a file sits where only
/// administrators can change it.
/// </summary>
public static class Elevation
{
    // Generic rights are normally mapped to specific ones before they are stored,
    // but an entry that kept them still grants everything they stand for.
    private const FileSystemRights GenericWriteOrAll = (FileSystemRights)0x50000000;

    // Rights that let a principal replace, delete, rename or re-permission an entry.
    internal const FileSystemRights ModifyingRights =
        FileSystemRights.WriteData
        | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    // Rights on a folder further up that let a principal move the install folder
    // away, or take the folder over and grant itself the rest. Write access alone
    // does not: it creates new entries, and cannot touch existing ones.
    internal const FileSystemRights AncestorTakeoverRights =
        FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier[] TrustedPrincipals =
    {
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464") // NT SERVICE\TrustedInstaller
    };

    public static bool IsElevated { get; } = ReadIsElevated();

    private static bool ReadIsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// True when nobody but administrators, SYSTEM and TrustedInstaller can replace
    /// <paramref name="filePath"/>: not through the file itself, not through its
    /// folder, not by recreating that folder once it is gone, and not by moving it or
    /// a folder above it out of the way. False for a file on anything but a local
    /// fixed drive, reached through a link, or with any of this unreadable.
    /// </summary>
    public static bool IsProtectedFromNonAdministrators(string filePath)
    {
        try
        {
            var file = new FileInfo(filePath);
            DirectoryInfo? folder = file.Directory;
            if (!file.Exists || folder is null || !IsOnLocalFixedDrive(file.FullName))
                return false;

            // Every entry on the way is checked as it stands on disk. A junction or a
            // symbolic link would have the permissions read from its target while the
            // folders above that target went unchecked.
            if (!IsProtectedEntry(file, ModifyingRights) || !IsProtectedEntry(folder, ModifyingRights))
                return false;

            // The parent must not let anyone else create entries either, or a folder
            // deleted with the startup task still pointing into it could be recreated
            // by anyone.
            DirectoryInfo? parent = folder.Parent;
            if (parent is not null && !IsProtectedEntry(parent, ModifyingRights))
                return false;

            for (DirectoryInfo? ancestor = parent?.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (!IsProtectedEntry(ancestor, AncestorTakeoverRights))
                    return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsProtectedEntry(FileSystemInfo entry, FileSystemRights rights)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;

        FileSystemSecurity security = entry switch
        {
            FileInfo file => file.GetAccessControl(),
            DirectoryInfo directory => directory.GetAccessControl(),
            _ => throw new InvalidOperationException()
        };

        return IsProtected(security, rights);
    }

    // On a network share "Administrators" means the server's administrators, and the
    // permissions that count are the server's.
    private static bool IsOnLocalFixedDrive(string fullPath)
    {
        string? root = Path.GetPathRoot(fullPath);
        return root is { Length: > 0 }
            && !root.StartsWith(@"\\", StringComparison.Ordinal)
            && new DriveInfo(root).DriveType == DriveType.Fixed;
    }

    /// <summary>
    /// True when the owner is trusted and no untrusted principal is allowed any of
    /// <paramref name="rights"/> on this entry. Entries that only pass rights on to
    /// children are ignored, since they do not apply here.
    /// </summary>
    internal static bool IsProtected(FileSystemSecurity security, FileSystemRights rights)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
            return false;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;

            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                continue;

            if ((rule.FileSystemRights & (rights | GenericWriteOrAll)) == 0)
                continue;

            if (rule.IdentityReference is not SecurityIdentifier sid || !IsTrusted(sid))
                return false;
        }

        return true;
    }

    private static bool IsTrusted(SecurityIdentifier sid) => TrustedPrincipals.Contains(sid);
}
