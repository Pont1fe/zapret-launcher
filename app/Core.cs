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
        public static readonly string History = Path.Combine(Root, "history.txt");
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
        public string Theme { get; set; }      // dark / light / system
        public bool Transparent { get; set; }  // фон Mica (Windows 11)
        public bool OnboardingDone { get; set; }
        public string Accent { get; set; }     // teal / blue / violet / green / orange / pink / system
        public int Scale { get; set; }         // масштаб интерфейса, %: 90 / 100 / 115 / 130
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
        public bool Compact { get; set; }      // компактный вид: только правая панель
        public string SecureDns { get; set; }  // "" — выключен; cloudflare / google / quad9 / adguard
        public List<string> DnsAdapters { get; set; }   // адаптеры, на которых лаунчер поменял DNS (чтобы вернуть как было)
        public string IpsetUpdated { get; set; }        // когда список IPSet обновлялся автоматически (yyyy-MM-dd)

        // launcher.json не было — первая установка (или после удаления); не сохраняется в файл
        [ScriptIgnore] public static bool Fresh { get; private set; }
        public List<string> Favorites { get; set; }
        public List<string> CollapsedGroups { get; set; }            // свёрнутые группы в списке стратегий
        public Dictionary<string, string> PickScores { get; set; }   // стратегия -> "открылось/всего/среднее мс" из последнего подбора
        public Dictionary<string, string> PickDetails { get; set; }  // стратегия -> "Сайт=мс;Сайт=-1" (-1 — не открылся) из последнего подбора
        public string PickDate { get; set; }
        public string SkipVersion { get; set; }
        public string LauncherRepo { get; set; }
        public Dictionary<string, NetProfile> NetworkProfiles { get; set; }

        public Config()
        {
            Strategy = "general.bat"; AutoUpdate = true; Watchdog = true; VpnPause = true; NetworkProfilesEnabled = true; Toasts = true;
            Language = "auto"; Theme = "dark"; Accent = "teal"; Scale = 100;
            SkipVersion = ""; LauncherRepo = Consts.LauncherRepo; PickDate = "";
            NetworkProfiles = new Dictionary<string, NetProfile>();
            Favorites = new List<string>();
            CollapsedGroups = new List<string>();
            PickScores = new Dictionary<string, string>();
            PickDetails = new Dictionary<string, string>();
            SecureDns = ""; DnsAdapters = new List<string>(); IpsetUpdated = "";
        }

        public static Config Load()
        {
            Config c = null;
            string raw = null;
            try
            {
                if (File.Exists(Paths.Config))
                {
                    raw = File.ReadAllText(Paths.Config, Encoding.UTF8);
                    c = new JavaScriptSerializer().Deserialize<Config>(raw);
                }
            }
            catch (Exception e) { Log.Write("Не удалось прочитать launcher.json: " + e.Message); }
            if (c == null) c = new Config();
            // приветствие показываем только новым установкам, не тем, кто уже пользуется лаунчером
            if (raw != null && !raw.Contains("\"OnboardingDone\"")) c.OnboardingDone = true;
            Fresh = raw == null;
            if (c.Favorites == null) c.Favorites = new List<string>();
            if (c.PickScores == null) c.PickScores = new Dictionary<string, string>();
            if (c.PickDetails == null) c.PickDetails = new Dictionary<string, string>();
            if (c.CollapsedGroups == null) c.CollapsedGroups = new List<string>();
            if (c.DnsAdapters == null) c.DnsAdapters = new List<string>();
            if (c.SecureDns == null) c.SecureDns = "";
            if (c.IpsetUpdated == null) c.IpsetUpdated = "";
            if (string.IsNullOrEmpty(c.Accent)) c.Accent = "teal";
            if (c.Scale != 90 && c.Scale != 115 && c.Scale != 130) c.Scale = 100;
            if (c.PickDate == null) c.PickDate = "";
            if (string.IsNullOrEmpty(c.Theme)) c.Theme = "dark";
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
        const long MaxSize = 1024 * 1024;   // больше 1 МБ — старый журнал уходит в launcher.log.1
        static int writes;

        public static void Write(string message)
        {
            string line = "[" + DateTime.Now.ToString("dd.MM HH:mm:ss") + "] " + I18n.T(message);
            lock (sync)
            {
                try
                {
                    if (writes++ % 50 == 0 && File.Exists(Paths.Log) && new FileInfo(Paths.Log).Length > MaxSize)
                    {
                        File.Delete(Paths.Log + ".1");
                        File.Move(Paths.Log, Paths.Log + ".1");
                    }
                    File.AppendAllText(Paths.Log, line + "\r\n", Encoding.UTF8);
                }
                catch { }
            }
            Action<string> h = Line;
            if (h != null) h(line);
        }
    }

    // Состояние обхода за последние сутки: по букве на минуту (o — работал, p — пауза из-за VPN, e — ошибка, f — выключен).
    // Минуты, когда лаунчер не работал (компьютер выключен), просто отсутствуют
    static class StateHistory
    {
        static readonly SortedDictionary<DateTime, char> minutes = new SortedDictionary<DateTime, char>();
        static int unsaved;

        public static void Load()
        {
            DateTime from = DateTime.Now.AddHours(-24);
            foreach (string line in Util.ReadLines(Paths.History))
            {
                string[] p = line.Split(' ');
                DateTime t;
                if (p.Length == 2 && p[1].Length == 1 &&
                    DateTime.TryParseExact(p[0], "yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t) && t >= from)
                    minutes[t] = p[1][0];
            }
        }

        public static void Record(string state)
        {
            DateTime now = DateTime.Now;
            DateTime m = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            minutes[m] = state == "on" ? 'o' : state == "pause" ? 'p' : state == "error" ? 'e' : 'f';
            foreach (DateTime old in minutes.Keys.Where(k => k < now.AddHours(-24)).ToList()) minutes.Remove(old);
            if (++unsaved >= 5) Save();
        }

        public static void Save()
        {
            unsaved = 0;
            try { Util.WriteLines(Paths.History, minutes.Select(kv => kv.Key.ToString("yyyyMMddHHmm") + " " + kv.Value)); } catch { }
        }

        public static List<KeyValuePair<DateTime, char>> Last24h()
        {
            DateTime from = DateTime.Now.AddHours(-24);
            return minutes.Where(kv => kv.Key >= from).ToList();
        }
    }

    public class Release
    {
        public string Version;
        public string Url;
        public string Name;
        public string Sha256;   // контрольная сумма файла из GitHub (поле digest); null — GitHub её не дал
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

        // Скачивает файл релиза и сверяет SHA-256 с тем, что сообщил GitHub: битая или подменённая загрузка не установится
        public static async Task Download(Release release, string file)
        {
            HttpResponseMessage r = await Http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead);
            r.EnsureSuccessStatusCode();
            using (Stream src = await r.Content.ReadAsStreamAsync())
            using (FileStream dst = File.Create(file))
                await src.CopyToAsync(dst);
            if (release.Sha256 == null) return;
            string actual = await Task.Run(() =>
            {
                using (FileStream fs = File.OpenRead(file))
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                    return string.Concat(sha.ComputeHash(fs).Select(b => b.ToString("x2")));
            });
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(file); } catch { }
                throw new Exception("Контрольная сумма " + release.Name + " не совпала — файл повреждён или подменён, установка отменена");
            }
            Log.Write("Контрольная сумма " + release.Name + " совпала");
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
                    object digest;
                    if (a.TryGetValue("digest", out digest) && digest is string && ((string)digest).StartsWith("sha256:"))
                        rel.Sha256 = ((string)digest).Substring(7);
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
