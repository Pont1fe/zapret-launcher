// Выводы мастера «Почему не работает?» и проверка ответа DNS — без окна, их проверяют тесты
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace ZapretLauncher
{
    static class Diagnosis
    {
        static string T(string s) { return I18n.T(s); }

        // Порядок важен: сначала то, без чего остальные проверки ничего не значат
        public static Verdict Diagnose(bool internet, bool dnsOk, bool secureDnsOn, bool installed, IList<Conflict> conflicts,
                                       bool running, bool paused, IList<string> failing, ICollection<string> blockedByAddress)
        {
            Func<string, string, string, Verdict> v = (title, text, fix) => new Verdict { Title = title, Text = text, Fix = fix };
            if (!internet)
                return v("Нет интернета", "Не открывается ни один сайт, даже незаблокированный. Проверьте кабель или Wi-Fi и перезагрузите роутер — обход тут ни при чём.", "");
            if (!installed) return v("zapret не установлен", "Файлов обхода нет — лаунчер скачает их заново.", "install");
            Conflict other = conflicts.FirstOrDefault(c => c.Kind != "vpn");
            if (other != null)
                return v("Мешает другая программа обхода", T(other.Text) + ". " + T("Нажмите «Исправить» — лаунчер уберёт помеху и перезапустит обход."), "conflicts");
            if (failing.Count == 0)
                return v("Всё работает", "Discord, YouTube и остальные сайты с главной открываются. Если не грузится другой сайт — добавьте его на странице «Домены».", "");
            if (!running && !paused) return v("Обход выключен", "Включите его — и сайты откроются.", "start");
            Conflict vpn = conflicts.FirstOrDefault(c => c.Kind == "vpn");
            if (vpn != null)
                return v("Включён другой VPN", T(vpn.Text) + ". " + T("Пока он включён, обход может не работать — выключите его или включите в настройках паузу обхода на время VPN."), "");
            if (!dnsOk && !secureDnsOn)
                return v("Провайдер подменяет адреса сайтов", "DNS провайдера отдаёт неправильные адреса заблокированных сайтов. Лаунчер включит защищённый DNS.", "dns");
            string names = string.Join(", ", failing.Select(T));
            if (failing.All(blockedByAddress.Contains))
                return v("Сайт заблокирован по адресу", names + ": " + T("провайдер режет сам сервер — zapret тут не поможет. Пустите этот сайт через VPN."), "vpn");
            return v("Стратегия не подходит", T("Не открывается") + ": " + names + ". " + T("Лаунчер подберёт стратегию, которая работает у вашего провайдера."), "pick");
        }

        // Ответ DNS, которому верить нельзя: пусто, 0.0.0.0, свой компьютер или адрес из домашней сети — так провайдеры «прячут» сайты
        public static bool BadDnsAnswer(IPAddress[] addresses)
        {
            if (addresses == null || addresses.Length == 0) return true;
            return addresses.Any(a =>
            {
                if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return true;
                byte[] b = a.GetAddressBytes();
                return b.Length == 4 && (b[0] == 10 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
            });
        }

        public static async Task<bool> DnsLooksRight(System.Threading.CancellationToken cancellation = default(System.Threading.CancellationToken))
        {
            foreach (string host in new[] { "discord.com", "www.youtube.com" })
            {
                try
                {
                    Task<IPAddress[]> t = Dns.GetHostAddressesAsync(host);
                    if (await Task.WhenAny(t, Task.Delay(4000, cancellation)) != t || t.IsFaulted || BadDnsAnswer(t.Result)) { cancellation.ThrowIfCancellationRequested(); return false; }
                }
                catch { cancellation.ThrowIfCancellationRequested(); return false; }
            }
            return true;
        }

    }
}
