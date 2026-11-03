// Сторож и подбор стратегии: проверка сайтов, перезапуск, автоподбор, «Вернуть прежнюю», сравнение после обновления
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ZapretLauncher
{
    partial class Launcher
    {
        // ---------- Сторож ----------
        async Task CrashGuard()
        {
            if (busy || !cfg.Watchdog || !wantRunning || pausedByVpn || Zapret.Running) return;
            if ((DateTime.Now - lastRestart).TotalSeconds < 30) return;
            lastRestart = DateTime.Now;
            Log.Write("Сторож: winws.exe не работает — перезапускаю");
            await StartBypassUi();
            if (!Zapret.Running && !pausedByVpn)
            {
                wantRunning = false;
                crashed = true;
                Notify("Сторож: перезапуск не удался — больше не пытаюсь, пока обход не включат вручную", new[] { "Включить обход", "start" });
            }
        }

        // Кнопки «Обновить пинг»: сразу перепроверить сайты и TCP-пинг
        async Task PingNow()
        {
            if (siteChecking || busy) return;
            await SiteCheck(false);
        }

        // Проверка сайтов: обновляет карточки; health — ещё и сторож (серия неудач — перезапуск, потом подбор новой стратегии)
        async Task SiteCheck(bool health)
        {
            siteCheckDue = DateTime.MaxValue;
            siteChecking = true;
            BuildSitePanel();
            List<SiteResult> res;
            try { res = await Sites.Test(cfg.Telegram, true); }
            finally { siteChecking = false; }
            // ручную проверку показываем и без обхода (видно, что заблокировано); фоновую — только при работающем обходе
            if (health && !Zapret.Running) { siteResults = null; BuildSitePanel(); return; }
            SiteResult control = res.FirstOrDefault(r => r.Name == Sites.Control);
            animateCards = true;   // свежие результаты — карточки появляются по очереди
            siteResults = res.Where(r => r.Name != Sites.Control).ToList();
            foreach (SiteResult r in siteResults)
            {
                List<int> h;
                if (!siteHistory.TryGetValue(r.Name, out h)) siteHistory[r.Name] = h = new List<int>();
                h.Add(r.Ok ? r.Ms : -1);
                if (h.Count > 16) h.RemoveAt(0);
            }
            // заблокированный по адресу сайт открылся (провайдер снял блокировку или он пошёл через VPN) — снова обычный
            if (siteResults.Where(r => r.Ok).ToList().Count(r => cfg.BlockedSites.Remove(r.Name)) > 0) cfg.Save();
            BuildSitePanel();
            if (control != null && control.Ok) CompareAfterUpdate();
            if (!health || busy || control == null || !control.Ok) return;   // нет интернета — менять стратегию бессмысленно

            // сайты, которые режут по адресу, сторож не считает: новая стратегия их всё равно не откроет.
            // Не считает и сайты, которые сейчас идут через VPN: обход на них не влияет
            List<SiteResult> watched = siteResults.Where(r => !cfg.BlockedSites.Contains(r.Name) && !SiteViaVpn(r.Name)).ToList();
            int ok = watched.Count(r => r.Ok);
            int expected = baseline.HasValue ? Math.Min(baseline.Value, watched.Count) : watched.Count;
            List<string> failing = watched.Where(r => !r.Ok).Select(r => r.Name).ToList();
            if (ok < expected && failing.Count > 0)
            {
                // не открылось — сразу перепроверяем эти сайты с запасом по времени: чаще всего это разовый сбой, а не блокировка
                List<SiteResult> recovered = (await Sites.Retest(failing, cfg.Telegram)).Where(r => r.Ok).ToList();
                if (busy || !Zapret.Running) return;   // пока перепроверяли, обход перезапустили или выключили — эти данные уже не о нём
                foreach (SiteResult again in recovered)
                {
                    ok++;
                    failing.Remove(again.Name);
                    int i = siteResults.FindIndex(r => r.Name == again.Name);
                    if (i >= 0) siteResults[i] = again;   // карточка и история — по повторной проверке
                    List<int> h;
                    if (siteHistory.TryGetValue(again.Name, out h) && h.Count > 0) h[h.Count - 1] = again.Ms;
                }
                if (recovered.Count > 0) BuildSitePanel();
            }
            // что делать — решает WatchdogPolicy (Watchdog.cs), здесь только действия
            WatchdogStep step = watchdog.Next(ok, expected, failing.Count, cfg.Watchdog, () => Conflicts.ActiveVpn() != null, DateTime.Now);
            if (step == WatchdogStep.Healthy) return;
            // первая неудача подряд — ещё не повод писать в журнал; со второй уже видно, что сайт не открывается всерьёз
            if (step != WatchdogStep.Wait || watchdog.Attempt >= 2)
                Log.Write("Сторож: не открывается " + string.Join(", ", failing) + " (проверка " + watchdog.Attempt + " из " + watchdog.Needed + ")");
            switch (step)
            {
                case WatchdogStep.Wait:
                    siteCheckDue = DateTime.Now.AddMinutes(WatchdogPolicy.RecheckMinutes);
                    return;
                case WatchdogStep.NotifyOnly:
                    Notify("Не открывается " + string.Join(", ", failing), new[] { "Подобрать стратегию", "pick" });
                    return;
                case WatchdogStep.VpnActive:
                    Log.Write("Сторож: включён VPN — стратегию не меняю");
                    return;
                case WatchdogStep.Restart:
                    Log.Write("Сторож: перезапускаю текущую стратегию — проверю ещё раз через минуту");
                    await RestartIfRunning();
                    siteCheckDue = DateTime.Now.AddMinutes(WatchdogPolicy.AfterRestartMinutes);
                    return;
                case WatchdogStep.Cooldown:
                    // второй раз подряд перебирать стратегии (и отключать обход на минуту) не будем
                    Notify("Не открывается " + string.Join(", ", failing) + ". " + T("Стратегию недавно уже меняли — подберите вручную, если нужно"), new[] { "Подобрать стратегию", "pick" });
                    return;
            }
            Notify("Стратегия перестала работать — подбираю новую");
            string before = cfg.Strategy;
            await RunBusy(async () =>
            {
                PickBest best = await AutoPick(true);
                if (best == null || best.Ok == 0) return;
                if (best.File == before) { Notify("Новая стратегия: " + Zapret.Title(best.File)); return; }
                strategyBeforeWatchdog = before;
                string change = T("Сторож сменил стратегию") + ": " + T(Zapret.Title(before)) + " → " + T(Zapret.Title(best.File));
                Text("StrategyChangedText").Text = change;
                Text("StrategyChangedText").ToolTip = change;
                E<FrameworkElement>("StrategyChangedBar").Visibility = Visibility.Visible;
                Notify(change, new[] { "Вернуть прежнюю", "revert" });
            }, "Подбор…");
        }

        string strategyBeforeWatchdog;   // стратегия до замены сторожем — для кнопки «Вернуть прежнюю»
        readonly WatchdogPolicy watchdog = new WatchdogPolicy();   // серия неудач, был ли перезапуск, когда был подбор

        async Task RevertStrategy()
        {
            E<FrameworkElement>("StrategyChangedBar").Visibility = Visibility.Collapsed;
            string prev = strategyBeforeWatchdog;
            strategyBeforeWatchdog = null;
            if (prev == null || !File.Exists(Paths.InZapret(prev))) return;
            Log.Write("Возвращаю стратегию " + Zapret.Title(prev) + " по просьбе пользователя");
            await SelectStrategy(prev);
        }

        // Сайт сейчас идёт через встроенный VPN: в режимах «Россия напрямую» и «Весь трафик» — любой из проверяемых (они не российские),
        // в режиме «Только выбранное» — если его набор целиком в маршрутах
        bool SiteViaVpn(string site)
        {
            if (!vpnRunning) return false;
            if (cfg.VpnMode != "split") return true;
            SitePreset p = BlockedPreset(site);
            return p != null && SitePresets.IsOn(p, cfg.VpnDomains);
        }

        // После автообновления zapret: если сайтов открывается меньше, чем до него, — предлагаем вернуть прошлую версию
        int? okBeforeUpdate;
        string updatedFrom;

        void CompareAfterUpdate()
        {
            if (updatedFrom == null || !okBeforeUpdate.HasValue || siteResults == null) return;
            string from = updatedFrom;
            int before = okBeforeUpdate.Value, now = siteResults.Count(r => r.Ok);
            updatedFrom = null;
            okBeforeUpdate = null;
            if (now >= before || Zapret.VersionOf(Paths.Backup) != from) return;
            string text = T("После обновления zapret сайты открываются хуже") + " (" + now + " " + T("из") + " " + siteResults.Count + " " + T("вместо") + " " + before + "). " +
                          T("Вернуть версию") + " " + from + "?";
            Log.Write(text);
            if (window.IsVisible)
            {
                if (Ask(text, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) RollbackUi(false);
            }
            else Notify(text, new[] { "Вернуть прошлую версию", "rollback" });
        }

        async void RollbackUi(bool ask)
        {
            if (ask && Dialog.Show(window, T("Вернуть zapret") + " " + Zapret.VersionOf(Paths.Backup) + " " + T("вместо") + " " + Zapret.LocalVersion + "?\n" + T("Ваши списки и фильтры сохранятся."),
                "Zapret Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await RunBusy(async () => { await Zapret.Rollback(cfg, s => StartBypass(false)); UpdateStrategyList(); UpdateTitle(); }, "Откат…");
        }

        // ---------- Подбор стратегии ----------
        class PickBest { public string File; public int Ok, Total, Score; }

        // favoritesOnly — быстрый подбор: только стратегии со звёздочкой
        async Task<PickBest> AutoPick(bool stopAtFirstPerfect, bool favoritesOnly = false)
        {
            List<string> strategies = Zapret.Strategies();
            if (favoritesOnly) strategies = strategies.Where(s => cfg.Favorites.Contains(s)).ToList();
            if (strategies.Count == 0) throw new Exception("zapret не установлен");
            string original = cfg.Strategy;
            cancelPick = false;
            picking = true;
            E<Button>("BtnMain").Content = T("Остановить подбор");
            PickBest best = null;
            Log.Write("Подбор стратегии: проверяю " + strategies.Count + " шт. (" + string.Join(", ", Sites.Targets(cfg.Telegram, false).Select(t => t.Key)) + ")");
            Stopwatch total = Stopwatch.StartNew();
            ShowPickProgress(0, strategies.Count);
            HashSet<string> seen = new HashSet<string>(), everOk = new HashSet<string>();   // для поиска сайтов, заблокированных по адресу
            int tested = 0;
            try
            {
                for (int i = 0; i < strategies.Count; i++)
                {
                    if (cancelPick) { Log.Write("Подбор остановлен"); break; }
                    string s = strategies[i];
                    Text("PowerStatus").Text = T("Подбор") + " " + (i + 1) + " / " + strategies.Count;
                    Text("PowerTimer").Text = T(Zapret.Title(s));
                    cfg.Strategy = s;
                    UpdateStrategyList();   // в списке подсвечивается проверяемая стратегия, метки обновляются по ходу
                    siteChecking = true;
                    BuildSitePanel();
                    List<SiteResult> res;
                    try
                    {
                        // первый запуск — с полной подготовкой системы, дальше — быстрый
                        try { await StartBypass(true, i > 0); }
                        catch { cfg.PickScores[s] = "0/0/0"; cfg.PickDetails.Remove(s); Log.Write("  " + Zapret.Title(s) + " — не запустилась"); ShowPickProgress(i + 1, strategies.Count); continue; }
                        await Task.Delay(400);
                        res = await Sites.Test(cfg.Telegram, false, 4);
                    }
                    finally { siteChecking = false; }
                    List<SiteResult> okRes = res.Where(r => r.Ok).ToList();
                    tested++;
                    foreach (SiteResult r in res) { seen.Add(r.Name); if (r.Ok) everOk.Add(r.Name); }
                    int avg = okRes.Count > 0 ? (int)okRes.Average(r => r.Ms) : 0;
                    cfg.PickScores[s] = okRes.Count + "/" + res.Count + "/" + avg;
                    cfg.PickDetails[s] = string.Join(";", res.Select(r => r.Name + "=" + (r.Ok ? r.Ms : -1)));
                    cfg.PickDate = DateTime.Now.ToString("dd.MM HH:mm");
                    Log.Write("  " + Zapret.Title(s) + " — " + string.Join(", ", res.Select(r => r.Name + ": " + (r.Ok ? "да" : "нет"))));
                    int score = okRes.Count * 100000 - avg;
                    if (best == null || score > best.Score)
                    {
                        best = new PickBest();
                        best.File = s; best.Ok = okRes.Count; best.Total = res.Count; best.Score = score;
                    }
                    // карточки сайтов показывают результат только что проверенной стратегии
                    siteResults = res;
                    BuildSitePanel();
                    Text("PowerHint").Visibility = Visibility.Visible;   // во время подбора — лучшая стратегия на данный момент
                    Text("PowerHint").Text = best.Ok > 0
                        ? T("Лучшая пока") + ": " + T(Zapret.Title(best.File)) + " · " + best.Ok + "/" + best.Total
                        : T("Пока ни одна стратегия не открыла сайты");
                    ShowPickProgress(i + 1, strategies.Count);
                    // «идеальная» — открылось всё, кроме того, что провайдер режет по адресу
                    if (stopAtFirstPerfect && res.All(r => r.Ok || cfg.BlockedSites.Contains(r.Name))) break;
                }
            }
            finally { picking = false; HidePickProgress(); }
            Log.Write("Подбор занял " + (int)total.Elapsed.TotalSeconds + " сек.");
            UpdateBlockedSites(seen, everOk, tested);
            cfg.Strategy = best != null && best.Ok > 0 ? best.File : original;
            cfg.Save();
            await StartBypass(false);
            UpdateStrategyList();
            if (best != null && best.Ok > 0)
            {
                Log.Write("Лучшая стратегия: " + Zapret.Title(best.File) + " (" + best.Ok + "/" + best.Total + ")");
                baseline = best.Ok;
            }
            else Log.Write("Ни одна стратегия не открыла проверочные сайты — оставляю прежнюю");
            return best;
        }

        // Сайт не открылся ни с одной из проверенных стратегий, хотя другие открывались, — провайдер блокирует его по адресу.
        // Такие сайты не мешают выбору стратегии и не будят сторожа; открылся хоть раз — снова обычный сайт
        void UpdateBlockedSites(HashSet<string> seen, HashSet<string> everOk, int tested)
        {
            List<string> fresh = new List<string>();
            foreach (string site in seen)
            {
                if (everOk.Contains(site)) cfg.BlockedSites.Remove(site);
                else if (tested >= 5 && everOk.Count > 0 && !cfg.BlockedSites.Contains(site)) { cfg.BlockedSites.Add(site); fresh.Add(site); }
            }
            cfg.Save();
            if (fresh.Count == 0) return;
            string names = string.Join(", ", fresh.Select(T));
            Notify(T("Провайдер блокирует по адресу") + ": " + names + ". " + T("zapret тут не поможет — пустите через VPN"), new[] { "Открыть VPN", "vpn" });
            BuildSitePanel();
        }

        // Полоска на карточке-переключателе и прогресс на значке в панели задач
        void ShowPickProgress(int done, int count)
        {
            double value = count == 0 ? 0 : (double)done / count;
            E<FrameworkElement>("PickProgress").Visibility = Visibility.Visible;
            ScaleTransform scale = E<Border>("PickFill").RenderTransform as ScaleTransform;
            if (scale == null || scale.IsFrozen) { scale = new ScaleTransform(0, 1); E<Border>("PickFill").RenderTransform = scale; }
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(value, new Duration(TimeSpan.FromMilliseconds(300))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            if (window.TaskbarItemInfo == null) window.TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();
            window.TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
            window.TaskbarItemInfo.ProgressValue = value;
        }

        void HidePickProgress()
        {
            E<FrameworkElement>("PickProgress").Visibility = Visibility.Collapsed;
            if (window.TaskbarItemInfo != null) window.TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
        }

        async Task StartAutoPickUi(bool favoritesOnly)
        {
            if (picking) { cancelPick = true; return; }
            if (Zapret.LocalVersion == null) return;
            if (favoritesOnly && cfg.Favorites.Count(f => File.Exists(Paths.InZapret(f))) < 2)
            {
                Ask("Для быстрого подбора отметьте звёздочкой хотя бы две стратегии в списке.");
                return;
            }
            string vpn = Conflicts.ActiveVpn();
            if (vpn != null && Dialog.Show(window, T("Интернет идёт через VPN") + " (" + vpn + ").\n\n" + T("Выключите VPN на время подбора, иначе результат будет неверным. Всё равно продолжить?"),
                "Zapret Launcher", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await RunBusy(async () =>
            {
                PickBest best = await AutoPick(false, favoritesOnly);
                if (best != null && best.Ok > 0) Notify("Подбор завершён: " + Zapret.Title(best.File) + " (" + best.Ok + " из " + best.Total + " сайтов)");
            }, "Подбор…");
        }
    }
}
