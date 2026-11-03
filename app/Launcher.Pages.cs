// Страницы и настройки: переключение страниц, список стратегий, обновление интерфейса, переключатели
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
    public class StrategyItem
    {
        public string File { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Badge { get; set; }
        public string Color { get; set; }
        public string Group { get; set; }
        public string StarGlyph { get; set; }
        public Brush StarColor { get; set; }
        public string StarTip { get; set; }
        public Brush MarkColor { get; set; }
        public Visibility MarkVisibility { get; set; }
        public string Tip { get; set; }          // результаты последнего подбора по сайтам (подсказка при наведении)
        public bool GroupOpen { get; set; }      // группа этой стратегии развёрнута
    }

    partial class Launcher
    {
        static readonly string[] SettingsGroups = { "main", "bypass", "net", "look", "launcher" };
        string settingsGroup = "main";

        void ShowSettingsGroup(string group)
        {
            double from = Array.IndexOf(SettingsGroups, group) >= Array.IndexOf(SettingsGroups, settingsGroup) ? 24 : -24;
            bool changed = group != settingsGroup;
            settingsGroup = group;
            int n = 0;
            foreach (UIElement child in E<StackPanel>("SettingsRoot").Children)
            {
                FrameworkElement fe = child as FrameworkElement;
                if (fe == null || !(fe.Tag is string)) continue;
                bool show = (string)fe.Tag == group;
                fe.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show && changed) SlideIn(fe, from, 50 * n++);   // разделы вкладки въезжают по очереди
            }
            E<ScrollViewer>("PageSettings").ScrollToTop();
        }

        int pageIndex;
        string vpnPane = "Servers";

        void ShowPage(string name)
        {
            // подсветка в боковом меню переезжает к выбранному пункту
            RadioButton nav = E<RadioButton>("Nav" + name);
            if (nav != null && nav.IsLoaded)
            {
                double y = nav.TranslatePoint(new Point(0, 0), E<Grid>("Sidebar")).Y - 11;
                TranslateTransform pill = E<Border>("NavPill").RenderTransform as TranslateTransform;
                if (pill == null || pill.IsFrozen) { pill = new TranslateTransform(); E<Border>("NavPill").RenderTransform = pill; }
                pill.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
            }
            string[] pages = { "Strategies", "Vpn", "Domains", "Settings", "Log" };   // в порядке бокового меню
            int index = Array.IndexOf(pages, name);
            // страница въезжает с той стороны, куда двинулись по меню: ниже — снизу, выше — сверху
            double from = index >= pageIndex ? 14 : -14;
            pageIndex = index;
            foreach (string p in pages)
            {
                FrameworkElement page = E<FrameworkElement>("Page" + p);
                page.Visibility = p == name ? Visibility.Visible : Visibility.Collapsed;
                if (p != name) continue;
                TranslateTransform tr = new TranslateTransform(0, from);
                page.RenderTransform = tr;
                Duration d = new Duration(TimeSpan.FromMilliseconds(260));
                IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
                tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, d) { EasingFunction = ease });
            }
            if (name == "Domains") UpdateDomainUi();
            if (name == "Vpn")
            {
                CheckClipboard();
                RefreshVpnUi();
                if (vpnPing.Count == 0 && cfg.VpnServers.Count > 0) { Task t = PingVpnServers(); }   // пинг — один раз при первом открытии
            }
            if (name == "Settings") UpdateFilterUi();
        }

        // ---------- Интерфейс ----------
        void UpdateTitle()
        {
            string v = Zapret.LocalVersion;
            Text("TitleText").Text = Consts.Version + (v != null ? "  ·  zapret " + v : "");
            Text("LauncherVersionText").Text = "Zapret Launcher " + Consts.Version + " — " + T("проверить обновление");
            Text("LauncherRepoText").Text = T("Обновления лаунчера") + ": github.com/" + cfg.LauncherRepo;
            string prev = SelfUpdate.PreviousLocal();
            Text("LauncherRollbackTitle").Text = prev != null ? T("Вернуть лаунчер версии") + " " + prev : T("Вернуть прошлую версию лаунчера");
        }

        static readonly string[] GroupOrder = { "Избранное", "Основная", "ALT", "FAKE TLS", "SIMPLE FAKE", "Экспериментальные", "Другие" };

        static string GroupOf(string file)
        {
            string t = Zapret.Title(file);
            if (t == "Стандартная") return "Основная";
            if (t.StartsWith("ALT")) return "ALT";
            if (t.StartsWith("FAKE TLS")) return "FAKE TLS";
            if (t.StartsWith("SIMPLE FAKE")) return "SIMPLE FAKE";
            if (t.StartsWith("EXP")) return "Экспериментальные";
            return "Другие";
        }

        void UpdateStrategyList()
        {
            filling = true;
            string query = E<TextBox>("SearchBox").Text.Trim();
            List<StrategyItem> items = Zapret.Strategies().Select(ToItem)
                .Where(i => query.Length == 0 || i.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || i.File.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            List<StrategyItem> ordered = items.Select((i, n) => new { i, n })
                .OrderBy(x => Array.IndexOf(GroupOrder, cfg.Favorites.Contains(x.i.File) ? "Избранное" : GroupOf(x.i.File))).ThenBy(x => x.n)
                .Select(x => x.i).ToList();
            // при поиске показываем все группы, иначе свёрнутые остаются свёрнутыми
            foreach (StrategyItem i in ordered) i.GroupOpen = query.Length > 0 || !cfg.CollapsedGroups.Contains(i.Group);
            ListCollectionView view = new ListCollectionView(ordered);
            view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            ListBox list = E<ListBox>("StrategyList");
            list.ItemsSource = view;
            list.SelectedItem = ordered.FirstOrDefault(i => i.File == cfg.Strategy);
            filling = false;
            Text("CurrentBadge").Text = Zapret.Badge(cfg.Strategy);
            E<Border>("CurrentBadgeBg").Background = B(Zapret.Color(cfg.Strategy));
            Text("CurrentName").Text = T(Zapret.Title(cfg.Strategy));
        }

        StrategyItem ToItem(string file)
        {
            bool fav = cfg.Favorites.Contains(file);
            StrategyItem i = new StrategyItem();
            i.File = file; i.Title = T(Zapret.Title(file)); i.Badge = Zapret.Badge(file); i.Color = Zapret.Color(file);
            i.Group = T(fav ? "★ Избранное" : GroupOf(file));
            i.StarGlyph = fav ? "\uE735" : "\uE734";
            i.StarColor = fav ? R("Wait") : R("Faint");
            i.StarTip = T(fav ? "Убрать из избранного" : "В избранное");
            // метка результата последнего подбора: зелёная — открылось всё, жёлтая — часть, красная — ничего
            string score;
            i.MarkVisibility = Visibility.Collapsed;
            i.MarkColor = R("Idle");
            i.Subtitle = file;
            if (cfg.PickScores.TryGetValue(file, out score))
            {
                string[] p = score.Split('/');
                int ok, total, avg;
                if (p.Length == 3 && int.TryParse(p[0], out ok) && int.TryParse(p[1], out total) && int.TryParse(p[2], out avg))
                {
                    i.MarkVisibility = Visibility.Visible;
                    i.MarkColor = total == 0 || ok == 0 ? R("Fail") : ok == total ? R("Ok") : R("Wait");
                    i.Subtitle = total == 0 ? file + "  ·  " + T("не запустилась") : file + "  ·  " + ok + "/" + total + (ok > 0 ? " · " + avg + " " + T("мс") : "");
                }
                i.Tip = PickTip(file, score.StartsWith("0/0/"));
            }
            return i;
        }

        // Подсказка к стратегии: что открылось на последнем подборе и за сколько
        string PickTip(string file, bool failedToStart)
        {
            string head = T("Последний подбор") + (cfg.PickDate.Length > 0 ? " · " + cfg.PickDate : "");
            if (failedToStart) return head + "\n" + T("Стратегия не запустилась");
            string details;
            if (!cfg.PickDetails.TryGetValue(file, out details)) return null;
            List<string> lines = new List<string> { head };
            foreach (string part in details.Split(';'))
            {
                string[] kv = part.Split('=');
                int ms;
                if (kv.Length != 2 || !int.TryParse(kv[1], out ms)) continue;
                lines.Add((ms >= 0 ? "✓  " : "✗  ") + T(kv[0] == "Видео" ? "Видео YouTube" : kv[0]) + " — " + (ms >= 0 ? ms + " " + T("мс") : T("не открылся")));
            }
            return string.Join("\n", lines);
        }

        void ToggleFavorite(string file)
        {
            if (!cfg.Favorites.Remove(file)) cfg.Favorites.Add(file);
            cfg.Save();
            UpdateStrategyList();
            UpdateTaskbarMenu();
        }

        void UpdateUi()
        {
            if (exiting) return;
            Process proc = Zapret.Winws().FirstOrDefault();
            bool running = proc != null;
            string local = Zapret.LocalVersion;
            bool hasUpdate = latest != null && Util.TestNewer(latest.Version, local);

            if (!busy)
            {
                if (running)
                {
                    Text("PowerStatus").Text = T(Zapret.ServiceRunning ? "Работает служба zapret" : "Обход работает");
                    string timer = "";
                    try { TimeSpan t = DateTime.Now - proc.StartTime; timer = ((int)t.TotalHours).ToString("00") + ":" + t.ToString(@"mm\:ss"); } catch { }
                    Text("PowerTimer").Text = T("Работает") + " " + timer + "  ·  " + T(Zapret.Title(cfg.Strategy));
                    Text("PowerHint").Text = T("Нажмите, чтобы выключить");
                    Text("PowerHint").Visibility = Visibility.Collapsed;   // переключатель и так виден — подсказка лишняя
                }
                else if (pausedByVpn)
                {
                    Text("PowerStatus").Text = T("Пауза: включён VPN");
                    Text("PowerTimer").Text = vpnName ?? " ";
                    Text("PowerHint").Text = T("Обход вернётся, когда VPN отключат");
                    Text("PowerHint").Visibility = Visibility.Visible;
                }
                else
                {
                    Text("PowerStatus").Text = T(local != null ? "Обход выключен" : "zapret не установлен");
                    Text("PowerTimer").Text = " ";
                    Text("PowerHint").Text = T("Нажмите, чтобы включить");
                    Text("PowerHint").Visibility = Visibility.Collapsed;
                }
            }
            SetActive(running);
            UpdateCardState(TrayState(running));
            if (running != trayActive)
            {
                trayActive = running;
                BuildSitePanel();
            }

            Text("VersionPill").Text = local != null ? "zapret " + local : T("zapret не установлен");
            TextBlock lt = Text("LatestText");
            lt.Text = hasUpdate ? T("доступна") + " " + latest.Version : latest != null ? T("последняя версия") : "";
            lt.SetResourceReference(TextBlock.ForegroundProperty, hasUpdate ? "AccentHover" : "Muted");
            Text("CardSubtitle").Text = "· " + (lastCheck.HasValue ? T("проверено") + " " + lastCheck.Value.ToString("dd.MM HH:mm") : T("не проверялось")) +
                (cfg.AutoUpdate ? " · " + T("автообновление") : "");
            Text("CardSubtitle").ToolTip = Text("CardSubtitle").Text.TrimStart('·', ' ');
            E<Button>("BtnInstallUpdate").Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
            if (!picking) E<Button>("BtnMain").Content = T("Подобрать стратегию");
            E<Button>("BtnRemoveService").Visibility = Zapret.Service() != null ? Visibility.Visible : Visibility.Collapsed;

            string old = Zapret.VersionOf(Paths.Backup);
            E<Button>("BtnRollback").Visibility = old != null && old != local ? Visibility.Visible : Visibility.Collapsed;
            Text("RollbackTitle").Text = Util.TestNewer(old, local) ? T("Вернуть zapret версии") + " " + old + " " + T("(отменить откат)") : T("Откатить zapret на версию") + " " + old;
            string tgv = Telegram.ProxyVersion;
            Text("TgSub").Text = T("Desktop — прокси tg-ws-proxy") + (tgv != null ? " v" + tgv : "") + T(", Web — через hosts");
            if (launcherLatest != null && Util.TestNewer(launcherLatest.Version, Consts.Version))
                Text("LauncherVersionText").Text = "Zapret Launcher " + Consts.Version + " — " + T("доступна") + " " + launcherLatest.Version + ", " + T("обновить");
            UpdateNetStatus();
            UpdateFlyout();
            UpdateTray();
        }

        void InitSwitches()
        {
            filling = true;   // автозапуск выставит Startup — запрос к Планировщику задач долгий
            E<CheckBox>("SwAutoUpdate").IsChecked = cfg.AutoUpdate;
            E<ToggleButton>("ChipAutoUpdate").IsChecked = cfg.AutoUpdate;
            E<CheckBox>("SwStartOnOpen").IsChecked = cfg.StartOnOpen;
            E<CheckBox>("SwWatchdog").IsChecked = cfg.Watchdog;
            E<CheckBox>("SwVpnPause").IsChecked = cfg.VpnPause;
            E<CheckBox>("SwNetProfiles").IsChecked = cfg.NetworkProfilesEnabled;
            E<CheckBox>("SwToasts").IsChecked = cfg.Toasts;
            E<CheckBox>("SwTelegram").IsChecked = cfg.Telegram;
            E<ToggleButton>("ChipTelegram").IsChecked = cfg.Telegram;
            E<RadioButton>(I18n.English ? "LangEn" : "LangRu").IsChecked = true;
            E<RadioButton>(cfg.Theme == "light" ? "ThemeLight" : cfg.Theme == "system" ? "ThemeSystem" : "ThemeDark").IsChecked = true;
            string accentKey = Theme.Accents.ContainsKey(cfg.Accent) || cfg.Accent == "system" ? cfg.Accent : "teal";
            E<RadioButton>("Accent" + char.ToUpper(accentKey[0]) + accentKey.Substring(1)).IsChecked = true;
            E<RadioButton>("Scale" + cfg.Scale).IsChecked = true;
            E<CheckBox>("SwSecureDns").IsChecked = cfg.SecureDns.Length > 0;
            string dnsKey = SecureDns.Providers.ContainsKey(cfg.SecureDns) ? cfg.SecureDns : "cloudflare";
            E<RadioButton>("Dns" + char.ToUpper(dnsKey[0]) + dnsKey.Substring(1)).IsChecked = true;
            if (!SecureDns.EncryptionSupported) Text("DnsSub").Text = T("В Windows 10 шифрования DNS нет — будет только публичный DNS вместо провайдерского");
            CheckBox mica = E<CheckBox>("SwMica");
            mica.IsChecked = cfg.Transparent && Theme.MicaSupported;
            mica.IsEnabled = Theme.MicaSupported;
            if (!Theme.MicaSupported) Text("MicaSub").Text = T("Доступно в Windows 11 22H2 и новее");
            filling = false;
        }

        async Task SetAutostart(bool on)
        {
            await RunBusy(() =>
            {
                Autostart.Set(on);
                Log.Write(on ? "Автозапуск с Windows включён" : "Автозапуск с Windows выключен");
                return Task.FromResult(0);
            });
            bool actual = Autostart.Enabled;
            E<CheckBox>("SwAutostart").IsChecked = actual;
            E<ToggleButton>("ChipAutostart").IsChecked = actual;
        }

        void SetAutoUpdate(bool on)
        {
            cfg.AutoUpdate = on;
            cfg.Save();
            E<CheckBox>("SwAutoUpdate").IsChecked = on;
            E<ToggleButton>("ChipAutoUpdate").IsChecked = on;
            UpdateUi();
        }

        async Task SetTelegram(bool on)
        {
            cfg.Telegram = on;
            cfg.Save();
            E<CheckBox>("SwTelegram").IsChecked = on;
            E<ToggleButton>("ChipTelegram").IsChecked = on;
            BuildSitePanel();
            // перезапуск обхода нужен, чтобы winws перечитал список доменов
            await RunBusy(async () =>
            {
                if (on)
                {
                    Telegram.SetWeb(true);
                    if (Zapret.Running) await StartBypass(false); else Log.Write("Telegram заработает вместе с обходом");
                }
                else
                {
                    await Telegram.StopProxy();
                    Telegram.SetWeb(false);
                    if (Zapret.Running) await StartBypass(false);
                }
            }, on ? "Telegram…" : null);
        }

        void SetLanguage(string lang)
        {
            if (filling || cfg.Language == lang) return;
            cfg.Language = lang;
            cfg.Save();
            Restart();
        }

        void SetTheme(string theme)
        {
            if (cfg.Theme == theme) return;
            cfg.Theme = theme;
            cfg.Save();
            ApplyTheme();
        }
    }
}
