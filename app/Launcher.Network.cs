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

        Task SetSecureDns(bool on, string provider) { return RunBusy(() => ChangeSecureDns(on, provider), "DNS…"); }

        async Task ChangeSecureDns(bool on, string provider)
        {
            try
            {
                if (cfg.SecureDns.Length > 0) await Task.Run(() => SecureDns.Disable(cfg.DnsAdapters));
                cfg.SecureDns = "";
                cfg.DnsAdapters = new List<string>();
                if (on)
                {
                    List<string> skipped = new List<string>();
                    List<string> adapters;
                    try { adapters = await Task.Run(() => SecureDns.Enable(provider, skipped)); }
                    catch (Exception e)
                    {
                        var unresolved = e.Data["DnsAdapters"] as List<string>;
                        if (unresolved != null) { cfg.SecureDns = provider; cfg.DnsAdapters = unresolved; }
                        cfg.Save();
                        throw;
                    }
                    if (skipped.Count > 0) Log.Write("DNS: на адаптере уже прописан свой DNS — его не трогаю: " + string.Join(", ", skipped));
                    if (adapters.Count == 0)
                        throw new Exception(skipped.Count > 0 ? T("На адаптере уже прописан свой DNS — лаунчер его не меняет, чтобы не потерять его при выключении") + ": " + string.Join(", ", skipped)
                                                              : T("Не найден сетевой адаптер с интернетом"));
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
            }
            finally
            {
                filling = true;
                E<CheckBox>("SwSecureDns").IsChecked = cfg.SecureDns.Length > 0;
                filling = false;
            }
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
        IEnumerable<string> VisibleSites() { return Sites.Targets(cfg.Telegram, false).Select(t => t.Key).Where(k => k != "Видео").Concat(cfg.MySites); }

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
            if (cfg.MySites.Count > 0) FetchFavicons();
            UniformGrid panel = E<UniformGrid>("SitePanel");
            panel.Children.Clear();
            bool stagger = animateCards && !siteChecking;
            animateCards = false;
            int n = 0, count = VisibleSites().Count();
            panel.Columns = Math.Max(1, Math.Min(3, count));   // со своими сайтами — два ряда по три
            foreach (string site in VisibleSites())
            {
                string color, https, tcp;
                SiteState(site, out color, out https, out tcp);
                SiteResult r = siteResults == null ? null : siteResults.FirstOrDefault(x => x.Name == site);
                bool blocked = r != null && !r.Ok && !siteChecking && cfg.BlockedSites.Contains(site);   // режут по адресу — zapret не поможет
                StackPanel content = new StackPanel();

                // значок сервиса, название и точка состояния
                Grid head = new Grid();
                FrameworkElement badge = SiteBadge(site, 26);
                badge.HorizontalAlignment = HorizontalAlignment.Left;
                head.Children.Add(badge);
                head.Children.Add(new TextBlock { Text = T(site), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(34, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                Ellipse dot = new Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Shape.FillProperty, color);
                head.Children.Add(dot);
                content.Children.Add(head);

                TextBlock status = new TextBlock
                {
                    Text = r == null || siteChecking ? https : T(r.Ok ? "Доступен" : blocked ? "Заблокирован" : "Недоступен"),
                    FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                status.SetResourceReference(TextBlock.ForegroundProperty, r == null || siteChecking ? "TextSub" : "Text");
                content.Children.Add(status);

                if (r != null && !siteChecking)
                {
                    TextBlock time = new TextBlock
                    {
                        Text = r.CheckedAt == default(DateTime) ? "" : T("Проверка в") + " " + r.CheckedAt.ToLocalTime().ToString("HH:mm"),
                        FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    time.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    content.Children.Add(time);
                }

                var details = new Expander { Header = T("Детали проверки"), Margin = new Thickness(0, 8, 0, 0) };
                details.SetResourceReference(Control.ForegroundProperty, "TextSub");
                details.Visibility = r != null && !siteChecking ? Visibility.Visible : Visibility.Collapsed;
                StackPanel measurements = new StackPanel();
                var timing = new TextBlock { Text = https + "  ·  " + tcp, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                timing.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                measurements.Children.Add(timing);
                List<int> history;
                int? delta = siteHistory.TryGetValue(site, out history) ? Home.SpeedDelta(history) : null;
                if (delta.HasValue && Math.Abs(delta.Value) >= 5)
                {
                    TextBlock trend = new TextBlock { Text = Math.Abs(delta.Value) + "% " + T(delta < 0 ? "быстрее обычного" : "медленнее обычного"),
                        FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
                    trend.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
                    measurements.Children.Add(trend);
                }
                details.Content = measurements;
                content.Children.Add(details);
                // график последних проверок — на всю ширину карточки
                Grid sparkHost = new Grid { Height = 22, Margin = new Thickness(0, 8, 0, 0) };
                string siteName = site;
                sparkHost.SizeChanged += (s, e) =>
                {
                    sparkHost.Children.Clear();
                    FrameworkElement spark = Sparkline(siteName, e.NewSize.Width, 22);
                    if (spark != null) sparkHost.Children.Add(spark);
                };
                measurements.Children.Add(sparkHost);

                Border card = new Border { Child = content, CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 14, 16, 12), Margin = Home.SiteCardMargin(n, panel.Columns, count) };
                card.SetResourceReference(Border.BackgroundProperty, "CardRaised");
                card.SetResourceReference(Border.BorderBrushProperty, "CardEdge");
                card.BorderThickness = new Thickness(1);
                if (siteChecking) Shimmer(card);
                else if (stagger) AppearLater(card, n);
                FrameworkElement item = LiftOnHover(card, Home.SiteUrl(site), site, blocked ? (Action)OpenVpnRoutes : null, details);
                if (blocked) item.ToolTip = T("Провайдер блокирует этот сайт по адресу — ни одна стратегия zapret его не открыла. Нажмите, чтобы пустить его через VPN");
                panel.Children.Add(item);
                n++;
            }
            UpdateGreeting();
            UpdateFlyout();
        }

        // Журнал хранит событие, а главная показывает последнее за текущий запуск.
        void SetConnectionEvent(string title, string detail = null)
        {
            string line = title + (string.IsNullOrEmpty(detail) ? "" : ": " + detail);
            Log.Write(line);
            TextBlock latest = Text("HomeEventText");
            latest.Text = T(title) + (string.IsNullOrEmpty(detail) ? "" : " · " + detail) + " · " + DateTime.Now.ToString("HH:mm");
            latest.ToolTip = latest.Text;
        }

        // Главная: приветствие (дата, итог в две строки, пояснение), карточки-иллюстрации и карточка VPN. Зовётся раз в секунду из UpdateUi
        void UpdateGreeting()
        {
            string state = cardState ?? "off";
            List<SiteResult> res = siteResults == null ? null : siteResults.Where(r => VisibleSites().Contains(r.Name)).ToList();
            System.Globalization.CultureInfo culture = new System.Globalization.CultureInfo(I18n.English ? "en-US" : "ru-RU");
            Text("GreetDate").Text = DateTime.Now.ToString(I18n.English ? "dddd, MMMM d" : "dddd, d MMMM", culture);
            // пока лаунчер занят (включает обход, подбирает, обновляется), состояние бывает «выключен» — оставляем прежний итог
            if (!busy || Text("GreetTitle").Text.Trim().Length == 0) Text("GreetTitle").Text = Home.GreetingTitle(state, siteChecking, res);
            string strategy = T(Zapret.Title(cfg.Strategy));
            string watch = I18n.English ? "Watchdog checks sites every " + HealthInterval().TotalMinutes + " min." : "Сторож проверяет сайты каждые " + HealthInterval().TotalMinutes + " мин.";
            // стратегия видна в карточках ниже, поэтому здесь — только то, чего больше нигде нет
            string names = state == "on" && !siteChecking ? Home.FailingNames(res) : "";
            string sub = state == "on" ? (names.Length > Home.GreetNamesMax ? T("Не открывается:") + " " + names + "." + (cfg.Watchdog ? " " : "") : "") + (cfg.Watchdog ? watch : "")
                       : state == "pause" ? T("Обход вернётся, когда VPN отключат")
                       : state == "error" ? T("Подробности — в журнале")
                       : T("Нажмите на карточку обхода, чтобы включить");
            Text("GreetSub").Text = sub;
            Text("GreetSub").Visibility = sub.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // стратегия: что показал последний подбор
            string score, pick = null;
            if (cfg.PickScores.TryGetValue(cfg.Strategy, out score))
            {
                string[] p = score.Split('/');
                if (p.Length == 3 && p[1] != "0") pick = I18n.English ? "opened " + p[0] + " of " + p[1] + " sites in the last pick" : "открыла " + p[0] + " из " + p[1] + " сайтов на подборе";
            }
            Text("IllStratTitle").Text = (I18n.English ? "Strategy " : "Стратегия ") + strategy;
            Text("IllStratSub").Text = pick ?? T("Нажмите, чтобы выбрать другую");

            // VPN: сервер, пинг на значке
            VpnServer s = CurrentServer;
            int ping;
            bool hasPing = s != null && vpnPing.TryGetValue(s.Link, out ping) && ping >= 0;
            string server = s == null ? "" : Vpn.DisplayName(s.Name);
            Text("IllVpnTitle").Text = T(s == null ? "VPN не настроен" : vpnRunning ? "VPN под рукой" : cfg.VpnOn ? "Подключение…" : "VPN выключен");
            Text("IllVpnSub").Text = s == null ? T("Добавьте ключ — выбранные сайты пойдут через VPN") : server + (cfg.VpnOn && !vpnRunning ? "" : " · " + T(vpnRunning ? "только выбранные сайты" : "включается в один клик"));
            E<FrameworkElement>("IllVpnBadge").Visibility = vpnRunning && hasPing ? Visibility.Visible : Visibility.Collapsed;
            if (hasPing) Text("IllVpnBadgeText").Text = vpnPing[s.Link].ToString();

            // сторож
            Text("IllWatchTitle").Text = T(cfg.Watchdog ? "Сторож на посту" : "Сторож спит");
            Text("IllWatchSub").Text = T(cfg.Watchdog ? "Сломается — сам починит" : "Включите — обход будет чиниться сам");

            // карточка VPN
            string code = s == null ? null : Vpn.CountryCode(s.Name);
            Border flag = E<Border>("HomeVpnFlag");
            if ((string)flag.Tag != (code ?? ""))
            {
                flag.Tag = code ?? "";
                flag.Child = code == null ? null : FlagElement(code, 22, 15);
                flag.Visibility = code == null ? Visibility.Collapsed : Visibility.Visible;
            }
            bool ready = vpnRunning && !VpnSwitching;
            bool blocking = VpnProtectionBlocking;
            string status = blocking ? "Трафик приостановлен"
                : ready ? (vpnVerifiedMs >= 0 ? "Подключён" : "Туннель запущен") : cfg.VpnOn ? "VPN переподключается…" : "Выключен";
            Text("HomeVpnName").Text = T(status);
            E<Ellipse>("HomeVpnDot").SetResourceReference(Shape.FillProperty,
                blocking || vpnHealthIssue != null ? "Fail" : ready ? (vpnVerifiedMs >= 0 ? "Ok" : "Wait") : cfg.VpnOn ? "Wait" : "Idle");
            Text("HomeVpnInfo").Text = s == null ? T("Сервер не добавлен") : server;
            Text("HomeVpnProtection").Text = VpnProtectionStatus();
            Text("HomeVpnProtection").SetResourceReference(TextBlock.ForegroundProperty,
                blocking ? "Wait" : VpnFirewall.Enabled ? "Ok" : "Muted");
            E<ToggleButton>("ChipVpn").IsChecked = ready;
            E<ToggleButton>("ChipVpn").IsEnabled = !busy;
            E<Button>("BtnVpnToggle").IsEnabled = s != null && !busy;
            E<ToggleButton>("HomeBypassToggle").IsEnabled = !busy && !picking;
            UpdateIllustrations();
            UpdateMascot(MascotMood(state, siteChecking, busy || picking, res));
        }

        // Картинки на карточках главной показывают состояние: сторож спит — щит бледный и «zZ»; VPN выключен — глобус серый,
        // подключается — вокруг крутится пунктир; идёт подбор — галочка стратегии сменяется крутящимися стрелками
        bool illVpnSpinning, illStratSpinning;

        void UpdateIllustrations()
        {
            bool watch = cfg.Watchdog;
            E<Ellipse>("IllWatchCircle").SetResourceReference(Shape.FillProperty, watch ? "AccentSoft" : "IconBg");
            E<UIElement>("IllWatchShield").Opacity = watch ? 1 : 0.6;
            TextBlock mark = Text("IllWatchCheck");
            mark.Text = watch ? "" : "zZ";
            if (watch) mark.SetResourceReference(TextBlock.FontFamilyProperty, "Icons"); else mark.FontFamily = new FontFamily("Segoe UI");
            mark.FontSize = watch ? 21 : 16;
            mark.SetResourceReference(TextBlock.ForegroundProperty, watch ? "Accent" : "Faint");

            bool connecting = cfg.VpnOn && !vpnRunning && CurrentServer != null;
            E<Ellipse>("IllVpnCircle").SetResourceReference(Shape.FillProperty, vpnRunning || connecting ? "AccentSoft" : "IconBg");
            Text("IllVpnGlobe").SetResourceReference(TextBlock.ForegroundProperty, vpnRunning || connecting ? "Accent" : "Faint");
            UIElement spin = E<UIElement>("IllVpnSpin");
            if (Spin(spin, connecting, ref illVpnSpinning, 2.4)) Fade(spin, connecting ? 1 : 0, TimeSpan.FromMilliseconds(200));

            TextBlock check = Text("IllStratCheck");
            check.Text = picking ? "" : "";
            Spin(check, picking, ref illStratSpinning, 1.2);
            if (picking)
            {
                Text("IllStratTitle").Text = T("Идёт подбор");
                Text("IllStratSub").Text = Text("PowerStatus").Text;   // «Подбор 5 / 19» — тот же счётчик, что на карточке обхода
            }
        }

        // Вращение по кругу, пока on; true — если состояние поменялось (анимация запущена или остановлена)
        static bool Spin(UIElement e, bool on, ref bool state, double seconds)
        {
            if (on == state) return false;
            state = on;
            RotateTransform r = new RotateTransform();
            e.RenderTransform = r;
            if (on) r.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(seconds))) { RepeatBehavior = RepeatBehavior.Forever });
            return true;
        }

        // Скорость VPN за последние минуты — линия на карточке главной
        async Task SaveMySites()
        {
            string error;
            List<string> list = Home.ParseMySites(E<TextBox>("MySitesBox").Text, out error);
            TextBlock info = Text("MySitesInfo");
            info.Visibility = Visibility.Visible;
            info.SetResourceReference(TextBlock.ForegroundProperty, list == null ? "Fail" : "Muted");
            if (list == null) { info.Text = error; return; }
            foreach (string old in cfg.MySites.Where(s => !list.Contains(s))) siteHistory.Remove(old);   // убранный сайт не вернётся со старым графиком
            cfg.MySites = list;
            cfg.Save();
            AutoBackup();
            E<TextBox>("MySitesBox").Text = string.Join(", ", list);
            if (siteResults != null) siteResults.RemoveAll(r => !VisibleSites().Contains(r.Name));
            BuildSitePanel();
            info.Text = T(list.Count == 0 ? "Свои сайты убраны" : "Сохранено — проверяю…");
            if (list.Count == 0) return;
            await PingNow();
            info.Text = T("Сохранено");
        }

        // Значок сайта: у трёх сервисов — их логотип (белым на фирменном цвете, контуры в сетке 24×24),
        // у своих сайтов — их favicon, пока его нет — глобус на сером
        static readonly Dictionary<string, string[]> Brands = new Dictionary<string, string[]>
        {
            { "Discord", new[] { "#5865F2", "F0 M6.5,6.3 C8,5.6 9.5,5.3 10.2,5.2 L10.6,6 C11.5,5.9 12.5,5.9 13.4,6 L13.8,5.2 C14.5,5.3 16,5.6 17.5,6.3 C19.6,9.4 20.4,12.6 20.2,16.4 C18.6,17.6 17,18.3 15.6,18.7 L14.8,17.4 C15.4,17.2 15.9,16.9 16.4,16.6 L16.1,16.3 C13.4,17.6 10.6,17.6 7.9,16.3 L7.6,16.6 C8.1,16.9 8.6,17.2 9.2,17.4 L8.4,18.7 C7,18.3 5.4,17.6 3.8,16.4 C3.6,12.6 4.4,9.4 6.5,6.3 Z M7.9,13.1 A1.4,1.55 0 1 0 10.7,13.1 A1.4,1.55 0 1 0 7.9,13.1 Z M13.3,13.1 A1.4,1.55 0 1 0 16.1,13.1 A1.4,1.55 0 1 0 13.3,13.1 Z" } },
            { "YouTube", new[] { "#FF0033", "M9.6,7.6 L16.8,12 L9.6,16.4 Z" } },
            { "Telegram", new[] { "#2AABEE", "M5.9,11.4 L17.6,6.9 C18.2,6.7 18.7,7 18.5,7.9 L16.5,17.4 C16.3,18.1 15.9,18.3 15.3,17.9 L12.2,15.6 L10.7,17 C10.5,17.2 10.4,17.3 10.1,17.3 L10.3,14.2 L15.9,9.1 C16.2,8.9 15.9,8.7 15.5,9 L8.6,13.4 L5.6,12.4 C5,12.2 5,11.8 5.9,11.4 Z" } },
        };
        static readonly string FaviconDir = System.IO.Path.Combine(Paths.Root, "favicons");
        static readonly Dictionary<string, ImageSource> favicons = new Dictionary<string, ImageSource>();
        readonly HashSet<string> faviconTried = new HashSet<string>();

        static FrameworkElement SiteBadge(string site, double size)
        {
            string[] brand;
            ImageSource icon = Brands.ContainsKey(site) ? null : Favicon(site);
            Border b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), VerticalAlignment = VerticalAlignment.Center };
            if (Brands.TryGetValue(site, out brand))
            {
                b.Background = B(brand[0]);
                System.Windows.Shapes.Path logo = new System.Windows.Shapes.Path { Data = Geometry.Parse(brand[1]), Fill = Brushes.White };
                if (site == "YouTube") { logo.Stroke = Brushes.White; logo.StrokeThickness = 1.4; logo.StrokeLineJoin = PenLineJoin.Round; }   // скруглённые углы треугольника
                Canvas grid = new Canvas { Width = 24, Height = 24 };
                grid.Children.Add(logo);
                b.Child = new Viewbox { Child = grid, Width = size * 0.8, Height = size * 0.8 };
            }
            else if (icon != null)
            {
                b.Background = Brushes.White;   // favicon рисуют под светлую вкладку браузера: чёрный значок GitHub на тёмной плитке пропал бы
                b.Child = new Image { Source = icon, Width = size * 0.62, Height = size * 0.62 };
                RenderOptions.SetBitmapScalingMode(b.Child, BitmapScalingMode.HighQuality);
            }
            else
            {
                b.Background = B("#6B7280");
                TextBlock globe = new TextBlock { Text = "", FontSize = size * 0.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                globe.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
                b.Child = globe;
            }
            return b;
        }

        static ImageSource Favicon(string host)
        {
            ImageSource img;
            if (!Home.SafeHost(host)) return null;
            if (favicons.TryGetValue(host, out img)) return img;
            string file = System.IO.Path.Combine(FaviconDir, host + ".ico");
            try { img = File.Exists(file) ? DecodeIcon(File.ReadAllBytes(file)) : null; } catch { img = null; }
            if (img != null) favicons[host] = img;
            return img;
        }

        // .ico, .png, .gif — что отдаст сайт; из нескольких размеров берём самый крупный. SVG и мусор — null
        public static ImageSource DecodeIcon(byte[] data)
        {
            try
            {
                System.Windows.Media.Imaging.BitmapDecoder d = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(data),
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                System.Windows.Media.Imaging.BitmapFrame best = d.Frames.OrderByDescending(f => f.PixelWidth).First();
                best.Freeze();
                return best;
            }
            catch { return null; }
        }

        // shortcut: берём только /favicon.ico, без разбора <link rel="icon"> в HTML — у сайтов без него остаётся глобус
        async void FetchFavicons()
        {
            bool got = false;
            foreach (string host in cfg.MySites.ToList())
            {
                if (!Home.SafeHost(host) || Favicon(host) != null || !faviconTried.Add(host)) continue;   // одна попытка за запуск: сайт мог быть заблокирован
                try
                {
                    using (System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8)))
                    {
                        System.Net.Http.HttpResponseMessage r = await Util.Http.GetAsync("https://" + host + "/favicon.ico", cts.Token);
                        long? len = r.Content.Headers.ContentLength;
                        if (!r.IsSuccessStatusCode || len > FaviconMaxBytes) continue;
                        byte[] data = await r.Content.ReadAsByteArrayAsync();
                        if (data.Length > FaviconMaxBytes || DecodeIcon(data) == null) continue;
                        Directory.CreateDirectory(FaviconDir);
                        File.WriteAllBytes(System.IO.Path.Combine(FaviconDir, host + ".ico"), data);
                        got = true;
                    }
                }
                catch { }
            }
            if (got) BuildSitePanel();
        }
        const int FaviconMaxBytes = 256 * 1024;

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
        FrameworkElement LiftOnHover(Border card, string url, string site, Action onClick = null, Expander detailsControl = null)
        {
            Grid wrap = new Grid { Margin = card.Margin };
            card.Margin = new Thickness(0);
            // в светлой теме мягкая тень видна всегда (карточки «парят»), при наведении — сильнее; в тёмной чёрной тени не видно —
            // там при наведении карточка мягко светится цветом акцента
            double rest = Theme.Dark ? 0 : 0.55;
            Border under = new Border { CornerRadius = card.CornerRadius, Opacity = rest };
            under.SetResourceReference(Border.BackgroundProperty, "CardRaised");
            under.Effect = Theme.Dark
                ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 26, ShadowDepth = 0, Opacity = 0.45, Color = Theme.Color("Accent") }
                : new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 7, Direction = 270, Opacity = 0.16, Color = Theme.Color("Shadow") };
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
                under.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(up ? 1 : rest, d));
            };
            wrap.MouseEnter += delegate { lift(true); };
            wrap.MouseLeave += delegate { lift(false); };
            // через explorer, чтобы браузер открылся без прав администратора
            wrap.MouseLeftButtonUp += (sender, e) =>
            {
                if (detailsControl != null && detailsControl.IsMouseOver) return;
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
