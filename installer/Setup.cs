// Установщик Zapret Launcher: копирует ZapretLauncher.exe, создаёт ярлыки и запись в «Установленных приложениях».
// Сборка: build.ps1 в корне (встроенный в Windows csc.exe, C# 5). Ключи: /S — без окна, /D=путь — папка установки.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Zapret Launcher Setup")]
[assembly: AssemblyProduct("Zapret Launcher")]
[assembly: AssemblyCompany("Pont1fe")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Pont1fe")]

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string dir = null;
        bool silent = false;
        foreach (string a in args)
        {
            if (a.Equals("/S", StringComparison.OrdinalIgnoreCase)) silent = true;
            else if (a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(3).Trim('"');
        }
        if (dir == null) dir = Installer.ExistingLocation() ?? @"C:\ZapretLauncher";

        if (silent)
        {
            try { Installer.Install(dir, true, true, Installer.CloseRunningLauncher()); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm(dir));
        return 0;
    }
}

static class Installer
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZapretLauncher";
    public const string ShortcutName = "Zapret Launcher.lnk";
    public const string ExeName = "ZapretLauncher.exe";
    static readonly string[] Legacy = { "ZapretLauncher.ps1", "ZapretLauncher.ps1.bak", "Start.bat", "launcher.ico" };   // PowerShell-версия

    public static byte[] Resource(string name)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        using (MemoryStream ms = new MemoryStream())
        {
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    static string ShortVersion(Version v) { return v.Major + "." + v.Minor + "." + v.Build; }

    // Сборку из ресурса можно загрузить только один раз за запуск (повторная загрузка бросает FileLoadException) — запоминаем версию
    static string version;
    public static string Version
    {
        get
        {
            if (version == null) version = ShortVersion(Assembly.ReflectionOnlyLoad(Resource(ExeName)).GetName().Version);
            return version;
        }
    }

    static RegistryKey Hklm() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }

    public static bool HasLauncher(string dir)
    {
        return File.Exists(Path.Combine(dir, ExeName)) || File.Exists(Path.Combine(dir, "ZapretLauncher.ps1"));
    }

    // Куда ставить нельзя: папку закрывают от изменений без прав администратора (владелец и права — на всё внутри),
    // поэтому не корень диска и не относительный путь, и не чужая непустая папка
    public static bool IsBadPath(string dir) { return !Path.IsPathRooted(dir) || Path.GetPathRoot(dir).TrimEnd('\\') == dir.TrimEnd('\\'); }
    public static bool IsForeignFolder(string dir) { return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any() && !HasLauncher(dir); }

    public static string ExistingLocation()
    {
        using (RegistryKey k = Hklm().OpenSubKey(UninstallKey))
        {
            string loc = k == null ? null : k.GetValue("InstallLocation") as string;
            if (loc != null && HasLauncher(loc)) return loc;
        }
        return HasLauncher(@"C:\ZapretLauncher") ? @"C:\ZapretLauncher" : null;
    }

    public static string InstalledVersion(string dir)
    {
        string exe = Path.Combine(dir, ExeName);
        if (File.Exists(exe))
        {
            try { return ShortVersion(AssemblyName.GetAssemblyName(exe).Version); } catch { return "?"; }
        }
        string script = Path.Combine(dir, "ZapretLauncher.ps1");
        if (!File.Exists(script)) return null;
        Match m = Regex.Match(File.ReadAllText(script), @"\$LauncherVersion\s*=\s*'([^']+)'");
        return m.Success ? m.Groups[1].Value : "1.x";
    }

    // Закрывает запущенный лаунчер — exe или PowerShell-версию (обход winws продолжает работать). true — если он был запущен
    public static bool CloseRunningLauncher()
    {
        bool found = false;
        foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try { p.Kill(); p.WaitForExit(5000); found = true; } catch { }
        }
        using (ManagementObjectSearcher q = new ManagementObjectSearcher(
            "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='powershell.exe'"))
        {
            foreach (ManagementObject p in q.Get())
            {
                string cmd = p["CommandLine"] as string;
                if (cmd == null || cmd.IndexOf("ZapretLauncher.ps1", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (cmd.IndexOf("-Uninstall", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                try { Process.GetProcessById(Convert.ToInt32(p["ProcessId"])).Kill(); found = true; } catch { }
            }
        }
        if (found) System.Threading.Thread.Sleep(800);
        return found;
    }

    static void CreateShortcut(string path, string exe)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic lnk = shell.CreateShortcut(path);
        lnk.TargetPath = exe;
        lnk.Arguments = "";
        lnk.WorkingDirectory = Path.GetDirectoryName(exe);
        lnk.IconLocation = exe + ",0";
        lnk.Description = "Zapret Launcher — обход блокировок Discord, YouTube и Telegram";
        lnk.Save();
        Marshal.FinalReleaseComObject(lnk);
        Marshal.FinalReleaseComObject(shell);
    }

    static void RunHidden(string exe, string args)
    {
        using (Process p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }))
            p.WaitForExit(60000);
    }

    public static void Install(string dir, bool desktop, bool startMenu, bool launch)
    {
        // окно установки уже предложило подпапку; здесь — для тихой установки (/S /D=...), до любых изменений
        if (IsBadPath(dir) || IsForeignFolder(dir))
            throw new Exception("Not installing to " + dir + ": choose an empty folder, not a drive root / Не ставлю в " + dir + ": нужна пустая папка, а не корень диска");
        Directory.CreateDirectory(dir);
        // Отсюда лаунчер запускает программы с правами администратора — менять папку может только администратор
        // (папка в корне диска иначе наследует «изменение» для всех пользователей)
        // владелец — тоже администраторы: владелец папки может вернуть себе права и без прав администратора
        RunHidden("icacls.exe", "\"" + dir.TrimEnd('\\') + "\" /setowner *S-1-5-32-544 /T /C /Q");
        RunHidden("icacls.exe", "\"" + dir.TrimEnd('\\') + "\" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /Q");
        string exe = Path.Combine(dir, ExeName);
        File.WriteAllBytes(exe, Resource(ExeName));
        foreach (string legacy in Legacy)
        {
            try { File.Delete(Path.Combine(dir, legacy)); } catch { }
        }

        if (desktop) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName), exe);
        if (startMenu) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName), exe);

        using (RegistryKey k = Hklm().CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Zapret Launcher");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Pont1fe (zapret by Flowseal)");
            k.SetValue("DisplayIcon", exe);
            k.SetValue("InstallLocation", dir);
            k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
            k.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --quiet");   // для winget и Scoop
            k.SetValue("URLInfoAbout", "https://github.com/Pont1fe/zapret-launcher");
            k.SetValue("EstimatedSize", 40000, RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        // Установщик запущен с правами администратора — лаунчер стартует без лишнего запроса UAC
        if (launch)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
    }
}

class SetupForm : Form
{
    // светлая лавандовая тема и фиолетовый акцент — как у лаунчера (app/Theme.cs, LightColors)
    static readonly Color Bg = Color.FromArgb(243, 242, 250);
    static readonly Color Card = Color.White;
    static readonly Color Line = Color.FromArgb(228, 225, 242);
    static readonly Color Text1 = Color.FromArgb(22, 21, 43);
    static readonly Color Muted = Color.FromArgb(126, 124, 152);
    static readonly Color Accent = Color.FromArgb(106, 77, 244);
    static readonly Color AccentHover = Color.FromArgb(124, 97, 255);
    static readonly Color AccentText = Color.White;
    static readonly Color Ok = Color.FromArgb(31, 168, 102);
    static readonly Color Fail = Color.FromArgb(214, 69, 69);

    // Русский для русской Windows, иначе английский
    static readonly bool En = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ru";
    static string L(string ru, string en) { return En ? en : ru; }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    TextBox pathBox;
    CheckBox chkDesktop, chkStart, chkLaunch;
    Label status, existing;
    Button btnInstall, btnCancel, btnBrowse;
    bool done;

    public SetupForm(string dir)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Text = L("Установка Zapret Launcher", "Zapret Launcher Setup");
        ClientSize = new Size(560, 400);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // логотип как в верхней панели лаунчера: «Z» на скруглённой фиолетовой плитке
        Panel logo = new Panel();
        logo.SetBounds(28, 26, 56, 56);
        logo.Paint += (s, e) =>
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Rectangle r = new Rectangle(0, 0, logo.Width - 1, logo.Height - 1);
            int d = r.Width * 32 / 100;   // радиус скругления — как 13 из 40 у плитки лаунчера
            using (System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath())
            using (System.Drawing.Drawing2D.LinearGradientBrush b = new System.Drawing.Drawing2D.LinearGradientBrush(r, AccentHover, Accent, 45f))
            using (System.Drawing.Drawing2D.GraphicsPath shield = new System.Drawing.Drawing2D.GraphicsPath())
            using (System.Drawing.Drawing2D.GraphicsPath z = new System.Drawing.Drawing2D.GraphicsPath())
            using (Pen pen = new Pen(Accent, r.Width * 0.09f * 0.52f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            {
                // логотип лаунчера, как в верхней панели и у значка: белый щит с «Z» в координатах 0..100, по центру плитки
                shield.AddBezier(50, 8, 64, 16, 76, 18, 88, 18); shield.AddLine(88, 18, 88, 46); shield.AddBezier(88, 46, 88, 70, 72, 86, 50, 94);
                shield.AddBezier(50, 94, 28, 86, 12, 70, 12, 46); shield.AddLine(12, 46, 12, 18); shield.AddBezier(12, 18, 24, 18, 36, 16, 50, 8); shield.CloseFigure();
                z.AddLines(new[] { new PointF(35, 33), new PointF(65, 33), new PointF(35, 67), new PointF(65, 67) });
                using (System.Drawing.Drawing2D.Matrix m = new System.Drawing.Drawing2D.Matrix())
                {
                    float k = r.Width * 0.52f / 100f;   // щит — примерно половина плитки, как 26 из 40 в лаунчере (через 100 единиц)
                    m.Translate(r.Width / 2f, r.Height / 2f); m.Scale(k, k); m.Translate(-50, -50);
                    shield.Transform(m); z.Transform(m);
                }
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
                g.FillPath(Brushes.White, shield);
                g.DrawPath(pen, z);
            }
        };
        Controls.Add(logo);

        Label title = MakeLabel("Zapret Launcher", 100, 26, 420, 32);
        title.Font = new Font("Segoe UI Semibold", 17f);
        Label sub = MakeLabel(L("Версия ", "Version ") + Installer.Version + L(" · обход блокировок Discord, YouTube и Telegram", " · bypass blocking of Discord, YouTube and Telegram"), 102, 60, 440, 22);
        sub.ForeColor = Muted;

        MakeLabel(L("Папка установки", "Install folder"), 28, 110, 300, 22);
        pathBox = new TextBox();
        pathBox.Text = dir;
        pathBox.BorderStyle = BorderStyle.FixedSingle;
        pathBox.BackColor = Card;
        pathBox.ForeColor = Text1;
        pathBox.SetBounds(28, 136, 400, 28);
        pathBox.TextChanged += delegate { UpdateExisting(); };
        Controls.Add(pathBox);

        btnBrowse = MakeButton(L("Обзор…", "Browse…"), 436, 134, 96, 30, false);
        btnBrowse.Click += delegate
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = L("Выберите папку. Внутри неё будет создана папка ZapretLauncher.", "Choose a folder. A ZapretLauncher folder will be created inside it.");
                if (d.ShowDialog(this) == DialogResult.OK)
                    pathBox.Text = Path.Combine(d.SelectedPath, "ZapretLauncher");
            }
        };

        existing = MakeLabel("", 28, 170, 504, 22);
        existing.ForeColor = Ok;

        chkDesktop = MakeCheck(L("Ярлык на рабочем столе", "Desktop shortcut"), 28, 204);
        chkStart = MakeCheck(L("Ярлык в меню «Пуск» (нужен для уведомлений)", "Start menu shortcut (needed for notifications)"), 28, 234);
        chkLaunch = MakeCheck(L("Запустить лаунчер после установки", "Launch after installation"), 28, 264);

        status = MakeLabel(L("Путь лучше выбирать без русских букв — так zapret работает надёжнее.", "Prefer a path with Latin characters only — zapret works more reliably."), 28, 304, 504, 40);
        status.ForeColor = Muted;

        btnCancel = MakeButton(L("Отмена", "Cancel"), 324, 350, 100, 34, false);
        btnCancel.Click += delegate { Close(); };
        btnInstall = MakeButton(L("Установить", "Install"), 432, 350, 100, 34, true);
        btnInstall.Click += delegate { OnInstall(); };
        AcceptButton = btnInstall;
        UpdateExisting();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;   // скруглённые углы (Windows 11); заголовок окна — светлый, как сама тема
        try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
    }

    Label MakeLabel(string text, int x, int y, int w, int h)
    {
        Label l = new Label();
        l.Text = text;
        l.SetBounds(x, y, w, h);
        Controls.Add(l);
        return l;
    }

    CheckBox MakeCheck(string text, int x, int y)
    {
        CheckBox c = new CheckBox();
        c.Text = text;
        c.Checked = true;
        c.FlatStyle = FlatStyle.Flat;
        c.FlatAppearance.BorderColor = Line;
        c.FlatAppearance.CheckedBackColor = Accent;
        c.SetBounds(x, y, 480, 26);
        Controls.Add(c);
        return c;
    }

    Button MakeButton(string text, int x, int y, int w, int h, bool accent)
    {
        Button b = new Button();
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = accent ? 0 : 1;
        b.FlatAppearance.BorderColor = Line;
        b.BackColor = accent ? Accent : Card;
        b.FlatAppearance.MouseOverBackColor = accent ? AccentHover : Color.FromArgb(240, 238, 249);
        b.ForeColor = accent ? AccentText : Text1;
        b.Cursor = Cursors.Hand;
        b.SetBounds(x, y, w, h);
        Controls.Add(b);
        return b;
    }

    void UpdateExisting()
    {
        string v = null;
        try { v = Installer.InstalledVersion(pathBox.Text.Trim()); } catch { }
        bool same = v == Installer.Version;
        existing.Text = v == null ? "" : same
            ? L("Версия ", "Version ") + v + L(" уже установлена — лаунчер будет переписан, настройки и zapret сохранятся", " is already installed — the launcher will be rewritten, settings and zapret are kept")
            : L("Найдена версия ", "Found version ") + v + L(" — она будет обновлена, настройки и zapret сохранятся", " — it will be updated, settings and zapret are kept");
        if (!done) btnInstall.Text = v == null ? L("Установить", "Install") : same ? L("Переустановить", "Reinstall") : L("Обновить", "Update");
    }

    void OnInstall()
    {
        if (done) { Close(); return; }
        string dir = pathBox.Text.Trim();
        if (Installer.IsBadPath(dir))
        {
            MessageBox.Show(this, L("Укажите папку, а не корень диска, например C:\\ZapretLauncher.", "Choose a folder, not a drive root, e.g. C:\\ZapretLauncher."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // в непустую чужую папку не ставим: лаунчер живёт в своей папке, и её закрывают от изменений без прав администратора
        if (Installer.IsForeignFolder(dir))
        {
            string sub = Path.Combine(dir, "ZapretLauncher");
            if (Installer.IsForeignFolder(sub))
            {
                MessageBox.Show(this, L("Папка не пустая — выберите пустую папку для лаунчера.", "The folder is not empty — choose an empty folder for the launcher."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, L("В папке " + dir + " уже есть другие файлы. Установить в " + sub + "?", "The folder " + dir + " already has other files. Install to " + sub + "?"),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            dir = sub;
            pathBox.Text = dir;
        }
        if (dir.Any(ch => ch > 127) &&
            MessageBox.Show(this, L("В пути есть русские буквы или спецсимволы — zapret с ними может работать нестабильно.\n\nВсё равно установить сюда?",
                "The path contains non-Latin or special characters — zapret may be unstable with them.\n\nInstall here anyway?"),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        btnInstall.Enabled = btnCancel.Enabled = btnBrowse.Enabled = false;
        status.ForeColor = Text1;
        status.Text = L("Устанавливаю…", "Installing…");
        Refresh();
        try
        {
            bool wasRunning = Installer.CloseRunningLauncher();
            Installer.Install(dir, chkDesktop.Checked, chkStart.Checked, chkLaunch.Checked || wasRunning);
            done = true;
            status.ForeColor = Ok;
            status.Text = L("Готово! Zapret Launcher ", "Done! Zapret Launcher ") + Installer.Version + L(" установлен в ", " is installed to ") + dir +
                (chkLaunch.Checked || wasRunning ? L(" и запущен.", " and running.") : ".");
            btnInstall.Text = L("Закрыть", "Close");
            btnInstall.Enabled = true;
        }
        catch (Exception e)
        {
            status.ForeColor = Fail;
            status.Text = L("Ошибка: ", "Error: ") + e.Message;
            btnInstall.Enabled = btnCancel.Enabled = btnBrowse.Enabled = true;
        }
    }
}
