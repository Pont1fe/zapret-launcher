// Дополнительно к обходу: резервная копия настроек и защищённый DNS (DNS через HTTPS)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZapretLauncher
{
    // Резервная копия: настройки лаунчера и пользовательские файлы zapret (списки доменов, Game Filter) в одном JSON-файле.
    // Автокопия лежит в %APPDATA%\ZapretLauncher — вне папки лаунчера, поэтому переживает удаление и переустановку
    static class SettingsBackup
    {
        public static readonly string AutoFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZapretLauncher", "backup.json");

        public static void Save(string path)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["app"] = "ZapretLauncher";
            data["version"] = Consts.Version;
            data["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            data["config"] = File.Exists(Paths.Config) ? File.ReadAllText(Paths.Config, Encoding.UTF8) : "";
            Dictionary<string, string> files = new Dictionary<string, string>();
            foreach (string f in Consts.UserFiles)
            {
                string full = Paths.InZapret(f);
                if (File.Exists(full)) files[f] = File.ReadAllText(full, Encoding.UTF8);
            }
            data["files"] = files;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(data), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // Тихая автокопия — не чаще раза в 10 минут
        static DateTime lastAuto = DateTime.MinValue;
        public static void Auto()
        {
            if ((DateTime.Now - lastAuto).TotalMinutes < 10 || !File.Exists(Paths.Config)) return;
            lastAuto = DateTime.Now;
            try { Save(AutoFile); } catch { }
        }

        // Дата создания копии или null, если файл не похож на копию лаунчера
        public static string Describe(string path)
        {
            try
            {
                Dictionary<string, object> d = Read(path);
                return d == null ? null : (d.ContainsKey("created") ? d["created"] as string : "?");
            }
            catch { return null; }
        }

        static Dictionary<string, object> Read(string path)
        {
            if (!File.Exists(path)) return null;
            Dictionary<string, object> d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (d == null || !"ZapretLauncher".Equals(d.ContainsKey("app") ? d["app"] : null) || !(d.ContainsKey("config") && d["config"] is string)) return null;
            return d;
        }

        // Восстанавливает настройки и файлы zapret; после этого лаунчер нужно перезапустить
        public static void Restore(string path)
        {
            Dictionary<string, object> d = Read(path);
            if (d == null) throw new Exception("Это не файл резервной копии Zapret Launcher");
            string config = (string)d["config"];
            if (config.Length > 0)
            {
                new JavaScriptSerializer().Deserialize<Config>(config);   // проверка, что настройки читаются, до замены файла
                File.WriteAllText(Paths.Config, config, new UTF8Encoding(false));
            }
            Dictionary<string, object> files = d.ContainsKey("files") ? d["files"] as Dictionary<string, object> : null;
            if (files != null && Directory.Exists(Paths.Zapret))
                foreach (KeyValuePair<string, object> kv in files)
                {
                    // только известные файлы — никаких путей из самого файла копии
                    if (!Consts.UserFiles.Contains(kv.Key) || !(kv.Value is string)) continue;
                    string full = Paths.InZapret(kv.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllText(full, (string)kv.Value, new UTF8Encoding(false));
                }
            Log.Write("Настройки восстановлены из резервной копии от " + (d.ContainsKey("created") ? d["created"] : "?"));
        }
    }

    // Защищённый DNS: адреса публичного DNS на сетевых адаптерах + шифрование DNS через HTTPS (Windows 11).
    // Помогает, когда провайдер блокирует сайты подменой DNS-ответов
    static class SecureDns
    {
        public class Provider { public string Name, Template; public string[] V4, V6; }

        public static readonly Dictionary<string, Provider> Providers = new Dictionary<string, Provider>
        {
            { "cloudflare", new Provider { Name = "Cloudflare", Template = "https://cloudflare-dns.com/dns-query", V4 = new[] { "1.1.1.1", "1.0.0.1" }, V6 = new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" } } },
            { "google", new Provider { Name = "Google", Template = "https://dns.google/dns-query", V4 = new[] { "8.8.8.8", "8.8.4.4" }, V6 = new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" } } },
            { "quad9", new Provider { Name = "Quad9", Template = "https://dns.quad9.net/dns-query", V4 = new[] { "9.9.9.9", "149.112.112.112" }, V6 = new[] { "2620:fe::fe", "2620:fe::9" } } },
            { "adguard", new Provider { Name = "AdGuard", Template = "https://dns.adguard-dns.com/dns-query", V4 = new[] { "94.140.14.14", "94.140.15.15" }, V6 = new[] { "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff" } } },
        };

        // Шифрование DNS в Windows есть с Windows 11 (сборка 22000)
        public static bool EncryptionSupported { get { return Environment.OSVersion.Version.Build >= 22000; } }

        // Настоящие адаптеры с интернетом (не VPN и не виртуальные)
        static List<NetworkInterface> Adapters()
        {
            List<NetworkInterface> list = new List<NetworkInterface>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                int index;
                try { index = ni.GetIPProperties().GetIPv4Properties().Index; } catch { continue; }
                bool gateway = ni.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
                if (gateway && Conflicts.IsRealAdapter(ni, index)) list.Add(ni);
            }
            return list;
        }

        static string Q(string s) { return "\"" + s.Replace("\"", "") + "\""; }

        // Включает DNS выбранного провайдера; возвращает имена адаптеров, на которых он включён
        public static List<string> Enable(string key)
        {
            Provider p = Providers[key];
            if (EncryptionSupported)
                foreach (string ip in p.V4.Concat(p.V6))
                {
                    // запись о шифровании для сервера: Windows сама будет ходить к нему по HTTPS.
                    // udpfallback=yes — если HTTPS к серверу недоступен, DNS не пропадёт совсем
                    Util.Run("netsh.exe", "dns delete encryption server=" + ip);
                    Util.Run("netsh.exe", "dns add encryption server=" + ip + " dohtemplate=" + p.Template + " autoupgrade=yes udpfallback=yes");
                }
            List<string> names = new List<string>();
            foreach (NetworkInterface ni in Adapters())
            {
                string n = Q(ni.Name);
                Util.Run("netsh.exe", "interface ipv4 set dnsservers name=" + n + " source=static address=" + p.V4[0] + " register=none validate=no");
                Util.Run("netsh.exe", "interface ipv4 add dnsservers name=" + n + " address=" + p.V4[1] + " index=2 validate=no");
                Util.Run("netsh.exe", "interface ipv6 set dnsservers name=" + n + " source=static address=" + p.V6[0] + " register=none validate=no");
                Util.Run("netsh.exe", "interface ipv6 add dnsservers name=" + n + " address=" + p.V6[1] + " index=2 validate=no");
                names.Add(ni.Name);
            }
            Util.Run("ipconfig.exe", "/flushdns");
            return names;
        }

        // Возвращает автоматический DNS (от роутера или провайдера) на адаптерах, где его меняли
        public static void Disable(IEnumerable<string> adapters)
        {
            foreach (string name in adapters)
            {
                Util.Run("netsh.exe", "interface ipv4 set dnsservers name=" + Q(name) + " source=dhcp");
                Util.Run("netsh.exe", "interface ipv6 set dnsservers name=" + Q(name) + " source=dhcp");
            }
            Util.Run("ipconfig.exe", "/flushdns");
        }

        // Работает ли DNS после переключения: имена известных сайтов должны находиться
        public static async Task<bool> Works()
        {
            foreach (string host in new[] { "ya.ru", "www.microsoft.com", "github.com" })
            {
                try
                {
                    Task<IPAddress[]> t = Dns.GetHostAddressesAsync(host);
                    if (await Task.WhenAny(t, Task.Delay(4000)) == t && !t.IsFaulted && t.Result.Length > 0) return true;
                }
                catch { }
            }
            return false;
        }
    }
}
