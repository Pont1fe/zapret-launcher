// Группы стратегий в списке и запасная стратегия для главной
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace ZapretLauncher
{
    static class Strategies
    {
        static string T(string s) { return I18n.T(s); }

        public static readonly string[] GroupOrder = { "Избранное", "Основная", "ALT", "FAKE TLS", "SIMPLE FAKE", "Экспериментальные", "Другие" };

        public static string GroupOf(string file)
        {
            string t = Zapret.Title(file);
            if (t == "Стандартная") return "Основная";
            if (t.StartsWith("ALT")) return "ALT";
            if (t.StartsWith("FAKE TLS")) return "FAKE TLS";
            if (t.StartsWith("SIMPLE FAKE")) return "SIMPLE FAKE";
            if (t.StartsWith("EXP")) return "Экспериментальные";
            return "Другие";
        }

        // Первая избранная кроме текущей; без избранных — та, что открыла больше всего сайтов на последнем подборе
        public static string BackupStrategy(string current, List<string> all, List<string> favorites, Dictionary<string, string> scores)
        {
            string fav = favorites.FirstOrDefault(f => f != current && all.Contains(f));
            if (fav != null) return fav;
            string best = null;
            double bestKey = -1;
            foreach (KeyValuePair<string, string> kv in scores)
            {
                string[] p = kv.Value.Split('/');
                int ok, total, avg;
                if (kv.Key == current || !all.Contains(kv.Key) || p.Length != 3 || !int.TryParse(p[0], out ok) || !int.TryParse(p[1], out total) || !int.TryParse(p[2], out avg) || ok == 0) continue;
                double key = ok * 100000.0 - avg;   // больше сайтов, при равенстве — быстрее
                if (key > bestKey) { bestKey = key; best = kv.Key; }
            }
            return best;
        }

    }
}
