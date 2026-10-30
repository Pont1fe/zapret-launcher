// Встроенный VPN-клиент: ядро sing-box (SagerNet), ваш сервер или подписка.
// Умная маршрутизация: через VPN идут только выбранные сайты и программы, остальное — напрямую (и через zapret)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZapretLauncher
{
    public class VpnServer
    {
        public string Name { get; set; }
        public string Link { get; set; }
    }

    static class Vpn
    {
        public const string Repo = "SagerNet/sing-box";
        public const string InterfaceName = "ZapretVPN";   // имя адаптера VPN — по нему лаунчер отличает свой VPN от чужого
        public static readonly string Dir = Path.Combine(Paths.Root, "vpn");
        public static readonly string Exe = Path.Combine(Dir, "sing-box.exe");
        public static readonly string ConfigFile = Path.Combine(Dir, "config.json");
        public static readonly string LogFile = Path.Combine(Dir, "sing-box.log");
        static readonly string VersionFile = Path.Combine(Dir, "version.txt");
        static readonly string[] Schemes = { "vless://", "vmess://", "trojan://", "ss://", "hysteria2://", "hy2://", "tuic://" };

        public static string Version { get { return File.Exists(VersionFile) && File.Exists(Exe) ? File.ReadAllText(VersionFile).Trim() : null; } }

        public static Process[] Processes()
        {
            return Process.GetProcessesByName("sing-box").Where(p =>
            {
                try { return string.Equals(p.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase); } catch { return false; }
            }).ToArray();
        }

        public static bool Running { get { return Processes().Length > 0; } }

        // Когда запущено ядро — для «Подключено 1:23:45» после перезапуска лаунчера
        public static DateTime? StartedAt()
        {
            try { Process p = Processes().FirstOrDefault(); return p == null ? (DateTime?)null : p.StartTime; } catch { return null; }
        }

        static string Asset { get { return Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") == "ARM64" ? "-windows-arm64.zip" : "-windows-amd64.zip"; } }

        public static Task<Release> Latest()
        {
            return Util.LatestRelease(Repo, n => n.StartsWith("sing-box-") && n.EndsWith(Asset) && !n.Contains("legacy"));
        }

        // Скачивает ядро (с проверкой SHA-256) и кладёт в папку vpn; работающее ядро перезапускать — дело вызывающего
        public static async Task Install(Release release)
        {
            Log.Write("Скачиваю ядро VPN sing-box " + release.Version + "...");
            string tmp = Path.Combine(Paths.Root, "vpn-download");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string zip = Path.Combine(tmp, "sing-box.zip");
            await Util.Download(release, zip);
            string extracted = Path.Combine(tmp, "x");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, extracted));
            string exe = Directory.GetFiles(extracted, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe == null) throw new Exception("В архиве sing-box нет sing-box.exe — установка отменена");
            Directory.CreateDirectory(Dir);
            foreach (string f in Directory.GetFiles(Path.GetDirectoryName(exe)))
                File.Copy(f, Path.Combine(Dir, Path.GetFileName(f)), true);
            File.WriteAllText(VersionFile, release.Version);
            try { Directory.Delete(tmp, true); } catch { }
            Log.Write("Установлено ядро VPN sing-box " + release.Version);
        }

        // ---------- Ссылки и подписки ----------
        public static bool IsLink(string s) { return Schemes.Any(x => s.StartsWith(x, StringComparison.OrdinalIgnoreCase)); }

        // Текст со ссылками (по одной в строке) или адрес подписки → список серверов
        public static async Task<List<VpnServer>> Parse(string text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                text = DecodeSubscription(await FetchSubscription(text));
            List<VpnServer> list = new List<VpnServer>();
            foreach (string raw in text.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string link = raw.Trim();
                if (!IsLink(link)) continue;
                BuildOutbound(link);   // ссылку, которую не разобрать, сразу отбрасываем с понятной ошибкой
                list.Add(new VpnServer { Name = NameOf(link), Link = link });
            }
            if (list.Count == 0)
                throw new Exception(text.StartsWith("happ://") ? "Это зашифрованная ссылка Happ — её может открыть только Happ. Попросите у провайдера обычную ссылку подписки или ключ vless://"
                                                               : "Не нашёл ни одной ссылки на сервер (vless://, vmess://, trojan://, ss://, hysteria2://, tuic://)");
            return list;
        }

        // Подписка — обычно base64 со списком ссылок, иногда сразу список
        static string DecodeSubscription(string body)
        {
            string t = body.Trim();
            if (IsLink(t.Split('\n')[0].Trim())) return t;
            string b = B64(t.Replace("\r", "").Replace("\n", ""));
            if (b != null && b.Split('\n').Any(l => IsLink(l.Trim()))) return b;
            if (t.StartsWith("{") || t.StartsWith("proxies:")) throw new Exception("Подписка в формате JSON/Clash пока не поддерживается — нужна подписка со ссылками");
            return t;
        }

        static string B64(string s)
        {
            try
            {
                s = s.Trim().Replace('-', '+').Replace('_', '/');
                while (s.Length % 4 != 0) s += "=";
                return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
            catch { return null; }
        }

        static string NameOf(string link)
        {
            if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> j = VmessJson(link);
                object ps;
                if (j != null && j.TryGetValue("ps", out ps) && ps is string && ((string)ps).Length > 0) return (string)ps;
            }
            int hash = link.IndexOf('#');
            if (hash >= 0 && hash < link.Length - 1) return Uri.UnescapeDataString(link.Substring(hash + 1).Replace('+', ' '));
            try { Uri u = new Uri(link.Split('#')[0]); return u.Host + ":" + u.Port; } catch { return link.Split(':')[0]; }
        }

        static Dictionary<string, object> VmessJson(string link)
        {
            string json = B64(link.Substring(8).Split('#')[0]);
            return json == null ? null : new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
        }

        static Dictionary<string, string> Query(Uri u)
        {
            Dictionary<string, string> q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in u.Query.TrimStart('?').Split('&'))
            {
                if (part.Length == 0) continue;
                string[] kv = part.Split(new[] { '=' }, 2);
                q[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
            }
            return q;
        }

        static string Get(Dictionary<string, string> q, string key, string def = "") { string v; return q.TryGetValue(key, out v) && v.Length > 0 ? v : def; }
        static string S(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : ""; }

        // ---------- Xray для ключей с транспортом xhttp (sing-box его не умеет) ----------
        // sing-box держит VPN-адаптер и маршрутизацию, а трафик такого сервера отдаёт локальному Xray (SOCKS на 127.0.0.1)
        public const string XrayRepo = "XTLS/Xray-core";
        public static readonly string XrayExe = Path.Combine(Dir, "xray.exe");
        static readonly string XrayConfig = Path.Combine(Dir, "xray.json");
        static readonly string XrayVersionFile = Path.Combine(Dir, "xray-version.txt");
        static int xrayPort;

        public static string XrayVersion { get { return File.Exists(XrayVersionFile) && File.Exists(XrayExe) ? File.ReadAllText(XrayVersionFile).Trim() : null; } }

        public static bool NeedsXray(string link)
        {
            if (!link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string t = Get(Query(new Uri(link.Split('#')[0])), "type").ToLowerInvariant();
                return t == "xhttp" || t == "splithttp";
            }
            catch { return false; }
        }

        public static Task<Release> XrayLatest()
        {
            string asset = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") == "ARM64" ? "Xray-windows-arm64-v8a.zip" : "Xray-windows-64.zip";
            return Util.LatestRelease(XrayRepo, n => n == asset);
        }

        public static async Task InstallXray(Release release)
        {
            Log.Write("Скачиваю ядро Xray " + release.Version + " для серверов с транспортом xhttp...");
            string tmp = Path.Combine(Paths.Root, "xray-download");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string zip = Path.Combine(tmp, "xray.zip");
            await Util.Download(release, zip);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, Path.Combine(tmp, "x")));
            string exe = Directory.GetFiles(Path.Combine(tmp, "x"), "xray.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe == null) throw new Exception("В архиве Xray нет xray.exe — установка отменена");
            Directory.CreateDirectory(Dir);
            File.Copy(exe, XrayExe, true);
            File.WriteAllText(XrayVersionFile, release.Version.TrimStart('v'));
            try { Directory.Delete(tmp, true); } catch { }
            Log.Write("Установлено ядро Xray " + release.Version);
        }

        // Конфигурация Xray: SOCKS на 127.0.0.1:port → сервер vless с транспортом xhttp
        public static string BuildXrayConfig(string link, int port)
        {
            Uri u = new Uri(link.Split('#')[0]);
            Dictionary<string, string> q = Query(u);
            string host = u.Host.Trim('[', ']');
            Dictionary<string, object> user = new Dictionary<string, object> { { "id", Uri.UnescapeDataString(u.UserInfo) }, { "encryption", Get(q, "encryption", "none") } };
            if (Get(q, "flow").Length > 0) user["flow"] = Get(q, "flow");
            string security = Get(q, "security", "none").ToLowerInvariant();
            Dictionary<string, object> stream = new Dictionary<string, object> { { "network", "xhttp" }, { "security", security } };
            if (security == "reality")
                stream["realitySettings"] = new Dictionary<string, object>
                {
                    { "serverName", Get(q, "sni", host) }, { "fingerprint", Get(q, "fp", "chrome") }, { "publicKey", Get(q, "pbk") },
                    { "shortId", Get(q, "sid") }, { "spiderX", Get(q, "spx") }
                };
            else if (security == "tls")
            {
                Dictionary<string, object> tls = new Dictionary<string, object> { { "serverName", Get(q, "sni", Get(q, "host", host)) } };
                if (Get(q, "fp").Length > 0) tls["fingerprint"] = Get(q, "fp");
                if (Get(q, "alpn").Length > 0) tls["alpn"] = Get(q, "alpn").Split(',');
                if (Get(q, "allowInsecure") == "1" || Get(q, "insecure") == "1") tls["allowInsecure"] = true;
                stream["tlsSettings"] = tls;
            }
            Dictionary<string, object> xhttp = new Dictionary<string, object> { { "path", Get(q, "path", "/") }, { "mode", Get(q, "mode", "auto") } };
            if (Get(q, "host").Length > 0) xhttp["host"] = Get(q, "host");
            string extra = Get(q, "extra");
            if (extra.Length > 0)
            {
                object parsed = null;
                try { parsed = new JavaScriptSerializer().DeserializeObject(extra); } catch { }
                if (parsed != null) xhttp["extra"] = parsed;
            }
            stream["xhttpSettings"] = xhttp;
            Dictionary<string, object> cfg = new Dictionary<string, object>
            {
                { "log", new Dictionary<string, object> { { "loglevel", "warning" }, { "error", Path.Combine(Dir, "xray.log") } } },
                { "inbounds", new object[] { new Dictionary<string, object> { { "tag", "socks-in" }, { "listen", "127.0.0.1" }, { "port", port }, { "protocol", "socks" }, { "settings", new Dictionary<string, object> { { "udp", true } } } } } },
                { "outbounds", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "tag", "proxy" }, { "protocol", "vless" },
                            { "settings", new Dictionary<string, object> { { "vnext", new object[] { new Dictionary<string, object> { { "address", host }, { "port", u.Port > 0 ? u.Port : 443 }, { "users", new object[] { user } } } } } } },
                            { "streamSettings", stream },
                        }
                    } },
            };
            return new JavaScriptSerializer().Serialize(cfg);
        }

        // Проверка конфигурации Xray самим ядром; null — всё в порядке
        public static string CheckXray(string path)
        {
            ProcessStartInfo psi = new ProcessStartInfo(XrayExe, "run -test -c \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Dir };
            using (Process p = Process.Start(psi))
            {
                Task<string> err = p.StandardError.ReadToEndAsync();
                string output = p.StandardOutput.ReadToEnd() + err.Result;
                p.WaitForExit(20000);
                if (p.HasExited && p.ExitCode == 0) return null;
                string last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
                return last ?? "xray -test: ошибка";
            }
        }

        static Process[] XrayProcesses()
        {
            return Process.GetProcessesByName("xray").Where(p =>
            {
                try { return string.Equals(p.MainModule.FileName, XrayExe, StringComparison.OrdinalIgnoreCase); } catch { return false; }
            }).ToArray();
        }

        static int FreePort()
        {
            System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // Запускает Xray и ждёт, пока он начнёт принимать соединения
        static async Task StartXray(string link)
        {
            if (XrayVersion == null) await InstallXray(await XrayLatest());
            xrayPort = FreePort();
            File.WriteAllText(XrayConfig, BuildXrayConfig(link, xrayPort), new UTF8Encoding(false));
            string error = await Task.Run(() => CheckXray(XrayConfig));
            if (error != null) throw new Exception("Настройки Xray не прошли проверку: " + error);
            Process proc = Process.Start(new ProcessStartInfo(XrayExe, "run -c \"" + XrayConfig + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Dir });
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(150);
                if (proc.HasExited) throw new Exception("Ядро Xray остановилось сразу после запуска");
                try
                {
                    using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
                        if (c.ConnectAsync("127.0.0.1", xrayPort).Wait(300) && c.Connected) return;
                }
                catch { }
            }
            throw new Exception("Ядро Xray не начало работать");
        }

        // Сервер из ссылки → outbound sing-box с тегом proxy
        public static Dictionary<string, object> BuildOutbound(string link)
        {
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["tag"] = "proxy";
            if (NeedsXray(link))
            {
                // xhttp — через локальный Xray; параметры ключа проверяет BuildXrayConfig
                BuildXrayConfig(link, 1);
                o["type"] = "socks";
                o["server"] = "127.0.0.1";
                o["server_port"] = xrayPort > 0 ? xrayPort : 10808;
                o["version"] = "5";
                return o;
            }
            string scheme = link.Substring(0, link.IndexOf("://")).ToLowerInvariant();
            if (scheme == "vmess")
            {
                Dictionary<string, object> j = VmessJson(link);
                if (j == null) throw new Exception("Не удалось прочитать ссылку vmess://");
                o["type"] = "vmess";
                o["server"] = S(j, "add");
                o["server_port"] = int.Parse(S(j, "port"));
                o["uuid"] = S(j, "id");
                o["alter_id"] = S(j, "aid").Length > 0 ? int.Parse(S(j, "aid")) : 0;
                o["security"] = S(j, "scy").Length > 0 ? S(j, "scy") : "auto";
                Dictionary<string, string> q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "type", S(j, "net") }, { "path", S(j, "path") }, { "host", S(j, "host") }, { "serviceName", S(j, "path") },
                    { "security", S(j, "tls") }, { "sni", S(j, "sni") }, { "fp", S(j, "fp") }, { "alpn", S(j, "alpn") }
                };
                Transport(o, q, S(j, "add"));
                return o;
            }

            Uri u;
            string body = link.Split('#')[0];
            if (scheme == "ss" && !body.Contains("@"))   // старый формат ss://base64(method:password@host:port)
            {
                string decoded = B64(body.Substring(5).Split('?')[0]);
                if (decoded == null) throw new Exception("Не удалось прочитать ссылку ss://");
                body = "ss://" + decoded;
            }
            try { u = new Uri(body); } catch { throw new Exception("Не удалось прочитать ссылку " + scheme + "://"); }
            Dictionary<string, string> query = Query(u);
            string user = Uri.UnescapeDataString(u.UserInfo);
            o["server"] = u.Host.Trim('[', ']');
            o["server_port"] = u.Port > 0 ? u.Port : 443;

            if (scheme == "vless")
            {
                o["type"] = "vless";
                o["uuid"] = user;
                string flow = Get(query, "flow");
                if (flow.Length > 0) o["flow"] = flow;
                Transport(o, query, u.Host);
            }
            else if (scheme == "trojan")
            {
                o["type"] = "trojan";
                o["password"] = user;
                if (Get(query, "security").Length == 0) query["security"] = "tls";   // trojan всегда поверх TLS
                Transport(o, query, u.Host);
            }
            else if (scheme == "ss")
            {
                string userinfo = user.Contains(":") ? user : B64(u.UserInfo) ?? "";
                int colon = userinfo.IndexOf(':');
                if (colon < 0) throw new Exception("В ссылке ss:// нет метода и пароля");
                o["type"] = "shadowsocks";
                o["method"] = userinfo.Substring(0, colon);
                o["password"] = userinfo.Substring(colon + 1);
            }
            else if (scheme == "hysteria2" || scheme == "hy2")
            {
                o["type"] = "hysteria2";
                o["password"] = user;
                Dictionary<string, object> tls = new Dictionary<string, object> { { "enabled", true }, { "server_name", Get(query, "sni", u.Host) } };
                if (Get(query, "insecure") == "1") tls["insecure"] = true;
                o["tls"] = tls;
                if (Get(query, "obfs") == "salamander") o["obfs"] = new Dictionary<string, object> { { "type", "salamander" }, { "password", Get(query, "obfs-password") } };
            }
            else if (scheme == "tuic")
            {
                int colon = user.IndexOf(':');
                o["type"] = "tuic";
                o["uuid"] = colon < 0 ? user : user.Substring(0, colon);
                o["password"] = colon < 0 ? "" : user.Substring(colon + 1);
                o["congestion_control"] = Get(query, "congestion_control", "bbr");
                Dictionary<string, object> tls = new Dictionary<string, object> { { "enabled", true }, { "server_name", Get(query, "sni", u.Host) } };
                string alpn = Get(query, "alpn");
                if (alpn.Length > 0) tls["alpn"] = alpn.Split(',');
                if (Get(query, "allow_insecure") == "1" || Get(query, "insecure") == "1") tls["insecure"] = true;
                o["tls"] = tls;
            }
            else throw new Exception("Неизвестный тип ссылки: " + scheme);
            return o;
        }

        // TLS / Reality и транспорт (ws, grpc, httpupgrade) для vless, vmess и trojan
        static void Transport(Dictionary<string, object> o, Dictionary<string, string> q, string host)
        {
            string net = Get(q, "type", "tcp").ToLowerInvariant();
            if (net == "xhttp" || net == "splithttp")
                throw new Exception("Транспорт xhttp поддерживается только для ключей vless://");
            if (net == "ws")
                o["transport"] = new Dictionary<string, object> { { "type", "ws" }, { "path", Get(q, "path", "/") }, { "headers", new Dictionary<string, object> { { "Host", Get(q, "host", host) } } } };
            else if (net == "grpc")
                o["transport"] = new Dictionary<string, object> { { "type", "grpc" }, { "service_name", Get(q, "serviceName") } };
            else if (net == "httpupgrade")
                o["transport"] = new Dictionary<string, object> { { "type", "httpupgrade" }, { "path", Get(q, "path", "/") }, { "host", Get(q, "host", host) } };
            else if (net != "tcp" && net != "raw" && net != "none")
                throw new Exception("Транспорт " + net + " пока не поддерживается");

            string security = Get(q, "security").ToLowerInvariant();
            if (security != "tls" && security != "reality") return;
            Dictionary<string, object> tls = new Dictionary<string, object> { { "enabled", true }, { "server_name", Get(q, "sni", Get(q, "host", host)) } };
            string alpn = Get(q, "alpn");
            if (alpn.Length > 0) tls["alpn"] = alpn.Split(',');
            if (Get(q, "allowInsecure") == "1" || Get(q, "insecure") == "1") tls["insecure"] = true;
            string fp = Get(q, "fp", security == "reality" ? "chrome" : "");
            if (fp.Length > 0) tls["utls"] = new Dictionary<string, object> { { "enabled", true }, { "fingerprint", fp } };
            if (security == "reality")
                tls["reality"] = new Dictionary<string, object> { { "enabled", true }, { "public_key", Get(q, "pbk") }, { "short_id", Get(q, "sid") } };
            o["tls"] = tls;
        }

        // ---------- Страна сервера ----------
        static readonly string[][] Countries =
        {
            new[] { "NL", "нидерланд", "голланд", "netherlands", "holland", "amsterdam", "амстердам" },
            new[] { "DE", "герман", "germany", "frankfurt", "франкфурт", "berlin", "берлин" },
            new[] { "FI", "финлянд", "finland", "helsinki", "хельсинки" },
            new[] { "SE", "швец", "sweden", "stockholm", "стокгольм" },
            new[] { "NO", "норвег", "norway", "oslo" },
            new[] { "DK", "дания", "дании", "denmark", "copenhagen", "копенгаген" },
            new[] { "PL", "польш", "poland", "warsaw", "варшав" },
            new[] { "FR", "франц", "france", "paris", "париж" },
            new[] { "GB", "великобритан", "англи", "united kingdom", "england", "london", "лондон", "britain" },
            new[] { "US", "сша", "usa", "united states", "america", "америк", "new york", "нью-йорк", "los angeles" },
            new[] { "CA", "канад", "canada", "toronto" },
            new[] { "JP", "япони", "japan", "tokyo", "токио" },
            new[] { "SG", "сингапур", "singapore" },
            new[] { "HK", "гонконг", "hong kong", "hongkong" },
            new[] { "KR", "корея", "кореи", "korea", "seoul", "сеул" },
            new[] { "TR", "турц", "turkey", "türkiye", "istanbul", "стамбул" },
            new[] { "KZ", "казахстан", "kazakhstan", "almaty", "алматы", "астана" },
            new[] { "EE", "эстони", "estonia", "tallinn", "таллин" },
            new[] { "LV", "латви", "latvia", "riga", "рига" },
            new[] { "LT", "литв", "lithuania", "vilnius", "вильнюс" },
            new[] { "CH", "швейцари", "switzerland", "zurich", "цюрих" },
            new[] { "AT", "австри", "austria", "vienna", "вена" },
            new[] { "CZ", "чехи", "czech", "prague", "прага" },
            new[] { "IT", "итали", "italy", "milan", "милан" },
            new[] { "ES", "испани", "spain", "madrid", "мадрид" },
            new[] { "PT", "португал", "portugal", "lisbon" },
            new[] { "BE", "бельги", "belgium", "brussels" },
            new[] { "IE", "ирланд", "ireland", "dublin" },
            new[] { "RO", "румыни", "romania", "bucharest" },
            new[] { "HU", "венгри", "hungary", "budapest" },
            new[] { "BG", "болгари", "bulgaria", "sofia" },
            new[] { "RS", "серби", "serbia", "belgrade" },
            new[] { "MD", "молдов", "moldova", "chisinau" },
            new[] { "UA", "украин", "ukraine", "kyiv", "kiev", "киев" },
            new[] { "GE", "грузи", "georgia", "tbilisi", "тбилиси" },
            new[] { "AM", "армени", "armenia", "yerevan", "ереван" },
            new[] { "AE", "оаэ", "эмират", "uae", "emirates", "dubai", "дубай" },
            new[] { "IL", "израил", "israel", "tel aviv" },
            new[] { "IN", "индия", "индии", "india", "mumbai" },
            new[] { "AU", "австрали", "australia", "sydney" },
            new[] { "BR", "бразили", "brazil", "sao paulo" },
            new[] { "RU", "росси", "russia", "moscow", "москва" },
        };

        static readonly Dictionary<string, string> CountryNames = new Dictionary<string, string>
        {
            { "NL", "Нидерланды" }, { "DE", "Германия" }, { "FI", "Финляндия" }, { "SE", "Швеция" }, { "NO", "Норвегия" }, { "DK", "Дания" },
            { "PL", "Польша" }, { "FR", "Франция" }, { "GB", "Великобритания" }, { "US", "США" }, { "CA", "Канада" }, { "JP", "Япония" },
            { "SG", "Сингапур" }, { "HK", "Гонконг" }, { "KR", "Южная Корея" }, { "TR", "Турция" }, { "KZ", "Казахстан" }, { "EE", "Эстония" },
            { "LV", "Латвия" }, { "LT", "Литва" }, { "CH", "Швейцария" }, { "AT", "Австрия" }, { "CZ", "Чехия" }, { "IT", "Италия" },
            { "ES", "Испания" }, { "PT", "Португалия" }, { "BE", "Бельгия" }, { "IE", "Ирландия" }, { "RO", "Румыния" }, { "HU", "Венгрия" },
            { "BG", "Болгария" }, { "RS", "Сербия" }, { "MD", "Молдова" }, { "UA", "Украина" }, { "GE", "Грузия" }, { "AM", "Армения" },
            { "AE", "ОАЭ" }, { "IL", "Израиль" }, { "IN", "Индия" }, { "AU", "Австралия" }, { "BR", "Бразилия" }, { "RU", "Россия" },
        };

        public static string CountryName(string code) { string n; return code != null && CountryNames.TryGetValue(code, out n) ? n : null; }

        // Картинка флага из ресурсов лаунчера; null — флага нет
        static readonly Dictionary<string, System.Windows.Media.ImageSource> flags = new Dictionary<string, System.Windows.Media.ImageSource>();
        public static System.Windows.Media.ImageSource Flag(string code)
        {
            if (code == null) return null;
            System.Windows.Media.ImageSource img;
            if (flags.TryGetValue(code, out img)) return img;
            using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("flag-" + code.ToLowerInvariant() + ".png"))
            {
                if (s != null)
                {
                    System.Windows.Media.Imaging.BitmapImage b = new System.Windows.Media.Imaging.BitmapImage();
                    b.BeginInit(); b.StreamSource = s; b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
                    img = b;
                }
            }
            flags[code] = img;
            return img;
        }

        // Двухбуквенный код страны по названию сервера: флаг-эмодзи, «[NL]», название страны или города; null — не понять
        public static string CountryCode(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            // флаг-эмодзи — пара «региональных букв» U+1F1E6..U+1F1FF (в UTF-16 — суррогатные пары)
            for (int i = 0; i + 3 < name.Length; i++)
            {
                int a = char.IsSurrogatePair(name, i) ? char.ConvertToUtf32(name, i) : 0;
                if (a < 0x1F1E6 || a > 0x1F1FF || !char.IsSurrogatePair(name, i + 2)) continue;
                int b = char.ConvertToUtf32(name, i + 2);
                if (b >= 0x1F1E6 && b <= 0x1F1FF) return "" + (char)('A' + a - 0x1F1E6) + (char)('A' + b - 0x1F1E6);
            }
            string lower = name.ToLowerInvariant();
            foreach (string[] c in Countries)
                for (int k = 1; k < c.Length; k++)
                    if (StartsWord(lower, c[k])) return c[0];
            // код отдельным словом: «NL-1», «[DE] Server», «Server FI»
            foreach (string token in name.Split(new[] { ' ', '-', '_', '|', '[', ']', '(', ')', '.', ',', '#' }, StringSplitOptions.RemoveEmptyEntries))
                if (token.Length == 2 && Countries.Any(c => c[0] == token.ToUpperInvariant()) && token == token.ToUpperInvariant()) return token;
            return null;
        }

        // Слово (или корень) встречается в начале какого-нибудь слова: «германия» — да, «скорее» для «коре» — нет
        static bool StartsWord(string text, string word)
        {
            for (int i = text.IndexOf(word); i >= 0; i = text.IndexOf(word, i + 1))
                if (i == 0 || !char.IsLetter(text[i - 1])) return true;
            return false;
        }

        // Название без флагов-эмодзи (Windows рисует их просто буквами)
        public static string DisplayName(string name)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsSurrogatePair(name, i))
                {
                    int cp = char.ConvertToUtf32(name, i);
                    if (cp >= 0x1F1E6 && cp <= 0x1F1FF) { i++; continue; }
                }
                sb.Append(name[i]);
            }
            string s = sb.ToString().Trim();
            return s.Length > 0 ? s : name;
        }

        // ---------- Пинг серверов ----------
        public const int PingUdp = -2;   // hysteria2 и tuic работают по UDP — TCP-пинг к ним ничего не скажет

        // Время TCP-соединения с сервером (мс); -1 — не отвечает
        public static async Task<int> Ping(VpnServer server)
        {
            Dictionary<string, object> o;
            try { o = BuildOutbound(server.Link); } catch { return -1; }
            string type = (string)o["type"];
            if (type == "hysteria2" || type == "tuic") return PingUdp;
            string host = (string)o["server"];
            int port = (int)o["server_port"];
            if (NeedsXray(server.Link))   // в outbound — локальный Xray, пингуем сам сервер
            {
                Uri u = new Uri(server.Link.Split('#')[0]);
                host = u.Host.Trim('[', ']');
                port = u.Port > 0 ? u.Port : 443;
            }
            try
            {
                using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    Task connect = c.ConnectAsync(host, port);
                    if (await Task.WhenAny(connect, Task.Delay(3000)) != connect || connect.IsFaulted) return -1;
                    return (int)sw.ElapsedMilliseconds;
                }
            }
            catch { return -1; }
        }

        // ---------- Статистика трафика (Clash API ядра, только для лаунчера: 127.0.0.1 и секретный ключ) ----------
        static int apiPort;
        static string apiSecret;
        static readonly System.Net.Http.HttpClient Api = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        static void NewApiEndpoint()
        {
            System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            apiPort = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            apiSecret = Guid.NewGuid().ToString("N");
        }

        // ---------- Честная проверка серверов: настоящий запрос через каждый сервер ----------
        public const string TestUrl = "https://www.gstatic.com/generate_204";

        static async Task<string> ApiGet(int port, string secret, string path, int timeoutMs)
        {
            using (System.Net.Http.HttpClient c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) })
            {
                System.Net.Http.HttpRequestMessage req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "http://127.0.0.1:" + port + path);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
                using (System.Net.Http.HttpResponseMessage r = await c.SendAsync(req))
                    return r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync() : null;
            }
        }

        // Задержка запроса через сервер (мс), -1 — сайт через него не открылся
        static async Task<int> Delay(int port, string secret, string outbound)
        {
            try
            {
                string json = await ApiGet(port, secret, "/proxies/" + outbound + "/delay?timeout=6000&url=" + Uri.EscapeDataString(TestUrl), 8000);
                if (json == null) return -1;
                Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                object v;
                return d != null && d.TryGetValue("delay", out v) ? Convert.ToInt32(v) : -1;
            }
            catch { return -1; }
        }

        // Проверка всех серверов сразу: отдельное ядро без VPN-адаптера (прав администратора не нужно), через каждый сервер — запрос к TestUrl.
        // Для серверов xhttp на время проверки поднимается свой Xray, и запрос идёт через него
        public static async Task<int[]> Delays(IList<VpnServer> servers)
        {
            int[] result = new int[servers.Count];
            if (Version == null) await Install(await Latest());
            if (servers.Any(s => NeedsXray(s.Link)) && XrayVersion == null) await InstallXray(await XrayLatest());
            List<object> outbounds = new List<object>();
            List<int> tested = new List<int>();
            List<Process> xrays = new List<Process>();
            for (int i = 0; i < servers.Count; i++)
            {
                Dictionary<string, object> o;
                if (NeedsXray(servers[i].Link))
                {
                    Process xr = StartTestXray(servers[i].Link, i, out o);
                    if (xr == null) { result[i] = -1; continue; }
                    xrays.Add(xr);
                }
                else
                {
                    try { o = BuildOutbound(servers[i].Link); } catch { result[i] = -1; continue; }
                }
                o["tag"] = "s" + i;
                outbounds.Add(o);
                tested.Add(i);
            }
            if (tested.Count == 0) { KillAll(xrays); return result; }
            outbounds.Add(new Dictionary<string, object> { { "type", "direct" }, { "tag", "direct" } });
            int port = FreePort();
            string secret = Guid.NewGuid().ToString("N");
            Dictionary<string, object> cfg = new Dictionary<string, object>
            {
                { "log", new Dictionary<string, object> { { "level", "error" }, { "output", Path.Combine(Dir, "test.log") } } },
                { "outbounds", outbounds.ToArray() },
                { "route", new Dictionary<string, object> { { "final", "direct" }, { "auto_detect_interface", true } } },
                { "experimental", new Dictionary<string, object> { { "clash_api", new Dictionary<string, object> { { "external_controller", "127.0.0.1:" + port }, { "secret", secret } } } } },
            };
            string path = Path.Combine(Dir, "test.json");
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(cfg), new UTF8Encoding(false));
            Process proc = Process.Start(new ProcessStartInfo(Exe, "run -c \"" + path + "\" -D \"" + Dir + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Dir });
            try
            {
                for (int i = 0; i < 40; i++)   // ждём, пока ядро начнёт отвечать
                {
                    await Task.Delay(150);
                    if (proc.HasExited) { foreach (int k in tested) result[k] = await Ping(servers[k]); return result; }   // запасной вариант — обычный пинг
                    try { if (await ApiGet(port, secret, "/version", 500) != null) break; } catch { }
                }
                int[] ms = await Task.WhenAll(tested.Select(i => Delay(port, secret, "s" + i)));
                for (int j = 0; j < tested.Count; j++) result[tested[j]] = ms[j];
            }
            finally { try { proc.Kill(); } catch { } KillAll(xrays); }
            return result;
        }

        static void KillAll(IEnumerable<Process> procs) { foreach (Process p in procs) { try { p.Kill(); } catch { } } }

        // Временный Xray для проверки одного сервера xhttp: SOCKS на свободном порту; outbound — как до него дойти из тестового ядра
        static Process StartTestXray(string link, int index, out Dictionary<string, object> outbound)
        {
            outbound = null;
            try
            {
                int port = FreePort();
                string path = Path.Combine(Dir, "xray-test-" + index + ".json");
                File.WriteAllText(path, BuildXrayConfig(link, port), new UTF8Encoding(false));
                Process p = Process.Start(new ProcessStartInfo(XrayExe, "run -c \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Dir });
                outbound = new Dictionary<string, object> { { "type", "socks" }, { "server", "127.0.0.1" }, { "server_port", port }, { "version", "5" } };
                return p;
            }
            catch { return null; }
        }

        // Задержка через работающий VPN — для автовыбора: сервер может отвечать на соединение, но не пропускать трафик
        public static Task<int> CurrentDelay() { return apiPort == 0 ? Task.FromResult(-1) : Delay(apiPort, apiSecret, "proxy"); }

        // Текущие соединения через ядро: сайт, программа, через VPN или напрямую
        public class Connection { public string Host, Process, ProcessPath; public bool ViaVpn; public long Bytes; public DateTime Start; }

        public static async Task<List<Connection>> Connections()
        {
            List<Connection> list = new List<Connection>();
            if (apiPort == 0) return list;
            try
            {
                string json = await ApiGet(apiPort, apiSecret, "/connections", 2000);
                Dictionary<string, object> d = json == null ? null : new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>;
                object[] conns = d == null ? null : d["connections"] as object[];
                if (conns == null) return list;
                foreach (object o in conns)
                {
                    Dictionary<string, object> c = o as Dictionary<string, object>;
                    if (c == null) continue;
                    Dictionary<string, object> m = c["metadata"] as Dictionary<string, object> ?? new Dictionary<string, object>();
                    object[] chains = c.ContainsKey("chains") ? c["chains"] as object[] : null;
                    string host = S(m, "host").Length > 0 ? S(m, "host") : S(m, "destinationIP");
                    string proc = Path.GetFileName(S(m, "processPath"));
                    DateTime start;
                    DateTime.TryParse(S(c, "start"), out start);
                    list.Add(new Connection
                    {
                        Host = host + (S(m, "destinationPort").Length > 0 && S(m, "destinationPort") != "443" && S(m, "destinationPort") != "80" ? ":" + S(m, "destinationPort") : ""),
                        Process = proc, ProcessPath = S(m, "processPath"), ViaVpn = chains != null && chains.Any(x => "proxy".Equals(x)),
                        Bytes = Convert.ToInt64(c.ContainsKey("download") ? c["download"] : 0) + Convert.ToInt64(c.ContainsKey("upload") ? c["upload"] : 0),
                        Start = start,
                    });
                }
            }
            catch { }
            return list;
        }

        // ---------- Подписка: список серверов и сведения о трафике и сроке (заголовок subscription-userinfo) ----------
        public class SubscriptionInfo { public long Used, Total; public DateTime? Expire; }

        // Сведения из последней загруженной подписки (null — провайдер их не прислал)
        public static string LastUserInfo;

        static async Task<string> FetchSubscription(string url)
        {
            using (System.Net.Http.HttpClient c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                // как популярный клиент — так панели провайдеров отдают обычный список ссылок, а не свой формат
                c.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN/7.0");
                using (System.Net.Http.HttpResponseMessage r = await c.GetAsync(url))
                {
                    r.EnsureSuccessStatusCode();
                    IEnumerable<string> values;
                    LastUserInfo = r.Headers.TryGetValues("subscription-userinfo", out values) ? values.FirstOrDefault() : null;
                    return await r.Content.ReadAsStringAsync();
                }
            }
        }

        // "upload=1; download=2; total=3; expire=1700000000" → использовано, всего, до какого числа
        public static SubscriptionInfo ParseUserInfo(string header)
        {
            if (string.IsNullOrEmpty(header)) return null;
            Dictionary<string, long> v = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in header.Split(';'))
            {
                string[] kv = part.Split('=');
                long n;
                if (kv.Length == 2 && long.TryParse(kv[1].Trim(), out n)) v[kv[0].Trim()] = n;
            }
            long up, down, total, expire;
            v.TryGetValue("upload", out up); v.TryGetValue("download", out down); v.TryGetValue("total", out total); v.TryGetValue("expire", out expire);
            SubscriptionInfo info = new SubscriptionInfo { Used = up + down, Total = total };
            if (expire > 0) info.Expire = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expire).ToLocalTime();
            return info;
        }

        // Сколько байт прошло через VPN с момента подключения; null — ядро не отвечает
        public static async Task<long[]> Traffic()
        {
            if (apiPort == 0) return null;
            try
            {
                System.Net.Http.HttpRequestMessage req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "http://127.0.0.1:" + apiPort + "/connections");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiSecret);
                using (System.Net.Http.HttpResponseMessage r = await Api.SendAsync(req))
                {
                    if (!r.IsSuccessStatusCode) return null;
                    Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(await r.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                    if (d == null) return null;
                    return new[] { Convert.ToInt64(d["downloadTotal"]), Convert.ToInt64(d["uploadTotal"]) };
                }
            }
            catch { return null; }
        }

        // ---------- Конфигурация ----------
        // split — через VPN только выбранные домены и программы; ru — российское напрямую, остальное через VPN; all — весь трафик через VPN
        const string RuleSetBase = "https://raw.githubusercontent.com/SagerNet/";
        static readonly string[] RuSuffixes = { ".ru", ".su", ".xn--p1ai" };   // .рф

        public static string BuildConfig(VpnServer server, string mode, IList<string> domains, IList<string> apps)
        {
            bool all = mode == "all", ru = mode == "ru";
            List<object> routeRules = new List<object>
            {
                new Dictionary<string, object> { { "action", "sniff" } },
                new Dictionary<string, object> { { "protocol", "dns" }, { "action", "hijack-dns" } },
                // проверка серверов (отдельное ядро и временные Xray) и сам Xray ходят к серверам напрямую — иначе трафик пошёл бы по кругу через VPN
                new Dictionary<string, object> { { "process_name", new[] { "sing-box.exe", "xray.exe" } }, { "outbound", "direct" } },
                new Dictionary<string, object> { { "ip_is_private", true }, { "outbound", "direct" } },
            };
            List<object> dnsRules = new List<object>();
            if (!all)
            {
                // списки «через VPN» работают и в режиме «Россия напрямую» — например, для российского сайта, которому нужен VPN
                if (domains.Count > 0)
                {
                    routeRules.Add(new Dictionary<string, object> { { "domain_suffix", domains.ToArray() }, { "outbound", "proxy" } });
                    dnsRules.Add(new Dictionary<string, object> { { "domain_suffix", domains.ToArray() }, { "server", "remote" } });   // адреса этих сайтов — через VPN, чтобы сайт видел страну сервера
                }
                if (apps.Count > 0) routeRules.Add(new Dictionary<string, object> { { "process_name", apps.ToArray() }, { "outbound", "proxy" } });
            }
            List<object> ruleSets = new List<object>();
            if (ru)
            {
                // базы SagerNet: российские сайты и российские IP-адреса; скачиваются напрямую и обновляются раз в неделю
                ruleSets.Add(new Dictionary<string, object> { { "type", "remote" }, { "tag", "geosite-ru" }, { "format", "binary" }, { "url", RuleSetBase + "sing-geosite/rule-set/geosite-category-ru.srs" }, { "download_detour", "direct" }, { "update_interval", "7d" } });
                ruleSets.Add(new Dictionary<string, object> { { "type", "remote" }, { "tag", "geoip-ru" }, { "format", "binary" }, { "url", RuleSetBase + "sing-geoip/rule-set/geoip-ru.srs" }, { "download_detour", "direct" }, { "update_interval", "7d" } });
                routeRules.Add(new Dictionary<string, object> { { "domain_suffix", RuSuffixes }, { "outbound", "direct" } });
                routeRules.Add(new Dictionary<string, object> { { "rule_set", new[] { "geosite-ru", "geoip-ru" } }, { "outbound", "direct" } });
                dnsRules.Add(new Dictionary<string, object> { { "domain_suffix", RuSuffixes }, { "server", "local" } });
                dnsRules.Add(new Dictionary<string, object> { { "rule_set", new[] { "geosite-ru" } }, { "server", "local" } });
            }

            Dictionary<string, object> route = new Dictionary<string, object>
            {
                { "rules", routeRules.ToArray() },
                { "final", all || ru ? "proxy" : "direct" },
                { "auto_detect_interface", true },
                { "find_process", true },   // имя программы у каждого соединения — для журнала соединений
                { "default_domain_resolver", "local" },
            };
            if (ruleSets.Count > 0) route["rule_set"] = ruleSets.ToArray();

            Dictionary<string, object> cfg = new Dictionary<string, object>
            {
                { "log", new Dictionary<string, object> { { "level", "warn" }, { "output", LogFile }, { "timestamp", true } } },
                { "dns", new Dictionary<string, object>
                    {
                        { "servers", new object[]
                            {
                                new Dictionary<string, object> { { "type", "https" }, { "tag", "remote" }, { "server", "1.1.1.1" }, { "detour", "proxy" } },
                                new Dictionary<string, object> { { "type", "local" }, { "tag", "local" } },
                            } },
                        { "rules", dnsRules.ToArray() },
                        { "final", all || ru ? "remote" : "local" },
                        { "strategy", "ipv4_only" },
                    } },
                { "inbounds", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "type", "tun" }, { "tag", "tun-in" }, { "interface_name", InterfaceName }, { "address", new[] { "172.19.0.1/30" } },
                            { "mtu", 9000 }, { "auto_route", true }, { "strict_route", all || ru }, { "stack", "mixed" },
                        }
                    } },
                { "outbounds", new object[] { BuildOutbound(server.Link), new Dictionary<string, object> { { "type", "direct" }, { "tag", "direct" } } } },
                { "route", route },
            };
            Dictionary<string, object> experimental = new Dictionary<string, object>();
            if (apiPort > 0) experimental["clash_api"] = new Dictionary<string, object> { { "external_controller", "127.0.0.1:" + apiPort }, { "secret", apiSecret } };
            if (ru) experimental["cache_file"] = new Dictionary<string, object> { { "enabled", true }, { "path", Path.Combine(Dir, "cache.db") } };   // базы не скачиваются заново при каждом запуске
            if (experimental.Count > 0) cfg["experimental"] = experimental;
            return new JavaScriptSerializer().Serialize(cfg);
        }

        // Проверка конфигурации самим ядром; null — всё в порядке, иначе текст ошибки
        public static string Check(string configPath)
        {
            string output;
            int code = RunCore("check -c \"" + configPath + "\"", out output);
            return code == 0 ? null : (output.Trim().Length > 0 ? output.Trim() : "sing-box check: код " + code);
        }

        static int RunCore(string args, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Dir };
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                Task<string> err = p.StandardError.ReadToEndAsync();
                output = p.StandardOutput.ReadToEnd() + err.Result;
                p.WaitForExit(20000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }

        // Запуск: конфигурация → проверка → ядро в фоне; ждём, пока появится адаптер VPN
        public static async Task Start(VpnServer server, string mode, IList<string> domains, IList<string> apps)
        {
            if (Version == null) await Install(await Latest());
            await Stop();
            NewApiEndpoint();
            if (NeedsXray(server.Link)) await StartXray(server.Link);
            File.WriteAllText(ConfigFile, BuildConfig(server, mode, domains, apps), new UTF8Encoding(false));
            string error = await Task.Run(() => Check(ConfigFile));
            if (error != null) throw new Exception("Настройки VPN не прошли проверку: " + error);
            try { File.Delete(LogFile); } catch { }
            ProcessStartInfo psi = new ProcessStartInfo(Exe, "run -c \"" + ConfigFile + "\" -D \"" + Dir + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Dir };
            Process proc = Process.Start(psi);
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(200);
                if (proc.HasExited) throw new Exception("Ядро VPN остановилось сразу после запуска: " + LastLogLine());
                if (System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == InterfaceName && n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)) return;
            }
            if (proc.HasExited) throw new Exception("Ядро VPN остановилось: " + LastLogLine());
        }

        public static async Task Stop()
        {
            Process[] procs = Processes();
            procs = procs.Concat(XrayProcesses()).ToArray();
            foreach (Process p in procs) { try { p.Kill(); } catch { } }
            if (procs.Length > 0) await Task.Run(() => { foreach (Process p in procs) { try { p.WaitForExit(5000); } catch { } } });
            apiPort = 0;
            xrayPort = 0;
        }

        // Лаунчер перезапустился, а ядро продолжает работать — берём адрес статистики из его конфигурации
        public static void ReadApiEndpoint()
        {
            try
            {
                Dictionary<string, object> c = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigFile)) as Dictionary<string, object>;
                Dictionary<string, object> api = (Dictionary<string, object>)((Dictionary<string, object>)c["experimental"])["clash_api"];
                apiPort = int.Parse(((string)api["external_controller"]).Split(':')[1]);
                apiSecret = (string)api["secret"];
            }
            catch { apiPort = 0; }
        }

        public static string LastLogLine()
        {
            try
            {
                string last = File.ReadAllLines(LogFile).LastOrDefault(l => l.Trim().Length > 0);
                return last ?? "журнал ядра пуст";
            }
            catch { return "журнал ядра недоступен"; }
        }
    }
}
