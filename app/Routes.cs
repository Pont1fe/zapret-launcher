// Маршруты VPN: файл экспорта, разбор импорта, имя программы, набор для заблокированного сайта
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace ZapretLauncher
{
    static class Routes
    {
        static string T(string s) { return I18n.T(s); }

        // Что бросили в окно: программы — пути к .exe (ярлык уже раскрыт), сайты — адреса из ссылки или текста.
        // Пути остаются целыми: в имени программы бывают пробелы. Сайтов — не больше 20, чтобы абзац текста не стал списком
        public const int DropSitesMax = 20;
        public static void ParseDrop(IEnumerable<string> files, string text, List<string> apps, List<string> sites)
        {
            foreach (string f in files ?? new string[0])
                if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && NormalizeApp(f) != null && !apps.Contains(f, StringComparer.OrdinalIgnoreCase)) apps.Add(f);
            foreach (string part in (text ?? "").Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (sites.Count >= DropSitesMax) break;
                string d = Zapret.NormalizeDomain(part);
                if (d != null && !sites.Contains(d)) sites.Add(d);
            }
        }

        // Файл — обычный текст: по сайту или программе на строку, комментарии после #.
        // Ключей и серверов в нём нет — только списки, поэтому им можно делиться
        public static string RoutesFile(IEnumerable<string> domains, IEnumerable<string> apps)
        {
            List<string> lines = new List<string>
            {
                "# Zapret Launcher — " + T("маршруты VPN") + " (" + DateTime.Now.ToString("dd.MM.yyyy") + ")",
                "# " + T("Сайты и программы, которые идут через VPN. Загрузить: VPN → Маршруты → «Импорт»"),
                ""
            };
            lines.AddRange(domains);
            if (apps.Any()) { lines.Add(""); lines.AddRange(apps); }
            return string.Join("\r\n", lines) + "\r\n";
        }

        // Разбор файла маршрутов: «что-то.exe» или путь Windows — программа, остальное (и «site.com/path») — сайт; что не подошло ни туда, ни туда — в bad
        public static void SplitRoutes(string raw, List<string> domains, List<string> apps, List<string> bad)
        {
            foreach (string part in SitePresets.SplitItems(raw, false))
            {
                bool app = part.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || part.Contains('\\');
                string item = app ? NormalizeAppRoute(part) : Zapret.NormalizeDomain(part);
                if (item == null) { bad.Add(part); continue; }
                List<string> list = app ? apps : domains;
                if (!list.Contains(item, StringComparer.OrdinalIgnoreCase)) list.Add(item);
            }
        }

        // "C:\...\Spotify.exe" или "spotify" → Spotify.exe / spotify.exe
        public static string NormalizeApp(string s)
        {
            s = s.Trim().Trim('"');
            if (s.Length == 0 || s.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) return null;   // «a|b» — не путь (GetFileName упал бы)
            s = System.IO.Path.GetFileName(s);
            if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s += ".exe";
            return s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ? null : s;
        }

        internal static string NormalizeAppRoute(string input)
        {
            string value = input.Trim().Trim('"');
            string name = NormalizeApp(value);
            if (name == null || value.IndexOfAny(new[] { '\\', '/' }) < 0) return name;
            if (!IsAppPath(value)) return null;
            try { return System.IO.Path.GetFullPath(value); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        internal static bool IsAppPath(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length > 3 && char.IsLetter(value[0]) &&
                value[1] == ':' && (value[2] == '\\' || value[2] == '/') &&
                value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool AddAppPaths(List<string> apps, IEnumerable<string> files)
        {
            bool changed = false;
            foreach (string file in files)
            {
                string path = NormalizeAppRoute(file);
                if (!IsAppPath(path)) continue;
                string name = System.IO.Path.GetFileName(path);
                changed |= apps.RemoveAll(app => string.Equals(app, name, StringComparison.OrdinalIgnoreCase)) > 0;
                if (apps.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
                apps.Add(path);
                changed = true;
            }
            return changed;
        }

        // Набор проверяемого сайта (домены и программа) — чтобы целиком пустить его через VPN
        public static SitePreset BlockedPreset(string site)
        {
            return site == "Видео" ? new SitePreset { Name = site, Domains = new[] { "googlevideo.com" } } : SitePresets.Find(site);
        }

    }
}
