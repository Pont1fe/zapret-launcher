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
    static readonly Color Bg = Color.FromArgb(30, 30, 35);
    static readonly Color Card = Color.FromArgb(35, 35, 40);
    static readonly Color Line = Color.FromArgb(58, 58, 68);
    static readonly Color Text1 = Color.FromArgb(237, 237, 240);
    static readonly Color Muted = Color.FromArgb(138, 138, 147);
    static readonly Color Accent = Color.FromArgb(123, 108, 246);

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
        Text = "Установка Zapret Launcher";
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
        Label sub = MakeLabel("Версия " + Installer.Version + " · обход блокировок Discord, YouTube и Telegram", 102, 60, 440, 22);
        sub.ForeColor = Muted;

        MakeLabel("Папка установки", 28, 110, 300, 22);
        pathBox = new TextBox();
        pathBox.Text = dir;
        pathBox.BorderStyle = BorderStyle.FixedSingle;
        pathBox.BackColor = Card;
        pathBox.ForeColor = Text1;
        pathBox.SetBounds(28, 136, 400, 28);
        pathBox.TextChanged += delegate { UpdateExisting(); };
        Controls.Add(pathBox);

        btnBrowse = MakeButton("Обзор…", 436, 134, 96, 30, false);
        btnBrowse.Click += delegate
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Выберите папку. Внутри неё будет создана папка ZapretLauncher.";
                if (d.ShowDialog(this) == DialogResult.OK)
                    pathBox.Text = Path.Combine(d.SelectedPath, "ZapretLauncher");
            }
        };

        existing = MakeLabel("", 28, 170, 504, 22);
        existing.ForeColor = Color.FromArgb(156, 144, 255);

        chkDesktop = MakeCheck("Ярлык на рабочем столе", 28, 204);
        chkStart = MakeCheck("Ярлык в меню «Пуск»", 28, 234);
        chkLaunch = MakeCheck("Запустить лаунчер после установки", 28, 264);

        status = MakeLabel("Путь лучше выбирать без русских букв — так zapret работает надёжнее.", 28, 304, 504, 40);
        status.ForeColor = Muted;

        btnCancel = MakeButton("Отмена", 324, 350, 100, 34, false);
        btnCancel.Click += delegate { Close(); };
        btnInstall = MakeButton("Установить", 432, 350, 100, 34, true);
        btnInstall.Click += delegate { OnInstall(); };
        AcceptButton = btnInstall;
        UpdateExisting();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;   // тёмный заголовок окна (Windows 10 2004+ / 11)
        try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }
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
        b.ForeColor = Color.White;
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
            ? "Версия " + v + " уже установлена — лаунчер будет переписан, настройки и zapret сохранятся"
            : "Найдена версия " + v + " — она будет обновлена, настройки и zapret сохранятся";
        if (!done) btnInstall.Text = v == null ? "Установить" : same ? "Переустановить" : "Обновить";
    }

    void OnInstall()
    {
        if (done) { Close(); return; }
        string dir = pathBox.Text.Trim();
        if (!Path.IsPathRooted(dir) || Path.GetPathRoot(dir).TrimEnd('\\') == dir.TrimEnd('\\'))
        {
            MessageBox.Show(this, "Укажите папку, а не корень диска, например C:\\ZapretLauncher.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (dir.Any(ch => ch > 127) &&
            MessageBox.Show(this, "В пути есть русские буквы или спецсимволы — zapret с ними может работать нестабильно.\n\nВсё равно установить сюда?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        btnInstall.Enabled = btnCancel.Enabled = btnBrowse.Enabled = false;
        status.ForeColor = Text1;
        status.Text = "Устанавливаю…";
        Refresh();
        try
        {
            bool wasRunning = Installer.CloseRunningLauncher();
            Installer.Install(dir, chkDesktop.Checked, chkStart.Checked, chkLaunch.Checked || wasRunning);
            done = true;
            status.ForeColor = Color.FromArgb(120, 220, 160);
            status.Text = "Готово! Zapret Launcher " + Installer.Version + " установлен в " + dir +
                (chkLaunch.Checked || wasRunning ? " и запущен." : ".");
            btnInstall.Text = "Закрыть";
            btnInstall.Enabled = true;
        }
        catch (Exception e)
        {
            status.ForeColor = Color.FromArgb(255, 138, 138);
            status.Text = "Ошибка: " + e.Message;
            btnInstall.Enabled = btnCancel.Enabled = btnBrowse.Enabled = true;
        }
    }
}
