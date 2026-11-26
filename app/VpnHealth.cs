using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace ZapretLauncher
{
    static class VpnHealth
    {
        public const string Unresponsive = "VPN не передаёт трафик. Лаунчер повторит подключение.";

        public static string Advice(string detail, bool startup)
        {
            string error = (detail ?? "").ToLowerInvariant();
            if (error.Contains("сначала добавьте сервер")) return "Сначала добавьте сервер: вставьте ключ или адрес подписки на странице VPN";
            if (error.Contains(Unresponsive.ToLowerInvariant())) return Unresponsive;
            if (error.Contains("другой vpn") || error.Contains("another vpn"))
                return "Выключите другой VPN и повторите подключение.";
            if (error.Contains("адаптер vpn не появился") || error.Contains("create tun") || error.Contains("wintun"))
                return "Не удалось создать адаптер VPN. Повторите подключение или перезапустите Windows.";
            if (error.Contains("unknown outbound type") || error.Contains("unknown endpoint type") || error.Contains("wireguard is not included"))
                return "Ядро VPN не поддерживает этот протокол. Обновите sing-box и повторите подключение.";
            if (error.Contains("x509:") || error.Contains("certificate") || error.Contains("tls handshake") || error.Contains("authenticationexception"))
                return "Не прошла проверка TLS. Проверьте время Windows, адрес сервера и SNI в ключе.";
            if (error.Contains("authentication failed") || error.Contains("invalid password") || error.Contains("invalid user") || error.Contains("unauthorized"))
                return "Сервер отклонил данные входа. Проверьте ключ или обновите подписку.";
            if (error.Contains("connection refused") || error.Contains("no such host") || error.Contains("i/o timeout") || error.Contains("timed out"))
                return "Сервер или проверочный сайт недоступен. Проверьте интернет и попробуйте другой сервер.";
            if (error.Contains("настройки vpn не прошли проверку") || error.Contains("format exception"))
                return "Ядро отклонило настройки VPN. Проверьте ключ и обновите ядро.";
            return startup ? "Не удалось запустить VPN. Повторите подключение или проверьте журнал."
                : "Туннель запущен, но проверочный запрос не прошёл. Повторите проверку или выберите другой сервер.";
        }

        // У системного AmneziaWG нет API sing-box. IP_UNICAST_IF закрепляет проверочный
        // IPv4-сокет за адаптером: успешный ответ по обычной сети здесь невозможен.
        public static async Task<int> ProbeAdapter(int interfaceIndex)
        {
            int result = await ProbeAddress(interfaceIndex, "1.1.1.1", "cloudflare-dns.com", "/dns-query?name=example.com&type=A");
            return result >= 0 ? result : await ProbeAddress(interfaceIndex, "8.8.8.8", "dns.google", "/resolve?name=example.com&type=A");
        }

        static async Task<int> ProbeAddress(int interfaceIndex, string address, string host, string path)
        {
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            {
                Task<int> probe = ProbeSocket(client, interfaceIndex, address, host, path);
                return await Task.WhenAny(probe, Task.Delay(6500)) == probe ? await probe : -1;
            }
        }

        static async Task<int> ProbeSocket(TcpClient client, int interfaceIndex, string address, string host, string path)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                client.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(interfaceIndex));
                await client.ConnectAsync(IPAddress.Parse(address), 443).ConfigureAwait(false);
                using (var tls = new SslStream(client.GetStream()))
                {
                    await tls.AuthenticateAsClientAsync(host).ConfigureAwait(false);
                    byte[] request = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\nAccept: application/dns-json\r\nConnection: close\r\n\r\n");
                    await tls.WriteAsync(request, 0, request.Length).ConfigureAwait(false);
                    using (var reader = new StreamReader(tls, Encoding.ASCII))
                    {
                        string status = await reader.ReadLineAsync().ConfigureAwait(false);
                        return status != null && (status.StartsWith("HTTP/1.1 200 ") || status.StartsWith("HTTP/1.0 200 ")) ? (int)sw.ElapsedMilliseconds : -1;
                    }
                }
            }
            catch (Exception) { return -1; }
        }
    }
}
