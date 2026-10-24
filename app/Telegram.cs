// Telegram: Desktop — локальный MTProto-прокси Flowseal/tg-ws-proxy, Web — записи в hosts и домены в списке обхода
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZapretLauncher
{
    static class Telegram
    {
        public static Process[] Proxy() { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.TgAsset)); }
        public static bool ProxyRunning { get { return Proxy().Length > 0; } }

        public static string ProxyVersion
        {
            get
            {
                string file = Path.Combine(Paths.TgDir, "version.txt");
                return File.Exists(file) && File.Exists(Paths.TgExe) ? File.ReadAllText(file).Trim() : null;
            }
        }

        public static Task<Release> LatestProxy() { return Util.LatestRelease(Consts.TgRepo, n => n == Paths.TgAsset); }

        // config.json: свой secret, без собственной проверки обновлений и автозапуска (этим управляет лаунчер)
        public static Dictionary<string, object> InitConfig()
        {
            Directory.CreateDirectory(Paths.TgData);
            string marker = Path.Combine(Paths.TgData, ".first_run_done_mtproto");   // окно-инструкцию не показываем
            if (!File.Exists(marker)) File.WriteAllText(marker, "");

            string path = Path.Combine(Paths.TgData, "config.json");
            JavaScriptSerializer js = new JavaScriptSerializer();
            Dictionary<string, object> cfg = null;
            try { if (File.Exists(path)) cfg = js.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>; } catch { }
            if (cfg == null)
            {
                byte[] bytes = new byte[16];
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                cfg = new Dictionary<string, object>();
                cfg["host"] = "127.0.0.1";
                cfg["port"] = 1443;
                cfg["secret"] = string.Concat(bytes.Select(b => b.ToString("x2")));
            }
            if (!false.Equals(Get(cfg, "check_updates")) || !false.Equals(Get(cfg, "autostart")))
            {
                cfg["check_updates"] = false;
                cfg["autostart"] = false;
                File.WriteAllText(path, js.Serialize(cfg), new UTF8Encoding(false));   // без BOM — его читает Python
            }
            return cfg;
        }

        static object Get(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) ? v : null; }

        public static async Task InstallProxy(Release release)
        {
            Log.Write("Скачиваю tg-ws-proxy " + release.Version + "...");
            Directory.CreateDirectory(Paths.TgDir);
            string tmp = Paths.TgExe + ".download";
            await Util.Download(release.Url, tmp);
            bool wasRunning = ProxyRunning;
            await StopProxy();
            if (File.Exists(Paths.TgExe)) File.Delete(Paths.TgExe);
            File.Move(tmp, Paths.TgExe);
            File.WriteAllText(Path.Combine(Paths.TgDir, "version.txt"), release.Version);
            Log.Write("Установлен tg-ws-proxy " + release.Version);
            if (wasRunning) StartProxyProcess();
        }

        static void StartProxyProcess()
        {
            ProcessStartInfo psi = new ProcessStartInfo(Paths.TgExe);
            psi.WorkingDirectory = Paths.TgDir;
            psi.UseShellExecute = false;
            Process.Start(psi);
        }

        // true — если прокси запущен впервые и Telegram ещё надо подключить
        public static async Task StartProxy(Config cfg)
        {
            if (ProxyRunning) return;
            if (ProxyVersion == null) await InstallProxy(await LatestProxy());
            Dictionary<string, object> tc = InitConfig();
            StartProxyProcess();
            Log.Write("Telegram-прокси запущен (" + Get(tc, "host") + ":" + Get(tc, "port") + ")");
            if (!cfg.TelegramLinked)
            {
                await Task.Delay(2000);
                Connect(cfg);
            }
        }

        public static async Task StopProxy()
        {
            Process[] procs = Proxy();
            foreach (Process p in procs) { try { p.Kill(); } catch { } }
            if (procs.Length > 0)
            {
                await Task.Run(() => { foreach (Process p in procs) { try { p.WaitForExit(5000); } catch { } } });
                Log.Write("Telegram-прокси остановлен");
            }
        }

        // Открывает tg://proxy в Telegram Desktop — там нужно нажать «Подключиться»
        public static void Connect(Config cfg)
        {
            Dictionary<string, object> tc = InitConfig();
            string host = Get(tc, "host") as string;
            if (string.IsNullOrEmpty(host) || host == "0.0.0.0") host = "127.0.0.1";
            string link = "tg://proxy?server=" + host + "&port=" + Get(tc, "port") + "&secret=dd" + Get(tc, "secret");
            // через explorer, чтобы Telegram не запустился с правами администратора
            Process.Start("explorer.exe", "\"" + link + "\"");
            cfg.TelegramLinked = true;
            cfg.Save();
            Log.Write("Ссылка на прокси открыта в Telegram — нажмите «Подключиться» (если Telegram не открылся, установите Telegram Desktop)");
        }

        // ---------- Telegram Web ----------
        static string[] HostsLines()
        {
            string[] cached = Util.ReadLines(Paths.HostsCache).Where(l => l.Trim().Length > 0).ToArray();
            return cached.Length > 0 ? cached : Consts.TgHostsDefault;
        }

        // Свежие адреса Telegram из репозитория zapret
        public static async Task UpdateHostsCache()
        {
            string content = await Util.GetString(Consts.HostsUrl + "?t=" + DateTime.Now.Ticks, 10);
            string[] lines = content.Split('\n').Select(l => l.Trim())
                .Where(l => Regex.IsMatch(l, @"^[0-9a-fA-F:.]+\s+\S+$") && Regex.IsMatch(l, @"telegram|telesco\.pe|tg\.dev|\st\.me$")).ToArray();
            if (lines.Length > 0) Util.WriteLines(Paths.HostsCache, lines);
        }

        public static void SetHosts(bool on)
        {
            Encoding enc = Encoding.Default;   // побайтово сохраняем остальной hosts как есть
            string content = File.ReadAllText(Paths.Hosts, enc);
            string rx = @"(?s)(\r?\n)*" + Regex.Escape(Consts.HostsBegin) + ".*?" + Regex.Escape(Consts.HostsEnd) + @"\r?\n?";
            string clean = Regex.Replace(content, rx, "\r\n").TrimEnd('\r', '\n');
            string updated = on
                ? clean + "\r\n\r\n" + Consts.HostsBegin + "\r\n" + string.Join("\r\n", HostsLines()) + "\r\n" + Consts.HostsEnd + "\r\n"
                : clean + "\r\n";
            if (updated == content) return;
            if (!File.Exists(Paths.HostsBackup)) File.Copy(Paths.Hosts, Paths.HostsBackup);
            try { File.WriteAllText(Paths.Hosts, updated, enc); }
            catch (Exception e) { throw new Exception("Не удалось изменить hosts (антивирус?): " + e.Message); }
            Util.Run("ipconfig.exe", "/flushdns");
            Log.Write(on ? "Адреса Telegram добавлены в hosts" : "Адреса Telegram убраны из hosts");
        }

        public static void SetDomains(bool on)
        {
            if (!Directory.Exists(Paths.Zapret)) return;
            Zapret.InitUserLists();
            string file = Zapret.UserListPath("general");
            string[] lines = Util.ReadLines(file);
            List<string> kept = new List<string>();
            bool inBlock = false;
            foreach (string l in lines)
            {
                if (l == Consts.HostsBegin) { inBlock = true; continue; }
                if (l == Consts.HostsEnd) { inBlock = false; continue; }
                if (!inBlock) kept.Add(l);
            }
            if (on) { kept.Add(Consts.HostsBegin); kept.AddRange(Consts.TgDomains); kept.Add(Consts.HostsEnd); }
            if (string.Join("\n", kept) != string.Join("\n", lines)) Util.WriteLines(file, kept);
        }

        public static void SetWeb(bool on)
        {
            SetHosts(on);
            SetDomains(on);
        }
    }
}
