// Решения сторожа: когда ждать, когда перезапустить стратегию, когда подбирать новую. Без окна, winws и сети —
// поэтому их проверяют тесты (tests/run.ps1); сами действия выполняет Launcher.SiteCheck
using System;

namespace ZapretLauncher
{
    enum WatchdogStep
    {
        Healthy,      // всё, что должно открываться, открылось — серия неудач сброшена
        Wait,         // не открылось, но неудач подряд ещё мало — перепроверить через RecheckMinutes
        NotifyOnly,   // сторож выключен в настройках — только уведомление с кнопкой «Подобрать стратегию»
        VpnActive,    // включён сторонний VPN — стратегию не трогаем
        Restart,      // сначала перезапуск текущей стратегии (winws мог «залипнуть»), перепроверка через AfterRestartMinutes
        Cooldown,     // подбор уже был меньше PickCooldownMinutes назад — второй раз подряд не перебираем, только уведомление
        Pick,         // подобрать новую стратегию
    }

    // Сторож не дёргается из-за разовых сбоев: перепроверяет раз в 2 минуты; если не открывается один сайт — ждёт 5 неудач
    // подряд (~8 минут), если несколько — 3 (~4 минуты). Сначала просто перезапускает текущую стратегию и только потом подбирает новую
    class WatchdogPolicy
    {
        public const int RecheckMinutes = 2, AfterRestartMinutes = 1, PickCooldownMinutes = 30;

        int streak;
        public int Attempt { get; private set; }     // номер этой неудачной проверки подряд — для журнала: «проверка 2 из 5»
        public int Needed { get; private set; }      // сколько неудач подряд нужно, чтобы действовать
        public bool Restarted { get; private set; }  // в этой серии неудач текущую стратегию уже перезапускали
        public DateTime LastPick { get; private set; }

        public WatchdogPolicy() { LastPick = DateTime.MinValue; }

        // ok — сколько проверяемых сайтов открылось, expected — сколько должно, failing — сколько не открылось.
        // vpnActive спрашивается, только когда дело дошло до действия: поиск VPN-адаптеров не бесплатный
        public WatchdogStep Next(int ok, int expected, int failing, bool enabled, Func<bool> vpnActive, DateTime now)
        {
            if (ok >= expected) { Reset(); return WatchdogStep.Healthy; }
            Needed = failing > 1 ? 3 : 5;
            Attempt = ++streak;
            if (streak < Needed) return WatchdogStep.Wait;
            if (!enabled) { streak = 0; return WatchdogStep.NotifyOnly; }
            if (vpnActive()) { streak = 0; return WatchdogStep.VpnActive; }
            if (!Restarted) { Restarted = true; return WatchdogStep.Restart; }
            streak = 0;
            Restarted = false;
            if ((now - LastPick).TotalMinutes < PickCooldownMinutes) return WatchdogStep.Cooldown;
            LastPick = now;
            return WatchdogStep.Pick;
        }

        // Новая серия: сайты открылись или компьютер вышел из сна
        public void Reset()
        {
            streak = 0;
            Restarted = false;
        }
    }
}
