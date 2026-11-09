// Помощник для меню на значке в панели задач: работает без прав администратора, поэтому пункты меню не вызывают UAC.
// Кладёт команду в %ProgramData%\ZapretLauncher\command.txt — работающий лаунчер её подхватит. Если лаунчер не запущен,
// запускает его через задачу автозапуска (без UAC), а если её нет — обычным способом.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

[assembly: AssemblyTitle("Zapret Launcher command helper")]
[assembly: AssemblyProduct("Zapret Launcher")]

static class Cmd
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 0) return 1;
        string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        // в папку лаунчера без прав администратора писать нельзя — команда идёт через общую папку ProgramData
        // (лаунчер может работать от другой учётной записи, и её профиль ему не виден)
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZapretLauncher");
        string file = Path.Combine(dir, "command.txt"), tmp = file + "." + Environment.UserName + ".tmp";   // свой временный файл у каждого пользователя
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, string.Join(" ", args));
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }
        catch { return 2; }

        if (Process.GetProcessesByName("ZapretLauncher").Length > 0) return 0;
        if (Run("schtasks.exe", "/Run /TN ZapretLauncher") == 0) return 0;
        try { Process.Start(new ProcessStartInfo(Path.Combine(root, "ZapretLauncher.exe")) { UseShellExecute = true }); } catch { }
        return 0;
    }

    static int Run(string exe, string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
            using (Process p = Process.Start(psi)) { p.WaitForExit(10000); return p.HasExited ? p.ExitCode : -1; }
        }
        catch { return -1; }
    }
}
