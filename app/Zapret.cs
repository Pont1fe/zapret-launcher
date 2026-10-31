// zapret: стратегии, запуск winws, списки, Game Filter, IPSet, установка, обновление и откат
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ZapretLauncher
{
    public class GameFilter
    {
        public string Mode = "disabled";
        public string Tcp = "1024-65535";
        public string Udp = "1024-65535";
    }

    static class Zapret
    {
        // Версию спрашивают каждую секунду — service.bat перечитываем, только когда он изменился
        static readonly Dictionary<string, KeyValuePair<DateTime, string>> versionCache = new Dictionary<string, KeyValuePair<DateTime, string>>();

        public static string VersionOf(string dir)
        {
            string svc = Path.Combine(dir, "service.bat");
            if (!File.Exists(svc)) return null;
            DateTime stamp = File.GetLastWriteTimeUtc(svc);
            KeyValuePair<DateTime, string> cached;
            if (versionCache.TryGetValue(svc, out cached) && cached.Key == stamp) return cached.Value;
            Match m = Regex.Match(File.ReadAllText(svc), "set \"LOCAL_VERSION=([^\"]+)\"");
            string version = m.Success ? m.Groups[1].Value : null;
            versionCache[svc] = new KeyValuePair<DateTime, string>(stamp, version);
            return version;
        }

        public static string LocalVersion { get { return VersionOf(Paths.Zapret); } }

        public static Process[] Winws() { return Process.GetProcessesByName("winws"); }

        // Список процессов — дорогой вызов, а Running спрашивают много раз за секунду: ответ живёт 300 мс
        static bool runningCached;
        static int runningAt;
        public static bool Running
        {
            get
            {
                int now = Environment.TickCount;
                if (runningAt == 0 || unchecked(now - runningAt) > 300) { runningCached = Winws().Length > 0; runningAt = now == 0 ? 1 : now; }
                return runningCached;
            }
        }
        static void ResetRunning() { runningAt = 0; }

        // Служба zapret из service.bat (null — не установлена)
        public static ServiceController Service()
        {
            try
            {
                ServiceController s = new ServiceController("zapret");
                ServiceControllerStatus probe = s.Status;   // бросает исключение, если службы нет
                return s;
            }
            catch { return null; }
        }

        public static bool ServiceRunning
        {
            get
            {
                ServiceController s = Service();
                try { return s != null && s.Status == ServiceControllerStatus.Running; } catch { return false; }
            }
        }

        public static List<string> Strategies()
        {
            if (!Directory.Exists(Paths.Zapret)) return new List<string>();
            return Directory.GetFiles(Paths.Zapret, "*.bat")
                .Select(Path.GetFileName)
                .Where(n => !n.StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Equals("general.bat", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => Regex.Replace(n, @"\d+", m => m.Value.PadLeft(8, '0')), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // "general (FAKE TLS AUTO ALT2).bat" -> FAKE TLS AUTO ALT2
        public static string Title(string file)
        {
            Match m = Regex.Match(file ?? "", @"\((.+)\)");
            return m.Success ? m.Groups[1].Value : "Стандартная";
        }

        public static string Badge(string file)
        {
            string title = Title(file);
            if (title == "Стандартная") return "G";
            string[] words = title.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string badge = words[0].Substring(0, 1);
            if (words.Length > 1 && words[words.Length - 1].StartsWith("ALT")) badge += "A";
            Match d = Regex.Match(title, @"(\d+)$");
            if (d.Success) badge += d.Groups[1].Value;
            return badge;
        }

        public static string Color(string file)
        {
            string t = Title(file);
            if (t.StartsWith("ALT")) return "#7B6CF6";
            if (t.StartsWith("FAKE TLS")) return "#E0901A";
            if (t.StartsWith("SIMPLE FAKE")) return "#14A37F";
            if (t.StartsWith("EXP")) return "#E5484D";
            return "#3B82F6";
        }

        // ---------- Списки (аналог load_user_lists из service.bat) ----------
        public static void InitUserLists()
        {
            string lists = Paths.InZapret("lists");
            if (!Directory.Exists(lists)) return;
            Dictionary<string, string[]> defaults = new Dictionary<string, string[]>();
            defaults["ipset-exclude-user.txt"] = new[] { Consts.IpsetPlaceholder };
            defaults["list-general-user.txt"] = new[] { "# Never leave this file empty", "domain.example.abc" };
            defaults["list-exclude-user.txt"] = new[] { "domain.example.abc" };
            foreach (KeyValuePair<string, string[]> kv in defaults)
            {
                string path = Path.Combine(lists, kv.Key);
                if (!File.Exists(path)) Util.WriteLines(path, kv.Value);
            }
        }

        public static string UserListPath(string kind) { return Paths.InZapret(@"lists\list-" + kind + "-user.txt"); }

        // Домены пользователя без комментариев, заглушки и блока Telegram
        public static List<string> UserDomains(string kind)
        {
            InitUserLists();
            List<string> result = new List<string>();
            bool inBlock = false;
            foreach (string line in Util.ReadLines(UserListPath(kind)))
            {
                string t = line.Trim();
                if (t == Consts.HostsBegin) { inBlock = true; continue; }
                if (t == Consts.HostsEnd) { inBlock = false; continue; }
                if (inBlock || t.Length == 0 || t.StartsWith("#") || t == "domain.example.abc") continue;
                result.Add(t);
            }
            return result;
        }

        public static void SaveUserDomains(string kind, IEnumerable<string> domains)
        {
            string path = UserListPath(kind);
            List<string> block = new List<string>();
            bool inBlock = false;
            foreach (string line in Util.ReadLines(path))
            {
                if (line.Trim() == Consts.HostsBegin) inBlock = true;
                if (inBlock) block.Add(line);
                if (line.Trim() == Consts.HostsEnd) inBlock = false;
            }
            List<string> output = new List<string>();
            if (kind == "general") output.Add("# Never leave this file empty");
            List<string> unique = domains.Where(d => !string.IsNullOrEmpty(d)).Distinct().ToList();
            if (unique.Count > 0) output.AddRange(unique); else output.Add("domain.example.abc");
            output.AddRange(block);
            Util.WriteLines(path, output);
        }

        // "https://www.Example.com/path" -> example.com; кириллические домены — в punycode
        public static string NormalizeDomain(string text)
        {
            string d = (text ?? "").Trim().ToLowerInvariant();
            d = Regex.Replace(d, @"^[a-z][a-z0-9+.-]*://", "");
            d = Regex.Replace(d, @"[/?#].*$", "");
            d = Regex.Replace(d, @":\d+$", "");
            d = Regex.Replace(d, @"^\*\.", "");
            d = Regex.Replace(d, @"^www\.", "");
            d = d.TrimEnd('.');
            if (d.Length == 0) return null;
            try { d = new IdnMapping().GetAscii(d); } catch { return null; }
            // зона — не из одних цифр, чтобы IP-адреса из списков вроде hosts не принимались за домены
            if (!Regex.IsMatch(d, @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+(?=[a-z0-9-]*[a-z])[a-z0-9-]{2,63}$")) return null;
            return d;
        }

        // ---------- Game Filter (формат utils\game_filter.enabled как в service.bat) ----------
        public static GameFilter ReadGameFilter()
        {
            GameFilter gf = new GameFilter();
            string file = Paths.InZapret(@"utils\game_filter.enabled");
            Regex range = new Regex(@"^\d+(-\d+)?(,\d+(-\d+)?)*$");
            foreach (string line in Util.ReadLines(file))
            {
                string[] kv = line.Split(new[] { '=' }, 2);
                string key = kv[0].Trim().ToLowerInvariant();
                string value = kv.Length > 1 ? kv[1].Trim() : "";
                if (key == "mode" && value.Length > 0) gf.Mode = value;
                else if (key == "all") gf.Mode = "all";
                else if (key == "tcp") { if (value.Length == 0) gf.Mode = "tcp"; else if (range.IsMatch(value)) gf.Tcp = value; }
                else if (key == "udp") { if (value.Length == 0) gf.Mode = "udp"; else if (range.IsMatch(value)) gf.Udp = value; }
            }
            return gf;
        }

        public static void SetGameFilter(string mode)
        {
            GameFilter gf = ReadGameFilter();
            Util.WriteLines(Paths.InZapret(@"utils\game_filter.enabled"), new[] { "mode=" + mode, "tcp=" + gf.Tcp, "udp=" + gf.Udp });
            Log.Write("Game Filter: " + mode);
        }

        // ---------- IPSet: none / loaded / any ----------
        public static string IpsetMode(string dir)
        {
            string file = Path.Combine(dir, @"lists\ipset-all.txt");
            if (!File.Exists(file)) return null;
            string content = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(content)) return "any";
            if (content.Contains(Consts.IpsetPlaceholder)) return "none";
            return "loaded";
        }

        public static async Task SetIpset(string mode)
        {
            string file = Paths.InZapret(@"lists\ipset-all.txt");
            string backup = file + ".backup";
            string current = IpsetMode(Paths.Zapret);
            if (current == mode) return;
            if (current == "loaded") File.Copy(file, backup, true);   // настоящий список храним в .backup
            if (mode == "any") File.WriteAllText(file, "");
            else if (mode == "none") Util.WriteLines(file, new[] { Consts.IpsetPlaceholder });
            else if (mode == "loaded")
            {
                if (File.Exists(backup)) File.Copy(backup, file, true);
                else { Util.WriteLines(file, new[] { Consts.IpsetPlaceholder }); await UpdateIpsetList(); }
            }
            Log.Write("IPSet: " + mode);
        }

        public static async Task UpdateIpsetList()
        {
            string content = await Util.GetString(Consts.IpsetUrl + "?t=" + DateTime.Now.Ticks, 20);
            string[] lines = content.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            if (lines.Length < 10) throw new Exception("Скачанный список IPSet подозрительно короткий — не применяю");
            string file = Paths.InZapret(@"lists\ipset-all.txt");
            // в режимах none/any обновляем сохранённую копию, чтобы не сбить режим
            Util.WriteLines(IpsetMode(Paths.Zapret) == "loaded" ? file : file + ".backup", lines);
            Log.Write("Список IPSet обновлён (" + lines.Length + " записей)");
        }

        // ---------- Аргументы стратегии ----------
        // Достаёт из general*.bat аргументы winws.exe и подставляет переменные; null — формат незнакомый
        public static string StrategyArgs(string bat)
        {
            string text = Regex.Replace(File.ReadAllText(bat), @"\^\r?\n", " ");
            string line = text.Split('\n').FirstOrDefault(l => l.Contains("winws.exe\""));
            if (line == null) return null;
            string args = line.Substring(line.IndexOf("winws.exe\"") + "winws.exe\"".Length).TrimEnd('\r');
            GameFilter gf = ReadGameFilter();
            string tcp = gf.Mode == "all" || gf.Mode == "tcp" ? gf.Tcp : "12";
            string udp = gf.Mode == "all" || gf.Mode == "udp" ? gf.Udp : "12";
            args = args.Replace("%BIN%", Paths.InZapret(@"bin\")).Replace("%LISTS%", Paths.InZapret(@"lists\"))
                .Replace("%~dp0", Paths.Zapret + "\\").Replace("%GameFilterTCP%", tcp).Replace("%GameFilterUDP%", udp)
                .Replace("%GameFilter%", tcp);
            args = Regex.Replace(args, @"\^(.)", "$1");   // экранирование cmd: ^! -> !
            if (Regex.IsMatch(args, @"%[^%\s]+%|[&|<>]")) return null;
            return args.Trim();
        }

        // ---------- Запуск и остановка ----------
        public static async Task Stop(bool unloadDriver)
        {
            Process[] procs = Winws();
            foreach (Process p in procs) { try { p.Kill(); } catch { } }
            if (procs.Length > 0)
            {
                await Task.Run(() => { foreach (Process p in procs) { try { p.WaitForExit(5000); } catch { } } });
                Log.Write("Обход остановлен");
            }
            ResetRunning();
            if (unloadDriver) await UnloadDriver();
        }

        public static async Task UnloadDriver()
        {
            foreach (string name in new[] { "WinDivert", "WinDivert14" })
            {
                await Util.RunAsync("sc.exe", "stop " + name);
                await Util.RunAsync("sc.exe", "delete " + name);
            }
        }

        public static async Task RemoveService()
        {
            await Util.RunAsync("sc.exe", "stop zapret");
            await Util.RunAsync("sc.exe", "delete zapret");
            await Task.Delay(500);
            Log.Write("Служба zapret (из service.bat) удалена");
        }

        // Запускает winws со стратегией; бросает исключение, если он не поднялся.
        // fast — для подбора: проверки системы уже сделаны первым запуском, ждём меньше
        public static async Task Start(string strategy, bool fast = false)
        {
            string bat = Paths.InZapret(strategy);
            if (!File.Exists(bat)) throw new Exception("Стратегия не найдена: " + strategy);
            await Stop(false);
            if (!fast)
            {
                // Драйвер от другой установки zapret выгружаем, если им больше никто не пользуется
                if (Conflicts.ForeignWinDivert() != null && Conflicts.OtherDpiTools().Count == 0)
                {
                    Log.Write("Выгружаю драйвер WinDivert от другой установки zapret");
                    await UnloadDriver();
                }
                InitUserLists();
                await Util.RunAsync("netsh.exe", "interface tcp set global timestamps=enabled");
            }

            string args = StrategyArgs(bat);
            if (args != null)
            {
                ProcessStartInfo psi = new ProcessStartInfo(Paths.InZapret(@"bin\winws.exe"), args);
                psi.WorkingDirectory = Paths.InZapret("bin");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            else
            {
                Log.Write("Незнакомый формат стратегии — запускаю .bat напрямую");
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\"\"");
                psi.WorkingDirectory = Paths.Zapret;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.EnvironmentVariables["NO_UPDATE_CHECK"] = "1";
                Process.Start(psi);
            }

            bool started = false;
            for (int i = 0; i < 30 && !started; i++) { await Task.Delay(100); started = Winws().Length > 0; }
            if (started) { await Task.Delay(fast ? 600 : 1500); started = Winws().Length > 0; }   // если winws сразу упал — что-то не так
            ResetRunning();
            if (!started)
                throw new Exception("winws.exe не запустился. Возможно, его удалил антивирус — добавьте папку лаунчера в исключения. " +
                    "Чтобы увидеть ошибку, запустите «" + strategy + "» из папки zapret вручную.");
        }

        // ---------- Установка, обновление, откат ----------
        public static async Task Install(Release release, Config cfg, Func<string, Task> startBypass)
        {
            Log.Write("Скачиваю " + release.Name + "...");
            if (Directory.Exists(Paths.Temp)) Directory.Delete(Paths.Temp, true);
            Directory.CreateDirectory(Paths.Temp);
            string zip = Path.Combine(Paths.Temp, "release.zip");
            await Util.Download(release, zip);
            string extracted = Path.Combine(Paths.Temp, "x");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, extracted));

            string svcBat = Directory.GetFiles(extracted, "service.bat", SearchOption.AllDirectories).FirstOrDefault();
            if (svcBat == null || !File.Exists(Path.Combine(Path.GetDirectoryName(svcBat), @"bin\winws.exe")))
                throw new Exception("Неожиданная структура архива — обновление отменено");
            string newDir = Path.GetDirectoryName(svcBat);

            bool wasRunning = Running;
            bool serviceWasRunning = ServiceRunning;
            string ipset = IpsetMode(Paths.Zapret);
            if (serviceWasRunning) await Util.RunAsync("sc.exe", "stop zapret");
            await Stop(true);

            if (Directory.Exists(Paths.Zapret))
            {
                CopyUserFiles(Paths.Zapret, newDir);
                if (Directory.Exists(Paths.Backup)) Directory.Delete(Paths.Backup, true);
                string moveError = null;
                try { Directory.Move(Paths.Zapret, Paths.Backup); }
                catch (Exception e) { moveError = e.Message; }
                if (moveError != null)
                {
                    if (wasRunning) await startBypass(cfg.Strategy);
                    throw new Exception("Не удалось заменить папку zapret (файлы заняты?): " + moveError);
                }
            }
            Directory.Move(newDir, Paths.Zapret);
            try { Directory.Delete(Paths.Temp, true); } catch { }
            if (ipset == "any" || ipset == "none") await SetIpset(ipset);

            cfg.SkipVersion = "";
            cfg.Save();
            Log.Write("Установлена версия zapret " + release.Version);
            if (serviceWasRunning) await Util.RunAsync("sc.exe", "start zapret");
            else if (wasRunning) await startBypass(cfg.Strategy);
        }

        static void CopyUserFiles(string from, string to)
        {
            foreach (string f in Consts.UserFiles)
            {
                string src = Path.Combine(from, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(to, f), true);
            }
        }

        // Меняет местами zapret и zapret.old; новую версию автообновление после этого пропускает
        public static async Task Rollback(Config cfg, Func<string, Task> startBypass)
        {
            string old = VersionOf(Paths.Backup);
            if (old == null) throw new Exception("Нет сохранённой прошлой версии zapret");
            string current = LocalVersion;
            bool wasRunning = Running;
            string ipset = IpsetMode(Paths.Zapret);
            await Stop(true);

            CopyUserFiles(Paths.Zapret, Paths.Backup);
            string swap = Paths.Zapret + ".swap";
            Directory.Move(Paths.Zapret, swap);
            try { Directory.Move(Paths.Backup, Paths.Zapret); }
            catch (Exception e) { Directory.Move(swap, Paths.Zapret); throw new Exception("Не удалось откатить: " + e.Message); }
            Directory.Move(swap, Paths.Backup);
            if (ipset != null) await SetIpset(ipset);

            cfg.SkipVersion = current;
            cfg.Save();
            Log.Write("zapret откатан на версию " + old + " (автообновление пропустит " + current + ")");
            if (wasRunning) await startBypass(cfg.Strategy);
        }
    }
}
