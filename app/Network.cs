// Сеть: проверка сайтов, поиск конфликтов (VPN, другие обходы), определение текущей сети для профилей
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretLauncher
{
    public class SiteResult
    {
        public string Name;
        public bool Ok;
        public int Ms;          // время HTTPS-запроса (DNS + TCP + TLS + ответ) через обход
        public int TcpMs = -1;  // время TCP-соединения — «пинг» до сервера; -1 — не удалось
    }

    static class Sites
    {
        public const string Control = "Контроль";

        // «Контроль» никогда не блокируется — по нему понятно, есть ли интернет вообще
        public static List<KeyValuePair<string, string>> Targets(bool telegram, bool control)
        {
            List<KeyValuePair<string, string>> t = new List<KeyValuePair<string, string>>();
            t.Add(new KeyValuePair<string, string>("Discord", "https://discord.com/api/v9/gateway"));
            t.Add(new KeyValuePair<string, string>("YouTube", "https://www.youtube.com/generate_204"));
            t.Add(new KeyValuePair<string, string>("Видео", "https://redirector.googlevideo.com/report_mapping"));
            if (telegram) t.Add(new KeyValuePair<string, string>("Telegram", "https://web.telegram.org/"));
            if (control) foreach (string url in ControlUrls) t.Add(new KeyValuePair<string, string>(Control, url));
            return t;
        }

        // Интернет есть, если открылся хотя бы один из этих адресов
        static readonly string[] ControlUrls = { "https://vk.com/", "https://ya.ru/", "http://www.msftconnecttest.com/connecttest.txt" };

        // Любой HTTP-ответ — значит до сервера достучались; блокировка DPI даёт сброс соединения или таймаут
        public static async Task<List<SiteResult>> Test(bool telegram, bool control, int timeoutSec = 6)
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.AllowAutoRedirect = false;
            using (HttpClient client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromSeconds(timeoutSec);
                client.DefaultRequestHeaders.ConnectionClose = true;   // без соединений, оставшихся от прошлой стратегии
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                List<KeyValuePair<string, string>> targets = Targets(telegram, control);
                Task<SiteResult>[] tasks = targets.Select(t => One(client, t.Key, t.Value)).ToArray();
                SiteResult[] all = await Task.WhenAll(tasks);
                List<SiteResult> results = all.Where(r => r.Name != Control).ToList();
                SiteResult[] controls = all.Where(r => r.Name == Control).ToArray();
                if (controls.Length > 0)
                {
                    SiteResult c = controls.FirstOrDefault(r => r.Ok) ?? controls[0];
                    results.Add(c);
                }
                return results;
            }
        }

        static async Task<SiteResult> One(HttpClient client, string name, string url)
        {
            SiteResult r = new SiteResult();
            r.Name = name;
            Task<int> tcp = TcpPing(new Uri(url));
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                using (HttpResponseMessage resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)) { }
                r.Ok = true;
                r.Ms = (int)sw.ElapsedMilliseconds;
            }
            catch { r.Ok = false; }
            r.TcpMs = await tcp;
            return r;
        }

        // Проверка любого сайта по домену: находится ли адрес (DNS), открывается ли по HTTPS и за сколько, TCP-пинг.
        // dnsOk=false — домен не нашёлся; тогда остальное не проверяем
        public static async Task<SiteResult> Check(string domain, Action<bool> dnsResult)
        {
            bool dnsOk;
            try
            {
                Task<IPAddress[]> t = Dns.GetHostAddressesAsync(domain);
                dnsOk = await Task.WhenAny(t, Task.Delay(5000)) == t && !t.IsFaulted && t.Result.Length > 0;
            }
            catch { dnsOk = false; }
            dnsResult(dnsOk);
            SiteResult r = new SiteResult { Name = domain };
            if (!dnsOk) return r;
            HttpClientHandler handler = new HttpClientHandler { AllowAutoRedirect = false };
            using (HttpClient client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromSeconds(8);
                client.DefaultRequestHeaders.ConnectionClose = true;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                return await One(client, domain, "https://" + domain + "/");
            }
        }

        // Время установки TCP-соединения; DPI его обычно не трогает, поэтому это честный «пинг» до сервера
        static async Task<int> TcpPing(Uri uri)
        {
            try
            {
                IPAddress[] addrs = await Dns.GetHostAddressesAsync(uri.Host);
                IPAddress ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
                if (ip == null) return -1;
                using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient(ip.AddressFamily))
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    Task connect = c.ConnectAsync(ip, uri.Port);
                    if (await Task.WhenAny(connect, Task.Delay(3000)) != connect || connect.IsFaulted) return -1;
                    return (int)sw.ElapsedMilliseconds;
                }
            }
            catch { return -1; }
        }
    }

    public class Conflict
    {
        public string Kind;   // vpn / dpi / driver
        public string Text;
    }

    static class Conflicts
    {
        [DllImport("iphlpapi.dll")]
        static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

        static readonly Dictionary<int, bool> physicalCache = new Dictionary<int, bool>();

        public static NetworkInterface ByIndex(int index)
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try { if (ni.GetIPProperties().GetIPv4Properties().Index == index) return ni; } catch { }
            }
            return null;
        }

        // Проверки сети идут в фоновых потоках — кэш общий, поэтому под замком
        public static bool IsPhysical(int index)
        {
            lock (physicalCache) return IsPhysicalLocked(index);
        }

        static bool IsPhysicalLocked(int index)
        {
            bool cached;
            if (physicalCache.TryGetValue(index, out cached)) return cached;
            bool physical = false;
            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher("SELECT PhysicalAdapter FROM Win32_NetworkAdapter WHERE InterfaceIndex=" + index))
                    foreach (ManagementObject o in q.Get()) physical = (bool)o["PhysicalAdapter"];
            }
            catch { }
            physicalCache[index] = physical;
            return physical;
        }

        // Адаптер, через который на самом деле идёт интернет (Windows сама учитывает метрики и маршруты /1 от VPN)
        public static NetworkInterface InternetAdapter(out int index)
        {
            uint idx;
            index = -1;
            if (GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0), out idx) != 0) return null;
            index = (int)idx;
            return ByIndex(index);
        }

        // Виртуальные адаптеры, которые Windows иногда считает «физическими» (Radmin, Hamachi, ZeroTier…)
        static readonly System.Text.RegularExpressions.Regex VirtualRx = new System.Text.RegularExpressions.Regex(
            @"virtual|vpn|radmin|hamachi|zerotier|tailscale|wintun|wireguard|tap-|\btun\b|sing-?box|clash|v2ray|xray|vmware|virtualbox|hyper-v|loopback|bluetooth",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public static bool IsRealAdapter(NetworkInterface ni, int index)
        {
            return IsPhysical(index) && !VirtualRx.IsMatch(ni.Name + " " + ni.Description);
        }

        // Имя VPN-адаптера, если интернет идёт через него; иначе null
        public static string ActiveVpn()
        {
            int index;
            NetworkInterface ni = InternetAdapter(out index);
            if (ni == null || index < 0) return null;
            return IsRealAdapter(ni, index) ? null : ni.Name;
        }

        public static List<Process> OtherDpiTools()
        {
            List<Process> list = new List<Process>();
            foreach (string n in new[] { "goodbyedpi", "ciadpi", "byedpi", "spoofdpi" }) list.AddRange(Process.GetProcessesByName(n));
            return list;
        }

        // Драйвер WinDivert, загруженный не из нашей папки (например, от старой установки zapret)
        public static string ForeignWinDivert()
        {
            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher("SELECT State, PathName FROM Win32_SystemDriver WHERE Name LIKE 'WinDivert%'"))
                    foreach (ManagementObject d in q.Get())
                    {
                        string path = d["PathName"] as string;
                        if ((d["State"] as string) == "Running" && path != null && path.IndexOf(Paths.Zapret, StringComparison.OrdinalIgnoreCase) < 0)
                            return Regex.Replace(path, @"^\\\?\?\\", "");
                    }
            }
            catch { }
            return null;
        }

        public static List<Conflict> All()
        {
            List<Conflict> list = new List<Conflict>();
            string vpn = ActiveVpn();
            if (vpn != null) list.Add(Make("vpn", "Интернет идёт через VPN (" + vpn + ") — мимо zapret"));
            foreach (Process p in OtherDpiTools()) list.Add(Make("dpi", "Запущен " + p.ProcessName + " — две программы обхода мешают друг другу"));
            foreach (Process p in Zapret.Winws())
            {
                string path = null;
                try { path = p.MainModule.FileName; } catch { }
                if (path != null && !path.StartsWith(Paths.Zapret, StringComparison.OrdinalIgnoreCase))
                    list.Add(Make("dpi", "Запущена другая копия zapret: " + path));
            }
            string driver = ForeignWinDivert();
            if (driver != null) list.Add(Make("driver", "Драйвер WinDivert загружен из другой папки (" + driver + ") — лаунчер заменит его при следующем включении обхода"));
            return list;
        }

        static Conflict Make(string kind, string text) { Conflict c = new Conflict(); c.Kind = kind; c.Text = text; return c; }
    }

    public class NetworkId
    {
        public string Id;
        public string Name;
    }

    // Текущая сеть — по физическому адаптеру (даже если включён VPN): Wi-Fi — по SSID, проводная — по MAC шлюза
    static class Networks
    {
        [DllImport("iphlpapi.dll")]
        static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physAddrLen);

        static IPAddress Gateway(NetworkInterface ni)
        {
            try
            {
                GatewayIPAddressInformation gw = ni.GetIPProperties().GatewayAddresses.FirstOrDefault(g =>
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                return gw == null ? null : gw.Address;
            }
            catch { return null; }
        }

        public static NetworkId Current()
        {
            // Если интернет идёт через настоящий адаптер — берём его; если через VPN — ищем настоящий с шлюзом
            int bestIndex;
            NetworkInterface best = Conflicts.InternetAdapter(out bestIndex);
            IPAddress gateway = best != null && Conflicts.IsRealAdapter(best, bestIndex) ? Gateway(best) : null;
            if (gateway == null)
            {
                best = null;
                int bestRank = int.MaxValue;
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    int index;
                    try { index = ni.GetIPProperties().GetIPv4Properties().Index; } catch { continue; }
                    IPAddress gw = Gateway(ni);
                    if (gw == null || !Conflicts.IsRealAdapter(ni, index)) continue;
                    int rank = ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 2;
                    if (rank < bestRank) { best = ni; gateway = gw; bestRank = rank; }
                }
            }
            if (best == null) return null;

            NetworkId id = new NetworkId();
            if (best.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                string ssid = WifiSsid(best.Name);
                if (ssid != null) { id.Id = "wifi:" + ssid; id.Name = "Wi-Fi «" + ssid + "»"; return id; }
            }
            string mac = GatewayMac(gateway);
            id.Id = "gw:" + (mac ?? best.Id + "/" + gateway);
            id.Name = best.Name + " · шлюз " + gateway;
            return id;
        }

        static string GatewayMac(IPAddress gateway)
        {
            try
            {
                byte[] mac = new byte[6];
                int len = mac.Length;
                if (SendARP(BitConverter.ToUInt32(gateway.GetAddressBytes(), 0), 0, mac, ref len) != 0 || len == 0) return null;
                return BitConverter.ToString(mac, 0, len);
            }
            catch { return null; }
        }

        static string WifiSsid(string interfaceName)
        {
            try
            {
                string output;
                Util.Run("netsh.exe", "wlan show interfaces", out output);
                string current = null;
                foreach (string raw in output.Split('\n'))
                {
                    string[] kv = raw.Split(new[] { ':' }, 2);
                    if (kv.Length < 2) continue;
                    string key = kv[0].Trim(), value = kv[1].Trim();
                    if (key == "Name" || key == "Имя") current = value;
                    else if (key == "SSID" && current == interfaceName && value.Length > 0) return value;
                }
            }
            catch { }
            return null;
        }
    }

    // Автозапуск при входе в Windows с наивысшими правами — через Планировщик задач (без UAC)
    static class Autostart
    {
        public static bool Enabled { get { return Util.Run("schtasks.exe", "/Query /TN " + Consts.TaskName) == 0; } }

        // При старте: задача есть — убеждаемся, что она ведёт на этот exe (папку могли перенести); пересоздаём, только если нет
        public static bool EnsureCurrent()
        {
            string xml;
            if (Util.Run("schtasks.exe", "/Query /TN " + Consts.TaskName + " /XML", out xml) != 0) return false;
            if (xml.IndexOf(Esc(Paths.Exe), StringComparison.OrdinalIgnoreCase) < 0) Set(true);
            return true;
        }

        public static void Set(bool on)
        {
            if (!on) { Util.Run("schtasks.exe", "/Delete /TN " + Consts.TaskName + " /F"); return; }
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Esc(user) + "</UserId></LogonTrigger></Triggers>\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + Esc(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + Esc(Paths.Exe) + "</Command><Arguments>--autostart</Arguments>" +
                "<WorkingDirectory>" + Esc(Paths.Root) + "</WorkingDirectory></Exec></Actions>\n" +
                "</Task>";
            string file = Path.Combine(Path.GetTempPath(), "ZapretLauncherTask.xml");
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            string output;
            int code = Util.Run("schtasks.exe", "/Create /TN " + Consts.TaskName + " /XML \"" + file + "\" /F", out output);
            File.Delete(file);
            if (code != 0) throw new Exception("Не удалось создать задачу автозапуска: " + output.Trim());
        }

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s); }
    }
}
