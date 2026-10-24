// Ядро: пути, настройки, журнал, общие утилиты и GitHub API
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZapretLauncher
{
    static class Paths
    {
        public static readonly string Exe = Assembly.GetExecutingAssembly().Location;
        public static readonly string Root = Path.GetDirectoryName(Exe);
        public static readonly string Zapret = Path.Combine(Root, "zapret");
        public static readonly string Backup = Path.Combine(Root, "zapret.old");
        public static readonly string Temp = Path.Combine(Root, "temp");
        public static readonly string Config = Path.Combine(Root, "launcher.json");
        public static readonly string Log = Path.Combine(Root, "launcher.log");
        public static readonly string HostsCache = Path.Combine(Root, "telegram-hosts.txt");
        public static readonly string HostsBackup = Path.Combine(Root, "hosts.backup");
        public static readonly string TgDir = Path.Combine(Root, "tgproxy");
        public static readonly string TgData = Path.Combine(TgDir, "TgWsProxy_data");
        public static readonly string TgAsset = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") == "ARM64"
            ? "TgWsProxy_windows_arm64.exe" : "TgWsProxy_windows.exe";
        public static readonly string TgExe = Path.Combine(TgDir, TgAsset);
        public static readonly string Hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        public static readonly string DesktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Zapret Launcher.lnk");
        public static readonly string StartMenuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Zapret Launcher.lnk");

        public static string InZapret(string relative) { return Path.Combine(Zapret, relative); }
    }

    static class Consts
    {
        public const string Version = AppVersion.Value;
        public const string ZapretRepo = "Flowseal/zapret-discord-youtube";
        public const string TgRepo = "Flowseal/tg-ws-proxy";
        public const string LauncherRepo = "Pont1fe/zapret-launcher";
        public const string TaskName = "ZapretLauncher";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ZapretLauncher";
        public const string IpsetPlaceholder = "203.0.113.113/32";
        public const string IpsetUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";
        public const string HostsUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/hosts";
        public const string HostsBegin = "# BEGIN ZapretLauncher Telegram";
        public const string HostsEnd = "# END ZapretLauncher Telegram";
        public static readonly string[] TgDomains = { "telegram.org", "t.me", "telegram.me", "telegram.dog", "telegram.space", "telesco.pe", "tg.dev", "cdn-telegram.org" };
        public static readonly string[] TgHostsDefault = new[] {
            "my.telegram.org", "desktop.telegram.org", "macos.telegram.org", "oauth.telegram.org", "oauth.tg.dev",
            "cdn.telesco.pe", "cdn1.telesco.pe", "cdn2.telesco.pe", "cdn3.telesco.pe", "cdn4.telesco.pe", "cdn5.telesco.pe", "cdn6.telesco.pe",
            "core.telegram.org", "zws4.web.telegram.org", "vesta.web.telegram.org", "vesta-1.web.telegram.org", "venus-1.web.telegram.org",
            "telegram.me", "telegram.dog", "telegram.space", "telesco.pe", "tg.dev", "telegram.org", "t.me", "api.telegram.org",
            "td.telegram.org", "venus.web.telegram.org", "web.telegram.org", "kws2-1.web.telegram.org", "kws2.web.telegram.org",
            "kws4-1.web.telegram.org", "kws4.web.telegram.org", "zws2-1.web.telegram.org", "zws2.web.telegram.org", "zws4-1.web.telegram.org"
        }.Select(h => "149.154.167.220 " + h).ToArray();
        // Файлы, которые пользователь мог изменить: переносятся при обновлении и откате zapret
        public static readonly string[] UserFiles = {
            @"lists\list-general-user.txt", @"lists\list-exclude-user.txt", @"lists\ipset-exclude-user.txt", @"utils\game_filter.enabled"
        };
    }

    public class NetProfile
    {
        public string Name { get; set; }
        public string Strategy { get; set; }
        public string Updated { get; set; }
    }

    // launcher.json — совместим с PowerShell-версией лаунчера
    public class Config
    {
        public string Strategy { get; set; }
        public bool AutoUpdate { get; set; }
        public bool StartOnOpen { get; set; }
        public bool Watchdog { get; set; }
        public bool VpnPause { get; set; }
        public bool NetworkProfilesEnabled { get; set; }
        public bool Telegram { get; set; }
        public bool TelegramLinked { get; set; }
        public bool Toasts { get; set; }
        public string Language { get; set; }   // auto / ru / en
        public string SkipVersion { get; set; }
        public string LauncherRepo { get; set; }
        public Dictionary<string, NetProfile> NetworkProfiles { get; set; }

        public Config()
        {
            Strategy = "general.bat"; AutoUpdate = true; Watchdog = true; VpnPause = true; NetworkProfilesEnabled = true; Toasts = true;
            Language = "auto";
            SkipVersion = ""; LauncherRepo = Consts.LauncherRepo;
            NetworkProfiles = new Dictionary<string, NetProfile>();
        }

        public static Config Load()
        {
            Config c = null;
            try
            {
                if (File.Exists(Paths.Config))
                    c = new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(Paths.Config, Encoding.UTF8));
            }
            catch (Exception e) { Log.Write("Не удалось прочитать launcher.json: " + e.Message); }
            if (c == null) c = new Config();
            if (c.NetworkProfiles == null) c.NetworkProfiles = new Dictionary<string, NetProfile>();
            if (string.IsNullOrEmpty(c.LauncherRepo)) c.LauncherRepo = Consts.LauncherRepo;
            if (c.SkipVersion == null) c.SkipVersion = "";
            if (string.IsNullOrEmpty(c.Strategy)) c.Strategy = "general.bat";
            if (string.IsNullOrEmpty(c.Language)) c.Language = "auto";
            return c;
        }

        public void Save()
        {
            try { File.WriteAllText(Paths.Config, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false)); }
            catch (Exception e) { Log.Write("Не удалось сохранить launcher.json: " + e.Message); }
        }
    }

    static class Log
    {
        public static event Action<string> Line;
        static readonly object sync = new object();

        public static void Write(string message)
        {
            string line = "[" + DateTime.Now.ToString("dd.MM HH:mm:ss") + "] " + I18n.T(message);
            lock (sync) { try { File.AppendAllText(Paths.Log, line + "\r\n", Encoding.UTF8); } catch { } }
            Action<string> h = Line;
            if (h != null) h(line);
        }
    }

    public class Release
    {
        public string Version;
        public string Url;
        public string Name;
    }

    static class Util
    {
        public static readonly HttpClient Http = CreateHttp();

        static HttpClient CreateHttp()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpClient c = new HttpClient();
            c.Timeout = TimeSpan.FromMinutes(5);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretLauncher/" + Consts.Version);
            return c;
        }

        // Запуск консольной утилиты без окна; возвращает код выхода
        public static int Run(string exe, string args)
        {
            string output;
            return Run(exe, args, out output);
        }

        public static int Run(string exe, string args, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.GetEncoding(866);
            using (Process p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }

        public static Task<int> RunAsync(string exe, string args) { return Task.Run(() => Run(exe, args)); }

        public static bool TestNewer(string latest, string local)
        {
            if (string.IsNullOrEmpty(local)) return true;
            if (string.IsNullOrEmpty(latest)) return false;
            Version a, b;
            if (System.Version.TryParse(latest, out a) && System.Version.TryParse(local, out b)) return a > b;
            return latest != local;
        }

        public static async Task<string> GetString(string url, int timeoutSec)
        {
            using (System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec)))
            {
                HttpResponseMessage r = await Http.GetAsync(url, cts.Token);
                r.EnsureSuccessStatusCode();
                return await r.Content.ReadAsStringAsync();
            }
        }

        public static async Task Download(string url, string file)
        {
            HttpResponseMessage r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            r.EnsureSuccessStatusCode();
            using (Stream src = await r.Content.ReadAsStreamAsync())
            using (FileStream dst = File.Create(file))
                await src.CopyToAsync(dst);
        }

        // Последний релиз репозитория; assetMatch выбирает нужный файл
        public static async Task<Release> LatestRelease(string repo, Func<string, bool> assetMatch)
        {
            string json = await GetString("https://api.github.com/repos/" + repo + "/releases/latest", 15);
            Dictionary<string, object> r = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            string tag = ((string)r["tag_name"]).TrimStart('v');
            foreach (object o in (object[])r["assets"])
            {
                Dictionary<string, object> a = (Dictionary<string, object>)o;
                string name = (string)a["name"];
                if (assetMatch(name))
                {
                    Release rel = new Release();
                    rel.Version = tag; rel.Name = name; rel.Url = (string)a["browser_download_url"];
                    return rel;
                }
            }
            throw new Exception("В релизе " + repo + " " + tag + " нет нужного файла");
        }

        public static void WriteLines(string path, IEnumerable<string> lines)
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(false));   // без BOM — так их читают winws и Python
        }

        public static string[] ReadLines(string path)
        {
            return File.Exists(path) ? File.ReadAllLines(path) : new string[0];
        }
    }
}
