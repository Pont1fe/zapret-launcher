// Трей, мини-окно у трея и значок на панели задач
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
        void UpdateTaskbarMenu()
        {
            try { TaskbarMenu.Update(cfg.Favorites.Where(f => File.Exists(Paths.InZapret(f)))); } catch { }
        }

        // ---------- Мини-окно у трея ----------
        void ToggleFlyout()
        {
            if (flyout != null && flyout.IsVisible) { HideFlyout(); return; }
            if ((DateTime.Now - flyoutHiddenAt).TotalMilliseconds < 300) return;   // клик по значку сам закрыл окно — не открываем снова
            if (flyout == null) CreateFlyout();
            flyout.Show();
            UpdateFlyout();
            flyout.UpdateLayout();
            Rect area = SystemParameters.WorkArea;
            flyout.Left = area.Right - flyout.ActualWidth;
            flyout.Top = area.Bottom - flyout.ActualHeight;
            flyout.Activate();
            FrameworkElement card = F<FrameworkElement>("FlyCard");
            TranslateTransform tr = new TranslateTransform(0, 14);
            card.RenderTransform = tr;
            Duration d = new Duration(TimeSpan.FromMilliseconds(200));
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d));
            tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        void HideFlyout()
        {
            if (flyout == null || !flyout.IsVisible) return;
            flyout.Hide();
            flyoutHiddenAt = DateTime.Now;
        }

        void CreateFlyout()
        {
            flyout = LoadWindow("flyout.xaml");
            // в trayState может быть метка VPN («on-vpn»), а картинки значка — только по состоянию обхода
            F<Image>("FlyIcon").Source = imageIcons[(trayState ?? "off").Replace("-vpn", "")];
            ApplyScale();
            if (I18n.English) F<Button>("FlyPing").Content = "Refresh";
            cardState = null;   // перекрасить мини-окно по состоянию
            F<Button>("FlyDiscord").Click += async delegate { HideFlyout(); await FixDiscordUi(); };
            F<Grid>("FlyVpn").MouseLeftButtonUp += async delegate { await ToggleVpn(); UpdateFlyout(); };
            flyout.Deactivated += delegate { HideFlyout(); };
            flyout.KeyDown += (s, e) => { if (e.Key == Key.Escape) HideFlyout(); };
            F<Grid>("FlyToggle").MouseLeftButtonUp += async delegate { await ToggleBypass(); };
            F<Button>("FlyOpen").Click += delegate { ShowWindow(); };
            F<Button>("FlyPing").Click += async delegate { await PingNow(); };
            F<Button>("FlyPick").Click += async delegate { ShowWindow(); await StartAutoPickUi(false); };
        }

        void UpdateFlyout()
        {
            if (flyout == null || !flyout.IsVisible) return;
            bool running = Zapret.Running;
            F<TextBlock>("FlyStatus").Text = Text("PowerStatus").Text;
            F<TextBlock>("FlyStrategy").Text = T("Стратегия") + ": " + T(Zapret.Title(cfg.Strategy));
            TimeSpan d = TimeSpan.FromMilliseconds(220);
            Fade(F<UIElement>("FlyActive"), running ? 1 : 0, d);
            Fade(F<UIElement>("FlyTrackOn"), running ? 1 : 0, d);
            Slide(F<Ellipse>("FlyKnob"), running ? 22 : 0, d);
            // переключатель VPN
            VpnServer vs = CurrentServer;
            F<FrameworkElement>("FlyVpn").Visibility = vs != null ? Visibility.Visible : Visibility.Collapsed;
            if (vs != null)
            {
                F<TextBlock>("FlyVpnText").Text = vpnRunning ? "VPN · " + Vpn.DisplayName(vs.Name) : cfg.VpnOn ? T("VPN подключается…") : T("VPN выключен");
                Fade(F<UIElement>("FlyVpnTrackOn"), vpnRunning ? 1 : 0, d);
                Slide(F<Ellipse>("FlyVpnKnob"), vpnRunning ? 18 : 0, d);
            }
            UniformGrid sites = F<UniformGrid>("FlySites");
            sites.Children.Clear();
            foreach (string site in VisibleSites())
            {
                string color, https, tcp;
                SiteState(site, out color, out https, out tcp);
                StackPanel p = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
                StackPanel head = new StackPanel { Orientation = Orientation.Horizontal };
                Ellipse dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                dot.SetResourceReference(Shape.FillProperty, color);
                head.Children.Add(dot);
                head.Children.Add(new TextBlock { Text = T(site), FontSize = 12, FontWeight = FontWeights.SemiBold });
                p.Children.Add(head);
                TextBlock ms = new TextBlock { Text = color == "Fail" ? T("недоступен") : https.Replace("HTTPS ", ""), FontSize = 11, Margin = new Thickness(13, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                ms.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                p.Children.Add(ms);
                Border b = new Border { Child = p, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 6, 8), Margin = new Thickness(0, 0, 6, 0) };
                b.SetResourceReference(Border.BackgroundProperty, "Card");
                sites.Children.Add(b);
            }
        }

        // Состояние для значка в трее: on — работает, pause — пауза из-за VPN, error — упал или сайты не открываются, off — выключен
        string TrayState(bool running)
        {
            if (pausedByVpn && !running) return "pause";
            if (running)
            {
                bool allFail = !picking && siteResults != null && siteResults.Count > 0 && siteResults.All(r => !r.Ok);
                return allFail ? "error" : "on";
            }
            return crashed || (wantRunning && !busy) ? "error" : "off";
        }

        // Дешёвая часть обновления — значок, подсказка и меню трея; в трее только она и работает
        void UpdateTray()
        {
            if (exiting) return;
            bool running = Zapret.Running;
            string state = TrayState(running);
            string iconKey = state + (vpnRunning ? "-vpn" : "");
            if (iconKey != trayState)
            {
                trayState = iconKey;
                tray.Icon = trayIcons[iconKey];
                SetTaskbarBadge(state, vpnRunning);
                window.Icon = windowIcons[state];
                E<Image>("TitleIcon").Source = imageIcons[state];
                if (flyout != null) F<Image>("FlyIcon").Source = imageIcons[state];
            }
            string local = Zapret.LocalVersion;
            miStart.Enabled = local != null && !busy;
            miStop.Enabled = (running || pausedByVpn) && !busy;
            miPick.Enabled = local != null && (!busy || picking);
            miPick.Text = T(picking ? "Остановить подбор" : "Подобрать стратегию");
            miFixDiscord.Enabled = !busy;
            string trayText = running
                ? "Zapret: " + T(state == "error" ? "сайты не открываются" : "работает") + " · " + T(Zapret.Title(cfg.Strategy))
                : pausedByVpn ? "Zapret: " + T("пауза (VPN)")
                : state == "error" ? "Zapret: " + T("ошибка — обход не работает") : "Zapret: " + T("остановлен");
            if (vpnRunning) trayText += " · VPN";
            tray.Text = trayText.Length > 63 ? trayText.Substring(0, 63) : trayText;
        }

        // Панель задач берёт значок из ярлыка, поэтому состояние показываем меткой в углу:
        // жёлтая — пауза, красная — ошибка, серая — выключен, синяя — работает через VPN; без метки — обход работает
        readonly Dictionary<string, ImageSource> badges = new Dictionary<string, ImageSource>();

        void SetTaskbarBadge(string state, bool vpn)
        {
            string key = state == "on" ? (vpn ? "vpn" : null) : state;
            if (window.TaskbarItemInfo == null) window.TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();
            if (key == null) { window.TaskbarItemInfo.Overlay = null; return; }
            ImageSource img;
            if (!badges.TryGetValue(key, out img))
            {
                System.Drawing.Color c = key == "pause" ? System.Drawing.Color.FromArgb(240, 176, 48) : key == "error" ? System.Drawing.Color.FromArgb(232, 72, 72)
                                       : key == "vpn" ? System.Drawing.Color.FromArgb(59, 130, 246) : System.Drawing.Color.FromArgb(128, 130, 140);
                badges[key] = img = Icons.ToImageSource(Icons.Badge(c));
            }
            window.TaskbarItemInfo.Overlay = img;
            window.TaskbarItemInfo.Description = tray.Text;
        }

        void BuildTray()
        {
            BuildTrayIcons();
            // сменили масштаб экрана — перерисовываем значки под новый размер
            SystemEvents.DisplaySettingsChanged += delegate
            {
                window.Dispatcher.BeginInvoke(new Action(() => { BuildTrayIcons(); trayState = null; UpdateTray(); }));
            };
            tray = new Forms.NotifyIcon();
            tray.Icon = trayIcons["off"];
            trayState = "off";
            tray.Text = "Zapret Launcher";
            tray.Visible = true;
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = System.Drawing.Color.FromArgb(30, 32, 37);
            menu.ShowImageMargin = false;
            menu.Font = new System.Drawing.Font("Segoe UI", 9.5f);
            miOpen = new Forms.ToolStripMenuItem(T("Открыть"));
            miOpen.Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
            miStart = new Forms.ToolStripMenuItem(T("Включить обход"));
            miStop = new Forms.ToolStripMenuItem(T("Выключить обход"));
            miStrategies = new Forms.ToolStripMenuItem(T("Стратегия"));
            miStrategies.DropDown.Renderer = new DarkMenuRenderer();
            miStrategies.DropDown.BackColor = menu.BackColor;
            miPick = new Forms.ToolStripMenuItem(T("Подобрать стратегию"));
            miFixDiscord = new Forms.ToolStripMenuItem(T("Починить Discord"));
            miVpn = new Forms.ToolStripMenuItem(T("Включить VPN"));
            miExit = new Forms.ToolStripMenuItem(T("Выход"));
            menu.Items.AddRange(new Forms.ToolStripItem[] { miOpen, miStart, miStop, miStrategies, miPick, miFixDiscord, miVpn, new Forms.ToolStripSeparator(), miExit });
            tray.ContextMenuStrip = menu;

            miOpen.Click += delegate { ShowWindow(); };
            miStart.Click += async delegate { await StartBypassUi(); };
            miStop.Click += async delegate { await StopBypassUi(); };
            miPick.Click += async delegate { ShowWindow(); await StartAutoPickUi(false); };
            miFixDiscord.Click += async delegate { await FixDiscordUi(); };
            miVpn.Click += async delegate { await ToggleVpn(); };
            miExit.Click += async delegate { await QuitAndStop(); };
            // клик по значку — мини-окно, двойной клик — главное окно
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) window.Dispatcher.BeginInvoke(new Action(ToggleFlyout)); };
            tray.MouseDoubleClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };
            menu.Opening += delegate
            {
                HideFlyout();
                miStrategies.DropDownItems.Clear();
                foreach (string s in Zapret.Strategies())
                {
                    Forms.ToolStripMenuItem item = new Forms.ToolStripMenuItem((cfg.Favorites.Contains(s) ? "★ " : "") + T(Zapret.Title(s)));
                    item.Checked = s == cfg.Strategy;
                    string file = s;
                    item.Click += async delegate { await SelectStrategy(file); };
                    miStrategies.DropDownItems.Add(item);
                }
                UpdateUi();
            };
        }

        void BuildTrayIcons()
        {
            int size = Icons.TraySize();
            trayIcons = new Dictionary<string, System.Drawing.Icon>();
            foreach (string state in new[] { "on", "off", "pause", "error" })
            {
                trayIcons[state] = Icons.ToIcon(Icons.Draw(size, state));
                trayIcons[state + "-vpn"] = Icons.ToIcon(Icons.Draw(size, state, true));   // с синей меткой — включён встроенный VPN
            }
        }
    }
}
