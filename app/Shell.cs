// Связь с Windows: значки, ярлыки, запись в «Установленных приложениях», тёмное меню трея, самообновление
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
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

        // Логотип: белый щит с буквой Z в скруглённом квадрате (тот же рисунок в build.ps1).
        // Фигура рисуется линиями и выравнивается по своим границам — ровно по центру при любом размере.
        // Цвет — состояние обхода: on — бирюзовый, pause — жёлтый (VPN), error — красный, off — серый; vpn — синяя метка в углу
        public static Bitmap Draw(int size, string state, bool vpn = false)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float pad = Math.Max(1f, size / 32f);
                RectangleF rect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
                System.Drawing.Color c1, c2;
                if (state == "on") { c1 = System.Drawing.Color.FromArgb(44, 211, 177); c2 = System.Drawing.Color.FromArgb(18, 140, 117); }
                else if (state == "pause") { c1 = System.Drawing.Color.FromArgb(246, 190, 70); c2 = System.Drawing.Color.FromArgb(196, 128, 20); }
                else if (state == "error") { c1 = System.Drawing.Color.FromArgb(240, 98, 98); c2 = System.Drawing.Color.FromArgb(168, 38, 48); }
                else { c1 = System.Drawing.Color.FromArgb(110, 112, 122); c2 = System.Drawing.Color.FromArgb(58, 60, 68); }
                System.Drawing.Color mid = System.Drawing.Color.FromArgb((c1.R + c2.R) / 2, (c1.G + c2.G) / 2, (c1.B + c2.B) / 2);
                using (GraphicsPath bg = RoundRect(rect, rect.Width * 0.28f))
                using (System.Drawing.Drawing2D.LinearGradientBrush brush = new System.Drawing.Drawing2D.LinearGradientBrush(rect, c1, c2, 45f))
                    g.FillPath(brush, bg);

                using (GraphicsPath shield = Shield())
                using (GraphicsPath z = new GraphicsPath())
                {
                    z.AddLines(new[] { new PointF(34, 32), new PointF(66, 32), new PointF(34, 68), new PointF(66, 68) });
                    // щит занимает ~56% квадрата (на маленьких размерах — больше, иначе теряется в трее)
                    RectangleF b = shield.GetBounds();
                    float k = size * (size <= 24 ? 0.66f : 0.56f) / Math.Max(b.Width, b.Height);
                    using (System.Drawing.Drawing2D.Matrix m = new System.Drawing.Drawing2D.Matrix())
                    {
                        m.Translate(size / 2f, size / 2f);
                        m.Scale(k, k);
                        m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
                        shield.Transform(m);
                        z.Transform(m);
                    }
                    g.FillPath(System.Drawing.Brushes.White, shield);
                    using (System.Drawing.Pen pen = new System.Drawing.Pen(mid, Math.Max(1.3f, size * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawPath(pen, z);
                }

                if (vpn)
                {
                    float d = size * 0.42f, x = size - d - pad * 0.5f, y = size - d - pad * 0.5f;
                    using (System.Drawing.SolidBrush blue = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(59, 130, 246)))
                    using (System.Drawing.Pen ring = new System.Drawing.Pen(System.Drawing.Color.White, Math.Max(1f, size * 0.06f)))
                    {
                        g.FillEllipse(blue, x, y, d, d);
                        g.DrawEllipse(ring, x, y, d, d);
                    }
                }
            }
            return bmp;
        }

        static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
            p.AddArc(r.Right - radius * 2, r.Y, radius * 2, radius * 2, 270, 90);
            p.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            p.AddArc(r.X, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Контур щита в координатах 0..100
        static GraphicsPath Shield()
        {
            GraphicsPath p = new GraphicsPath();
            p.AddBezier(50, 8, 64, 16, 76, 18, 88, 18);
            p.AddLine(88, 18, 88, 46);
            p.AddBezier(88, 46, 88, 70, 72, 86, 50, 94);
            p.AddBezier(50, 94, 28, 86, 12, 70, 12, 46);
            p.AddLine(12, 46, 12, 18);
            p.AddBezier(12, 18, 24, 18, 36, 16, 50, 8);
            p.CloseFigure();
            return p;
        }

        public static Icon ToIcon(Bitmap bmp) { return Icon.FromHandle(bmp.GetHicon()); }

        // Метка состояния в углу значка на панели задач: цветной кружок с белой обводкой
        public static Bitmap Badge(System.Drawing.Color color)
        {
            Bitmap bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            using (System.Drawing.SolidBrush fill = new System.Drawing.SolidBrush(color))
            using (System.Drawing.Pen ring = new System.Drawing.Pen(System.Drawing.Color.White, 4))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(fill, 3, 3, 26, 26);
                g.DrawEllipse(ring, 3, 3, 26, 26);
            }
            return bmp;
        }

        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        // После обновления: Windows хранит значки программ в кэше и показывает старую картинку — просим перечитать
        public static void RefreshShellIcons()
        {
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("ie4uinit.exe", "-show") { UseShellExecute = false, CreateNoWindow = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }

        [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] static extern uint GetDpiForSystem();

        // Размер значка в трее при текущем масштабе экрана (16 при 100%, 20 при 125%, 24 при 150%…):
        // рисуем ровно в нём, чтобы Windows не ужимала картинку и щит оставался чётким
        public static int TraySize()
        {
            try { return Math.Max(16, GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem())); }
            catch { return Math.Max(16, SystemInformation.SmallIconSize.Width); }
        }

        // Значок окна из нескольких размеров (как .ico): Windows берёт подходящий для панели задач и Alt+Tab, а не растягивает один
        public static ImageSource WindowIcon(string state)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            byte[][] frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (Bitmap bmp = Draw(sizes[i], state))
                using (MemoryStream ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); frames[i] = ms.ToArray(); }
            MemoryStream ico = new MemoryStream();
            using (BinaryWriter w = new BinaryWriter(ico, System.Text.Encoding.ASCII, true))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] % 256)); w.Write((byte)(sizes[i] % 256)); w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32); w.Write(frames[i].Length); w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (byte[] f in frames) w.Write(f);
            }
            ico.Position = 0;
            return BitmapFrame.Create(ico, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

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

    // Меню на значке в панели задач (правый клик). Пункты запускают ZapretLauncherCmd.exe — он без прав администратора,
    // поэтому UAC не появляется; команда приходит лаунчеру через command.txt
    static class TaskbarMenu
    {
        public static readonly string Helper = Path.Combine(Paths.Root, "ZapretLauncherCmd.exe");
        public static readonly string CommandFile = Path.Combine(Paths.Root, "command.txt");

        // Помощник лежит внутри лаунчера; кладём его рядом, если его нет или он от другой версии
        public static void EnsureHelper()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZapretLauncherCmd.exe"))
            {
                if (s == null) return;
                byte[] bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                if (File.Exists(Helper) && new FileInfo(Helper).Length == bytes.Length && File.ReadAllBytes(Helper).SequenceEqual(bytes)) return;
                File.WriteAllBytes(Helper, bytes);
            }
        }

        public static void Update(System.Collections.Generic.IEnumerable<string> favorites)
        {
            System.Windows.Shell.JumpList list = new System.Windows.Shell.JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
            list.JumpItems.Add(Item("Включить обход", "start", "Запустить обход с выбранной стратегией"));
            list.JumpItems.Add(Item("Выключить обход", "stop", "Остановить обход"));
            list.JumpItems.Add(Item("Подобрать стратегию", "pick", "Проверить все стратегии и выбрать лучшую"));
            list.JumpItems.Add(Item("Быстрый подбор по избранному", "pickfav", "Проверить только избранные стратегии"));
            list.JumpItems.Add(Item("Починить Discord", "fixdiscord", "Закрыть Discord, очистить его кэш и открыть заново"));
            foreach (string f in favorites)
            {
                System.Windows.Shell.JumpTask t = Item(I18n.T(Zapret.Title(f)), "strategy " + f, f);
                t.CustomCategory = I18n.T("Избранные стратегии");
                list.JumpItems.Add(t);
            }
            System.Windows.Shell.JumpList.SetJumpList(System.Windows.Application.Current, list);
            list.Apply();
        }

        static System.Windows.Shell.JumpTask Item(string title, string command, string description)
        {
            return new System.Windows.Shell.JumpTask
            {
                Title = I18n.T(title), Description = I18n.T(description), ApplicationPath = Helper, Arguments = "\"" + command + "\"",
                IconResourcePath = Paths.Exe, IconResourceIndex = 0, WorkingDirectory = Paths.Root
            };
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

        // Копия прошлой версии лаунчера — для отката одной кнопкой
        public static readonly string PrevFile = Path.Combine(Paths.Root, "ZapretLauncher.prev.exe");

        // Скачивает новый exe и ставит его на место текущего (работающий exe можно переименовать, но не перезаписать)
        public static async Task Apply(Release release)
        {
            string tmp = Paths.Exe + ".new";
            await Util.Download(release, tmp);
            Verify(tmp, release.Version, false);
            try { File.Copy(Paths.Exe, PrevFile, true); } catch { }   // текущая версия станет «прошлой»
            Replace(tmp);
            Log.Write("Лаунчер обновлён до версии " + release.Version);
        }

        // Файл — действительно лаунчер нужной версии (exact — ровно эта версия, иначе не старее)
        static void Verify(string file, string version, bool exact)
        {
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(file); }
            catch { File.Delete(file); throw new Exception("Скачанный файл лаунчера повреждён — операция отменена"); }
            Version expected;
            bool ok = name.Name == "ZapretLauncher" && (!Version.TryParse(version, out expected) || (exact ? VersionOf(name) == ShortVersion(expected) : name.Version >= expected));
            if (!ok)
            {
                File.Delete(file);
                throw new Exception("Файл не похож на лаунчер версии " + version + " — операция отменена");
            }
        }

        static void Replace(string tmp)
        {
            string old = Paths.Exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(Paths.Exe, old);
            try { File.Move(tmp, Paths.Exe); }
            catch { File.Move(old, Paths.Exe); throw; }
        }

        static string ShortVersion(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }
        static string VersionOf(AssemblyName n) { return ShortVersion(n.Version); }

        // Версия локальной копии прошлого лаунчера, если она старше текущего; иначе null
        public static string PreviousLocal()
        {
            try
            {
                if (!File.Exists(PrevFile)) return null;
                string v = VersionOf(AssemblyName.GetAssemblyName(PrevFile));
                return Util.TestNewer(Consts.Version, v) ? v : null;
            }
            catch { return null; }
        }

        // Предыдущий опубликованный релиз: самая новая версия, которая старше текущей
        public static async Task<Release> PreviousRemote(string repo)
        {
            string json = await Util.GetString("https://api.github.com/repos/" + repo + "/releases?per_page=50", 20);
            Release best = null;
            foreach (object o in (object[])new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json))
            {
                System.Collections.Generic.Dictionary<string, object> r = (System.Collections.Generic.Dictionary<string, object>)o;
                if (true.Equals(r["draft"]) || true.Equals(r["prerelease"])) continue;
                string tag = ((string)r["tag_name"]).TrimStart('v');
                if (!Util.TestNewer(Consts.Version, tag) || (best != null && !Util.TestNewer(tag, best.Version))) continue;
                foreach (object a in (object[])r["assets"])
                {
                    System.Collections.Generic.Dictionary<string, object> asset = (System.Collections.Generic.Dictionary<string, object>)a;
                    if (!Asset.Equals((string)asset["name"], StringComparison.OrdinalIgnoreCase)) continue;
                    best = new Release { Version = tag, Name = Asset, Url = (string)asset["browser_download_url"] };
                    object digest;
                    if (asset.TryGetValue("digest", out digest) && digest is string && ((string)digest).StartsWith("sha256:")) best.Sha256 = ((string)digest).Substring(7);
                }
            }
            return best;
        }

        // Откат: локальная копия (version == PreviousLocal()) или скачанный релиз; после — перезапуск лаунчера
        public static async Task Rollback(Release remote)
        {
            string tmp = Paths.Exe + ".new";
            string version;
            if (remote == null)
            {
                version = PreviousLocal();
                if (version == null) throw new Exception("Нет сохранённой прошлой версии лаунчера");
                File.Copy(PrevFile, tmp, true);
            }
            else
            {
                version = remote.Version;
                await Util.Download(remote, tmp);
            }
            Verify(tmp, version, true);
            try { File.Copy(Paths.Exe, PrevFile, true); } catch { }   // откат можно отменить: текущая версия станет «прошлой»
            Replace(tmp);
            Log.Write("Лаунчер возвращён на версию " + version);
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
