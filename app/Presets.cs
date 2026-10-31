// Готовые наборы сайтов: все домены сервиса и его программа — чтобы добавить их одной кнопкой
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

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
        public static readonly SitePreset[] All =
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
        public static readonly string[] Groups = { "Общение", "Медиа", "Соцсети", "Работа", "ИИ" };

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
