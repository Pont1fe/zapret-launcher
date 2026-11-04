// Обход: запуск и остановка winws, выбор стратегии, пауза при VPN, конфликты с другими программами
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
        // ---------- Запуск и остановка ----------
        async Task StartBypass(bool quiet, bool fast = false)
        {
            if (!File.Exists(Paths.InZapret(cfg.Strategy)))
            {
                if (!File.Exists(Paths.InZapret("general.bat"))) throw new Exception("zapret не установлен");
                Log.Write("Стратегия " + cfg.Strategy + " не найдена, использую general.bat");
                cfg.Strategy = "general.bat";
                cfg.Save();
            }
            if (Zapret.ServiceRunning)
            {
                if (Ask("Работает служба zapret, установленная через service.bat.\nЧтобы управлять обходом из лаунчера, её нужно удалить (автозапуск лаунчер настроит сам).\n\nУдалить службу?",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await Zapret.RemoveService();
            }
            if (!quiet && cfg.VpnPause && (vpnName = Conflicts.ActiveVpn()) != null)
            {
                wantRunning = true;
                pausedByVpn = true;
                await Zapret.Stop(false);
                Log.Write("Включён VPN (" + vpnName + ") — обход включится, когда VPN отключат");
                return;
            }
            if (cfg.Telegram && !quiet) { try { Telegram.SetWeb(true); } catch (Exception e) { Log.Write("Telegram Web: " + e.Message); } }

            await Zapret.Start(cfg.Strategy, fast);
            wantRunning = true;
            pausedByVpn = false;
            crashed = false;
            if (quiet) return;
            Log.Write("Запущена стратегия: " + Zapret.Title(cfg.Strategy));
            SaveNetworkProfile();
            siteResults = null;
            siteCheckDue = DateTime.Now.AddSeconds(4);
            if (cfg.Telegram) { try { await Telegram.StartProxy(cfg); } catch (Exception e) { Log.Write("Telegram-прокси не запустился: " + e.Message); } }
        }

        async Task StopBypass()
        {
            wantRunning = false;
            pausedByVpn = false;
            crashed = false;
            await Zapret.Stop(false);
            await Telegram.StopProxy();
            siteResults = null;
            BuildSitePanel();
        }

        Task StartBypassUi() { return RunBusy(() => StartBypass(false), "Подключение…"); }
        Task StopBypassUi() { return RunBusy(StopBypass, "Отключение…"); }
        // Лаунчер занят (идёт другой перезапуск) — не теряем изменение: перезапустим, как только он освободится
        bool bypassRestartPending, vpnRestartPending;

        Task RestartIfRunning()
        {
            if (!Zapret.Running) return Task.FromResult(0);
            if (busy) { bypassRestartPending = true; return Task.FromResult(0); }
            return RunBusy(() => StartBypass(false), "Применяю…");
        }

        void RunPendingRestarts()
        {
            if (!bypassRestartPending && !vpnRestartPending) return;
            window.Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (busy) return;   // снова занят — выполнится после следующего RunBusy
                if (bypassRestartPending) { bypassRestartPending = false; await RestartIfRunning(); }
                if (vpnRestartPending) { vpnRestartPending = false; if (cfg.VpnOn) { await RunBusy(StartVpnOrLock, "VPN…"); RefreshVpnUi(); } }
            }));
        }

        async Task ToggleBypass()
        {
            if (picking) cancelPick = true;
            else if (Zapret.Running || pausedByVpn) await StopBypassUi();
            else if (Zapret.LocalVersion != null) await StartBypassUi();
            else await InstallZapretUi();
        }

        // ---------- Пауза при VPN ----------
        async Task CheckVpn()
        {
            vpnName = await Task.Run(() => Conflicts.ActiveVpn());
            if (!cfg.VpnPause || busy || exiting) return;
            if (vpnName != null && Zapret.Running && wantRunning)
            {
                await RunBusy(async () =>
                {
                    await Zapret.Stop(false);
                    await Telegram.StopProxy();
                    pausedByVpn = true;
                    siteResults = null;
                    BuildSitePanel();
                }, "Пауза…");
                Notify("Включён VPN (" + vpnName + ") — обход на паузе");
            }
            else if (vpnName == null && pausedByVpn && wantRunning)
            {
                Log.Write("VPN отключён — включаю обход");
                await StartBypassUi();
            }
        }

        // ---------- Конфликты ----------
        async Task UpdateConflicts()
        {
            List<Conflict> conflicts;
            try { conflicts = await Task.Run(() => Conflicts.All()); } catch { return; }
            if (exiting) return;
            if (cfg.VpnPause) conflicts = conflicts.Where(c => c.Kind != "vpn").ToList();   // с паузой VPN — штатная ситуация
            string text = string.Join("\n", conflicts.Select(c => T(c.Text)));
            if (text != conflictText)
            {
                conflictText = text;
                foreach (Conflict c in conflicts) Log.Write("Внимание: " + c.Text);
            }
            Text("ConflictText").Text = text.Replace("\n", " · ");
            E<Border>("ConflictBar").ToolTip = text.Length > 0 ? text : null;   // полный текст — при наведении
            E<Border>("ConflictBar").Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            // чужие программы обхода и драйвер лаунчер убирает сам; VPN — нет, это решает пользователь
            E<Button>("BtnFixConflict").Visibility = conflicts.Any(c => c.Kind != "vpn") ? Visibility.Visible : Visibility.Collapsed;
        }

        async Task FixConflicts()
        {
            if (Dialog.Show(window, T("Закрыть мешающие программы обхода, остановить их службы и выгрузить чужой драйвер WinDivert? Обход лаунчера перезапустится."),
                "Zapret Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await RunBusy(async () =>
            {
                bool restart = Zapret.Running || wantRunning;
                await Zapret.Stop(true);
                await Conflicts.Fix();
                if (restart) await StartBypass(true);
                await UpdateConflicts();
                Log.Write(E<Border>("ConflictBar").Visibility == Visibility.Visible ? "Не всё удалось исправить — подробности выше" : "Конфликт исправлен");
            }, "Исправляю…");
        }

        // «Починить Discord»: включить обход, если он выключен, закрыть Discord, очистить кэш и открыть заново
        async Task FixDiscordUi()
        {
            if (busy) return;
            ShowWindow();
            if (Ask("Закрыть Discord, очистить его кэш и открыть заново?\nВход и настройки сохранятся. Если идёт звонок, он прервётся.",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await RunBusy(async () =>
            {
                if (!Zapret.Running && !pausedByVpn && Zapret.LocalVersion != null) await StartBypass(false);
                bool opened = await DiscordFix.Run();
                if (!opened) Ask("Кэш Discord очищен. Откройте Discord вручную — ярлык в меню «Пуск» не найден.");
            }, "Discord…");
        }

        async Task SelectStrategy(string file)
        {
            if (file == cfg.Strategy) return;
            // выбрали стратегию сами — предложение сторожа вернуть прежнюю больше не нужно
            E<FrameworkElement>("StrategyChangedBar").Visibility = Visibility.Collapsed;
            strategyBeforeWatchdog = null;
            cfg.Strategy = file;
            cfg.Save();
            UpdateStrategyList();
            if (Zapret.Running) await StartBypassUi();
        }
    }
}
