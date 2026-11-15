// Форматы для показа: скорость, объём, длительность, итог добавления, строки журнала
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace ZapretLauncher
{
    static class Formats
    {
        static string T(string s) { return I18n.T(s); }

        public static string Mbps(double v) { return (v < 10 ? v.ToString("0.0") : v.ToString("0")) + " " + T("Мбит/с"); }

        public static string Bytes(long b)
        {
            return b >= 1L << 30 ? (b / 1073741824.0).ToString("0.0") + " " + T("ГБ") : b >= 1L << 20 ? (b / 1048576.0).ToString("0") + " " + T("МБ") : Math.Max(1, b / 1024) + " " + T("КБ");
        }

        public static string Span(int minutes)
        {
            return minutes >= 60 ? (minutes / 60) + " " + T("ч") + (minutes % 60 > 0 ? " " + (minutes % 60) + " " + T("мин") : "") : minutes + " " + T("мин");
        }

        // Итог добавления: сколько добавлено, что уже было, что не распознано (первые несколько)
        public static string AddedInfo(int fresh, int found, List<string> bad, string badText, string dupText, string normalText)
        {
            string s = fresh > 1 ? T("Добавлено") + ": " + fresh : fresh == 0 && found > 0 ? T(dupText) : "";
            if (bad.Count > 0)
                s += (s.Length > 0 ? ". " : "") + T(badText) + ": " + string.Join(", ", bad.Take(5)) + (bad.Count > 5 ? " (+" + (bad.Count - 5) + ")" : "");
            return s.Length > 0 ? s : T(normalText);
        }

        public static readonly string[] LogErrors = { "ошибка", "Ошибка", "Не удалось", "не удалось", "Exception", "не работает", "упал", "остановилось",
                                               "error", "Error", "failed", "Could not", "Couldn't", "isn't working", "stopped right" };

        public static readonly string[] LogWarnings = { "Сторож", "Внимание", "не отвечает", "не открывается", "перестала", "блокирует",
                                                 "Watchdog", "Warning", "not responding", "doesn't open", "stopped working", "blocks" };

        public static readonly string[] LogGood = { "Запущена стратегия", "Лучшая стратегия", "Новая стратегия", "VPN подключён", "обновлён до",
                                             "Started strategy", "Best strategy", "New strategy", "VPN connected", "updated to" };

        public static LogLine ParseLog(string line)
        {
            LogLine l = new LogLine { Raw = line, Time = "", Text = line, Kind = "" };
            if (line.StartsWith("[") && line.IndexOf("] ") > 0)
            {
                int end = line.IndexOf("] ");
                l.Time = line.Substring(1, end - 1);
                l.Text = line.Substring(end + 2);
            }
            else if (line.StartsWith("   ") || line.StartsWith("\t")) { l.Kind = "trace"; l.Text = line.Trim(); return l; }   // строки стека после ошибки
            l.Kind = LogErrors.Any(k => l.Text.Contains(k)) ? "err" : LogWarnings.Any(k => l.Text.Contains(k)) ? "warn" : LogGood.Any(k => l.Text.Contains(k)) ? "ok" : "";
            return l;
        }

    }
}
