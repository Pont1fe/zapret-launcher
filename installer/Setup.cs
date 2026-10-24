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

    public static string Version
    {
        get { return ShortVersion(Assembly.ReflectionOnlyLoad(Resource(ExeName)).GetName().Version); }
    }

    static RegistryKey Hklm() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); }

    static bool HasLauncher(string dir)
    {
        return File.Exists(Path.Combine(dir, ExeName)) || File.Exists(Path.Combine(dir, "ZapretLauncher.ps1"));
    }

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

    public static void Install(string dir, bool desktop, bool startMenu, bool launch)
    {
        Directory.CreateDirectory(dir);
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
    static readonly Color Bg = Color.FromArgb(25, 26, 31);
    static readonly Color Card = Color.FromArgb(30, 32, 37);
    static readonly Color Line = Color.FromArgb(44, 46, 53);
    static readonly Color Text1 = Color.FromArgb(236, 238, 242);
    static readonly Color Muted = Color.FromArgb(139, 142, 152);
    static readonly Color Accent = Color.FromArgb(31, 184, 154);
    static readonly Color AccentText = Color.FromArgb(11, 31, 27);

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

        PictureBox logo = new PictureBox();
        logo.Image = Image.FromStream(new MemoryStream(Installer.Resource("logo.png")));
        logo.SizeMode = PictureBoxSizeMode.Zoom;
        logo.SetBounds(28, 26, 56, 56);
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
        existing.ForeColor = Color.FromArgb(44, 211, 177);

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
        int on = 1, round = 2;   // тёмный заголовок (Windows 10 2004+ / 11) и скруглённые углы (Windows 11)
        try { DwmSetWindowAttribute(Handle, 20, ref on, 4); DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
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
        if (!Path.IsPathRooted(dir) || Path.GetPathRoot(dir).TrimEnd('\\') == dir.TrimEnd('\\'))
        {
            MessageBox.Show(this, L("Укажите папку, а не корень диска, например C:\\ZapretLauncher.", "Choose a folder, not a drive root, e.g. C:\\ZapretLauncher."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
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
            status.ForeColor = Color.FromArgb(120, 220, 160);
            status.Text = L("Готово! Zapret Launcher ", "Done! Zapret Launcher ") + Installer.Version + L(" установлен в ", " is installed to ") + dir +
                (chkLaunch.Checked || wasRunning ? L(" и запущен.", " and running.") : ".");
            btnInstall.Text = L("Закрыть", "Close");
            btnInstall.Enabled = true;
        }
        catch (Exception e)
        {
            status.ForeColor = Color.FromArgb(255, 138, 138);
            status.Text = L("Ошибка: ", "Error: ") + e.Message;
            btnInstall.Enabled = btnCancel.Enabled = btnBrowse.Enabled = true;
        }
    }
}
