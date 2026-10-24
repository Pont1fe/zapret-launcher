// Главное окно, трей, таймеры: запуск обхода, подбор стратегии, сторож, пауза при VPN, профили сетей
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
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
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
    }

    class Launcher
    {
        readonly bool autostart;
        readonly Config cfg;
        Window window;
        Forms.NotifyIcon tray;
        Forms.ToolStripMenuItem miOpen, miStart, miStop, miStrategies, miPick, miExit;
        System.Drawing.Icon iconOn, iconOff;
        DispatcherTimer statusTimer, updateTimer, healthTimer;

        // состояние
        bool busy, picking, cancelPick, exiting, hintShown, filling, pulseOn, trayActive, siteChecking, netChanged = true;
        bool wantRunning;        // пользователь хочет, чтобы обход работал
        bool pausedByVpn;        // обход выключен, пока включён VPN
        int tick, failStreak;
        int? baseline;           // сколько сайтов открывала лучшая стратегия (если все недоступны — не дёргаемся зря)
        DateTime lastRestart = DateTime.MinValue, siteCheckDue = DateTime.MaxValue;
        Release latest, launcherLatest;
        DateTime? lastCheck;
        string conflictText = "", vpnName;
        NetworkId currentNet;
        List<SiteResult> siteResults;
        readonly Dictionary<string, string> pickResults = new Dictionary<string, string>();

        static readonly BrushConverter BrushConv = new BrushConverter();
        static Brush B(string hex) { return (Brush)BrushConv.ConvertFromString(hex); }

        T E<T>(string name) where T : class { return window.FindName(name) as T; }
        TextBlock Text(string name) { return E<TextBlock>(name); }

        public Launcher(bool autostart)
        {
            this.autostart = autostart;
            cfg = Config.Load();
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.xaml"))
                window = (Window)XamlReader.Load(s);
            BuildTray();
            WireEvents();
            Log.Line += line => window.Dispatcher.BeginInvoke(new Action(() =>
            {
                TextBox box = E<TextBox>("LogBox");
                box.AppendText(line + "\r\n");
                box.ScrollToEnd();
            }));
            try { foreach (string line in File.ReadLines(Paths.Log).Reverse().Take(200).Reverse()) E<TextBox>("LogBox").AppendText(line + "\r\n"); } catch { }

            System.Drawing.Bitmap big = Icons.Draw(64, true);
            ImageSource src = Icons.ToImageSource(big);
            window.Icon = src;
            E<Image>("TitleIcon").Source = src;

            InitSwitches();
            UpdateTitle();
            if (Zapret.Running) wantRunning = true;
            UpdateStrategyList();
            BuildSitePanel();
            UpdateUi();
            if (!autostart) window.Show();

            NetworkChange.NetworkAddressChanged += delegate { netChanged = true; };
            DispatcherTimer startup = new DispatcherTimer();
            startup.Interval = TimeSpan.FromMilliseconds(400);
            startup.Tick += async delegate { startup.Stop(); await Startup(); };
            startup.Start();
        }

        public void ShowWindow()
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }

        // ---------- Старт ----------
        async Task Startup()
        {
            Log.Write("Лаунчер " + Consts.Version + " запущен" + (autostart ? " (автозапуск)" : ""));
            if (Paths.Root.Any(c => c > 127))
                Log.Write("Внимание: в пути к лаунчеру есть кириллица или спецсимволы — zapret может работать некорректно. Переместите папку, например, в C:\\ZapretLauncher");
            try { if (Autostart.Enabled) Autostart.Set(true); } catch { }   // путь мог измениться (переезд папки, PowerShell-версия)
            try { Shell.MigrateShortcuts(); Shell.UpdateUninstallEntry(); } catch (Exception e) { Log.Write("Ярлыки: " + e.Message); }
            foreach (string legacy in new[] { "ZapretLauncher.ps1", "Start.bat", "launcher.ico" })
                try { File.Delete(System.IO.Path.Combine(Paths.Root, legacy)); } catch { }
            UpdateConflicts();
            CheckNetwork();

            bool installedNow = false;
            if (Zapret.LocalVersion == null)
            {
                Log.Write("zapret ещё не установлен — скачиваю последнюю версию");
                await InstallZapretUi();
                installedNow = true;
            }
            UpdateStrategyList();

            if ((autostart || cfg.StartOnOpen) && Zapret.LocalVersion != null && !Zapret.Running) await StartBypassUi();
            else if (cfg.Telegram && Zapret.Running && !Telegram.ProxyRunning) await RunBusy(() => Telegram.StartProxy(cfg), "TELEGRAM…");
            if (Zapret.Running) siteCheckDue = DateTime.Now.AddSeconds(3);

            updateTimer = Timer(TimeSpan.FromHours(6), async delegate
            {
                bool ok = await BackgroundUpdate();
                updateTimer.Interval = ok ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(5);
            });
            if (!installedNow && !await BackgroundUpdate()) updateTimer.Interval = TimeSpan.FromMinutes(5);
            healthTimer = Timer(TimeSpan.FromMinutes(3), delegate { if (Zapret.Running) siteCheckDue = DateTime.Now; });
            statusTimer = Timer(TimeSpan.FromSeconds(1), async delegate { await OnTick(); });
            UpdateTitle();
            UpdateUi();
        }

        DispatcherTimer Timer(TimeSpan interval, EventHandler tickHandler)
        {
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = interval;
            t.Tick += tickHandler;
            t.Start();
            return t;
        }

        // Раз в секунду: интерфейс, сторож по упавшему winws, VPN (5 с), сеть (10 с), конфликты (15 с), проверка сайтов
        async Task OnTick()
        {
            if (busy || exiting) return;
            tick++;
            UpdateUi();
            if (tick % 5 == 1) await CheckVpn();
            if (netChanged || tick % 10 == 2) { netChanged = false; await CheckNetworkAndApply(); }
            if (tick % 15 == 3) UpdateConflicts();
            await CrashGuard();
            if (!busy && !siteChecking && Zapret.Running && DateTime.Now >= siteCheckDue) await SiteCheck(true);
        }

        // ---------- Запуск и остановка ----------
        async Task StartBypass(bool quiet)
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
                if (MessageBox.Show(window, "Работает служба zapret, установленная через service.bat.\nЧтобы управлять обходом из лаунчера, её нужно удалить (автозапуск лаунчер настроит сам).\n\nУдалить службу?",
                    "Zapret Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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

            await Zapret.Start(cfg.Strategy);
            wantRunning = true;
            pausedByVpn = false;
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
            await Zapret.Stop(false);
            await Telegram.StopProxy();
            siteResults = null;
            BuildSitePanel();
        }

        Task StartBypassUi() { return RunBusy(() => StartBypass(false), "ПОДКЛЮЧЕНИЕ…"); }
        Task StopBypassUi() { return RunBusy(StopBypass, "ОТКЛЮЧЕНИЕ…"); }
        Task RestartIfRunning() { return Zapret.Running ? RunBusy(() => StartBypass(false), "ПРИМЕНЯЮ…") : Task.FromResult(0); }

        async Task RunBusy(Func<Task> action, string busyText = null)
        {
            if (busy) return;
            busy = true;
            window.Cursor = Cursors.AppStarting;
            if (busyText != null) { Text("PowerStatus").Text = busyText; Text("PowerTimer").Text = " "; }
            try { await action(); }
            catch (Exception e)
            {
                Log.Write("Ошибка: " + e.Message);
                if (window.IsVisible) MessageBox.Show(window, e.Message, "Zapret Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                else tray.ShowBalloonTip(5000, "Zapret Launcher", e.Message, Forms.ToolTipIcon.Error);
            }
            finally
            {
                busy = false;
                window.Cursor = null;
                UpdateUi();
            }
        }

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
                Log.Write("Сторож: перезапуск не удался — больше не пытаюсь, пока обход не включат вручную");
            }
        }

        // Проверка сайтов: обновляет индикаторы; health — ещё и сторож (две неудачи подряд — подбор новой стратегии)
        async Task SiteCheck(bool health)
        {
            siteCheckDue = DateTime.MaxValue;
            siteChecking = true;
            BuildSitePanel();
            List<SiteResult> res;
            try { res = await Sites.Test(cfg.Telegram, true); }
            finally { siteChecking = false; }
            if (!Zapret.Running) { siteResults = null; BuildSitePanel(); return; }
            SiteResult control = res.FirstOrDefault(r => r.Name == Sites.Control);
            siteResults = res.Where(r => r.Name != Sites.Control).ToList();
            BuildSitePanel();
            if (!health || !cfg.Watchdog || busy || control == null || !control.Ok) return;   // нет интернета — менять стратегию бессмысленно

            int ok = siteResults.Count(r => r.Ok);
            int expected = baseline.HasValue ? Math.Min(baseline.Value, siteResults.Count) : siteResults.Count;
            if (ok >= expected) { failStreak = 0; return; }
            failStreak++;
            Log.Write("Сторож: не открывается " + string.Join(", ", siteResults.Where(r => !r.Ok).Select(r => r.Name)) + " (проверка " + failStreak + " из 2)");
            if (failStreak < 2) { siteCheckDue = DateTime.Now.AddMinutes(1); return; }
            failStreak = 0;
            if (Conflicts.ActiveVpn() != null) { Log.Write("Сторож: включён VPN — стратегию не меняю"); return; }
            Balloon("Стратегия перестала работать — подбираю новую");
            await RunBusy(async () =>
            {
                PickBest best = await AutoPick(true);
                if (best != null && best.Ok > 0) Balloon("Новая стратегия: " + Zapret.Title(best.File));
            }, "ПОДБОР…");
        }

        // ---------- Подбор стратегии ----------
        class PickBest { public string File; public int Ok, Total, Score; }

        async Task<PickBest> AutoPick(bool stopAtFirstPerfect)
        {
            List<string> strategies = Zapret.Strategies();
            if (strategies.Count == 0) throw new Exception("zapret не установлен");
            string original = cfg.Strategy;
            pickResults.Clear();
            cancelPick = false;
            picking = true;
            ((Button)window.FindName("BtnMain")).Content = "Остановить подбор";
            PickBest best = null;
            Log.Write("Подбор стратегии: проверяю " + strategies.Count + " шт. (" + string.Join(", ", Sites.Targets(cfg.Telegram, false).Select(t => t.Key)) + ")");
            try
            {
                for (int i = 0; i < strategies.Count; i++)
                {
                    if (cancelPick) { Log.Write("Подбор остановлен"); break; }
                    string s = strategies[i];
                    Text("PowerStatus").Text = "ПОДБОР " + (i + 1) + " / " + strategies.Count;
                    Text("PowerTimer").Text = Zapret.Title(s);
                    cfg.Strategy = s;
                    try { await StartBypass(true); }
                    catch { pickResults[s] = "не запустилась"; Log.Write("  " + Zapret.Title(s) + " — не запустилась"); continue; }
                    await Task.Delay(1000);
                    List<SiteResult> res = await Sites.Test(cfg.Telegram, false);
                    List<SiteResult> okRes = res.Where(r => r.Ok).ToList();
                    int avg = okRes.Count > 0 ? (int)okRes.Average(r => r.Ms) : 0;
                    pickResults[s] = okRes.Count + "/" + res.Count + (okRes.Count > 0 ? " · " + avg + " мс" : "");
                    Log.Write("  " + Zapret.Title(s) + " — " + string.Join(", ", res.Select(r => r.Name + ": " + (r.Ok ? "да" : "нет"))));
                    int score = okRes.Count * 100000 - avg;
                    if (best == null || score > best.Score)
                    {
                        best = new PickBest();
                        best.File = s; best.Ok = okRes.Count; best.Total = res.Count; best.Score = score;
                    }
                    if (stopAtFirstPerfect && okRes.Count == res.Count) break;
                }
            }
            finally { picking = false; }
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

        async Task StartAutoPickUi()
        {
            if (picking) { cancelPick = true; return; }
            if (Zapret.LocalVersion == null) return;
            string vpn = Conflicts.ActiveVpn();
            if (vpn != null && MessageBox.Show(window, "Интернет идёт через VPN (" + vpn + ").\n\nВыключите VPN на время подбора, иначе результат будет неверным. Всё равно продолжить?",
                "Zapret Launcher", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await RunBusy(async () =>
            {
                PickBest best = await AutoPick(false);
                if (best != null && best.Ok > 0) Balloon("Подбор завершён: " + Zapret.Title(best.File) + " (" + best.Ok + " из " + best.Total + " сайтов)");
            }, "ПОДБОР…");
        }

        // ---------- Пауза при VPN ----------
        async Task CheckVpn()
        {
            vpnName = Conflicts.ActiveVpn();
            if (!cfg.VpnPause || busy) return;
            if (vpnName != null && Zapret.Running && wantRunning)
            {
                await RunBusy(async () =>
                {
                    await Zapret.Stop(false);
                    await Telegram.StopProxy();
                    pausedByVpn = true;
                    siteResults = null;
                    BuildSitePanel();
                }, "ПАУЗА…");
                Balloon("Включён VPN (" + vpnName + ") — обход на паузе");
            }
            else if (vpnName == null && pausedByVpn && wantRunning)
            {
                Log.Write("VPN отключён — включаю обход");
                await StartBypassUi();
            }
        }

        // ---------- Профили сетей ----------
        void CheckNetwork()
        {
            NetworkId net = null;
            try { net = Networks.Current(); } catch { }
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
            CheckNetwork();
            string after = currentNet == null ? null : currentNet.Id;
            if (after == null || after == before || before == null) return;

            NetProfile p;
            if (cfg.NetworkProfilesEnabled && cfg.NetworkProfiles.TryGetValue(after, out p) && p.Strategy != cfg.Strategy && File.Exists(Paths.InZapret(p.Strategy)))
            {
                cfg.Strategy = p.Strategy;
                cfg.Save();
                UpdateStrategyList();
                Balloon(currentNet.Name + ": стратегия " + Zapret.Title(p.Strategy));
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
                ? "Сеть не определена"
                : "Сейчас: " + currentNet.Name + ". Лаунчер запоминает рабочую стратегию для каждой сети и переключается сам";
            StackPanel list = E<StackPanel>("NetListPanel");
            list.Children.Clear();
            foreach (KeyValuePair<string, NetProfile> kv in cfg.NetworkProfiles.OrderBy(k => k.Value.Name))
            {
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel info = new StackPanel();
                bool here = currentNet != null && kv.Key == currentNet.Id;
                info.Children.Add(new TextBlock { Text = kv.Value.Name + (here ? "  · сейчас" : ""), FontSize = 13 });
                info.Children.Add(new TextBlock { Text = "Стратегия: " + Zapret.Title(kv.Value.Strategy) + " · " + kv.Value.Updated, FontSize = 11, Foreground = B("#8A8A93") });
                row.Children.Add(info);
                Button del = new Button { Content = "\uE711", Tag = kv.Key, Style = (Style)window.FindResource("IconButton"), Width = 28, Height = 28, FontSize = 11, ToolTip = "Забыть сеть" };
                del.Click += delegate
                {
                    cfg.NetworkProfiles.Remove((string)del.Tag);
                    cfg.Save();
                    UpdateNetUi();
                };
                Grid.SetColumn(del, 1);
                row.Children.Add(del);
                list.Children.Add(new Border { Child = row, Padding = new Thickness(16, 8, 10, 8), BorderBrush = B("#2E2E35"), BorderThickness = new Thickness(0, 1, 0, 0) });
            }
            UpdateNetStatus();
        }

        void UpdateNetStatus()
        {
            string net = currentNet == null ? "сеть не определена" : currentNet.Name;
            Text("NetStatus").Text = pausedByVpn ? "Пауза: интернет идёт через VPN (" + (vpnName ?? "VPN") + ")" : net;
        }

        // ---------- Индикаторы сайтов ----------
        void BuildSitePanel()
        {
            WrapPanel panel = E<WrapPanel>("SitePanel");
            panel.Children.Clear();
            bool running = Zapret.Running;
            foreach (KeyValuePair<string, string> t in Sites.Targets(cfg.Telegram, false))
            {
                SiteResult r = siteResults == null ? null : siteResults.FirstOrDefault(x => x.Name == t.Key);
                string color, tip;
                if (siteChecking) { color = "#E0B040"; tip = "проверяю…"; }
                else if (!running || r == null) { color = "#5A5A66"; tip = running ? "ещё не проверялось" : "обход выключен"; }
                else if (r.Ok) { color = "#3DD68C"; tip = "открывается · " + r.Ms + " мс"; }
                else { color = "#FF6B6B"; tip = "не открывается"; }
                StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = B(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                content.Children.Add(new TextBlock { Text = t.Key, FontSize = 11, Foreground = B("#C8C8D0") });
                panel.Children.Add(new Border
                {
                    Child = content, CornerRadius = new CornerRadius(10), Background = B("#202027"),
                    BorderBrush = B("#2E2E35"), BorderThickness = new Thickness(1), Padding = new Thickness(9, 3, 10, 3),
                    Margin = new Thickness(3), ToolTip = t.Key + ": " + tip
                });
            }
        }

        // ---------- Конфликты ----------
        void UpdateConflicts()
        {
            List<Conflict> conflicts = Conflicts.All();
            if (cfg.VpnPause) conflicts = conflicts.Where(c => c.Kind != "vpn").ToList();   // с паузой VPN — штатная ситуация
            string text = string.Join("\n", conflicts.Select(c => c.Text));
            if (text != conflictText)
            {
                conflictText = text;
                foreach (Conflict c in conflicts) Log.Write("Внимание: " + c.Text);
            }
            Border bar = E<Border>("ConflictBar");
            Text("ConflictText").Text = text;
            bar.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            int top = 50;
            if (text.Length > 0) { bar.UpdateLayout(); top = Math.Min(110, Math.Max(50, (int)bar.ActualHeight + 22)); }
            E<Grid>("RingArea").Margin = new Thickness(0, top, 0, 0);
        }

        // ---------- Обновления ----------
        async Task InstallZapretUi()
        {
            await RunBusy(async () =>
            {
                if (latest == null) latest = await Util.LatestRelease(Consts.ZapretRepo, n => n.EndsWith(".zip"));
                await Zapret.Install(latest, cfg, s => StartBypass(false));
                UpdateStrategyList();
                UpdateTitle();
            }, "ОБНОВЛЕНИЕ…");
        }

        async Task CheckUpdates(bool quiet, bool allowAuto)
        {
            latest = await Util.LatestRelease(Consts.ZapretRepo, n => n.EndsWith(".zip"));
            lastCheck = DateTime.Now;
            string local = Zapret.LocalVersion;
            if (Util.TestNewer(latest.Version, local))
            {
                bool skipped = allowAuto && cfg.SkipVersion == latest.Version;
                if (allowAuto && cfg.AutoUpdate && !skipped)
                {
                    Log.Write("Доступна новая версия zapret: " + latest.Version + " (установлена: " + local + ")");
                    await Zapret.Install(latest, cfg, s => StartBypass(false));
                    UpdateStrategyList();
                    UpdateTitle();
                    Balloon("zapret обновлён до версии " + latest.Version);
                }
                else if (!skipped || !quiet) Balloon("Доступна новая версия zapret: " + latest.Version + (skipped ? " (пропущена после отката)" : ""));
            }
            else if (!quiet) Log.Write("zapret: установлена последняя версия (" + local + ")");

            if (cfg.Telegram)
            {
                try { await Telegram.UpdateHostsCache(); Telegram.SetHosts(true); }
                catch (Exception e) { Log.Write("Не удалось обновить адреса Telegram: " + e.Message); }
                string tgLocal = Telegram.ProxyVersion;
                if (tgLocal != null)
                {
                    try
                    {
                        Release tg = await Telegram.LatestProxy();
                        if (Util.TestNewer(tg.Version, tgLocal))
                        {
                            if (allowAuto && cfg.AutoUpdate) { await Telegram.InstallProxy(tg); Balloon("tg-ws-proxy обновлён до версии " + tg.Version); }
                            else Balloon("Доступна новая версия tg-ws-proxy: " + tg.Version);
                        }
                        else if (!quiet) Log.Write("tg-ws-proxy: установлена последняя версия (" + tgLocal + ")");
                    }
                    catch (Exception e) { Log.Write("tg-ws-proxy: не удалось проверить обновления: " + e.Message); }
                }
            }

            await CheckLauncherUpdate(quiet, allowAuto && cfg.AutoUpdate);
        }

        async Task CheckLauncherUpdate(bool quiet, bool install)
        {
            try
            {
                launcherLatest = await SelfUpdate.Latest(cfg.LauncherRepo);
                if (!Util.TestNewer(launcherLatest.Version, Consts.Version))
                {
                    if (!quiet) Log.Write("Лаунчер: установлена последняя версия (" + Consts.Version + ")");
                    return;
                }
                if (!install) { Balloon("Доступна новая версия лаунчера: " + launcherLatest.Version); return; }
                await SelfUpdate.Apply(launcherLatest);
                Restart();
            }
            catch (Exception e) { if (!quiet) Log.Write("Лаунчер: не удалось проверить обновления (" + cfg.LauncherRepo + "): " + e.Message); }
        }

        // Фоновые обновления — под флагом busy, чтобы сторож не перезапускал обход посреди установки
        async Task<bool> BackgroundUpdate()
        {
            if (busy) return false;
            busy = true;
            try { await CheckUpdates(true, true); return true; }
            catch (Exception e) { Log.Write("Не удалось проверить обновления: " + e.Message); return false; }
            finally { busy = false; UpdateUi(); }
        }

        void Restart()
        {
            App.ReleaseMutex();
            Process.Start(Paths.Exe, (window.IsVisible ? "" : "--autostart ") + "--restarted");
            Exit();
        }

        // ---------- Интерфейс ----------
        void UpdateTitle()
        {
            string v = Zapret.LocalVersion;
            Text("TitleText").Text = "Zapret Launcher " + Consts.Version + (v != null ? "  ·  zapret " + v : "");
            Text("LauncherVersionText").Text = "Zapret Launcher " + Consts.Version + " — проверить обновление";
            Text("LauncherRepoText").Text = "Обновления лаунчера: github.com/" + cfg.LauncherRepo;
        }

        void UpdateStrategyList()
        {
            filling = true;
            string query = E<TextBox>("SearchBox").Text.Trim();
            List<StrategyItem> items = Zapret.Strategies().Select(ToItem)
                .Where(i => query.Length == 0 || i.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || i.File.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            ListBox list = E<ListBox>("StrategyList");
            list.ItemsSource = items;
            list.SelectedItem = items.FirstOrDefault(i => i.File == cfg.Strategy);
            filling = false;
            Text("CurrentBadge").Text = Zapret.Badge(cfg.Strategy);
            E<Border>("CurrentBadgeBg").Background = B(Zapret.Color(cfg.Strategy));
            Text("CurrentName").Text = Zapret.Title(cfg.Strategy);
        }

        StrategyItem ToItem(string file)
        {
            StrategyItem i = new StrategyItem();
            i.File = file; i.Title = Zapret.Title(file); i.Badge = Zapret.Badge(file); i.Color = Zapret.Color(file);
            string r;
            i.Subtitle = pickResults.TryGetValue(file, out r) ? file + "  ·  " + r : file;
            return i;
        }

        void SetPulse(bool on)
        {
            if (on == pulseOn) return;
            pulseOn = on;
            Ellipse circle = E<Ellipse>("PowerCircle");
            Ellipse ring = E<Ellipse>("OuterRing");
            DropShadowEffect glow = (DropShadowEffect)circle.Effect;
            if (on)
            {
                DoubleAnimation anim = new DoubleAnimation(0.25, 0.9, new Duration(TimeSpan.FromSeconds(1.8)));
                anim.AutoReverse = true;
                anim.RepeatBehavior = RepeatBehavior.Forever;
                ring.BeginAnimation(UIElement.OpacityProperty, anim);
                glow.Opacity = 0.75;
                circle.Stroke = B("#6A5EE0");
                Text("PowerIcon").Foreground = B("#9C90FF");
            }
            else
            {
                ring.BeginAnimation(UIElement.OpacityProperty, null);
                ring.Opacity = 0.15;
                glow.Opacity = 0;
                circle.Stroke = B("#3A3A48");
                Text("PowerIcon").Foreground = B("#8A8A93");
            }
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
                    Text("PowerStatus").Text = Zapret.ServiceRunning ? "ПОДКЛЮЧЁН · СЛУЖБА" : "ПОДКЛЮЧЁН";
                    string timer = " ";
                    try { TimeSpan t = DateTime.Now - proc.StartTime; timer = ((int)t.TotalHours).ToString("00") + ":" + t.ToString(@"mm\:ss"); } catch { }
                    Text("PowerTimer").Text = timer;
                }
                else if (pausedByVpn) { Text("PowerStatus").Text = "ПАУЗА · VPN"; Text("PowerTimer").Text = vpnName ?? " "; }
                else { Text("PowerStatus").Text = local != null ? "ОТКЛЮЧЁН" : "НЕ УСТАНОВЛЕН"; Text("PowerTimer").Text = " "; }
            }
            SetPulse(running);
            if (running != trayActive || tray.Icon == null)
            {
                trayActive = running;
                tray.Icon = running ? iconOn : iconOff;
                BuildSitePanel();
            }

            Text("VersionPill").Text = local != null ? "zapret v" + local : "zapret не установлен";
            TextBlock lt = Text("LatestText");
            lt.Text = hasUpdate ? "Доступна: " + latest.Version : latest != null ? "Последняя версия" : "Не проверено";
            lt.Foreground = B(hasUpdate ? "#9C90FF" : "#EDEDF0");
            Text("CardSubtitle").Text = (lastCheck.HasValue ? lastCheck.Value.ToString("dd.MM.yyyy HH:mm") : "не проверялось") + " | " +
                (cfg.AutoUpdate ? "Автообновление - 6ч." : "Автообновление выкл.");
            E<Button>("BtnInstallUpdate").Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
            if (!picking) E<Button>("BtnMain").Content = "Подобрать стратегию";
            E<Button>("BtnRemoveService").Visibility = Zapret.Service() != null ? Visibility.Visible : Visibility.Collapsed;

            string old = Zapret.VersionOf(Paths.Backup);
            E<Button>("BtnRollback").Visibility = old != null && old != local ? Visibility.Visible : Visibility.Collapsed;
            Text("RollbackTitle").Text = Util.TestNewer(old, local) ? "Вернуть zapret версии " + old + " (отменить откат)" : "Откатить zapret на версию " + old;
            string tgv = Telegram.ProxyVersion;
            Text("TgSub").Text = "Desktop — прокси tg-ws-proxy" + (tgv != null ? " v" + tgv : "") + ", Web — через hosts";
            if (launcherLatest != null && Util.TestNewer(launcherLatest.Version, Consts.Version))
                Text("LauncherVersionText").Text = "Zapret Launcher " + Consts.Version + " — доступна " + launcherLatest.Version + ", обновить";
            UpdateNetStatus();

            miStart.Enabled = local != null && !busy;
            miStop.Enabled = (running || pausedByVpn) && !busy;
            miPick.Enabled = local != null && (!busy || picking);
            miPick.Text = picking ? "Остановить подбор" : "Подобрать стратегию";
            string trayText = running ? "Zapret: работает · " + Zapret.Title(cfg.Strategy) : pausedByVpn ? "Zapret: пауза (VPN)" : "Zapret: остановлен";
            tray.Text = trayText.Length > 63 ? trayText.Substring(0, 63) : trayText;
        }

        void Balloon(string text)
        {
            Log.Write(text);
            if (!window.IsVisible) tray.ShowBalloonTip(5000, "Zapret Launcher", text, Forms.ToolTipIcon.Info);
        }

        void ShowPage(string name)
        {
            foreach (string p in new[] { "Strategies", "Domains", "Settings", "Log" })
                E<FrameworkElement>("Page" + p).Visibility = p == name ? Visibility.Visible : Visibility.Collapsed;
            if (name == "Domains") UpdateDomainUi();
            if (name == "Settings") UpdateFilterUi();
        }

        string DomainKind { get { return E<RadioButton>("DomGeneral").IsChecked == true ? "general" : "exclude"; } }

        void UpdateDomainUi()
        {
            if (!Directory.Exists(Paths.Zapret)) return;
            List<string> domains = Zapret.UserDomains(DomainKind);
            E<ListBox>("DomainList").ItemsSource = domains;
            TextBlock empty = Text("DomainEmpty");
            empty.Visibility = domains.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            empty.Text = DomainKind == "general" ? "Своих доменов пока нет — добавьте сайт, который нужно разблокировать"
                                                  : "Исключений нет — сюда добавляют сайты, которые ломаются от обхода";
        }

        async Task AddDomain()
        {
            string raw = E<TextBox>("DomainInput").Text;
            if (raw.Trim().Length == 0) return;
            List<string> added = new List<string>(), bad = new List<string>();
            foreach (string part in raw.Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string d = Zapret.NormalizeDomain(part);
                if (d != null) added.Add(d); else bad.Add(part);
            }
            List<string> current = Zapret.UserDomains(DomainKind);
            List<string> fresh = added.Where(d => !current.Contains(d)).Distinct().ToList();
            TextBlock info = Text("DomainInfo");
            info.Text = bad.Count > 0 ? "Не похоже на домен: " + string.Join(", ", bad)
                      : fresh.Count == 0 && added.Count > 0 ? "Этот домен уже есть в списке"
                      : "Поддомены учитываются автоматически. Изменения применяются сразу.";
            if (fresh.Count == 0) return;
            Zapret.SaveUserDomains(DomainKind, current.Concat(fresh));
            Log.Write("Добавлено в «" + (DomainKind == "general" ? "Обходить" : "Не трогать") + "»: " + string.Join(", ", fresh));
            E<TextBox>("DomainInput").Text = "";
            UpdateDomainUi();
            await RestartIfRunning();
        }

        async Task RemoveDomain(string domain)
        {
            Zapret.SaveUserDomains(DomainKind, Zapret.UserDomains(DomainKind).Where(d => d != domain));
            Log.Write("Удалён домен: " + domain);
            UpdateDomainUi();
            await RestartIfRunning();
        }

        void UpdateFilterUi()
        {
            if (!Directory.Exists(Paths.Zapret)) return;
            string gf = Zapret.ReadGameFilter().Mode;
            E<RadioButton>("GfOff").IsChecked = gf != "tcp" && gf != "udp" && gf != "all";
            E<RadioButton>("GfTcp").IsChecked = gf == "tcp";
            E<RadioButton>("GfUdp").IsChecked = gf == "udp";
            E<RadioButton>("GfAll").IsChecked = gf == "all";
            string ip = Zapret.IpsetMode(Paths.Zapret);
            E<RadioButton>("IpNone").IsChecked = ip == "none";
            E<RadioButton>("IpLoaded").IsChecked = ip == "loaded";
            E<RadioButton>("IpAny").IsChecked = ip == "any";
        }

        void InitSwitches()
        {
            filling = true;
            bool auto = false;
            try { auto = Autostart.Enabled; } catch { }
            E<CheckBox>("SwAutostart").IsChecked = auto;
            E<ToggleButton>("ChipAutostart").IsChecked = auto;
            E<CheckBox>("SwAutoUpdate").IsChecked = cfg.AutoUpdate;
            E<ToggleButton>("ChipAutoUpdate").IsChecked = cfg.AutoUpdate;
            E<CheckBox>("SwStartOnOpen").IsChecked = cfg.StartOnOpen;
            E<CheckBox>("SwWatchdog").IsChecked = cfg.Watchdog;
            E<CheckBox>("SwVpnPause").IsChecked = cfg.VpnPause;
            E<CheckBox>("SwNetProfiles").IsChecked = cfg.NetworkProfilesEnabled;
            E<CheckBox>("SwTelegram").IsChecked = cfg.Telegram;
            E<ToggleButton>("ChipTelegram").IsChecked = cfg.Telegram;
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
            }, on ? "TELEGRAM…" : null);
        }

        async Task SelectStrategy(string file)
        {
            if (file == cfg.Strategy) return;
            cfg.Strategy = file;
            cfg.Save();
            UpdateStrategyList();
            if (Zapret.Running) await StartBypassUi();
        }

        // ---------- События ----------
        void Click(string name, Func<Task> handler) { E<ButtonBase>(name).Click += async delegate { await handler(); }; }
        void ClickSync(string name, Action handler) { E<ButtonBase>(name).Click += delegate { handler(); }; }

        void WireEvents()
        {
            ClickSync("BtnMinimize", () => { window.WindowState = WindowState.Minimized; });
            ClickSync("BtnClose", () => window.Close());
            foreach (string p in new[] { "Strategies", "Domains", "Settings", "Log" })
            {
                string page = p;
                E<RadioButton>("Nav" + p).Checked += delegate { ShowPage(page); };
            }
            ClickSync("NavFolder", () => Process.Start("explorer.exe", "\"" + Paths.Zapret + "\""));
            ClickSync("BtnFolder2", () => Process.Start("explorer.exe", "\"" + Paths.Zapret + "\""));
            ClickSync("NavInfo", () => Process.Start("https://github.com/" + cfg.LauncherRepo));

            Grid power = E<Grid>("PowerButton");
            power.MouseEnter += delegate { ((ScaleTransform)power.RenderTransform).ScaleX = ((ScaleTransform)power.RenderTransform).ScaleY = 1.03; };
            power.MouseLeave += delegate { ((ScaleTransform)power.RenderTransform).ScaleX = ((ScaleTransform)power.RenderTransform).ScaleY = 1; };
            power.MouseLeftButtonUp += async delegate
            {
                if (picking) cancelPick = true;
                else if (Zapret.Running || pausedByVpn) await StopBypassUi();
                else if (Zapret.LocalVersion != null) await StartBypassUi();
                else await InstallZapretUi();
            };
            E<WrapPanel>("SitePanel").MouseLeftButtonUp += async delegate { if (Zapret.Running && !siteChecking && !busy) await SiteCheck(false); };

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

            Click("BtnCheck", () => RunBusy(() => CheckUpdates(false, false), "ПРОВЕРКА…"));
            Click("BtnInstallUpdate", InstallZapretUi);
            Click("BtnMain", StartAutoPickUi);
            Click("BtnPick", StartAutoPickUi);
            ClickSync("BtnService", () =>
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + Paths.InZapret("service.bat") + "\" admin\"");
                psi.WorkingDirectory = Paths.Zapret;
                psi.UseShellExecute = false;
                psi.EnvironmentVariables["NO_UPDATE_CHECK"] = "1";
                Process.Start(psi);
            });
            Click("BtnRemoveService", () => RunBusy(async () => { await Zapret.RemoveService(); await Zapret.Stop(true); }));
            Click("BtnRollback", async () =>
            {
                if (MessageBox.Show(window, "Вернуть zapret " + Zapret.VersionOf(Paths.Backup) + " вместо " + Zapret.LocalVersion + "?\nВаши списки и фильтры сохранятся.",
                    "Zapret Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await RunBusy(async () => { await Zapret.Rollback(cfg, s => StartBypass(false)); UpdateStrategyList(); UpdateTitle(); }, "ОТКАТ…");
            });
            Click("BtnUninstall", async () =>
            {
                MessageBoxResult a = MessageBox.Show(window,
                    "Остановить обход и убрать автозапуск, службы WinDivert, записи Telegram в hosts и ярлыки?\n\nДа — удалить и папку " + Paths.Root + " целиком\nНет — оставить файлы\nОтмена — ничего не делать",
                    "Zapret Launcher — удаление", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (a == MessageBoxResult.Cancel) return;
                await RunBusy(async () => { await Uninstaller.Remove(a == MessageBoxResult.Yes); Exit(); }, "УДАЛЕНИЕ…");
            });
            Click("BtnLauncherUpdate", () => RunBusy(async () =>
            {
                launcherLatest = await SelfUpdate.Latest(cfg.LauncherRepo);
                if (!Util.TestNewer(launcherLatest.Version, Consts.Version)) { MessageBox.Show(window, "Установлена последняя версия лаунчера (" + Consts.Version + ").", "Zapret Launcher"); return; }
                if (MessageBox.Show(window, "Доступна версия " + launcherLatest.Version + ". Обновить лаунчер сейчас? Обход продолжит работать.",
                    "Zapret Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await SelfUpdate.Apply(launcherLatest);
                Restart();
            }, "ПРОВЕРКА…"));

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
            Click("BtnIpsetUpdate", async () => { await RunBusy(Zapret.UpdateIpsetList, "ОБНОВЛЕНИЕ…"); await RestartIfRunning(); });

            // домены
            ClickSync("DomGeneral", UpdateDomainUi);
            ClickSync("DomExclude", UpdateDomainUi);
            Click("BtnAddDomain", AddDomain);
            TextBox input = E<TextBox>("DomainInput");
            input.KeyDown += async (s, e) => { if (e.Key == Key.Return) await AddDomain(); };
            input.TextChanged += delegate { Text("DomainHint").Visibility = input.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; };
            E<ListBox>("DomainList").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(async (s, e) =>
            {
                Button b = e.OriginalSource as Button;
                if (b != null && b.Tag is string) await RemoveDomain((string)b.Tag);
            }));

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
                UpdateConflicts();
                if (!cfg.VpnPause && pausedByVpn) await StartBypassUi();
            });
            ClickSync("SwNetProfiles", () =>
            {
                cfg.NetworkProfilesEnabled = E<CheckBox>("SwNetProfiles").IsChecked == true;
                cfg.Save();
                Log.Write(cfg.NetworkProfilesEnabled ? "Профили сетей включены" : "Профили сетей выключены");
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
            }, "TELEGRAM…"));

            window.Closing += (s, e) =>
            {
                if (exiting) return;
                e.Cancel = true;
                window.Hide();
                if (!hintShown)
                {
                    tray.ShowBalloonTip(4000, "Zapret Launcher", "Лаунчер свёрнут в трей. Выход — через меню значка.", Forms.ToolTipIcon.Info);
                    hintShown = true;
                }
            };
        }

        void BuildTray()
        {
            iconOn = Icons.ToIcon(Icons.Draw(32, true));
            iconOff = Icons.ToIcon(Icons.Draw(32, false));
            tray = new Forms.NotifyIcon();
            tray.Icon = iconOff;
            tray.Text = "Zapret Launcher";
            tray.Visible = true;
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = System.Drawing.Color.FromArgb(35, 35, 40);
            menu.ShowImageMargin = false;
            menu.Font = new System.Drawing.Font("Segoe UI", 9.5f);
            miOpen = new Forms.ToolStripMenuItem("Открыть");
            miOpen.Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
            miStart = new Forms.ToolStripMenuItem("Включить обход");
            miStop = new Forms.ToolStripMenuItem("Выключить обход");
            miStrategies = new Forms.ToolStripMenuItem("Стратегия");
            miStrategies.DropDown.Renderer = new DarkMenuRenderer();
            miStrategies.DropDown.BackColor = menu.BackColor;
            miPick = new Forms.ToolStripMenuItem("Подобрать стратегию");
            miExit = new Forms.ToolStripMenuItem("Выход (обход продолжит работать)");
            menu.Items.AddRange(new Forms.ToolStripItem[] { miOpen, miStart, miStop, miStrategies, miPick, new Forms.ToolStripSeparator(), miExit });
            tray.ContextMenuStrip = menu;

            miOpen.Click += delegate { ShowWindow(); };
            miStart.Click += async delegate { await StartBypassUi(); };
            miStop.Click += async delegate { await StopBypassUi(); };
            miPick.Click += async delegate { ShowWindow(); await StartAutoPickUi(); };
            miExit.Click += delegate { Exit(); };
            tray.DoubleClick += delegate { ShowWindow(); };
            menu.Opening += delegate
            {
                miStrategies.DropDownItems.Clear();
                foreach (string s in Zapret.Strategies())
                {
                    Forms.ToolStripMenuItem item = new Forms.ToolStripMenuItem(Zapret.Title(s));
                    item.Checked = s == cfg.Strategy;
                    string file = s;
                    item.Click += async delegate { await SelectStrategy(file); };
                    miStrategies.DropDownItems.Add(item);
                }
                UpdateUi();
            };
        }

        public void Exit()
        {
            exiting = true;
            foreach (DispatcherTimer t in new[] { statusTimer, updateTimer, healthTimer }) if (t != null) t.Stop();
            tray.Visible = false;
            tray.Dispose();
            window.Close();
            Application.Current.Shutdown();
        }
    }
}
