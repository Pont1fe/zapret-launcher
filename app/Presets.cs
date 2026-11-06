// Готовые наборы сайтов: все домены сервиса и его программа — чтобы добавить их одной кнопкой.
// Встроенные наборы — запасной вариант: свежие лаунчер берёт из presets/sites.json в репозитории (см. Parse и Launcher.UpdateSitePresets)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ZapretLauncher
{
    class SitePreset
    {
        public string Name;
        public string[] Domains;
        public string[] Apps = new string[0];   // программы сервиса — для маршрутов VPN
        public bool VpnOnly;                    // сервис закрыт по стране — zapret не поможет, набор нужен только для VPN
        public string Group;                    // вкладка в «Готовых наборах»
        public string Mark;                     // значок: буква на плашке цвета сервиса
        public string Color;
    }

    static class SitePresets
    {
        static readonly SitePreset[] BuiltIn =
        {
            new SitePreset { Name = "YouTube", Group = "Медиа", Mark = "▶", Color = "#FF0033", Domains = new[] { "youtube.com", "youtu.be", "ytimg.com", "ggpht.com", "googlevideo.com" } },
            new SitePreset { Name = "Discord", Group = "Общение", Mark = "D", Color = "#5865F2", Domains = new[] { "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media" }, Apps = new[] { "Discord.exe" } },
            new SitePreset { Name = "Telegram", Group = "Общение", Mark = "T", Color = "#2AABEE", Domains = new[] { "telegram.org", "t.me", "telegram.me", "telesco.pe", "tdesktop.com", "telegra.ph" }, Apps = new[] { "Telegram.exe" } },
            new SitePreset { Name = "WhatsApp", Group = "Общение", Mark = "W", Color = "#25D366", Domains = new[] { "whatsapp.com", "whatsapp.net", "wa.me" }, Apps = new[] { "WhatsApp.exe", "WhatsApp.Root.exe" } },
            new SitePreset { Name = "Signal", Group = "Общение", Mark = "S", Color = "#3A76F0", Domains = new[] { "signal.org", "whispersystems.org", "signal.art" }, Apps = new[] { "Signal.exe" } },
            new SitePreset { Name = "Viber", Group = "Общение", Mark = "V", Color = "#7360F2", Domains = new[] { "viber.com" }, Apps = new[] { "Viber.exe" } },
            new SitePreset { Name = "Instagram", Group = "Соцсети", Mark = "I", Color = "#E1306C", Domains = new[] { "instagram.com", "cdninstagram.com", "ig.me", "instagr.am", "fbcdn.net" } },
            new SitePreset { Name = "Facebook", Group = "Соцсети", Mark = "f", Color = "#1877F2", Domains = new[] { "facebook.com", "fb.com", "fbcdn.net", "facebook.net", "fbsbx.com", "messenger.com" } },
            new SitePreset { Name = "X (Twitter)", Group = "Соцсети", Mark = "X", Color = "#2A2A2E", Domains = new[] { "x.com", "twitter.com", "twimg.com", "t.co" } },
            new SitePreset { Name = "Spotify", Group = "Медиа", Mark = "S", Color = "#1DB954", Domains = new[] { "spotify.com", "scdn.co", "spotifycdn.com", "spotifycdn.net", "pscdn.co" }, Apps = new[] { "Spotify.exe" } },
            new SitePreset { Name = "SoundCloud", Group = "Медиа", Mark = "S", Color = "#FF5500", Domains = new[] { "soundcloud.com", "sndcdn.com" } },
            new SitePreset { Name = "Twitch", Group = "Медиа", Mark = "T", Color = "#9146FF", Domains = new[] { "twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net", "twitchsvc.net" } },
            new SitePreset { Name = "Netflix", Group = "Медиа", Mark = "N", Color = "#E50914", Domains = new[] { "netflix.com", "netflix.net", "nflxvideo.net", "nflximg.net", "nflxext.com", "nflxso.net" } },
            new SitePreset { Name = "LinkedIn", Group = "Работа", Mark = "in", Color = "#0A66C2", Domains = new[] { "linkedin.com", "licdn.com" } },
            new SitePreset { Name = "Patreon", Group = "Работа", Mark = "P", Color = "#F96854", Domains = new[] { "patreon.com", "patreonusercontent.com" } },
            new SitePreset { Name = "Notion", Group = "Работа", Mark = "N", Color = "#37352F", Domains = new[] { "notion.so", "notion.site", "notion.com" }, Apps = new[] { "Notion.exe" } },
            // ИИ-сервисы: закрыты для России по адресу, поэтому только через VPN
            new SitePreset { Name = "ChatGPT", Group = "ИИ", Mark = "G", Color = "#10A37F", Domains = new[] { "chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com", "chat.com" }, Apps = new[] { "ChatGPT.exe" }, VpnOnly = true },
            new SitePreset { Name = "Gemini", Group = "ИИ", Mark = "✦", Color = "#4285F4", Domains = new[] { "gemini.google.com", "aistudio.google.com", "notebooklm.google.com", "generativelanguage.googleapis.com", "alkalimakersuite-pa.clients6.google.com" }, VpnOnly = true },
            new SitePreset { Name = "Grok", Group = "ИИ", Mark = "G", Color = "#2A2A2E", Domains = new[] { "grok.com", "x.ai" }, VpnOnly = true },
            new SitePreset { Name = "Perplexity", Group = "ИИ", Mark = "P", Color = "#20808D", Domains = new[] { "perplexity.ai", "pplx.ai" }, Apps = new[] { "Perplexity.exe" }, VpnOnly = true },
            new SitePreset { Name = "Copilot", Group = "ИИ", Mark = "C", Color = "#0078D4", Domains = new[] { "copilot.microsoft.com" }, VpnOnly = true },
        };

        // Вкладки «Готовых наборов» — в этом порядке
        static readonly string[] BuiltInGroups = { "Общение", "Медиа", "Соцсети", "Работа", "ИИ" };

        // Действующие наборы и вкладки: встроенные или из файла репозитория
        public static SitePreset[] All { get; private set; }
        public static string[] Groups { get; private set; }
        public static string Updated { get; private set; }   // дата файла из репозитория; null — встроенные
        static SitePresets() { All = BuiltIn; Groups = BuiltInGroups; }

        // ---------- Наборы из репозитория ----------
        // Файл presets/sites.json: { "format": 1, "updated": "yyyy-MM-dd", "groups": [...], "sets": [{ name, group, mark, color, domains, apps, vpnOnly }] }.
        // Лаунчер скачивает его вместе с проверкой обновлений и хранит копию рядом с собой
        public const int Format = 1;   // формат, который понимает эта версия; файл новее — не трогаем, ждём обновления лаунчера
        public static readonly string Url = "https://raw.githubusercontent.com/" + Consts.LauncherRepo + "/main/presets/sites.json";
        public static readonly string Cache = System.IO.Path.Combine(Paths.Root, "site-presets.json");

        public class PresetFile
        {
            public string Updated;
            public string[] Groups;
            public SitePreset[] Sets;
            public List<string> Skipped = new List<string>();   // наборы с ошибками — пропущены, остальные работают
        }

        static readonly Regex ColorRx = new Regex("^#[0-9A-Fa-f]{6}$");
        static readonly Regex AppRx = new Regex(@"^[\w .()+&-]+\.exe$", RegexOptions.IgnoreCase);

        // Разбор и проверка файла; null и error — если файл целиком не годится (тогда остаются прежние наборы)
        public static PresetFile Parse(string json, out string error)
        {
            error = null;
            Dictionary<string, object> root;
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json.Length > 1000000) { error = "пустой или слишком большой файл"; return null; }
                root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception e) { error = "не JSON: " + e.Message; return null; }
            if (root == null) { error = "не JSON-объект"; return null; }
            int format = root.ContainsKey("format") && root["format"] is int ? (int)root["format"] : 0;
            if (format < 1) { error = "нет поля format"; return null; }
            if (format > Format) { error = "format " + format + " — новее, чем понимает эта версия лаунчера"; return null; }

            List<string> groups = Strings(root, "groups").Distinct().Where(g => g.Length > 0 && g.Length <= 40).ToList();
            object[] rawSets = root.ContainsKey("sets") ? root["sets"] as object[] : null;
            if (groups.Count == 0 || rawSets == null || rawSets.Length == 0) { error = "нет вкладок или наборов"; return null; }
            if (rawSets.Length > 500) { error = "слишком много наборов"; return null; }

            PresetFile f = new PresetFile { Updated = root.ContainsKey("updated") ? root["updated"] as string : null };
            List<SitePreset> sets = new List<SitePreset>();
            foreach (object o in rawSets)
            {
                Dictionary<string, object> s = o as Dictionary<string, object>;
                string name = s != null && s.ContainsKey("name") ? (s["name"] as string ?? "").Trim() : "";
                if (name.Length == 0 || name.Length > 40 || sets.Any(x => x.Name == name)) { f.Skipped.Add(name.Length > 0 ? name : "?"); continue; }
                SitePreset p = new SitePreset
                {
                    Name = name,
                    Group = s.ContainsKey("group") ? s["group"] as string : null,
                    Mark = s.ContainsKey("mark") ? s["mark"] as string : null,
                    Color = s.ContainsKey("color") ? s["color"] as string : null,
                    Domains = Strings(s, "domains").ToArray(),
                    Apps = Strings(s, "apps").ToArray(),
                    VpnOnly = s.ContainsKey("vpnOnly") && s["vpnOnly"] is bool && (bool)s["vpnOnly"]
                };
                bool ok = groups.Contains(p.Group) && !string.IsNullOrEmpty(p.Mark) && p.Mark.Length <= 3 && p.Color != null && ColorRx.IsMatch(p.Color) &&
                          p.Domains.Length > 0 && p.Domains.Length <= 200 && p.Domains.All(d => Zapret.NormalizeDomain(d) == d) &&
                          p.Apps.Length <= 20 && p.Apps.All(a => AppRx.IsMatch(a));
                if (ok) sets.Add(p); else f.Skipped.Add(name);
            }
            if (sets.Count == 0) { error = "ни одного правильного набора"; return null; }
            f.Sets = sets.ToArray();
            f.Groups = groups.Where(g => sets.Any(p => p.Group == g)).ToArray();
            return f;
        }

        static IEnumerable<string> Strings(Dictionary<string, object> d, string key)
        {
            object[] a = d.ContainsKey(key) ? d[key] as object[] : null;
            return a == null ? new string[0] : a.OfType<string>().Select(x => x.Trim()).Where(x => x.Length > 0);
        }

        public static void Use(PresetFile f)
        {
            Groups = f.Groups;
            All = f.Sets;
            Updated = f.Updated;
        }

        // При запуске: наборы из сохранённой копии файла, если она есть и годится
        public static void LoadCache()
        {
            try
            {
                if (!System.IO.File.Exists(Cache)) return;
                string error;
                PresetFile f = Parse(System.IO.File.ReadAllText(Cache, Encoding.UTF8), out error);
                if (f != null) Use(f);
            }
            catch { }
        }

        // Наборы обновились: что дописать в список, чтобы включённые наборы остались включёнными.
        // on — список, по которому набор считается включённым (домены), target — куда дописываем (домены или программы).
        // Набор, из которого пользователь убрал хоть один домен, уже выключен — его не трогаем
        public static List<string> Additions(IEnumerable<SitePreset> old, IEnumerable<SitePreset> fresh, ICollection<string> on, ICollection<string> target, bool apps)
        {
            List<string> result = new List<string>();
            foreach (SitePreset o in old.Where(o => IsOn(o, on)))
            {
                SitePreset n = fresh.FirstOrDefault(x => x.Name == o.Name);
                if (n == null) continue;
                foreach (string x in apps ? n.Apps : n.Domains)
                    if (!target.Contains(x, StringComparer.OrdinalIgnoreCase) && !result.Contains(x, StringComparer.OrdinalIgnoreCase)) result.Add(x);
            }
            return result;
        }

        // Файл для репозитория из встроенных наборов (tools/presets.ps1)
        public static string BuiltInJson(string updated)
        {
            List<object> sets = BuiltIn.Select(p =>
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["name"] = p.Name; d["group"] = p.Group; d["mark"] = p.Mark; d["color"] = p.Color; d["domains"] = p.Domains;
                if (p.Apps.Length > 0) d["apps"] = p.Apps;
                if (p.VpnOnly) d["vpnOnly"] = true;
                return (object)d;
            }).ToList();
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["format"] = Format; root["updated"] = updated; root["groups"] = BuiltInGroups; root["sets"] = sets;
            return new JavaScriptSerializer().Serialize(root);
        }

        public static SitePreset Find(string name)
        {
            return All.FirstOrDefault(p => p.Name == name);
        }

        public static bool IsOn(SitePreset p, ICollection<string> domains)
        {
            return p.Domains.All(d => domains.Contains(d, StringComparer.OrdinalIgnoreCase));
        }

        // Что убрать вместе с набором: общие домены и программы остаются, если их использует другой включённый набор
        public static HashSet<string> RemovalSet(SitePreset p, ICollection<string> domains, bool apps)
        {
            HashSet<string> keep = new HashSet<string>(All.Where(o => o != p && IsOn(o, domains)).SelectMany(o => apps ? o.Apps : o.Domains), StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>((apps ? p.Apps : p.Domains).Where(x => !keep.Contains(x)), StringComparer.OrdinalIgnoreCase);
        }

        // Несколько доменов сразу: через пробел, запятую или с новой строки; после # — комментарий (как в списках zapret и hosts).
        // IP-адреса из строк hosts вроде «0.0.0.0 example.com» пропускаются
        // Пути программ — «C:\Program Files\…» — по пробелам не делятся: там разделители только строки, запятые и точки с запятой
        public static List<string> SplitItems(string raw, bool bySpaces = true)
        {
            IPAddress ip;
            char[] separators = bySpaces ? new[] { ' ', ',', ';', '\r', '\t' } : new[] { ',', ';', '\r', '\t' };
            return (raw ?? "").Split('\n')
                .Select(l => { int c = l.IndexOf('#'); return c >= 0 ? l.Substring(0, c) : l; })
                .SelectMany(l => l.Split(separators, StringSplitOptions.RemoveEmptyEntries))
                .Select(t => t.Trim().Trim('"').Trim())
                .Where(t => t.Length > 0 && !IPAddress.TryParse(t, out ip))
                .ToList();
        }
    }
}
