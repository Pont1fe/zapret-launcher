// Логика главной без окна: заголовок приветствия, плашка скорости, раскладка карточек, «Мои сайты»
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace ZapretLauncher
{
    static class Home
    {
        static string T(string s) { return I18n.T(s); }

        // Итог в две строки: что с обходом и что с сайтами
        public static string GreetingTitle(string state, bool checking, List<SiteResult> res)
        {
            if (state == "pause") return T("Обход на паузе.") + "\n" + T("Работает VPN.");
            if (state == "error") return T("Обход не работает.") + "\n" + T("Сайты не открываются.");
            if (state != "on") return T("Обход выключен.") + "\n" + T("Включить — один клик.");
            if (checking || res == null || res.Count == 0) return T("Обход работает.") + "\n" + T("Проверяю сайты…");
            string names = FailingNames(res);
            if (names.Length == 0) return T("Обход работает.") + "\n" + T("Всё открывается.");
            // крупный заголовок не переносит длинный адрес посередине: такие имена — в строке под ним (FailingNames)
            return T("Обход работает.") + "\n" + (names.Length <= GreetNamesMax ? T("Не открывается:") + " " + names + "." : T("Не всё открывается."));
        }

        public const int GreetNamesMax = 16;   // столько букв крупного заголовка помещается в строку: «Discord, YouTube» — да, «steamcommunity.com» — уже нет

        public static string FailingNames(List<SiteResult> res) { return res == null ? "" : string.Join(", ", res.Where(r => !r.Ok).Select(r => T(r.Name))); }

        // Отступы карточки сайта в сетке: зазор 14 между соседними, у краёв — ноль, все ширины равны; между рядами — 14
        public static Thickness SiteCardMargin(int n, int columns, int count)
        {
            int col = n % columns;
            bool lastRow = n / columns == (count - 1) / columns;
            return new Thickness(14.0 * col / columns, 0, 14.0 * (columns - 1 - col) / columns, lastRow ? 0 : 14);
        }

        // «Мои сайты» из настроек: адреса через запятую, пробел или с новой строки → домены (как на странице «Домены»), не больше трёх
        public const int MySitesMax = 3;

        public static List<string> ParseMySites(string text, out string error)
        {
            error = null;
            List<string> list = new List<string>();
            foreach (string part in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string d = Zapret.NormalizeDomain(part);
                if (d == null) { error = T("Не похоже на адрес сайта:") + " " + part; return null; }
                if (!list.Contains(d)) list.Add(d);
            }
            if (list.Count > MySitesMax) { error = I18n.English ? "No more than " + MySitesMax + " sites" : "Не больше " + MySitesMax + " сайтов"; return null; }
            return list;
        }

        // На сколько процентов последняя проверка быстрее (минус) или медленнее (плюс) прошлых; null — мало данных или последняя не удалась
        public static int? SpeedDelta(List<int> h)
        {
            if (h == null || h.Count < 3 || h[h.Count - 1] < 0) return null;
            List<int> prev = h.Take(h.Count - 1).Where(v => v >= 0).Reverse().Take(6).ToList();
            if (prev.Count < 2) return null;
            double avg = prev.Average();
            return avg <= 0 ? (int?)null : (int)Math.Round((h[h.Count - 1] - avg) / avg * 100);
        }

        // имя сайта идёт в путь файла — из launcher.json, правленного руками, может прийти что угодно
        public static bool SafeHost(string host) { return System.Text.RegularExpressions.Regex.IsMatch(host ?? "", @"^[a-z0-9-]+(\.[a-z0-9-]+)+$"); }

        public static string SiteUrl(string site)
        {
            if (site == "Discord") return "https://discord.com/app";
            if (site == "YouTube") return "https://www.youtube.com/";
            if (site == "Telegram") return "https://web.telegram.org/";
            if (site.Contains(".")) return "https://" + site + "/";   // свой сайт пользователя — имя карточки и есть адрес
            return null;
        }

    }
}
