// Тексты о VPN: название режима и напоминания о подписке
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;

namespace ZapretLauncher
{
    static class VpnInfo
    {
        static string T(string s) { return I18n.T(s); }

        public static string ModeName(string mode) { return mode == "all" ? "весь трафик" : mode == "ru" ? "Россия напрямую" : "только выбранное"; }

        // Что напомнить о подписке сейчас: ключ этапа → текст. Этапы: за 3 дня, за сутки, закончилась; трафик 90% и 100%.
        // Ключ содержит дату окончания и размер тарифа: продлили подписку — ключ новый, и напоминание придёт снова, когда подойдёт срок
        public static Dictionary<string, string> SubscriptionWarnings(Vpn.SubscriptionInfo info, DateTime now)
        {
            Dictionary<string, string> w = new Dictionary<string, string>();
            if (info == null) return w;
            if (info.Expire.HasValue)
            {
                double days = (info.Expire.Value - now).TotalDays;
                string date = info.Expire.Value.ToString("dd.MM.yyyy HH:mm");
                string key = "expire " + info.Expire.Value.ToString("yyyyMMddHHmm") + " ";
                if (days <= 0) w[key + "0"] = "Подписка VPN закончилась: " + date + ". Продлите её у своего провайдера";
                else if (days <= 3)
                    w[key + (days <= 1 ? "1" : "3")] = "Подписка VPN заканчивается: " + date + " (осталось дней: " + (int)Math.Ceiling(days) + "). Продлите её у своего провайдера";
            }
            if (info.Total > 0)
            {
                double ratio = (double)info.Used / info.Total;
                string key = "traffic " + info.Total + " ";
                string used = Formats.Bytes(info.Used) + " из " + Formats.Bytes(info.Total);
                if (ratio >= 1) w[key + "100"] = "Трафик подписки VPN закончился: использовано " + used;
                else if (ratio >= 0.9) w[key + "90"] = "Трафик подписки VPN почти закончился: использовано " + used;
            }
            return w;
        }

    }
}
