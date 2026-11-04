// AmneziaWG: серверы Amnezia (в том числе Amnezia Premium — «Файлы конфигурации» в их приложении) и свои серверы AmneziaWG.
// Ядро — официальный клиент AmneziaWG для Windows (amnezia-vpn/amneziawg-windows-client): его MSI распаковывается в vpn\awg
// без установки, туннель поднимает служба Windows «AmneziaWGTunnel$ZapretVPN». Адаптер называется ZapretVPN, как у sing-box, —
// по этому имени лаунчер отличает свой VPN от чужого. Маршрутизации по сайтам и программам здесь нет: через туннель идёт весь трафик
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ZapretLauncher
{
    static class Awg
    {
        public const string Repo = "amnezia-vpn/amneziawg-windows-client";
        public const string Scheme = "amneziawg://";   // файл настроек хранится как ключ: amneziawg://<base64url текста>#имя
        public static readonly string Dir = Path.Combine(Vpn.Dir, "awg");
        public static readonly string Exe = Path.Combine(Dir, "amneziawg.exe");
        static readonly string VersionFile = Path.Combine(Dir, "version.txt");

        public static string Version { get { return File.Exists(VersionFile) && File.Exists(Exe) ? File.ReadAllText(VersionFile).Trim() : null; } }

        public static bool IsAwg(string link) { return link != null && link.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase); }

        // ---------- Файл настроек ----------
        // Текст похож на файл WireGuard/AmneziaWG: есть разделы [Interface] и [Peer]
        public static bool LooksLikeConf(string text)
        {
            return text != null && Regex.IsMatch(text, @"^\s*\[Interface\]", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                                && Regex.IsMatch(text, @"^\s*\[Peer\]", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        }

        // Поля файла: «Раздел.Поле» → значение (у нескольких [Peer] — значения первого)
        public static Dictionary<string, string> Fields(string conf)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = "";
            foreach (string raw in (conf ?? "").Split('\n'))
            {
                string line = raw.Split('#')[0].Trim();
                Match s = Regex.Match(line, @"^\[(\w+)\]$");
                if (s.Success) { section = s.Groups[1].Value; continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = section + "." + line.Substring(0, eq).Trim();
                if (!d.ContainsKey(key)) d[key] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        // Без этих полей туннель не поднимется — проверяем сразу при добавлении, а не при подключении
        public static Dictionary<string, string> Check(string conf)
        {
            if (!LooksLikeConf(conf)) throw new Exception("Это не файл настроек AmneziaWG: нужны разделы [Interface] и [Peer]");
            Dictionary<string, string> f = Fields(conf);
            foreach (string key in new[] { "Interface.PrivateKey", "Interface.Address", "Peer.PublicKey", "Peer.Endpoint" })
                if (!f.ContainsKey(key) || f[key].Length == 0) throw new Exception(Tr("В файле настроек AmneziaWG нет поля") + " " + key.Split('.')[1]);
            if (f["Interface.PrivateKey"].StartsWith("$")) throw new Exception("В файле настроек AmneziaWG нет ключа — скачайте файл в приложении Amnezia заново");
            return f;
        }

        // Файл настроек → сервер. Имя — по имени файла (Amnezia называет файлы кодом страны: «DE.conf» → флаг Германии), иначе по адресу сервера
        public static VpnServer FromConf(string conf, string fileName)
        {
            Dictionary<string, string> f = Check(conf);
            string stem = string.IsNullOrEmpty(fileName) ? "" : Path.GetFileNameWithoutExtension(fileName).Trim();
            if (stem.Length == 0) stem = Regex.Replace(f["Peer.Endpoint"], @":\d+$", "").Trim('[', ']');
            else if (stem.Length == 2) stem = stem.ToUpperInvariant();
            string name = "AmneziaWG · " + stem;
            string text = conf.Replace("\r\n", "\n").Trim() + "\n";
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return new VpnServer { Name = name, Link = Scheme + b64 + "#" + Uri.EscapeDataString(name) };
        }

        public static string ConfOf(string link)
        {
            string s = link.Substring(Scheme.Length).Split('#')[0].Replace('-', '+').Replace('_', '/');
            while (s.Length % 4 != 0) s += "=";
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch { throw new Exception("Ключ AmneziaWG повреждён — добавьте файл настроек заново"); }
        }

        static string Tr(string s) { return I18n.T(s); }

        // ---------- Ядро ----------
        static string Arch { get { return Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") == "ARM64" ? "arm64" : "amd64"; } }

        public static Task<Release> Latest() { return Util.LatestRelease(Repo, n => n.StartsWith("amneziawg-" + Arch + "-") && n.EndsWith(".msi")); }

        // MSI скачивается с проверкой SHA-256 и распаковывается без установки: msiexec /a только копирует файлы пакета
        // (amneziawg.exe, awg.exe, wintun.dll) и ничего не регистрирует в системе
        public static async Task Install(Release release)
        {
            Log.Write("Скачиваю ядро AmneziaWG " + release.Version + "...");
            string tmp = Path.Combine(Paths.Root, "awg-download");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string msi = Path.Combine(tmp, "amneziawg.msi");
            await Util.Download(release, msi);
            string extracted = Path.Combine(tmp, "x");
            int code = (await RunAsync("msiexec.exe", "/a \"" + msi + "\" /qn TARGETDIR=\"" + extracted + "\"", 120000)).Item1;
            string exe = Directory.Exists(extracted) ? Directory.GetFiles(extracted, "amneziawg.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
            if (exe == null) throw new Exception("Не удалось распаковать AmneziaWG (msiexec: код " + code + ")");
            Directory.CreateDirectory(Dir);
            foreach (string file in Directory.GetFiles(Path.GetDirectoryName(exe)))
                File.Copy(file, Path.Combine(Dir, Path.GetFileName(file)), true);
            File.WriteAllText(VersionFile, release.Version);
            try { Directory.Delete(tmp, true); } catch { }
            Log.Write("Установлено ядро AmneziaWG " + release.Version);
        }

        // ---------- Туннель ----------
        // Два вида туннеля. «Весь трафик»: туннель ZapretVPN сам забирает весь интернет (свои маршруты, как в приложении Amnezia).
        // «Только выбранное» и «Россия напрямую»: туннель ZapretAWG без маршрутов (Table = off) — выход для sing-box,
        // который остаётся главным и решает, что пустить через VPN, а что напрямую
        public const string ChainTunnel = "ZapretAWG";
        static readonly string[] Tunnels = { Vpn.InterfaceName, ChainTunnel };

        static string ConfFile(string tunnel) { return Path.Combine(Dir, tunnel + ".conf"); }   // имя файла — это имя туннеля и адаптера
        static string ServiceName(string tunnel) { return "AmneziaWGTunnel$" + tunnel; }

        static ServiceControllerStatus? Status(string tunnel)
        {
            try { using (ServiceController sc = new ServiceController(ServiceName(tunnel))) return sc.Status; }
            catch { return null; }   // службы нет
        }

        // Туннель «весь трафик» работает (тогда sing-box не нужен)
        public static bool Running { get { return Status(Vpn.InterfaceName) == ServiceControllerStatus.Running; } }

        // Туннель-выход для sing-box должен работать, а остановился — VPN в целом не работает, его нужно перезапустить
        public static bool ChainBroken { get { ServiceControllerStatus? s = Status(ChainTunnel); return s.HasValue && s != ServiceControllerStatus.Running; } }

        static NetworkInterface Adapter(string tunnel)
        {
            try { return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == tunnel); }
            catch { return null; }
        }

        // Когда поднят туннель — для «Подключено 1:23:45» после перезапуска лаунчера: файл настроек пишется при каждом подключении
        public static DateTime? StartedAt() { return Running && File.Exists(ConfFile(Vpn.InterfaceName)) ? File.GetLastWriteTime(ConfFile(Vpn.InterfaceName)) : (DateTime?)null; }

        // Настройки туннеля-выхода: без маршрутов (иначе он забрал бы весь трафик и включил блокировку всего остального)
        // и без DNS (адреса сайтов узнаёт sing-box: для VPN — через VPN, для остального — как обычно)
        public static string ChainConf(string conf)
        {
            List<string> lines = new List<string>();
            string section = "";
            foreach (string raw in conf.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                Match s = Regex.Match(line, @"^\[(\w+)\]");
                if (s.Success)
                {
                    if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase)) lines.Add("Table = off");
                    section = s.Groups[1].Value;
                }
                else if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(line, @"^(DNS|Table)\s*=", RegexOptions.IgnoreCase)) continue;
                lines.Add(raw.TrimEnd());
            }
            if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase)) lines.Add("Table = off");   // [Interface] — последний раздел
            return string.Join("\n", lines).Trim() + "\n";
        }

        // Выход sing-box в туннель AmneziaWG: соединения привязываются к его адаптеру и к его адресу. Без адреса Windows может
        // подставить отправителем адрес домашней сети (192.168…) — сервер AmneziaWG такие пакеты молча отбрасывает
        public static Dictionary<string, object> ChainOutbound(string conf)
        {
            Dictionary<string, object> o = new Dictionary<string, object> { { "type", "direct" }, { "tag", "proxy" }, { "bind_interface", ChainTunnel } };
            string ip = TunnelAddress(conf);
            if (ip != null) o["inet4_bind_address"] = ip;
            return o;
        }

        // Адрес IPv4 туннеля из Address («10.8.1.2/32, fd00::2/128» → 10.8.1.2); null — нет
        public static string TunnelAddress(string conf)
        {
            string value;
            if (!Fields(conf).TryGetValue("Interface.Address", out value)) return null;
            foreach (string part in value.Split(','))
            {
                System.Net.IPAddress ip;
                if (System.Net.IPAddress.TryParse(part.Trim().Split('/')[0], out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return ip.ToString();
            }
            return null;
        }

        // chain — туннель-выход для sing-box (режимы «Только выбранное» и «Россия напрямую»), иначе — весь трафик через туннель
        public static async Task Start(VpnServer server, bool chain)
        {
            string conf = ConfOf(server.Link);
            Check(conf);
            if (Version == null) await Install(await Latest());
            await Stop();
            string tunnel = chain ? ChainTunnel : Vpn.InterfaceName;
            string file = ConfFile(tunnel);
            File.WriteAllText(file, (chain ? ChainConf(conf) : conf).Replace("\n", "\r\n"), new UTF8Encoding(false));
            Tuple<int, string> install = await RunAsync(Exe, "/installtunnelservice \"" + file + "\"", 30000);
            if (install.Item1 != 0) { await Stop(); throw new Exception("AmneziaWG не запустил туннель: " + Short(install.Item2, "код " + install.Item1)); }
            // служба ставится с автозапуском — после перезагрузки VPN поднимался бы сам, без лаунчера. Включать его — дело лаунчера
            await RunAsync("sc.exe", "config \"" + ServiceName(tunnel) + "\" start= demand", 10000);
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(250);
                NetworkInterface ni = Adapter(tunnel);
                if (ni != null && ni.OperationalStatus == OperationalStatus.Up && Status(tunnel) == ServiceControllerStatus.Running)
                {
                    if (chain)
                    {
                        await AddFallbackRoute();
                        await WaitAddress(tunnel, TunnelAddress(conf));   // sing-box привязывается к этому адресу — он должен уже быть у адаптера
                    }
                    return;
                }
                if (i >= 8 && Status(tunnel) == ServiceControllerStatus.Stopped) break;   // служба остановилась — дальше ждать незачем
            }
            string log = await Task.Run(() => LastLogLine());
            await Stop();
            throw new Exception("Туннель AmneziaWG не поднялся" + (log != null ? ": " + log : " — проверьте, что файл настроек свежий и подписка активна"));
        }

        // Адрес туннеля появляется у адаптера не сразу (Windows сначала проверяет, не занят ли он). Пока его нет, привязка
        // к нему даёт ошибку «The requested address is not valid in its context» — ждём до 5 секунд
        static async Task WaitAddress(string tunnel, string ip)
        {
            if (ip == null) return;
            for (int i = 0; i < 25; i++)
            {
                NetworkInterface ni = Adapter(tunnel);
                if (ni != null && ni.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == ip && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)) return;
                await Task.Delay(200);
            }
        }

        // Windows отправляет пакет через адаптер, только если у адаптера есть подходящий маршрут. Даём туннелю-выходу маршрут
        // «по умолчанию» с самым большим весом: обычный трафик по нему не пойдёт, а соединения sing-box, привязанные к адаптеру, — пойдут
        static async Task AddFallbackRoute()
        {
            foreach (string args in new[] { "interface ipv4 set interface interface=\"" + ChainTunnel + "\" metric=9000",
                                            "interface ipv4 add route prefix=0.0.0.0/0 interface=\"" + ChainTunnel + "\" metric=9000 store=active" })
            {
                Tuple<int, string> r = await RunAsync("netsh.exe", args, 10000);
                if (r.Item1 != 0) Log.Write("AmneziaWG: netsh " + args.Split(' ')[2] + " — " + Short(r.Item2, "код " + r.Item1));   // без маршрута трафик в туннель не пойдёт
            }
        }

        public static async Task Stop()
        {
            foreach (string tunnel in Tunnels)
            {
                if (Status(tunnel) == null) { DeleteConf(tunnel); continue; }
                if (File.Exists(Exe)) await RunAsync(Exe, "/uninstalltunnelservice " + tunnel, 30000);
                else
                {
                    await RunAsync("sc.exe", "stop \"" + ServiceName(tunnel) + "\"", 15000);
                    await RunAsync("sc.exe", "delete \"" + ServiceName(tunnel) + "\"", 15000);
                }
                for (int i = 0; i < 40 && Status(tunnel) != null; i++) await Task.Delay(250);   // служба удаляется не мгновенно
                DeleteConf(tunnel);
            }
        }

        static void DeleteConf(string tunnel) { try { File.Delete(ConfFile(tunnel)); } catch { } }   // в файле — закрытый ключ, без туннеля ему лежать незачем
        // Трафик через туннель с момента подключения: [загружено, отдано]
        public static long[] Traffic()
        {
            NetworkInterface ni = Running ? Adapter(Vpn.InterfaceName) : null;
            if (ni == null) return null;
            try { IPInterfaceStatistics st = ni.GetIPStatistics(); return new[] { st.BytesReceived, st.BytesSent }; }
            catch { return null; }
        }

        // Открывается ли сайт через туннель (весь трафик идёт через него): время в мс, -1 — нет
        public static async Task<int> Delay()
        {
            try
            {
                using (System.Net.Http.HttpClient c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) })
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    using (System.Net.Http.HttpResponseMessage r = await c.GetAsync(Vpn.TestUrl))
                        return r.IsSuccessStatusCode ? (int)sw.ElapsedMilliseconds : -1;
                }
            }
            catch { return -1; }
        }

        // ---------- Диагностика ----------
        // Работающий туннель: «весь трафик» или выход для sing-box; null — ни одного
        public static string ActiveTunnel
        {
            get { return Running ? Vpn.InterfaceName : Status(ChainTunnel) == ServiceControllerStatus.Running ? ChainTunnel : null; }
        }

        // Счётчики адаптера туннеля: Tx — сколько ушло в туннель (к серверу), Rx — сколько пришло из него (ответы сервера).
        // awg.exe из пакета AmneziaWG для этого не годится: он старее ядра и отвечает «protocol error»
        public class TunnelStats { public long Rx, Tx; }

        public static TunnelStats Stats(string tunnel)
        {
            NetworkInterface ni = Adapter(tunnel);
            if (ni == null) return null;
            try { IPInterfaceStatistics st = ni.GetIPStatistics(); return new TunnelStats { Rx = st.BytesReceived, Tx = st.BytesSent }; }
            catch { return null; }
        }
        // Адреса сервера AmneziaWG — пакеты самого туннеля к нему sing-box должен пускать напрямую, а не по кругу в VPN
        public static string[] EndpointIps(string conf)
        {
            string endpoint;
            if (!Fields(conf).TryGetValue("Peer.Endpoint", out endpoint)) return new string[0];
            string host = Regex.Replace(endpoint.Trim(), @":\d+$", "").Trim('[', ']');
            System.Net.IPAddress ip;
            if (System.Net.IPAddress.TryParse(host, out ip)) return new[] { ip.ToString() };
            try { return System.Net.Dns.GetHostAddresses(host).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.ToString()).ToArray(); }
            catch { return new string[0]; }
        }

        // Последние строки журнала ядра AmneziaWG — для журнала лаунчера, когда что-то не так
        public static string LogTail(int count)
        {
            if (!File.Exists(Exe)) return null;
            string output;
            RunTool(Exe, "/dumplog", 10000, out output);
            string[] lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Error")).ToArray();
            lines = lines.Where(l => l.Contains("[" + ChainTunnel + "]") || l.Contains("[" + Vpn.InterfaceName + "]")).ToArray();   // только наши туннели
            return lines.Length == 0 ? null : string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - count)).Select(l => Regex.Replace(l, @"^\S+\s+\S+:\s*", "")));
        }

        // Последняя строка журнала AmneziaWG — причина, если туннель не поднялся
        static string LastLogLine()
        {
            if (!File.Exists(Exe)) return null;
            string output;
            RunTool(Exe, "/dumplog", 10000, out output);
            string last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("Error"));
            return last == null ? null : Regex.Replace(last, @"^\S+\s+\S+:\s*", "");   // без даты и источника
        }

        static string Short(string text, string fallback)
        {
            string t = (text ?? "").Trim();
            return t.Length == 0 ? fallback : t.Length > 300 ? t.Substring(0, 300) + "…" : t;
        }

        static Task<Tuple<int, string>> RunAsync(string exe, string args, int timeoutMs)
        {
            return Task.Run(() => { string output; int code = RunTool(exe, args, timeoutMs, out output); return Tuple.Create(code, output); });
        }

        // Консольный запуск с выводом stdout и stderr: AmneziaWG пишет ошибки в stderr (без него показал бы окно с ошибкой)
        static int RunTool(string exe, string args, int timeoutMs, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                Task<string> err = p.StandardError.ReadToEndAsync();
                Task<string> outp = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "превышено время ожидания"; return -1; }
                output = outp.Result + err.Result;
                return p.ExitCode;
            }
        }
    }
}
