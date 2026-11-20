// Маршруты VPN: сайты и приложения через VPN, наборы, журнал соединений, заблокированные по адресу сайты
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
        bool VpnSitesKind { get { return E<RadioButton>("VpnKindSites").IsChecked == true; } }

        async Task AddVpnListItem()
        {
            if (await AddVpnListItems(E<TextBox>("VpnListInput").Text)) E<TextBox>("VpnListInput").Text = "";
        }

        string[] PickProtectionFiles()
        {
            var dialog = new OpenFileDialog
            {
                Title = T("Выбрать программу (.exe)…"),
                Filter = T("Программы Windows") + " (*.exe)|*.exe",
                CheckFileExists = true,
                Multiselect = true
            };
            return dialog.ShowDialog(window) == true ? dialog.FileNames : new string[0];
        }

        Task PickVpnApps() { return busy || quitting ? Task.FromResult(0) : AddProtectedApps(PickProtectionFiles()); }

        Task AddProtectedApps(IEnumerable<string> paths)
        {
            var next = new List<string>(cfg.VpnProtectedApps);
            return Routes.AddAppPaths(next, paths.Where(File.Exists)) ? SetProtectedApps(next) : Task.FromResult(0);
        }

        static bool MatchesProtectionPreset(SitePreset preset, string path)
        {
            return path != null && preset.Apps.Any(app => path.Replace('/', '\\').EndsWith("\\" + app, StringComparison.OrdinalIgnoreCase));
        }

        async Task ToggleProtectionPreset(SitePreset preset)
        {
            if (busy || quitting) return;
            var next = cfg.VpnProtectedApps.Where(path => !MatchesProtectionPreset(preset, path)).ToList();
            if (next.Count != cfg.VpnProtectedApps.Count) { await SetProtectedApps(next); return; }

            var paths = new List<string>();
            foreach (string name in preset.Apps)
            {
                foreach (Process process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(name)))
                {
                    using (process)
                        try { paths.Add(process.MainModule.FileName); } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
                }
                foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                    using (RegistryKey key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + name))
                        if (key != null && key.GetValue("") is string) paths.Add(((string)key.GetValue("")).Trim('"'));
            }
            string[] found = VpnFirewall.AppScope(paths.Where(path => MatchesProtectionPreset(preset, path)));
            if (found.Length == 0)
            {
                Text("VpnProtectHint").Text = T("Программа не найдена. Запустите её или выберите файл .exe.");
                found = PickProtectionFiles().Where(path => MatchesProtectionPreset(preset, path)).ToArray();
                if (found.Length == 0) return;
            }
            await AddProtectedApps(found);
        }

        async Task SetProtectedApps(List<string> next)
        {
            if (busy || quitting) return;
            if (next.Count > VpnFirewall.MaxApps)
            {
                Text("VpnProtectHint").Text = T("Можно выбрать до 31 программы.");
                return;
            }

            await RunBusy(async () =>
            {
                var previous = cfg.VpnProtectedApps;
                cfg.VpnProtectedApps = next;
                try
                {
                    if (cfg.VpnOn && cfg.VpnKillSwitch) ApplyVpnFirewall(true);
                }
                catch { cfg.VpnProtectedApps = previous; throw; }
                Text("VpnProtectHint").Text = "";
                if (cfg.VpnKillSwitch) await ApplyVpnChanges();
                else cfg.Save();
            });
            RefreshVpnUi();
        }

        void RefreshVpnKillScope()
        {
            string scope = !cfg.VpnKillSwitch ? "Защита выключена." : cfg.VpnProtectedApps.Count == 0
                ? "Выберите программы для защиты." : VpnFirewall.AppScope(cfg.VpnProtectedApps).Length < cfg.VpnProtectedApps.Count
                ? "Некоторые файлы .exe не найдены. Выберите их заново."
                : "При обрыве остановится трафик выбранных программ.";
            Text("VpnKillScope").Text = T(scope);
            FillPresets(E<StackPanel>("VpnProtectPresets"), cfg.VpnProtectedApps, ToggleProtectionPreset);
            FillItemList(E<StackPanel>("VpnProtectedApps"), cfg.VpnProtectedApps, "", false,
                path => SetProtectedApps(cfg.VpnProtectedApps.Where(app => app != path).ToList()), null);
        }

        async Task<bool> AddVpnListItems(string raw)
        {
            List<string> list = VpnSitesKind ? cfg.VpnDomains : cfg.VpnApps;
            List<string> added = new List<string>(), bad = new List<string>();
            int found = 0;
            IEnumerable<string> parts = SitePresets.SplitItems(raw, VpnSitesKind);
            // путь («C:\Program Files\…») — целиком, а имена программ через пробел — по одному: «Spotify Discord»
            if (!VpnSitesKind)
                parts = parts.SelectMany(t => t.IndexOfAny(new[] { '\\', '/' }) >= 0 ? new[] { t } : t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (string part in parts)
            {
                string item = VpnSitesKind ? Zapret.NormalizeDomain(part) : Routes.NormalizeAppRoute(part);
                if (item == null) { bad.Add(part); continue; }
                found++;
                if (!list.Contains(item, StringComparer.OrdinalIgnoreCase) && !added.Contains(item, StringComparer.OrdinalIgnoreCase)) added.Add(item);
            }
            Text("VpnListInfo").Text = Formats.AddedInfo(added.Count, found, bad, VpnSitesKind ? "Не похоже на домен" : "Нужно имя программы, например Spotify.exe",
                                                 "Уже есть в списке", " ");
            if (added.Count == 0) return false;
            list.AddRange(added);
            await ApplyVpnChanges();
            return true;
        }

        // ---------- Экспорт и импорт маршрутов (сам файл — Routes) ----------
        void ExportRoutes()
        {
            if (cfg.VpnDomains.Count + cfg.VpnApps.Count == 0) { Text("VpnListInfo").Text = T("Список пуст — сохранять нечего"); return; }
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = T("Сохранить маршруты VPN"), FileName = "zapret-launcher-routes.txt", DefaultExt = ".txt",
                Filter = T("Текстовые списки") + " (*.txt)|*.txt"
            };
            if (dlg.ShowDialog(window) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, Routes.RoutesFile(cfg.VpnDomains, cfg.VpnApps), new System.Text.UTF8Encoding(true));
                Text("VpnListInfo").Text = T("Маршруты сохранены") + " — " + T("Сайты") + ": " + cfg.VpnDomains.Count + ", " + T("Программы") + ": " + cfg.VpnApps.Count;
                Log.Write("VPN: маршруты сохранены в " + System.IO.Path.GetFileName(dlg.FileName));
            }
            catch (Exception e) { Ask(T("Не удалось сохранить файл: ") + e.Message, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        async Task ImportRoutes()
        {
            string raw = ReadListFile();
            if (raw == null) return;
            List<string> domains = new List<string>(), apps = new List<string>(), bad = new List<string>();
            Routes.SplitRoutes(raw, domains, apps, bad);
            List<string> newDomains = domains.Where(d => !cfg.VpnDomains.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
            List<string> newApps = apps.Where(a => !cfg.VpnApps.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
            string info = newDomains.Count + newApps.Count > 0
                ? T("Добавлено") + " — " + T("Сайты") + ": " + newDomains.Count + ", " + T("Программы") + ": " + newApps.Count
                : domains.Count + apps.Count > 0 ? T("Всё из файла уже есть в списке") : T("В файле не нашлось сайтов и программ");
            if (bad.Count > 0) info += ". " + T("Не распознано") + ": " + string.Join(", ", bad.Take(5)) + (bad.Count > 5 ? " (+" + (bad.Count - 5) + ")" : "");
            Text("VpnListInfo").Text = info;
            if (newDomains.Count + newApps.Count == 0) return;
            cfg.VpnDomains.AddRange(newDomains);
            cfg.VpnApps.AddRange(newApps);
            Log.Write("VPN: маршруты загружены из файла: " + string.Join(", ", newDomains.Concat(newApps)));
            await ApplyVpnChanges();
        }

        async Task ToggleVpnPreset(SitePreset p)
        {
            if (SitePresets.IsOn(p, cfg.VpnDomains))
            {
                HashSet<string> domains = SitePresets.RemovalSet(p, cfg.VpnDomains, false), apps = SitePresets.RemovalSet(p, cfg.VpnDomains, true);
                cfg.VpnDomains.RemoveAll(domains.Contains);
                cfg.VpnApps.RemoveAll(apps.Contains);
                Log.Write("VPN: набор «" + p.Name + "» убран из маршрутов");
            }
            else
            {
                AddPresetToVpn(p);
                Log.Write("VPN: набор «" + p.Name + "» теперь идёт через VPN");
            }
            await ApplyVpnChanges();
        }

        void AddPresetToVpn(SitePreset p)
        {
            foreach (string d in p.Domains)
                if (!cfg.VpnDomains.Contains(d, StringComparer.OrdinalIgnoreCase)) cfg.VpnDomains.Add(d);
            foreach (string app in p.Apps)
                if (!cfg.VpnApps.Contains(app, StringComparer.OrdinalIgnoreCase)) cfg.VpnApps.Add(app);
        }

        // ---------- Журнал соединений ----------
        bool vpnConnShown;

        async Task RefreshConnections()
        {
            StackPanel list = E<StackPanel>("VpnConnList");
            if (!vpnConnShown) { list.Children.Clear(); return; }
            if (!vpnRunning)
            {
                list.Children.Clear();
                TextBlock off = new TextBlock { Text = T("VPN выключен — соединений нет"), FontSize = 12, Margin = new Thickness(2, 0, 0, 6) };
                off.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                list.Children.Add(off);
                return;
            }
            if (AwgDirect)
            {
                list.Children.Clear();
                Text("VpnConnInfo").Text = " ";
                TextBlock awg = new TextBlock { Text = T("С сервером AmneziaWG в режиме «Весь трафик» журнала соединений нет — он есть в режимах «Только выбранное» и «Россия напрямую»"), FontSize = 12, Margin = new Thickness(2, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
                awg.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                list.Children.Add(awg);
                return;
            }
            List<Vpn.Connection> conns = await Vpn.Connections();
            RenderConnections(conns);
        }

        // Журнал по программам: значок, название, сколько соединений и трафика, под ней — её сайты (сначала свежие)
        void RenderConnections(List<Vpn.Connection> conns)
        {
            StackPanel list = E<StackPanel>("VpnConnList");
            list.Children.Clear();
            Text("VpnConnInfo").Text = T("Сейчас соединений") + ": " + conns.Count + " · " + T("через VPN") + ": " + conns.Count(c => c.ViaVpn) + " · " + T("напрямую") + ": " + conns.Count(c => !c.ViaVpn);
            var byApp = conns.Where(c => !string.IsNullOrEmpty(c.Host))
                .GroupBy(c => (c.Process ?? "").ToLowerInvariant())
                .Select(g => new
                {
                    Name = g.Select(c => c.Process).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "",
                    Path = g.Select(c => c.ProcessPath).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
                    Bytes = g.Sum(c => c.Bytes), Vpn = g.Count(c => c.ViaVpn), Count = g.Count(),
                    Sites = g.GroupBy(c => c.Host).Select(s => new { Host = s.Key, ViaVpn = s.Any(c => c.ViaVpn), Bytes = s.Sum(c => c.Bytes), Start = s.Max(c => c.Start), Count = s.Count() })
                             .OrderByDescending(s => s.Start).ToList(),
                })
                .OrderByDescending(a => a.Bytes).Take(15).ToList();
            foreach (var a in byApp)
            {
                StackPanel body = new StackPanel();
                Grid head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition());
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.Children.Add(AppIconElement(a.Path));
                StackPanel title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                title.Children.Add(new TextBlock { Text = AppTitle(a.Name, a.Path), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                TextBlock sub = new TextBlock { FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                                                Text = T("соединений") + ": " + a.Count + (a.Vpn == a.Count ? " · " + T("все через VPN") : a.Vpn > 0 ? " · " + T("через VPN") + ": " + a.Vpn : " · " + T("напрямую")) };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                title.Children.Add(sub);
                Grid.SetColumn(title, 1);
                head.Children.Add(title);
                TextBlock total = new TextBlock { Text = Formats.Bytes(a.Bytes), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                total.SetResourceReference(TextBlock.ForegroundProperty, a.Vpn > 0 ? "AccentHover" : "TextSub");
                Grid.SetColumn(total, 2);
                head.Children.Add(total);
                body.Children.Add(head);

                const int shown = 4;
                foreach (var s in a.Sites.Take(shown))
                {
                    Grid row = new Grid { Margin = new Thickness(32, 5, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Ellipse dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                    dot.SetResourceReference(Shape.FillProperty, s.ViaVpn ? "Accent" : "Idle");
                    row.Children.Add(dot);
                    TextBlock host = new TextBlock { Text = s.Host + (s.Count > 1 ? " ×" + s.Count : ""), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(host, 1);
                    row.Children.Add(host);
                    TextBlock via = new TextBlock { Text = T(s.ViaVpn ? "VPN" : "напрямую") + " · " + Formats.Bytes(s.Bytes), FontSize = 10.5, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    via.SetResourceReference(TextBlock.ForegroundProperty, s.ViaVpn ? "AccentHover" : "Faint");
                    Grid.SetColumn(via, 2);
                    row.Children.Add(via);
                    body.Children.Add(row);
                }
                if (a.Sites.Count > shown)
                {
                    TextBlock more = new TextBlock { Text = T("и ещё сайтов") + ": " + (a.Sites.Count - shown), FontSize = 11, Margin = new Thickness(32, 5, 0, 0) };
                    more.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
                    body.Children.Add(more);
                }
                Border card = new Border { Child = body, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 6) };
                card.SetResourceReference(Border.BackgroundProperty, "Card");
                list.Children.Add(card);
            }
        }

        readonly Dictionary<string, ImageSource> appIcons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> appTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Значок программы из её exe; нет пути или не читается — общий значок приложения
        FrameworkElement AppIconElement(string path)
        {
            ImageSource icon = null;
            if (!string.IsNullOrEmpty(path) && !appIcons.TryGetValue(path, out icon))
            {
                try
                {
                    using (System.Drawing.Icon ico = System.Drawing.Icon.ExtractAssociatedIcon(path))
                    {
                        icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        icon.Freeze();
                    }
                }
                catch { icon = null; }
                appIcons[path] = icon;
            }
            if (icon != null) return new Image { Source = icon, Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            Border b = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            b.SetResourceReference(Border.BackgroundProperty, "IconBg");
            TextBlock g = new TextBlock { Text = "", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            g.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
            g.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            b.Child = g;
            return b;
        }

        // Понятное название программы: описание из exe («Telegram Desktop»), иначе имя файла без .exe
        string AppTitle(string name, string path)
        {
            if (name.Length == 0) return T("Система и службы");
            string title;
            if (path != null && appTitles.TryGetValue(path, out title)) return title;
            title = System.IO.Path.GetFileNameWithoutExtension(name);
            try
            {
                string desc = path != null ? FileVersionInfo.GetVersionInfo(path).FileDescription : null;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40) title = desc.Trim();
            }
            catch { }
            if (path != null) appTitles[path] = title;
            return title;
        }

        List<string> UncoveredBlockedSites()
        {
            return cfg.BlockedSites.Where(s => Routes.BlockedPreset(s) != null && !SitePresets.IsOn(Routes.BlockedPreset(s), cfg.VpnDomains)).ToList();
        }

        async Task AddBlockedToVpn()
        {
            foreach (string site in UncoveredBlockedSites())
            {
                AddPresetToVpn(Routes.BlockedPreset(site));
                Log.Write(site + " теперь идёт через VPN");
            }
            await ApplyVpnChanges();
        }

        // Результат последней проверки сервера: мс; -1 — не работает; Vpn.PingUdp — проверить нельзя; null — ещё не проверяли
        int? PingOf(VpnServer s) { return ServerList.Ping(vpnPing, s); }
        double SpeedOf(VpnServer s) { double v; return vpnSpeed.TryGetValue(s.Link, out v) ? v : 0; }

        void RefreshVpnUi()
        {
            if (exiting) return;
            bool on = vpnRunning;
            VpnServer server = CurrentServer;
            bool ready = on && !VpnSwitching;
            Text("VpnConnectedServer").Text = server == null ? T("Сервер не добавлен") : Vpn.DisplayName(server.Name);
            Text("VpnSub").Text = server == null ? T("Добавьте сервер ниже") : T(VpnInfo.ModeName(cfg.VpnMode));
            Text("VpnServerProtection").Text = VpnProtectionStatus();
            RefreshVpnHealthUi();
            Fade(E<UIElement>("VpnCardOn"), ready ? 1 : 0, TimeSpan.FromMilliseconds(180));
            Button connect = E<Button>("BtnVpnToggle");
            connect.Content = T(on || vpnLocked ? "Отключить" : "Подключить");
            connect.IsEnabled = server != null && !busy;
            E<Border>("VpnFlagHost").Child = FlagElement(server == null ? null : Vpn.CountryCode(server.Name), 54, 36);            E<FrameworkElement>("VpnHelp").Visibility = !cfg.VpnHelpDone ? Visibility.Visible : Visibility.Collapsed;
            UpdateVpnTimer();
            filling = true;
            E<ToggleButton>("ChipVpn").IsChecked = ready;
            E<RadioButton>(cfg.VpnMode == "all" ? "VpnModeAll" : cfg.VpnMode == "ru" ? "VpnModeRu" : "VpnModeSplit").IsChecked = true;
            E<CheckBox>("SwVpnAuto").IsChecked = cfg.VpnAutoBest;
            E<CheckBox>("SwVpnKill").IsChecked = cfg.VpnKillSwitch;
            filling = false;

            RefreshVpnKillScope();
            E<Button>("BtnVpnPing").IsEnabled = cfg.VpnServers.Count > 0 && !vpnPinging;
            E<Button>("BtnVpnBest").IsEnabled = cfg.VpnServers.Count > 1 && !vpnPinging;
            E<Button>("BtnVpnSpeed").IsEnabled = cfg.VpnServers.Any(s => !Awg.IsAwg(s.Link)) && !vpnPinging && !vpnSpeedTesting;
            trayState = null;   // значок трея — с меткой VPN или без
            Text("VpnModeText").Text = T(cfg.VpnMode == "all"
                ? "Весь интернет идёт через сервер VPN. Обход zapret в это время не нужен."
                : cfg.VpnMode == "ru"
                ? "Российские сайты и адреса — напрямую, всё остальное — через VPN. Банки и Госуслуги работают как обычно. Ниже можно добавить сайты и программы, которым всегда нужен VPN."
                : "Через VPN — только сайты и программы из списка ниже. Discord, YouTube и остальное идут напрямую и через обход, без потери скорости.");
            E<FrameworkElement>("VpnSplitPanel").Visibility = cfg.VpnMode == "all" ? Visibility.Collapsed : Visibility.Visible;
            // в режимах «Россия напрямую» и «Весь трафик» такие сайты и так идут через VPN
            List<string> uncovered = cfg.VpnMode != "split" ? new List<string>() : UncoveredBlockedSites();
            E<FrameworkElement>("VpnBlockedBar").Visibility = uncovered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (uncovered.Count > 0)
                Text("VpnBlockedText").Text = T("Провайдер блокирует по адресу") + ": " + string.Join(", ", uncovered.Select(T)) + ". " + T("zapret тут не поможет — пустите через VPN");
            Text("VpnListHint").Text = T(VpnSitesKind ? "Например: example.com" : "Например: Spotify.exe");
            if (miVpn != null)
            {
                miVpn.Text = T(on ? "Выключить VPN" : "Включить VPN");
                miVpn.Enabled = server != null;
                miVpnServer.Visible = cfg.VpnServers.Count > 1;   // один сервер — выбирать не из чего
                miVpnMode.Visible = server != null;
            }
            RenderSubscription();

            // серверы по странам: сверху вкладки (флаг, страна, сколько серверов), ниже — серверы выбранной страны.
            // При поиске вкладки не мешают: показываются совпадения по всем странам
            E<FrameworkElement>("VpnSearchBox").Visibility = cfg.VpnServers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            string query = E<TextBox>("VpnSearch").Text.Trim();
            StackPanel servers = E<StackPanel>("VpnServerList");
            servers.Children.Clear();
            // после проверки: неработающие можно скрыть (выбранный сервер виден всегда)
            int dead = cfg.VpnServers.Count(s => PingOf(s) == -1);
            ToggleButton hideDead = E<ToggleButton>("VpnHideDead");
            hideDead.Visibility = dead > 0 ? Visibility.Visible : Visibility.Collapsed;
            hideDead.Content = T("Скрыть неработающие") + " (" + dead + ")";
            filling = true;
            hideDead.IsChecked = cfg.VpnHideDead;
            filling = false;
            HashSet<int> visible = new HashSet<int>(ServerList.Visible(cfg.VpnServers, vpnPing, cfg.VpnSelected, cfg.VpnHideDead));
            var all = cfg.VpnServers.Select((s, i) => new { s, i, code = Vpn.CountryCode(s.Name) }).Where(x => visible.Contains(x.i) && (vpnSource == null || (x.s.SubscriptionId ?? "") == vpnSource)
                    && (!vpnFavoritesOnly || x.s.Favorite)).ToList();
            var groups = all.GroupBy(x => x.code ?? "").ToList();
            WrapPanel tabs = E<WrapPanel>("VpnTabs");
            tabs.Children.Clear();
            bool useTabs = groups.Count > 1 && query.Length == 0;
            tabs.Visibility = useTabs ? Visibility.Visible : Visibility.Collapsed;
            if (useTabs)
            {
                // открытая вкладка: выбранная вручную, иначе страна текущего сервера
                if (vpnTab == null || !groups.Any(g => g.Key == vpnTab))
                    vpnTab = (groups.FirstOrDefault(g => g.Any(x => x.i == cfg.VpnSelected)) ?? groups[0]).Key;
                foreach (var g in groups)
                    tabs.Children.Add(CountryTab(g.Key, g.Count(), g.Key == vpnTab, g.Any(x => x.i == cfg.VpnSelected)));
            }
            var shown = all.Where(x => query.Length > 0
                    ? x.s.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                      || (Vpn.CountryName(x.code) ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                      || T(Vpn.CountryName(x.code) ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    : !useTabs || (x.code ?? "") == vpnTab)
                .OrderByDescending(x => x.s.Favorite).ThenBy(x => ServerList.Rank(PingOf(x.s))).ThenBy(x => x.i).ToList();   // работающие — наверх, быстрые первыми
            foreach (var x in shown) servers.Children.Add(ServerRow(x.s, x.i, x.code));
            if (shown.Count == 0)
                servers.Children.Add(cfg.VpnServers.Count == 0
                    ? EmptyState("", "Серверы не добавлены", "Добавьте подписку или ключ VPN", true)
                    : EmptyState("", "Ничего не найдено", "Проверьте поиск и выбранные фильтры", true));

            FillPresets(E<StackPanel>("VpnPresets"), cfg.VpnDomains, ToggleVpnPreset);

            RenderVpnList();
        }

        // Сайты или программы через VPN: наборы — карточкой, остальное — по строке; при длинном списке — поиск
        void RenderVpnList()
        {
            StackPanel items = E<StackPanel>("VpnList");
            List<string> list = VpnSitesKind ? cfg.VpnDomains : cfg.VpnApps;
            E<FrameworkElement>("VpnListSearchBox").Visibility = list.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
            string query = list.Count > 8 ? E<TextBox>("VpnListSearch").Text.Trim() : "";
            FillItemList(items, list, query, true, async value => { list.Remove(value); await ApplyVpnChanges(); },
                         VpnSitesKind ? (Func<SitePreset, Task>)ToggleVpnPreset : null);
            if (list.Count == 0)
                items.Children.Add(VpnSitesKind ? EmptyState("", "Список пуст", "Добавьте сайты, которым нужен VPN", true)
                                                : EmptyState("", "Список пуст", "Добавьте программы, которым нужен VPN", true));
        }
    }
}
