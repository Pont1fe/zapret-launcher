// Отчёт для разбора проблем: журналы и сведения о системе одним архивом на рабочем столе.
// Ключи серверов, адрес подписки, файлы настроек VPN и имя пользователя Windows в отчёт не попадают
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretLauncher
{
    static class Report
    {
        static readonly Regex KeyLinks = new Regex(@"\b(vless|vmess|trojan|ss|hysteria2|hy2|tuic|amneziawg|vpn|happ)://\S+", RegexOptions.IgnoreCase);
        static readonly Regex Secrets = new Regex(@"\b(PrivateKey|PresharedKey|PublicKey|password|uuid|secret)(\s*[=:]\s*)\S+", RegexOptions.IgnoreCase);
        static readonly Regex LongBase64 = new Regex(@"[A-Za-z0-9+/_-]{40,}={0,2}");

        // Убирает всё, что даёт доступ к VPN или выдаёт пользователя; hosts — адреса серверов VPN (отчёт может уйти в открытую заявку на GitHub)
        public static string Sanitize(string text, string subscription) { return Sanitize(text, subscription, null); }
        public static string Sanitize(string text, string subscription, IEnumerable<string> hosts)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string s = text;
            if (!string.IsNullOrEmpty(subscription)) s = s.Replace(subscription, "<подписка>");
            if (hosts != null)
                foreach (string h in hosts.Where(h => !string.IsNullOrEmpty(h) && h.Length >= 4).Distinct().OrderByDescending(h => h.Length))
                    s = Regex.Replace(s, @"(?<![\w.-])" + Regex.Escape(h) + @"(?![\w-]|\.\w)", "<сервер>", RegexOptions.IgnoreCase);
            s = KeyLinks.Replace(s, "$1://<ключ>");
            s = Secrets.Replace(s, "$1$2<скрыто>");
            s = LongBase64.Replace(s, "<скрыто>");   // ключи WireGuard и прочее длинное base64
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 3) s = Regex.Replace(s, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase);
            return s;
        }

        // Адрес сервера из ключа (без DNS): host из ссылки, Endpoint из файла AmneziaWG. IP — в обычной записи, как в журналах
        public static string ServerHost(string link)
        {
            string h = RawHost(link);
            System.Net.IPAddress ip;
            return h != null && System.Net.IPAddress.TryParse(h, out ip) ? ip.ToString() : h;
        }

        static string RawHost(string link)
        {
            try
            {
                if (string.IsNullOrEmpty(link)) return null;
                if (Awg.IsAwg(link)) return Awg.EndpointHost(Awg.ConfOf(link));
                if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) || !link.Split('#')[0].Contains("@"))
                    return (string)Vpn.BuildOutbound(link)["server"];   // vmess и старый ss — адрес внутри base64
                return new Uri(link.Split('#')[0]).Host.Trim('[', ']');
            }
            catch { return null; }
        }

        public static List<string> ServerHosts(Config cfg)
        {
            return cfg.VpnServers.Select(s => ServerHost(s.Link)).Where(h => !string.IsNullOrEmpty(h) && h != "127.0.0.1").Distinct().ToList();
        }

        // Тип ключа без самого ключа: «vless», «amneziawg»
        static string Scheme(string link) { int i = (link ?? "").IndexOf("://"); return i > 0 ? link.Substring(0, i) : "?"; }

        // Сводка: версии, обход, VPN (без ключей), сеть, конфликты
        public static string Summary(Config cfg)
        {
            StringBuilder sb = new StringBuilder();
            Action<string, object> line = (k, v) => sb.AppendLine(k + ": " + v);
            sb.AppendLine("Zapret Launcher — отчёт " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            line("Лаунчер", AppVersion.Value);
            line("Windows", Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? ", 64-бит" : ", 32-бит"));
            line("Язык", cfg.Language);
            sb.AppendLine();
            sb.AppendLine("== Обход");
            line("zapret", Zapret.LocalVersion ?? "не установлен");
            line("Стратегия", cfg.Strategy);
            line("winws работает", Zapret.Running);
            line("Сторож", cfg.Watchdog);
            line("Заблокированы по адресу", cfg.BlockedSites.Count == 0 ? "—" : string.Join(", ", cfg.BlockedSites));
            sb.AppendLine();
            sb.AppendLine("== VPN");
            line("Включён", cfg.VpnOn);
            line("Режим", cfg.VpnMode);
            line("Не пускать мимо VPN", cfg.VpnKillSwitch);
            line("Ядро работает", Vpn.Running + (Vpn.Locked ? " (блокировка: VPN не работает)" : ""));
            VpnServer current = cfg.VpnServers.Count == 0 ? null : cfg.VpnServers[Math.Min(cfg.VpnSelected, cfg.VpnServers.Count - 1)];
            line("Сервер", current == null ? "—" : current.Name + " (" + Scheme(current.Link) + ")");
            line("Серверов", cfg.VpnServers.Count + " (" + string.Join(", ", cfg.VpnServers.GroupBy(s => Scheme(s.Link)).Select(g => g.Key + " " + g.Count())) + ")");
            line("Подписка", cfg.VpnSubscription.Length > 0 ? "есть, обновлена " + cfg.VpnSubUpdated : "нет");
            line("Сайты через VPN", cfg.VpnDomains.Count == 0 ? "—" : string.Join(", ", cfg.VpnDomains));
            line("Программы через VPN", cfg.VpnApps.Count == 0 ? "—" : string.Join(", ", cfg.VpnApps));
            line("sing-box", Vpn.Version ?? "не скачан");
            line("Xray", Vpn.XrayVersion ?? "не скачан");
            line("AmneziaWG", Awg.Version ?? "не скачан");
            sb.AppendLine();
            sb.AppendLine("== Сеть");
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    IPInterfaceProperties p = ni.GetIPProperties();
                    string ips = string.Join(", ", p.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.Address));
                    string gw = string.Join(", ", p.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
                    string dns = string.Join(", ", p.DnsAddresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
                    sb.AppendLine("- " + ni.Name + " [" + ni.Description + "] " + ni.OperationalStatus + (ips.Length > 0 ? " · " + ips : "") +
                                  (gw.Length > 0 ? " · шлюз " + gw : "") + (dns.Length > 0 ? " · DNS " + dns : ""));
                }
            }
            catch (Exception e) { sb.AppendLine("не удалось прочитать: " + e.Message); }
            line("Другой VPN", Conflicts.ActiveVpn() ?? "нет");
            string tools = string.Join(", ", Conflicts.OtherDpiTools().Select(p => p.ProcessName).Distinct());
            line("Другие программы обхода", tools.Length > 0 ? tools : "нет");
            line("Чужой WinDivert", Conflicts.ForeignWinDivert() ?? "нет");
            try
            {
                string svc = string.Join(", ", ServiceController.GetServices().Where(s => s.ServiceName.StartsWith("AmneziaWGTunnel$")).Select(s => s.ServiceName + "=" + s.Status));
                line("Службы AmneziaWG", svc.Length > 0 ? svc : "нет");
            }
            catch { }
            return Sanitize(sb.ToString(), cfg.VpnSubscription, ServerHosts(cfg));
        }

        // Короткая сводка для открытой заявки на GitHub: только версии и режимы — без сайтов, серверов и сети
        public static string IssueSystem(Config cfg)
        {
            VpnServer current = cfg.VpnServers.Count == 0 ? null : cfg.VpnServers[Math.Min(Math.Max(cfg.VpnSelected, 0), cfg.VpnServers.Count - 1)];
            List<string> l = new List<string>();
            l.Add("Windows: " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
            l.Add("zapret: " + (Zapret.LocalVersion ?? "-") + ", strategy: " + (string.IsNullOrEmpty(cfg.Strategy) ? "-" : cfg.Strategy) + ", watchdog: " + (cfg.Watchdog ? "on" : "off"));
            l.Add("VPN: " + (cfg.VpnOn ? "on" : "off") + ", mode: " + cfg.VpnMode + (current != null ? ", " + Scheme(current.Link) : "") +
                  (cfg.VpnKillSwitch ? ", kill switch" : "") + ", servers: " + cfg.VpnServers.Count + (cfg.VpnSubscription.Length > 0 ? " (subscription)" : ""));
            l.Add("cores: sing-box " + (Vpn.Version ?? "-") + ", Xray " + (Vpn.XrayVersion ?? "-") + ", AmneziaWG " + (Awg.Version ?? "-"));
            string other = Conflicts.ActiveVpn();
            if (other != null) l.Add("other VPN: " + other);
            return string.Join("\n", l);
        }

        // Адрес новой заявки с заполненными полями формы .github/ISSUE_TEMPLATE/bug.yml (поля — по их id)
        public static string IssueUrl(string repo, string version, string system)
        {
            return "https://github.com/" + repo + "/issues/new?template=bug.yml" +
                   "&version=" + Uri.EscapeDataString(version ?? "") + "&system=" + Uri.EscapeDataString(system ?? "");
        }

        // Последние строки файла, даже если он сейчас открыт на запись
        static string Tail(string path, int lines)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                {
                    string[] all = r.ReadToEnd().Split('\n');
                    return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
                }
            }
            catch (Exception e) { return "не удалось прочитать: " + e.Message; }
        }

        // Архив: сводка, журнал лаунчера, журналы ядра VPN, таблица маршрутов, журнал AmneziaWG — всё без ключей
        public static void Create(Config cfg, string zipPath)
        {
            string sub = cfg.VpnSubscription;
            List<string> hosts = ServerHosts(cfg);
            Dictionary<string, string> files = new Dictionary<string, string>();
            files["report.txt"] = Summary(cfg);
            files["launcher.log"] = Tail(Paths.Log, 5000);
            files["sing-box.log"] = Tail(Vpn.LogFile, 3000);
            files["sing-box.prev.log"] = Tail(Path.Combine(Vpn.Dir, "sing-box.prev.log"), 3000);
            string routes;
            Util.Run("route.exe", "print -4", out routes);
            files["routes.txt"] = routes;
            if (Awg.Version != null) files["amneziawg.log"] = Awg.LogTail(80);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (KeyValuePair<string, string> f in files)
                {
                    if (f.Value == null) continue;
                    using (StreamWriter w = new StreamWriter(zip.CreateEntry(f.Key).Open(), new UTF8Encoding(true)))
                        w.Write(Sanitize(f.Value, sub, hosts));
                }
        }
    }
}
