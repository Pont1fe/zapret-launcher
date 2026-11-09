// Порядок и выбор серверов VPN по последней проверке — без окна, чтобы это проверялось тестами.
// Решения общие для списка в окне, меню значка в трее, автовыбора и замера скорости.
// pings: ссылка → мс; -1 — сайт через сервер не открылся; Vpn.PingUdp — так не проверить (UDP, AmneziaWG); нет записи — не проверялся
using System.Collections.Generic;
using System.Linq;

namespace ZapretLauncher
{
    static class ServerList
    {
        public static int? Ping(IDictionary<string, int> pings, VpnServer s) { int ms; return pings.TryGetValue(s.Link, out ms) ? ms : (int?)null; }

        // Место в списке: работающие по скорости, потом «без проверки», потом непроверенные, неработающие — в конце
        public static int Rank(int? ms)
        {
            if (!ms.HasValue) return 2000000;
            if (ms.Value >= 0) return ms.Value;
            return ms.Value == Vpn.PingUdp ? 1000000 : 3000000;
        }

        // Какие серверы показывать и в каком порядке. hideDead — скрыть неработающие; выбранный виден всегда
        public static List<int> Visible(IList<VpnServer> servers, IDictionary<string, int> pings, int selected, bool hideDead)
        {
            bool hiding = hideDead && servers.Any(s => Ping(pings, s) == -1);
            return Enumerable.Range(0, servers.Count)
                .Where(i => !hiding || i == selected || Ping(pings, servers[i]) != -1)
                .OrderBy(i => Rank(Ping(pings, servers[i]))).ThenBy(i => i).ToList();
        }

        // Самый быстрый из ответивших (UDP-серверы — после всех с замером); -1 — никто не ответил. exclude — кроме этого сервера
        public static int Best(IList<VpnServer> servers, IDictionary<string, int> pings, int exclude = -1)
        {
            return Enumerable.Range(0, servers.Count)
                .Where(i => i != exclude && Ping(pings, servers[i]).HasValue && Ping(pings, servers[i]) != -1)
                .OrderBy(i => Rank(Ping(pings, servers[i]))).ThenBy(i => i)
                .DefaultIfEmpty(-1).First();
        }

        // Для замера скорости: выбранный сервер и самые быстрые по проверке, всего не больше max.
        // Неработающие и AmneziaWG пропускаем: через тестовое ядро их не проверить
        public static List<int> SpeedCandidates(IList<VpnServer> servers, IDictionary<string, int> pings, int selected, int max)
        {
            return Enumerable.Range(0, servers.Count)
                .Where(i => !Awg.IsAwg(servers[i].Link) && Ping(pings, servers[i]) != -1)
                .OrderBy(i => i == selected ? -1 : Rank(Ping(pings, servers[i]))).ThenBy(i => i)
                .Take(max).ToList();
        }
    }
}
