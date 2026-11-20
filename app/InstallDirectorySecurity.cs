using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ZapretLauncher
{
    internal static class InstallDirectorySecurity
    {
        static readonly SecurityIdentifier Administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        static readonly SecurityIdentifier SystemAccount = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        static bool Trusted(IdentityReference sid)
        {
            return sid.Equals(Administrators) || sid.Equals(SystemAccount) || sid.Value == TrustedInstaller;
        }

        internal static bool OthersCanWrite(FileSystemSecurity acl)
        {
            return acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
                rule.AccessControlType == AccessControlType.Allow && !Trusted(rule.IdentityReference) &&
                (rule.FileSystemRights & WriteRights) != 0);
        }

        internal static bool NeedsLock(FileSystemSecurity acl)
        {
            return !Trusted(acl.GetOwner(typeof(SecurityIdentifier))) || OthersCanWrite(acl);
        }

        static bool LauncherEntry(string path)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            return name.StartsWith("zapretlauncher") || name.StartsWith("zapret.") || name.StartsWith("vpn-download") ||
                name.StartsWith("launcher.") || name.StartsWith("telegram-hosts.") || name.StartsWith("hosts.backup") ||
                new[] { "zapret", "vpn", "tgproxy", "temp", "history.txt", "wallpaper.img", "start.bat", "crash.txt", "crash.prev.txt" }.Contains(name);
        }

        internal static void SecureInstallation(string root)
        {
            root = Path.GetFullPath(root).TrimEnd('\\');
            if (root.Length <= Path.GetPathRoot(root + "\\").TrimEnd('\\').Length)
                throw new IOException("Выберите отдельную папку для Zapret Launcher.");
            for (DirectoryInfo parent = new DirectoryInfo(root); parent != null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("В пути установки есть ссылка: " + parent.FullName);
            string[] entries = Directory.GetFileSystemEntries(root);
            if (NeedsLock(Directory.GetAccessControl(root)) && entries.Any(path => !LauncherEntry(path)))
                throw new IOException("Для защиты файлов поместите Zapret Launcher в отдельную папку.");
            Secure(root, false);
            foreach (string entry in entries.Where(LauncherEntry)) Secure(entry, true);
        }

        static void Secure(string path, bool recursive)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Папка установки и её файлы не должны быть ссылками: " + path);
            bool directory = (attributes & FileAttributes.Directory) != 0;
            FileSystemSecurity current = directory ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
            if (NeedsLock(current))
            {
                FileSystemSecurity secured = directory ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
                var inherit = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
                secured.SetAccessRuleProtection(true, false);
                secured.SetOwner(Administrators);
                secured.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                secured.AddAccessRule(new FileSystemAccessRule(SystemAccount, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                secured.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                if (directory) Directory.SetAccessControl(path, (DirectorySecurity)secured);
                else File.SetAccessControl(path, (FileSecurity)secured);
                current = directory ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
                if (NeedsLock(current)) throw new IOException("Не удалось защитить файл или папку установки: " + path);
            }
            if (directory && recursive)
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                    Secure(child, true);
        }
    }
}
