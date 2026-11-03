// Сеть: профили сетей, защищённый DNS, проверка сайта, файл hosts, карточки сайтов
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
        // ---------- Защищённый DNS ----------
        string DnsChoice()
        {
            foreach (string key in SecureDns.Providers.Keys)
                if (E<RadioButton>("Dns" + char.ToUpper(key[0]) + key.Substring(1)).IsChecked == true) return key;
            return "cloudflare";
        }

        async Task SetSecureDns(bool on, string provider)
        {
            await RunBusy(async () =>
            {
                if (cfg.SecureDns.Length > 0) await Task.Run(() => SecureDns.Disable(cfg.DnsAdapters));
                cfg.SecureDns = "";
                cfg.DnsAdapters = new List<string>();
                if (on)
                {
                    List<string> adapters = await Task.Run(() => SecureDns.Enable(provider));
                    if (adapters.Count == 0) throw new Exception("Не найден сетевой адаптер с интернетом");
                    cfg.SecureDns = provider;
                    cfg.DnsAdapters = adapters;
                    cfg.Save();
                    // DNS не отвечает (например, провайдер режет и HTTPS, и обычный DNS к серверу) — возвращаем как было, чтобы не остаться без интернета
                    if (!await SecureDns.Works())
                    {
                        await Task.Run(() => SecureDns.Disable(adapters));
                        cfg.SecureDns = "";
                        cfg.DnsAdapters = new List<string>();
                        cfg.Save();
                        throw new Exception(T("DNS") + " " + SecureDns.Providers[provider].Name + " " + T("не отвечает в вашей сети — вернул автоматический DNS. Попробуйте другой сервер."));
                    }
                    Log.Write("Защищённый DNS включён: " + SecureDns.Providers[provider].Name + " (" + string.Join(", ", adapters) + ")");
                }
                else
                {
                    cfg.Save();
                    Log.Write("Защищённый DNS выключен — DNS снова автоматический");
                }
            }, "DNS…");
            filling = true;
            E<CheckBox>("SwSecureDns").IsChecked = cfg.SecureDns.Length > 0;
            filling = false;
        }

        // ---------- Проверка сайта ----------
        async Task CheckDomain()
        {
            TextBlock info = Text("DomainInfo");
            string d = Zapret.NormalizeDomain(E<TextBox>("DomainInput").Text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
            if (d == null) { info.Text = T("Введите адрес сайта, например example.com"); return; }
            Button btn = E<Button>("BtnCheckDomain");
            btn.IsEnabled = false;
            info.Text = d + ": " + T("проверяю…");
            bool dns = true;
            SiteResult r;
            try { r = await Sites.Check(d, ok => dns = ok); }
            finally { btn.IsEnabled = true; }
            bool listed = Zapret.UserDomains("general").Any(x => d == x || d.EndsWith("." + x));
            string state = Zapret.Running ? T("обход включён") : pausedByVpn ? T("пауза из-за VPN") : T("обход выключен");
            if (!dns)
                info.Text = d + ": " + T("домен не найден (DNS). Если сайт существует, провайдер может подменять DNS — включите «Защищённый DNS» в настройках.");
            else if (r.Ok)
                info.Text = d + ": " + T("открывается") + " — " + r.Ms + " " + T("мс") + (r.TcpMs >= 0 ? ", TCP " + r.TcpMs + " " + T("мс") : "") + " (" + state + ")";
            else
                info.Text = d + ": " + T("не открывается") + " (" + state + "). " +
                    (listed ? T("Сайт уже в списке — попробуйте подобрать стратегию.") : Zapret.Running ? T("Нажмите «Добавить», чтобы обходить его, и проверьте снова.") : T("Включите обход и проверьте снова."));
        }

        // Блок Telegram в hosts стёр антивирус или «чистильщик» — возвращаем
        string hostsError;
        void CheckHosts()
        {
            if (!cfg.Telegram || Zapret.LocalVersion == null) return;
            try
            {
                if (Telegram.HostsOk()) { hostsError = null; return; }
                Log.Write("Записи Telegram в hosts пропали или изменены — восстанавливаю");
                Telegram.SetHosts(true);
                hostsError = null;
            }
            catch (Exception e)
            {
                if (hostsError != e.Message) Log.Write("Не удалось проверить hosts: " + e.Message);   // не повторяем одну и ту же ошибку каждую минуту
                hostsError = e.Message;
            }
        }

        // ---------- Профили сетей ----------
        async Task CheckNetwork()
        {
            NetworkId net = null;
            try { net = await Task.Run(() => Networks.Current()); } catch { }
            if ((net == null ? null : net.Id) != (currentNet == null ? null : currentNet.Id) || tick < 3)
            {
                currentNet = net;
                if (net != null && tick >= 3) Log.Write("Сеть: " + net.Name);
                UpdateNetUi();
            }
        }

        async Task CheckNetworkAndApply()
        {
            string before = currentNet == null ? null : currentNet.Id;
            await CheckNetwork();
            string after = currentNet == null ? null : currentNet.Id;
            if (after == null || after == before || before == null) return;

            NetProfile p;
            if (cfg.NetworkProfilesEnabled && cfg.NetworkProfiles.TryGetValue(after, out p) && p.Strategy != cfg.Strategy && File.Exists(Paths.InZapret(p.Strategy)))
            {
                cfg.Strategy = p.Strategy;
                cfg.Save();
                UpdateStrategyList();
                Notify(currentNet.Name + ": стратегия " + Zapret.Title(p.Strategy));
                if (Zapret.Running) await RestartIfRunning();
            }
            else if (Zapret.Running)
            {
                baseline = null;
                siteCheckDue = DateTime.Now.AddSeconds(10);   // новая сеть — проверим сайты, при проблемах сторож подберёт стратегию
            }
        }

        void SaveNetworkProfile()
        {
            if (!cfg.NetworkProfilesEnabled || currentNet == null) return;
            NetProfile p;
            if (cfg.NetworkProfiles.TryGetValue(currentNet.Id, out p) && p.Strategy == cfg.Strategy) return;
            p = new NetProfile();
            p.Name = currentNet.Name; p.Strategy = cfg.Strategy; p.Updated = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
            cfg.NetworkProfiles[currentNet.Id] = p;
            cfg.Save();
            UpdateNetUi();
        }

        void UpdateNetUi()
        {
            Text("NetCurrentText").Text = currentNet == null
                ? T("Сеть не определена")
                : T("Сейчас") + ": " + T(currentNet.Name) + ". " + T("Лаунчер запоминает рабочую стратегию для каждой сети и переключается сам");
            StackPanel list = E<StackPanel>("NetListPanel");
            list.Children.Clear();
            foreach (KeyValuePair<string, NetProfile> kv in cfg.NetworkProfiles.OrderBy(k => k.Value.Name))
            {
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel info = new StackPanel();
                bool here = currentNet != null && kv.Key == currentNet.Id;
                info.Children.Add(new TextBlock { Text = T(kv.Value.Name) + (here ? "  · " + T("сейчас") : ""), FontSize = 13 });
                TextBlock sub = new TextBlock { Text = T("Стратегия") + ": " + T(Zapret.Title(kv.Value.Strategy)) + " · " + kv.Value.Updated, FontSize = 11 };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                info.Children.Add(sub);
                row.Children.Add(info);
                Button del = new Button { Content = "\uE711", Tag = kv.Key, Width = 28, Height = 28, FontSize = 11, ToolTip = T("Забыть сеть") };
                del.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
                del.Click += delegate
                {
                    cfg.NetworkProfiles.Remove((string)del.Tag);
                    cfg.Save();
                    UpdateNetUi();
                };
                Grid.SetColumn(del, 1);
                row.Children.Add(del);
                Border b = new Border { Child = row, Padding = new Thickness(16, 8, 10, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
                b.SetResourceReference(Border.BorderBrushProperty, "Line");
                list.Children.Add(b);
            }
            UpdateNetStatus();
        }

        void UpdateNetStatus()
        {
            string id = currentNet == null ? "" : currentNet.Id;
            Text("NetIcon").Text = pausedByVpn ? "\uE705" : id.StartsWith("wifi:") ? "\uE701" : "\uE839";
            Text("NetStatus").Text = pausedByVpn ? "VPN · " + (vpnName ?? "VPN") : currentNet == null ? T("сеть не определена") : T(currentNet.Name);
        }

        // ---------- Карточки сайтов ----------
        // «Видео» (серверы googlevideo) проверяется при подборе и сторожем, но карточкой не показывается
        IEnumerable<string> VisibleSites() { return Sites.Targets(cfg.Telegram, false).Select(t => t.Key).Where(k => k != "Видео"); }

        void SiteState(string site, out string color, out string https, out string tcp)
        {
            SiteResult r = siteResults == null ? null : siteResults.FirstOrDefault(x => x.Name == site);
            bool running = Zapret.Running;
            if (siteChecking) { color = "Wait"; https = T("проверяю…"); tcp = " "; }
            else if (r == null) { color = "Idle"; https = running ? T("не проверялось") : pausedByVpn ? T("пауза") : T("обход выключен"); tcp = " "; }
            else
            {
                color = r.Ok ? "Ok" : "Fail";
                https = r.Ok ? "HTTPS " + r.Ms + " " + T("мс") : T("не открывается");
                tcp = r.TcpMs >= 0 ? "TCP " + r.TcpMs + " " + T("мс") : "TCP —";
            }
        }

        void BuildSitePanel()
        {
            // окно скрыто — карточки не перестраиваем (их соберёт показ окна), обновляем только мини-окно
            if (!window.IsVisible) { UpdateFlyout(); return; }
            UniformGrid panel = E<UniformGrid>("SitePanel");
            panel.Children.Clear();
            bool stagger = animateCards && !siteChecking;
            animateCards = false;
            int n = 0;
            foreach (string site in VisibleSites())
            {
                string color, https, tcp;
                SiteState(site, out color, out https, out tcp);
                SiteResult r = siteResults == null ? null : siteResults.FirstOrDefault(x => x.Name == site);
                bool blocked = r != null && !r.Ok && !siteChecking && cfg.BlockedSites.Contains(site);   // режут по адресу — zapret не поможет
                StackPanel content = new StackPanel();

                // значок сервиса, название и точка состояния
                Grid head = new Grid();
                string[] look = SiteLook(site);
                Border badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = B(look[1]), HorizontalAlignment = HorizontalAlignment.Left };
                TextBlock glyph = new TextBlock { Text = look[0], FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
                badge.Child = glyph;
                head.Children.Add(badge);
                head.Children.Add(new TextBlock { Text = T(site), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(34, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                Ellipse dot = new Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Shape.FillProperty, color);
                head.Children.Add(dot);
                content.Children.Add(head);

                // крупно — время открытия сайта; если не открылся или ещё не проверен — короткий текст
                TextBlock big = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                if (r != null && r.Ok && !siteChecking)
                {
                    big.Inlines.Add(new System.Windows.Documents.Run(r.Ms.ToString()) { FontSize = 24, FontWeight = FontWeights.SemiBold });
                    System.Windows.Documents.Run unit = new System.Windows.Documents.Run(" " + T("мс")) { FontSize = 12 };
                    unit.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
                    big.Inlines.Add(unit);
                }
                else
                {
                    big.Text = blocked ? T("заблокирован") : r != null && !r.Ok && !siteChecking ? T("недоступен") : https;
                    big.FontSize = 14;
                    big.FontWeight = FontWeights.SemiBold;
                    big.Margin = new Thickness(0, 14, 0, 6);
                    big.SetResourceReference(TextBlock.ForegroundProperty, color == "Fail" ? "Fail" : "TextSub");
                }
                content.Children.Add(big);
                TextBlock t = new TextBlock { Text = blocked ? T("по адресу — поможет VPN") + " →" : tcp, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap };
                t.SetResourceReference(TextBlock.ForegroundProperty, blocked ? "AccentHover" : "Muted");
                content.Children.Add(t);

                // график последних проверок — на всю ширину карточки
                Grid sparkHost = new Grid { Height = 22, Margin = new Thickness(0, 8, 0, 0) };
                string siteName = site;
                sparkHost.SizeChanged += (s, e) =>
                {
                    sparkHost.Children.Clear();
                    FrameworkElement spark = Sparkline(siteName, e.NewSize.Width, 22);
                    if (spark != null) sparkHost.Children.Add(spark);
                };
                content.Children.Add(sparkHost);

                Border card = new Border { Child = content, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 12, 12, 10), Margin = new Thickness(0, 0, 8, 8) };
                card.SetResourceReference(Border.BackgroundProperty, "CardRaised");
                if (siteChecking) Shimmer(card);
                else if (stagger) AppearLater(card, n);
                FrameworkElement item = LiftOnHover(card, SiteUrl(site), site, blocked ? (Action)OpenVpnRoutes : null);
                if (blocked) item.ToolTip = T("Провайдер блокирует этот сайт по адресу — ни одна стратегия zapret его не открыла. Нажмите, чтобы пустить его через VPN");
                panel.Children.Add(item);
                n++;
            }
            UpdateFlyout();
        }

        // Значок (Segoe MDL2) и фирменный цвет сервиса для карточки
        static string[] SiteLook(string site)
        {
            if (site == "Discord") return new[] { "", "#5865F2" };
            if (site == "YouTube") return new[] { "", "#FF2D2D" };
            if (site == "Telegram") return new[] { "", "#2AABEE" };
            return new[] { "", "#6B7280" };
        }

        static string SiteUrl(string site)
        {
            if (site == "Discord") return "https://discord.com/app";
            if (site == "YouTube") return "https://www.youtube.com/";
            if (site == "Telegram") return "https://web.telegram.org/";
            return null;
        }

        // Карточка приподнимается при наведении (тень и сдвиг вверх), клик открывает сайт в браузере.
        // Тень — у отдельной подложки: эффект на самой карточке размыл бы текст
        // Страница VPN, вкладка «Маршруты» — туда ведут подсказки «поможет VPN»
        void OpenVpnRoutes()
        {
            ShowWindow();
            E<RadioButton>("NavVpn").IsChecked = true;
            E<RadioButton>("VpnTabRoutes").IsChecked = true;
        }

        // onClick — своё действие по клику вместо открытия сайта
        FrameworkElement LiftOnHover(Border card, string url, string site, Action onClick = null)
        {
            Grid wrap = new Grid { Margin = card.Margin };
            card.Margin = new Thickness(0);
            Border under = new Border { CornerRadius = card.CornerRadius, Opacity = 0 };
            under.SetResourceReference(Border.BackgroundProperty, "CardRaised");
            under.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = Theme.Dark ? 0.5 : 0.18, Color = Colors.Black };
            wrap.Children.Add(under);
            wrap.Children.Add(card);
            if (url == null && onClick == null) return wrap;
            wrap.Cursor = Cursors.Hand;
            wrap.ToolTip = T("Открыть") + " " + site;
            TranslateTransform tr = new TranslateTransform();
            wrap.RenderTransform = tr;
            Duration d = new Duration(TimeSpan.FromMilliseconds(160));
            Action<bool> lift = up =>
            {
                tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(up ? -3 : 0, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                under.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(up ? 1 : 0, d));
            };
            wrap.MouseEnter += delegate { lift(true); };
            wrap.MouseLeave += delegate { lift(false); };
            // через explorer, чтобы браузер открылся без прав администратора
            wrap.MouseLeftButtonUp += delegate
            {
                if (onClick != null) onClick();
                else Process.Start("explorer.exe", "\"" + url + "\"");
            };
            return wrap;
        }

        // Появление карточек по очереди: лёгкое затухание и сдвиг снизу
        static void AppearLater(FrameworkElement e, int index)
        {
            TimeSpan delay = TimeSpan.FromMilliseconds(70 * index);
            Duration d = new Duration(TimeSpan.FromMilliseconds(260));
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            TranslateTransform tr = new TranslateTransform(0, 10);
            e.RenderTransform = tr;
            e.Opacity = 0;
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d) { BeginTime = delay, EasingFunction = ease });
            tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, d) { BeginTime = delay, EasingFunction = ease });
        }

        // Мини-график HTTPS-времени последних проверок; провалы — красные точки внизу
        FrameworkElement Sparkline(string site, double width, double height)
        {
            List<int> h;
            if (!siteHistory.TryGetValue(site, out h) || h.Count < 2) return null;
            List<int> ok = h.Where(v => v >= 0).ToList();
            double max = ok.Count > 0 ? ok.Max() : 1, min = ok.Count > 0 ? ok.Min() : 0;
            if (max - min < 50) { max += 25; min = Math.Max(0, min - 25); }
            Canvas c = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
            Polyline line = new Polyline { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
            line.SetResourceReference(Shape.StrokeProperty, "Accent");
            for (int i = 0; i < h.Count; i++)
            {
                double x = h.Count == 1 ? width : i * width / (h.Count - 1);
                if (h[i] < 0)
                {
                    Ellipse fail = new Ellipse { Width = 4, Height = 4 };
                    fail.SetResourceReference(Shape.FillProperty, "Fail");
                    Canvas.SetLeft(fail, x - 2);
                    Canvas.SetTop(fail, height - 4);
                    c.Children.Add(fail);
                    continue;
                }
                line.Points.Add(new Point(x, height - 2 - (h[i] - min) / (max - min) * (height - 4)));
            }
            c.Children.Insert(0, line);
            return c;
        }

        static void Shimmer(UIElement e)
        {
            DoubleAnimation a = new DoubleAnimation(1, 0.45, new Duration(TimeSpan.FromMilliseconds(650)));
            a.AutoReverse = true;
            a.RepeatBehavior = RepeatBehavior.Forever;
            e.BeginAnimation(UIElement.OpacityProperty, a);
        }
    }
}
