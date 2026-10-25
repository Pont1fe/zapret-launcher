// Связь с Windows: значки, ярлыки, запись в «Установленных приложениях», тёмное меню трея, самообновление
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ZapretLauncher
{
    static class Icons
    {
        public static Bitmap Draw(int size, bool active) { return Draw(size, active ? "on" : "off"); }

        // Щит в скруглённом квадрате (тот же рисунок в build.ps1). Цвет — состояние обхода:
        // on — бирюзовый, pause — жёлтый (VPN), error — красный, off — серый
        public static Bitmap Draw(int size, string state)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float pad = Math.Max(1f, size / 32f);
                RectangleF rect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
                System.Drawing.Color c1, c2;
                if (state == "on") { c1 = System.Drawing.Color.FromArgb(44, 211, 177); c2 = System.Drawing.Color.FromArgb(18, 140, 117); }
                else if (state == "pause") { c1 = System.Drawing.Color.FromArgb(246, 190, 70); c2 = System.Drawing.Color.FromArgb(196, 128, 20); }
                else if (state == "error") { c1 = System.Drawing.Color.FromArgb(240, 98, 98); c2 = System.Drawing.Color.FromArgb(168, 38, 48); }
                else { c1 = System.Drawing.Color.FromArgb(110, 112, 122); c2 = System.Drawing.Color.FromArgb(58, 60, 68); }
                float r = rect.Width * 0.28f;
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
                    path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
                    path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                    path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                    path.CloseFigure();
                    using (System.Drawing.Drawing2D.LinearGradientBrush brush = new System.Drawing.Drawing2D.LinearGradientBrush(rect, c1, c2, 45f)) g.FillPath(brush, path);
                }
                using (Font font = new Font("Segoe MDL2 Assets", size * 0.5f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel))
                using (StringFormat f = new StringFormat())
                {
                    f.Alignment = StringAlignment.Center;
                    f.LineAlignment = StringAlignment.Center;
                    g.DrawString("", font, System.Drawing.Brushes.White, rect, f);
                }
            }
            return bmp;
        }

        public static Icon ToIcon(Bitmap bmp) { return Icon.FromHandle(bmp.GetHicon()); }

        public static ImageSource ToImageSource(Bitmap bmp)
        {
            return Imaging.CreateBitmapSourceFromHIcon(bmp.GetHicon(), System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
    }

    static class Shell
    {
        public static void CreateShortcut(string path)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic lnk = shell.CreateShortcut(path);
            lnk.TargetPath = Paths.Exe;
            lnk.Arguments = "";
            lnk.WorkingDirectory = Paths.Root;
            lnk.IconLocation = Paths.Exe + ",0";
            lnk.Description = "Zapret Launcher — обход блокировок Discord, YouTube и Telegram";
            lnk.Save();
            Marshal.FinalReleaseComObject(lnk);
            Marshal.FinalReleaseComObject(shell);
        }

        // Ярлык ведёт на этот лаунчер (exe или старый ZapretLauncher.ps1 из той же папки)
        public static bool ShortcutPointsHere(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic lnk = shell.CreateShortcut(path);
                string target = (string)lnk.TargetPath + " " + (string)lnk.Arguments;
                Marshal.FinalReleaseComObject(lnk);
                Marshal.FinalReleaseComObject(shell);
                return target.IndexOf(Paths.Root, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // Старые ярлыки PowerShell-версии переводим на exe
        public static void MigrateShortcuts()
        {
            foreach (string path in new[] { Paths.DesktopShortcut, Paths.StartMenuShortcut })
            {
                if (!ShortcutPointsHere(path)) continue;
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic lnk = shell.CreateShortcut(path);
                bool old = ((string)lnk.TargetPath).EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase);
                Marshal.FinalReleaseComObject(lnk);
                Marshal.FinalReleaseComObject(shell);
                if (old) CreateShortcut(path);
            }
        }

        public static void RemoveShortcuts()
        {
            foreach (string path in new[] { Paths.DesktopShortcut, Paths.StartMenuShortcut })
                if (ShortcutPointsHere(path)) File.Delete(path);
        }

        static RegistryKey Hklm() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }

        // Запись создаёт установщик; лаунчер поддерживает в ней актуальную версию и путь
        public static void UpdateUninstallEntry()
        {
            using (RegistryKey k = Hklm().OpenSubKey(Consts.UninstallKey, true))
            {
                if (k == null) return;
                k.SetValue("DisplayVersion", Consts.Version);
                k.SetValue("DisplayIcon", Paths.Exe);
                k.SetValue("InstallLocation", Paths.Root);
                k.SetValue("UninstallString", "\"" + Paths.Exe + "\" --uninstall");
            }
        }

        public static void RemoveUninstallEntry()
        {
            try { Hklm().DeleteSubKeyTree(Consts.UninstallKey, false); } catch { }
        }
    }

    // «Починить Discord»: закрыть, очистить кэш (вход и настройки не трогаем), открыть снова — без прав администратора
    static class DiscordFix
    {
        static readonly string[][] Variants =
        {
            new[] { "Discord", "discord", "Discord" },
            new[] { "DiscordPTB", "discordptb", "Discord PTB" },
            new[] { "DiscordCanary", "discordcanary", "Discord Canary" },
        };
        static readonly string[] CacheDirs = { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" };

        // Возвращает true, если Discord удалось открыть снова
        public static async Task<bool> Run()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            System.Collections.Generic.List<string[]> wasRunning = new System.Collections.Generic.List<string[]>();
            foreach (string[] v in Variants)
            {
                System.Diagnostics.Process[] procs = System.Diagnostics.Process.GetProcessesByName(v[0]);
                if (procs.Length > 0) wasRunning.Add(v);
                foreach (System.Diagnostics.Process p in procs) { try { p.Kill(); } catch { } }
                await Task.Run(() => { foreach (System.Diagnostics.Process p in procs) { try { p.WaitForExit(5000); } catch { } } });
            }
            await Task.Delay(500);   // Discord отпускает файлы кэша не сразу

            long freed = 0;
            foreach (string[] v in Variants)
                foreach (string dir in CacheDirs)
                    freed += await Task.Run(() => DeleteDir(Path.Combine(appData, v[1], dir)));
            Util.Run("ipconfig.exe", "/flushdns");
            Log.Write("Discord: " + (wasRunning.Count > 0 ? "закрыт, " : "") + "кэш очищен (" + (freed / 1048576) + " МБ)");

            bool opened = false;
            foreach (string[] v in wasRunning.Count > 0 ? wasRunning.ToArray() : new[] { Variants[0] })
            {
                string lnk = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\Discord Inc", v[2] + ".lnk");
                if (!File.Exists(lnk)) continue;
                // через explorer, чтобы Discord не запустился с правами администратора
                System.Diagnostics.Process.Start("explorer.exe", "\"" + lnk + "\"");
                opened = true;
            }
            Log.Write(opened ? "Discord открыт заново" : "Ярлык Discord не найден — откройте Discord вручную");
            return opened;
        }

        static long DeleteDir(string dir)
        {
            long size = 0;
            if (!Directory.Exists(dir)) return 0;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { long len = new FileInfo(f).Length; File.Delete(f); size += len; } catch { }
            }
            try { Directory.Delete(dir, true); } catch { }
            return size;
        }
    }

    // Самообновление: последний релиз Pont1fe/zapret-launcher, файл ZapretLauncher.exe
    static class SelfUpdate
    {
        public const string Asset = "ZapretLauncher.exe";

        public static Task<Release> Latest(string repo)
        {
            return Util.LatestRelease(repo, n => n.Equals(Asset, StringComparison.OrdinalIgnoreCase));
        }

        public static void CleanupOld()
        {
            try { if (File.Exists(Paths.Exe + ".old")) File.Delete(Paths.Exe + ".old"); } catch { }
        }

        // Скачивает новый exe и ставит его на место текущего (работающий exe можно переименовать, но не перезаписать)
        public static async Task Apply(Release release)
        {
            string tmp = Paths.Exe + ".new";
            await Util.Download(release, tmp);
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(tmp); }
            catch { File.Delete(tmp); throw new Exception("Скачанный файл обновления повреждён — обновление отменено"); }
            Version expected;
            if (name.Name != "ZapretLauncher" || (Version.TryParse(release.Version, out expected) && name.Version < expected))
            {
                File.Delete(tmp);
                throw new Exception("Файл обновления не похож на лаунчер версии " + release.Version + " — обновление отменено");
            }
            string old = Paths.Exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(Paths.Exe, old);
            try { File.Move(tmp, Paths.Exe); }
            catch { File.Move(old, Paths.Exe); throw; }
            Log.Write("Лаунчер обновлён до версии " + release.Version);
        }
    }

    public class DarkMenuColors : ProfessionalColorTable
    {
        static System.Drawing.Color C(int r, int g, int b) { return System.Drawing.Color.FromArgb(r, g, b); }
        public override System.Drawing.Color MenuItemSelected { get { return C(58, 58, 67); } }
        public override System.Drawing.Color MenuItemBorder { get { return C(58, 58, 67); } }
        public override System.Drawing.Color MenuBorder { get { return C(52, 52, 60); } }
        public override System.Drawing.Color ToolStripDropDownBackground { get { return C(35, 35, 40); } }
        public override System.Drawing.Color ImageMarginGradientBegin { get { return C(35, 35, 40); } }
        public override System.Drawing.Color ImageMarginGradientMiddle { get { return C(35, 35, 40); } }
        public override System.Drawing.Color ImageMarginGradientEnd { get { return C(35, 35, 40); } }
        public override System.Drawing.Color SeparatorDark { get { return C(58, 58, 67); } }
        public override System.Drawing.Color SeparatorLight { get { return C(35, 35, 40); } }
        public override System.Drawing.Color MenuItemSelectedGradientBegin { get { return C(58, 58, 67); } }
        public override System.Drawing.Color MenuItemSelectedGradientEnd { get { return C(58, 58, 67); } }
        public override System.Drawing.Color MenuItemPressedGradientBegin { get { return C(58, 58, 67); } }
        public override System.Drawing.Color MenuItemPressedGradientEnd { get { return C(58, 58, 67); } }
        public override System.Drawing.Color CheckBackground { get { return C(123, 108, 246); } }
        public override System.Drawing.Color CheckSelectedBackground { get { return C(142, 129, 255); } }
        public override System.Drawing.Color CheckPressedBackground { get { return C(142, 129, 255); } }
    }

    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? System.Drawing.Color.FromArgb(237, 237, 240) : System.Drawing.Color.FromArgb(110, 110, 120);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = System.Drawing.Color.FromArgb(200, 200, 208);
            base.OnRenderArrow(e);
        }
    }
}
