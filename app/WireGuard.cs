using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ZapretLauncher
{
    static class WireGuard
    {
        public const string Scheme = "wireguard://";
        public static bool IsLink(string link) { return link != null && link.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase); }

        public static VpnServer Import(string text, string path)
        {
            if (Awg.Fields(text).Keys.Any(k => Regex.IsMatch(k, @"\AInterface\.(Jc|Jmin|Jmax|[SHI][0-9]+)\z", RegexOptions.IgnoreCase)))
                return Awg.FromConf(text, path);
            text = text.Trim().Replace("\r\n", "\n");
            string host = PeerOf(Endpoint(text))["address"].ToString();
            string name = "WireGuard · " + (string.IsNullOrEmpty(path) ? host : Path.GetFileNameWithoutExtension(path));
            return new VpnServer { Name = name, Link = Scheme + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "#" + Uri.EscapeDataString(name) };
        }

        static Dictionary<string, object> PeerOf(Dictionary<string, object> endpoint)
        {
            return ((Dictionary<string, object>[])endpoint["peers"])[0];
        }

        public static string Host(string link) { return (string)PeerOf(FromLink(link))["address"]; }
        public static Dictionary<string, object> FromLink(string link) { return Endpoint(TextOf(link)); }

        static string TextOf(string link)
        {
            try { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(link.Substring(Scheme.Length).Split('#')[0])); }
            catch (ArgumentException) { throw new FormatException("Не удалось прочитать ключ WireGuard"); }
            catch (FormatException) { throw new FormatException("Не удалось прочитать ключ WireGuard"); }
        }

        public static string Dns(string link)
        {
            string dns;
            return Awg.Fields(TextOf(link)).TryGetValue("Interface.DNS", out dns) ? dns.Split(',')[0].Trim() : null;
        }

        static Dictionary<string, object> Endpoint(string text)
        {
            if (!Awg.LooksLikeConf(text) || Regex.Matches(text, @"^\s*\[Peer\]", RegexOptions.Multiline | RegexOptions.IgnoreCase).Count != 1 ||
                Regex.Matches(text, @"^\s*\[Interface\]", RegexOptions.Multiline | RegexOptions.IgnoreCase).Count != 1)
                throw new FormatException("Для WireGuard нужен файл с одним разделом [Interface] и одним [Peer]");

            var fields = Awg.Fields(text);
            var supported = new HashSet<string>(new[] {
                "Interface.Address", "Interface.PrivateKey", "Interface.DNS", "Interface.MTU", "Interface.ListenPort",
                "Peer.Endpoint", "Peer.PublicKey", "Peer.PresharedKey", "Peer.AllowedIPs", "Peer.PersistentKeepalive"
            }, StringComparer.OrdinalIgnoreCase);
            if (fields.Keys.Any(k => !supported.Contains(k)))
                throw new FormatException("В файле WireGuard есть неподдерживаемые параметры. Уберите скрипты и настройки системных маршрутов.");

            Func<string, string> required = field =>
            {
                string value;
                if (!fields.TryGetValue(field, out value) || value.Length == 0)
                    throw new FormatException(I18n.T("В файле WireGuard нет поля") + " " + field.Split('.')[1]);
                return value;
            };
            Uri address;
            string hostPort = required("Peer.Endpoint");
            if (hostPort.IndexOfAny(new[] { '/', '?', '#', '@', '\\', ' ' }) >= 0 ||
                !Uri.TryCreate("wg://" + hostPort, UriKind.Absolute, out address) ||
                address.HostNameType == UriHostNameType.Unknown || address.Port <= 0)
                throw new FormatException("Некорректный адрес Endpoint в файле WireGuard");

            string host = address.IdnHost.Trim('[', ']');
            IPAddress endpointIp;
            if (IPAddress.TryParse(host, out endpointIp)) host = endpointIp.ToString();
            var peer = new Dictionary<string, object>
            {
                { "address", host }, { "port", address.Port },
                { "public_key", Key(required("Peer.PublicKey")) },
                { "allowed_ips", Prefixes(required("Peer.AllowedIPs")) }
            };
            var endpoint = new Dictionary<string, object>
            {
                { "type", "wireguard" }, { "tag", "proxy" }, { "system", false },
                { "address", Prefixes(required("Interface.Address")) },
                { "private_key", Key(required("Interface.PrivateKey")) },
                { "peers", new[] { peer } }
            };
            string optional;
            if (fields.TryGetValue("Peer.PresharedKey", out optional)) peer["pre_shared_key"] = Key(optional);
            CopyNumber(fields, "Peer.PersistentKeepalive", peer, "persistent_keepalive_interval", 0, 65535);
            CopyNumber(fields, "Interface.MTU", endpoint, "mtu", 576, 65535);
            CopyNumber(fields, "Interface.ListenPort", endpoint, "listen_port", 0, 65535);
            if (fields.TryGetValue("Interface.DNS", out optional))
                foreach (string dns in optional.Split(','))
                {
                    IPAddress ip;
                    if (!IPAddress.TryParse(dns.Trim(), out ip)) throw new FormatException("В поле DNS WireGuard допустимы только IP-адреса");
                }
            return endpoint;
        }

        static string Key(string value)
        {
            try { if (Convert.FromBase64String(value).Length == 32) return value; }
            catch (FormatException) { }
            throw new FormatException("Некорректный ключ в файле WireGuard");
        }

        static string[] Prefixes(string value)
        {
            return value.Split(',').Select(part =>
            {
                string[] items = part.Trim().Split('/');
                IPAddress ip;
                int bits;
                if (!IPAddress.TryParse(items[0], out ip))
                    throw new FormatException("Некорректный IP-префикс в файле WireGuard");
                int max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                if (items.Length == 1) return ip + "/" + max;
                if (items.Length != 2 || !int.TryParse(items[1], out bits) || bits < 0 || bits > max)
                    throw new FormatException("Некорректный IP-префикс в файле WireGuard");
                return ip + "/" + bits;
            }).ToArray();
        }

        static void CopyNumber(Dictionary<string, string> fields, string field, Dictionary<string, object> target, string name, int min, int max)
        {
            string value;
            if (!fields.TryGetValue(field, out value)) return;
            int number;
            if (!int.TryParse(value, out number) || number < min || number > max)
                throw new FormatException(I18n.T("Некорректное число в файле WireGuard") + " (" + field.Split('.')[1] + ")");
            if (number != 0) target[name] = number;
        }
    }
}
