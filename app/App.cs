// Точка входа: один экземпляр (повторный запуск открывает окно), --autostart, --uninstall, --restarted
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

[assembly: AssemblyTitle("Zapret Launcher")]
[assembly: AssemblyProduct("Zapret Launcher")]
[assembly: AssemblyDescription("Запуск и автообновление zapret-discord-youtube")]
[assembly: AssemblyVersion(ZapretLauncher.AppVersion.Value + ".0")]
[assembly: AssemblyFileVersion(ZapretLauncher.AppVersion.Value + ".0")]

namespace ZapretLauncher
{
    static class AppVersion
    {
        public const string Value = "3.3.0";
    }

    static class App
    {
        const string MutexName = @"Global\ZapretLauncher";
        const string ShowEventName = @"Global\ZapretLauncherShow";
        static Mutex mutex;
        static bool owned;

        [STAThread]
        static int Main(string[] args)
        {
            bool autostart = args.Contains("--autostart");
            bool uninstall = args.Contains("--uninstall");
            bool restarted = args.Contains("--restarted");
            SelfUpdate.CleanupOld();
            Config cfg = Config.Load();
            I18n.Init(cfg.Language);

            // приложение, общие стили и тема нужны до первого окна и диалога
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("styles.xaml"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Load(s));
            Theme.Apply(cfg.Theme, cfg.Transparent);

            if (uninstall) return Uninstaller.Interactive();

            bool created;
            mutex = new Mutex(true, MutexName, out created);
            if (!created && restarted)
            {
                // после самообновления старый экземпляр ещё может завершаться
                try { created = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { created = true; }
            }
            if (!created)
            {
                EventWaitHandle show;
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out show)) show.Set();
                else Dialog.Show(null, I18n.T("Лаунчер уже запущен — ищите значок в трее."));
                return 0;
            }
            owned = true;

            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("Непредвиденная ошибка: " + e.Exception);
                Dialog.Show(null, I18n.T(e.Exception.Message), "Zapret Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            Launcher launcher = new Launcher(autostart, cfg);

            EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            ThreadPool.RegisterWaitForSingleObject(showEvent, delegate
            {
                app.Dispatcher.BeginInvoke(new Action(launcher.ShowWindow));
            }, null, -1, false);

            app.Run();
            ReleaseMutex();
            return 0;
        }

        public static void ReleaseMutex()
        {
            if (!owned) return;
            owned = false;
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
        }
    }

    static class Uninstaller
    {
        // Вызов из «Установленных приложений» Windows
        public static int Interactive()
        {
            if (Dialog.Show(null, I18n.T("Удалить Zapret Launcher?\n\nОбход и Telegram-прокси будут остановлены. Удалятся автозапуск, драйвер WinDivert, записи Telegram в hosts, ярлыки и папка ") +
                Paths.Root + I18n.T(" со всеми файлами."), I18n.T("Удаление Zapret Launcher"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return 1;
            CloseOtherInstances();
            try
            {
                Remove(true).GetAwaiter().GetResult();
                Dialog.Show(null, I18n.T("Zapret Launcher удалён."));
                return 0;
            }
            catch (Exception e)
            {
                Dialog.Show(null, I18n.T("Не удалось удалить полностью: ") + I18n.T(e.Message), "Zapret Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        static void CloseOtherInstances()
        {
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.Exe)))
                if (p.Id != Process.GetCurrentProcess().Id) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            // и PowerShell-версию лаунчера из той же папки
            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='powershell.exe'"))
                    foreach (ManagementObject o in q.Get())
                    {
                        string cmd = o["CommandLine"] as string;
                        if (cmd != null && cmd.IndexOf(Path.Combine(Paths.Root, "ZapretLauncher.ps1"), StringComparison.OrdinalIgnoreCase) >= 0)
                            try { Process.GetProcessById(Convert.ToInt32(o["ProcessId"])).Kill(); } catch { }
                    }
            }
            catch { }
        }

        public static async Task Remove(bool deleteFiles)
        {
            await Zapret.Stop(true);
            await Telegram.StopProxy();
            if (Zapret.Service() != null) await Zapret.RemoveService();
            Autostart.Set(false);
            try { Telegram.SetHosts(false); } catch (Exception e) { Log.Write(e.Message); }
            Shell.RemoveShortcuts();
            Shell.RemoveUninstallEntry();
            Log.Write("Автозапуск, службы, ярлыки и записи в hosts удалены");
            // папку удаляем, только если это действительно папка лаунчера, а не корень диска
            if (deleteFiles && Paths.Root.Length > 3 && File.Exists(Paths.Exe))
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 3 /nobreak >nul & rmdir /s /q \"" + Paths.Root + "\"");
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetTempPath();
                Process.Start(psi);
            }
        }
    }
}
