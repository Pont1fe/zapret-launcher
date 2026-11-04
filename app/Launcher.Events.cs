// Привязка кнопок и переключателей окна к действиям
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
        // ---------- События ----------
        void Click(string name, Func<Task> handler) { E<ButtonBase>(name).Click += async delegate { await handler(); }; }
        void ClickSync(string name, Action handler) { E<ButtonBase>(name).Click += delegate { handler(); }; }

        void WireEvents()
        {
            ClickSync("BtnMinimize", () => { window.WindowState = WindowState.Minimized; });
            ClickSync("BtnCompact", () => SetCompact(!cfg.Compact, true));
            ClickSync("BtnMaximize",() => { window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; });
            E<Canvas>("HistoryBar").SizeChanged += delegate { RenderHistory(); };
            ClickSync("BtnClose", () => window.Close());
            foreach (string p in new[] { "Strategies", "Domains", "Vpn", "Settings", "Log" })
            {
                string page = p;
                E<RadioButton>("Nav" + p).Checked += delegate { ShowPage(page); };
            }
            ClickSync("NavFolder", () => Process.Start("explorer.exe", "\"" + Paths.Zapret + "\""));
            ClickSync("NavInfo", () => Process.Start("https://github.com/" + cfg.LauncherRepo));

            E<FrameworkElement>("PowerButton").MouseLeftButtonUp += async delegate { await ToggleBypass(); };
            Click("BtnSiteCheck", PingNow);
            Click("BtnPing", PingNow);

            TextBox search = E<TextBox>("SearchBox");
            search.TextChanged += delegate
            {
                Text("SearchHint").Visibility = search.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
                UpdateStrategyList();
            };
            ListBox strategies = E<ListBox>("StrategyList");
            strategies.SelectionChanged += async delegate
            {
                StrategyItem item = strategies.SelectedItem as StrategyItem;
                if (filling || item == null || item.File == cfg.Strategy) return;
                await SelectStrategy(item.File);
            };
            strategies.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                // заголовок группы: запоминаем, свёрнута ли она
                ToggleButton g = e.OriginalSource as ToggleButton;
                if (g != null && g.Tag is string && E<TextBox>("SearchBox").Text.Trim().Length == 0)
                {
                    string group = (string)g.Tag;
                    cfg.CollapsedGroups.Remove(group);
                    if (g.IsChecked != true) cfg.CollapsedGroups.Add(group);
                    cfg.Save();
                    return;
                }
                Button b = e.OriginalSource as Button;
                if (b != null && b.Tag is string) ToggleFavorite((string)b.Tag);
            }));

            Click("BtnCheck", () => RunBusy(() => CheckUpdates(false, false), "Проверка…"));
            Click("BtnInstallUpdate", InstallZapretUi);
            Click("BtnMain", () => StartAutoPickUi(false));
            Click("BtnQuickPick", () => StartAutoPickUi(true));
            ClickSync("BtnService", () =>
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + Paths.InZapret("service.bat") + "\" admin\"");
                psi.WorkingDirectory = Paths.Zapret;
                psi.UseShellExecute = false;
                psi.EnvironmentVariables["NO_UPDATE_CHECK"] = "1";
                Process.Start(psi);
            });
            Click("BtnFixDiscord", FixDiscordUi);
            // VPN

            Click("ChipVpn", async () => { await ToggleVpn(); E<ToggleButton>("ChipVpn").IsChecked = vpnRunning; });
            Click("BtnVpnAdd", AddVpnServers);
            Click("BtnVpnRefresh", RefreshSubscription);
            Click("BtnVpnPing", PingVpnServers);
            Click("BtnVpnBest", SelectBestServer);
            Click("BtnVpnClipAdd", AddFromClipboard);
            Click("BtnVpnAddBlocked", AddBlockedToVpn);
            Click("BtnFixConflict", FixConflicts);
            Click("BtnVpnQrFile", QrFromFile);
            Click("BtnVpnQrScreen", QrFromScreen);
            Click("BtnVpnAwgFile", AwgFromFile);
            ClickSync("SwVpnAuto", () => { cfg.VpnAutoBest = E<CheckBox>("SwVpnAuto").IsChecked == true; cfg.Save(); });
            ClickSync("VpnHideDead", () => { cfg.VpnHideDead = E<ToggleButton>("VpnHideDead").IsChecked == true; cfg.Save(); RefreshVpnUi(); });
            Click("SwVpnKill", async () =>
            {
                cfg.VpnKillSwitch = E<CheckBox>("SwVpnKill").IsChecked == true;
                cfg.Save();
                // выключили, пока блокировка работает, — снимаем её вместе с VPN: иначе сайты так и не откроются
                if (!cfg.VpnKillSwitch && vpnLocked) { await RunBusy(StopVpn, "VPN…"); RefreshVpnUi(); }
            });
            window.Activated += delegate { if (E<FrameworkElement>("PageVpn").Visibility == Visibility.Visible) CheckClipboard(); };
            Click("BtnVpnListAdd", AddVpnListItem);
            Click("BtnVpnListFile", async () => { string text = ReadListFile(); if (text != null) await AddVpnListItems(text); });
            Click("VpnModeSplit", async () => { if (filling || cfg.VpnMode == "split") return; cfg.VpnMode = "split"; await ApplyVpnChanges(); });
            Click("VpnModeAll", async () => { if (filling || cfg.VpnMode == "all") return; cfg.VpnMode = "all"; await ApplyVpnChanges(); });
            Click("VpnModeRu", async () => { if (filling || cfg.VpnMode == "ru") return; cfg.VpnMode = "ru"; await ApplyVpnChanges(); });
            TextBox vpnSearch = E<TextBox>("VpnSearch");
            vpnSearch.TextChanged += delegate { Text("VpnSearchHint").Visibility = vpnSearch.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; RefreshVpnUi(); };
            E<FrameworkElement>("VpnSubBarBg").SizeChanged += (s, e) => { E<Border>("VpnSubBar").Width = Math.Max(0, e.NewSize.Width * subRatio); };
            foreach (string pane in new[] { "Servers", "Routes", "Conn" })
            {
                string p = pane;
                E<RadioButton>("VpnTab" + p).Checked += async delegate
                {
                    string[] tabs = { "Servers", "Routes", "Conn" };
                    double from = Array.IndexOf(tabs, p) >= Array.IndexOf(tabs, vpnPane) ? 24 : -24;
                    vpnPane = p;
                    foreach (string q in tabs)
                        E<FrameworkElement>("VpnPane" + q).Visibility = q == p ? Visibility.Visible : Visibility.Collapsed;
                    SlideIn(E<FrameworkElement>("VpnPane" + p), from);
                    vpnConnShown = p == "Conn";   // журнал обновляется, только пока открыта его вкладка
                    await RefreshConnections();
                };
            }
            // вкладки настроек: разделы помечены Tag с именем вкладки
            foreach (string[] st in new[] { new[] { "SetTabMain", "main" }, new[] { "SetTabBypass", "bypass" }, new[] { "SetTabNet", "net" }, new[] { "SetTabLook", "look" }, new[] { "SetTabLauncher", "launcher" } })
            {
                string group = st[1];
                E<RadioButton>(st[0]).Checked += delegate { ShowSettingsGroup(group); };
            }
            ShowSettingsGroup("main");
            ClickSync("BtnVpnHelpOk",() => { cfg.VpnHelpDone = true; cfg.Save(); RefreshVpnUi(); });
            E<FrameworkElement>("VpnCard").MouseLeftButtonUp += async delegate { if (CurrentServer != null && !busy) await ToggleVpn(); };
            E<FrameworkElement>("VpnGraph").SizeChanged += delegate { DrawVpnGraph(); };
            ClickSync("VpnKindSites", RefreshVpnUi);
            ClickSync("VpnKindApps", RefreshVpnUi);
            TextBox vpnInput = E<TextBox>("VpnInput"), vpnListInput = E<TextBox>("VpnListInput");
            vpnInput.KeyDown += async (s, e) => { if (e.Key == Key.Return) await AddVpnServers(); };
            vpnInput.TextChanged += delegate { Text("VpnHint").Visibility = vpnInput.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; };
            vpnListInput.KeyDown += async (s, e) => { if (e.Key == Key.Return) await AddVpnListItem(); };
            PasteAsOneLine(vpnListInput);
            vpnListInput.TextChanged += delegate { Text("VpnListHint").Visibility = vpnListInput.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; };

            ClickSync("BtnBackupSave", BackupSave);
            ClickSync("BtnBackupRestore", BackupRestore);
            Click("BtnLauncherRollback", RollbackLauncherUi);
            Click("BtnCheckDomain", CheckDomain);
            Click("SwSecureDns", () => SetSecureDns(E<CheckBox>("SwSecureDns").IsChecked == true, DnsChoice()));
            foreach (string key in SecureDns.Providers.Keys)
            {
                string k = key;
                // другой сервер при включённом защищённом DNS — переключаем сразу
                Click("Dns" + char.ToUpper(k[0]) + k.Substring(1), () => cfg.SecureDns.Length > 0 && cfg.SecureDns != k ? SetSecureDns(true, k) : Task.FromResult(0));
            }
            Click("BtnRemoveService",() => RunBusy(async () => { await Zapret.RemoveService(); await Zapret.Stop(true); }));
            ClickSync("BtnRollback", () => RollbackUi(true));
            Click("BtnUninstall", async () =>
            {
                MessageBoxResult a = Dialog.Show(window,
                    T("Остановить обход и убрать автозапуск, службы WinDivert, записи Telegram в hosts и ярлыки?") + "\n\n" +
                    T("Да — удалить и папку") + " " + Paths.Root + " " + T("целиком") + "\n" + T("Нет — оставить файлы") + "\n" + T("Отмена — ничего не делать"),
                    T("Удалить всё"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (a == MessageBoxResult.Cancel) return;
                await RunBusy(async () => { await Uninstaller.Remove(a == MessageBoxResult.Yes); Exit(); }, "Удаление…");
            });
            Click("BtnLauncherUpdate", () => UpdateLauncherUi(true));

            // фильтры
            foreach (string[] gf in new[] { new[] { "GfOff", "disabled" }, new[] { "GfTcp", "tcp" }, new[] { "GfUdp", "udp" }, new[] { "GfAll", "all" } })
            {
                string mode = gf[1];
                Click(gf[0], async () => { Zapret.SetGameFilter(mode); await RestartIfRunning(); });
            }
            foreach (string[] ip in new[] { new[] { "IpNone", "none" }, new[] { "IpLoaded", "loaded" }, new[] { "IpAny", "any" } })
            {
                string mode = ip[1];
                Click(ip[0], async () => { await RunBusy(() => Zapret.SetIpset(mode)); UpdateFilterUi(); await RestartIfRunning(); });
            }
            Click("BtnIpsetUpdate", async () => { await RunBusy(Zapret.UpdateIpsetList, "Обновление…"); await RestartIfRunning(); });

            // домены
            ClickSync("DomGeneral", UpdateDomainUi);
            ClickSync("DomExclude", UpdateDomainUi);
            Click("BtnAddDomain", AddDomain);
            Click("BtnDomainFile", async () => { string text = ReadListFile(); if (text != null) await AddDomains(text); });
            TextBox input = E<TextBox>("DomainInput");
            input.KeyDown += async (s, e) => { if (e.Key == Key.Return) await AddDomain(); };
            PasteAsOneLine(input);
            input.TextChanged += delegate { Text("DomainHint").Visibility = input.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; };
            Click("BtnRevertStrategy", RevertStrategy);
            ClickSync("BtnStrategyChangedClose", () => E<FrameworkElement>("StrategyChangedBar").Visibility = Visibility.Collapsed);

            // журнал
            SearchBox("LogSearch", "LogSearchHint", FilterLog);
            ClickSync("LogImportant", FilterLog);
            Click("BtnLogCopy", CopyLog);
            Click("BtnLogReport", SaveReport);
            ClickSync("BtnLogOpen", () => { try { Process.Start("notepad.exe", "\"" + Paths.Log + "\""); } catch (Exception e) { Log.Write(e.Message); } });
            E<ListBox>("LogList").IsVisibleChanged += (s, e) =>
            {
                if ((bool)e.NewValue && logView.Count > 0) E<ListBox>("LogList").ScrollIntoView(logView[logView.Count - 1]);
            };
            SearchBox("DomainSearch", "DomainSearchHint", UpdateDomainUi);
            SearchBox("VpnListSearch", "VpnListSearchHint", RenderVpnList);

            // оформление
            ClickSync("ThemeDark", () => SetTheme("dark"));
            ClickSync("ThemeLight", () => SetTheme("light"));
            ClickSync("ThemeSystem", () => SetTheme("system"));
            ClickSync("SwMica", () =>
            {
                cfg.Transparent = E<CheckBox>("SwMica").IsChecked == true;
                cfg.Save();
                ApplyTheme();
            });
            foreach (string accent in Theme.Accents.Keys.Concat(new[] { "system" }))
            {
                string a = accent;
                ClickSync("Accent" + char.ToUpper(a[0]) + a.Substring(1), () => SetAccent(a));
            }
            foreach (int scale in new[] { 90, 100, 115, 130 })
            {
                int sc = scale;
                ClickSync("Scale" + sc, () => SetScale(sc));
            }
            // клик по «?» не должен переключать настройку, внутри которой он стоит
            foreach (string help in new[] { "HelpWatchdog", "HelpVpn", "HelpNet", "HelpGf", "HelpIpset", "HelpDns" })
                E<UIElement>(help).MouseLeftButtonDown += (s, e) => e.Handled = true;
            ClickSync("LangRu", () => SetLanguage("ru"));
            ClickSync("LangEn", () => SetLanguage("en"));
            ClickSync("BtnOnboarding", ShowOnboarding);
            Click("OnbNext", () => OnboardingNext(false));
            Click("OnbSkip", () => OnboardingNext(true));

            // переключатели
            Click("SwAutostart", () => SetAutostart(E<CheckBox>("SwAutostart").IsChecked == true));
            Click("ChipAutostart", () => SetAutostart(E<ToggleButton>("ChipAutostart").IsChecked == true));
            ClickSync("SwAutoUpdate", () => SetAutoUpdate(E<CheckBox>("SwAutoUpdate").IsChecked == true));
            ClickSync("ChipAutoUpdate", () => SetAutoUpdate(E<ToggleButton>("ChipAutoUpdate").IsChecked == true));
            ClickSync("SwStartOnOpen", () => { cfg.StartOnOpen = E<CheckBox>("SwStartOnOpen").IsChecked == true; cfg.Save(); });
            ClickSync("SwWatchdog", () =>
            {
                cfg.Watchdog = E<CheckBox>("SwWatchdog").IsChecked == true;
                cfg.Save();
                Log.Write(cfg.Watchdog ? "Сторож включён" : "Сторож выключен");
            });
            Click("SwVpnPause", async () =>
            {
                cfg.VpnPause = E<CheckBox>("SwVpnPause").IsChecked == true;
                cfg.Save();
                Log.Write(cfg.VpnPause ? "Пауза при VPN включена" : "Пауза при VPN выключена");
                await UpdateConflicts();
                if (!cfg.VpnPause && pausedByVpn) await StartBypassUi();
            });
            ClickSync("SwNetProfiles", () =>
            {
                cfg.NetworkProfilesEnabled = E<CheckBox>("SwNetProfiles").IsChecked == true;
                cfg.Save();
                Log.Write(cfg.NetworkProfilesEnabled ? "Профили сетей включены" : "Профили сетей выключены");
            });
            ClickSync("SwToasts", () =>
            {
                cfg.Toasts = E<CheckBox>("SwToasts").IsChecked == true;
                cfg.Save();
                if (cfg.Toasts && !Toasts.Show("Zapret Launcher", T("Уведомления включены"), null, null))
                    Log.Write("Уведомления Windows недоступны — нужен ярлык лаунчера в меню «Пуск»; пока будут подсказки в трее");
            });
            Click("SwTelegram", () => SetTelegram(E<CheckBox>("SwTelegram").IsChecked == true));
            Click("ChipTelegram", () => SetTelegram(E<ToggleButton>("ChipTelegram").IsChecked == true));
            Click("BtnTgConnect", () => RunBusy(async () =>
            {
                if (!Zapret.Running) throw new Exception("Сначала включите обход — Telegram-прокси работает вместе с ним");
                if (!cfg.Telegram)
                {
                    cfg.Telegram = true;
                    E<CheckBox>("SwTelegram").IsChecked = true;
                    E<ToggleButton>("ChipTelegram").IsChecked = true;
                }
                cfg.TelegramLinked = true;   // ссылку откроем ниже сами
                cfg.Save();
                await Telegram.StartProxy(cfg);
                Telegram.Connect(cfg);
            }, "Telegram…"));

            window.Closing += (s, e) =>
            {
                if (exiting) return;
                e.Cancel = true;
                window.Hide();
                if (!hintShown)
                {
                    tray.ShowBalloonTip(4000, "Zapret Launcher", T("Лаунчер свёрнут в трей. Клик по значку — мини-окно, выход — через меню значка."), Forms.ToolTipIcon.Info);
                    hintShown = true;
                }
            };
        }
    }
}
