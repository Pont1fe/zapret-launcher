// Главное окно, мини-окно у трея, таймеры: запуск обхода, подбор стратегии, сторож, пауза при VPN, профили сетей, уведомления
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

    class Launcher
    {
        readonly bool autostart;
        readonly Config cfg;
        Window window, flyout;
        Forms.NotifyIcon tray;
        Forms.ToolStripMenuItem miOpen, miStart, miStop, miStrategies, miPick, miFixDiscord, miVpn, miExit;
        Dictionary<string, System.Drawing.Icon> trayIcons;
        Dictionary<string, ImageSource> windowIcons;   // значок окна и панели задач — того же цвета, что в трее
        Dictionary<string, ImageSource> imageIcons;    // тот же значок картинкой — в заголовке и мини-окне
        string trayState, cardState;
        DateTime historyMinute;
        bool animateCards;                              // следующая сборка карточек сайтов — с появлением по очереди
        DispatcherTimer statusTimer, updateTimer;

        // состояние
        bool busy, picking, cancelPick, exiting, hintShown, filling, trayActive, siteChecking, netChanged = true;
        bool? visualActive;
        bool wantRunning;        // пользователь хочет, чтобы обход работал
        bool pausedByVpn;        // обход выключен, пока включён VPN
        bool crashed;            // сторож не смог перезапустить winws — значок в трее красный, пока обход не включат
        int tick, failStreak, onboardingStep;
        int? baseline;           // сколько сайтов открывала лучшая стратегия (если какие-то недоступны всегда — не дёргаемся зря)
        DateTime lastRestart = DateTime.MinValue, siteCheckDue = DateTime.MaxValue, flyoutHiddenAt = DateTime.MinValue;
        DateTime healthDue = DateTime.MaxValue, trimDue = DateTime.MaxValue;   // следующая фоновая проверка сайтов; когда отдать память системе
        Release latest, launcherLatest;
        DateTime? lastCheck;
        string conflictText = "", vpnName;
        NetworkId currentNet;
        List<SiteResult> siteResults;
        readonly Dictionary<string, List<int>> siteHistory = new Dictionary<string, List<int>>();   // HTTPS-время последних проверок (-1 — не открылся)

        static Brush B(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        static Brush R(string key) { return Theme.Brush(key); }
        static string T(string s) { return I18n.T(s); }

        T E<T>(string name) where T : class { return window.FindName(name) as T; }
        TextBlock Text(string name) { return E<TextBlock>(name); }
        T F<T>(string name) where T : class { return flyout == null ? null : flyout.FindName(name) as T; }

        static Window LoadWindow(string resource)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (StreamReader r = new StreamReader(s))
                return (Window)XamlReader.Parse(I18n.TranslateXaml(r.ReadToEnd()));
        }

        public Launcher(bool autostart, Config cfg)
        {
            this.autostart = autostart;
            this.cfg = cfg;
            window = LoadWindow("ui.xaml");
            window.SourceInitialized += delegate { Theme.ApplyWindow(window); };
            Toasts.Init();
            BuildTray();
            WireEvents();
            Log.Line += line => window.Dispatcher.BeginInvoke(new Action(() =>
            {
                TextBox box = E<TextBox>("LogBox");
                box.AppendText(line + "\r\n");
                box.ScrollToEnd();
            }));
            try { foreach (string line in File.ReadLines(Paths.Log).Reverse().Take(200).Reverse()) E<TextBox>("LogBox").AppendText(line + "\r\n"); } catch { }

            windowIcons = new Dictionary<string, ImageSource>();
            imageIcons = new Dictionary<string, ImageSource>();
            foreach (string state in new[] { "on", "off", "pause", "error" })
            {
                windowIcons[state] = Icons.WindowIcon(state);
                imageIcons[state] = Icons.ToImageSource(Icons.Draw(48, state));   // для заголовка и мини-окна — с запасом под масштаб экрана
            }
            window.Icon = windowIcons["off"];
            E<Image>("TitleIcon").Source = imageIcons["off"];

            if (I18n.English) Text("PingText").Text = "Refresh";   // «Обновить» в остальных местах — это Update
            StateHistory.Load();
            RestoreWindowSize();
            ApplyScale();
            InitSwitches();
            UpdateTitle();
            if (Zapret.Running) wantRunning = true;
            UpdateStrategyList();
            BuildSitePanel();
            UpdateUi();
            if (!autostart) { window.Show(); BuildSitePanel(); }   // до показа окна карточки не собираются

            // смена сети и пробуждение — сразу перепроверяем VPN, сеть и сайты, не дожидаясь таймеров
            NetworkChange.NetworkAddressChanged += delegate { netChanged = true; };
            NetworkChange.NetworkAvailabilityChanged += delegate { netChanged = true; };
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == PowerModes.Resume) window.Dispatcher.BeginInvoke(new Action(OnResume));
            };
            window.IsVisibleChanged += delegate
            {
                if (window.IsVisible)
                {
                    BuildSitePanel(); UpdateUi(); RenderHistory();
                    if (whatsNewPending) window.Dispatcher.BeginInvoke(new Action(ShowWhatsNew), DispatcherPriority.ApplicationIdle);
                }
                else { trimDue = DateTime.Now.AddSeconds(20); SaveWindowSize(); AutoBackup(); }
            };
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (cfg.Theme == "system" && e.Category == UserPreferenceCategory.General)
                    window.Dispatcher.BeginInvoke(new Action(ApplyTheme));
            };
            DispatcherTimer startup = new DispatcherTimer();
            startup.Interval = TimeSpan.FromMilliseconds(400);
            startup.Tick += async delegate { startup.Stop(); await Startup(); };
            startup.Start();
        }

        public void ShowWindow()
        {
            HideFlyout();
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
            // schtasks и WMI небыстрые — опрашиваем их в фоне, окно уже на экране
            bool autoOn = false;
            try { autoOn = await Task.Run(() => Autostart.EnsureCurrent()); } catch { }   // путь мог измениться (переезд папки)
            filling = true;
            E<CheckBox>("SwAutostart").IsChecked = autoOn;
            E<ToggleButton>("ChipAutostart").IsChecked = autoOn;
            filling = false;
            try { Shell.MigrateShortcuts(); Shell.UpdateUninstallEntry(); } catch (Exception e) { Log.Write("Ярлыки: " + e.Message); }
            foreach (string legacy in new[] { "ZapretLauncher.ps1", "Start.bat", "launcher.ico" })
                try { File.Delete(System.IO.Path.Combine(Paths.Root, legacy)); } catch { }
            await UpdateConflicts();
            await CheckNetwork();
            CheckHosts();

            bool installedNow = false;
            if (Zapret.LocalVersion == null)
            {
                Log.Write("zapret ещё не установлен — скачиваю последнюю версию");
                await InstallZapretUi();
                installedNow = true;
            }
            UpdateStrategyList();
            // новая установка, а от прошлой осталась автокопия настроек — предлагаем вернуть
            if (Config.Fresh && !autostart && OfferAutoRestore()) return;
            backupReady = true;   // до этого момента автокопию не трогаем: свежие настройки затёрли бы старые
            AutoBackup();
            // «Что нового» — после обновления лаунчера; новой установке не показываем
            if (cfg.LastSeenVersion != Consts.Version) Icons.RefreshShellIcons();   // новая версия могла принести новый значок
            // у обновившихся с 3.x поля LastSeenVersion ещё нет (пустое) — им тоже показываем
            if (Config.Fresh) { cfg.LastSeenVersion = Consts.Version; cfg.Save(); }
            else if (cfg.LastSeenVersion != Consts.Version) { if (window.IsVisible) ShowWhatsNew(); else whatsNewPending = true; }
            if (!cfg.OnboardingDone && !autostart) ShowOnboarding();

            try { TaskbarMenu.EnsureHelper(); UpdateTaskbarMenu(); } catch (Exception e) { Log.Write("Меню в панели задач: " + e.Message); }
            WatchCommands();

            // при входе в Windows сеть поднимается не сразу — ждём интернет, иначе первая проверка ошибочно покажет сбой
            if (autostart && Zapret.LocalVersion != null && !Zapret.Running) await WaitForInternet(90);
            if ((autostart || cfg.StartOnOpen) && Zapret.LocalVersion != null && !Zapret.Running) await StartBypassUi();
            else if (cfg.Telegram && Zapret.Running && !Telegram.ProxyRunning) await RunBusy(() => Telegram.StartProxy(cfg), "Telegram…");
            if (Zapret.Running) siteCheckDue = DateTime.Now.AddSeconds(3);

            // VPN был включён до перезапуска лаунчера или Windows — включаем снова
            vpnRunning = Vpn.Running;
            if (vpnRunning) { Vpn.ReadApiEndpoint(); vpnConnectedAt = Vpn.StartedAt(); }   // лаунчер перезапустился, а VPN работал
            if (cfg.VpnOn && !vpnRunning && CurrentServer != null)
            {
                try { await StartVpn(true); Log.Write("VPN подключён: " + CurrentServer.Name); }
                catch (Exception e) { Log.Write("VPN не подключился: " + e.Message); }
            }
            RefreshVpnUi();

            updateTimer = Timer(TimeSpan.FromHours(6), async delegate
            {
                bool ok = await BackgroundUpdate();
                updateTimer.Interval = ok ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(5);
            });
            if (!installedNow && !await BackgroundUpdate()) updateTimer.Interval = TimeSpan.FromMinutes(5);
            healthDue = DateTime.Now.Add(HealthInterval());
            statusTimer = Timer(TimeSpan.FromSeconds(1), async delegate { await OnTick(); });
            // после самообновления старый exe ещё может быть занят завершающимся процессом — пробуем удалить ещё несколько раз
            for (int i = 0; i < 10 && File.Exists(Paths.Exe + ".old"); i++) { await Task.Delay(1000); SelfUpdate.CleanupOld(); }
            UpdateTitle();
            UpdateUi();
        }

        // ---------- Встроенный VPN ----------
        bool vpnRunning;                 // обновляется в тике: опрос процессов ядра недешёвый
        int vpnFails;
        DateTime vpnLastStart = DateTime.MinValue;

        VpnServer CurrentServer { get { return cfg.VpnServers.Count == 0 ? null : cfg.VpnServers[Math.Min(cfg.VpnSelected, cfg.VpnServers.Count - 1)]; } }
        bool VpnSitesKind { get { return E<RadioButton>("VpnKindSites").IsChecked == true; } }

        // ---------- Пинг, автовыбор сервера, трафик ----------
        readonly Dictionary<string, int> vpnPing = new Dictionary<string, int>();   // ссылка → мс (-1 нет ответа, -2 UDP)
        bool vpnPinging;
        int vpnDeadChecks;
        DateTime vpnHealthDue = DateTime.MinValue;
        long[] vpnTrafficLast;
        DateTime vpnTrafficAt;
        string vpnSpeedText = "";

        async Task PingVpnServers()
        {
            if (vpnPinging || cfg.VpnServers.Count == 0) return;
            vpnPinging = true;
            Text("VpnInfo").Text = T("Проверяю серверы: открываю сайт через каждый…");
            try
            {
                List<VpnServer> list = cfg.VpnServers.ToList();
                int[] ms = await Vpn.Delays(list);   // настоящий запрос через каждый сервер, а не просто соединение
                for (int i = 0; i < list.Count; i++) vpnPing[list[i].Link] = ms[i];
                int ok = ms.Count(m => m >= 0);
                Text("VpnInfo").Text = T("Работают серверов") + ": " + ok + " " + T("из") + " " + list.Count + ". " + T("Время — сколько открывается сайт через сервер");
            }
            finally { vpnPinging = false; }
            RefreshVpnUi();
        }

        // Индекс самого быстрого отвечающего сервера (UDP-серверы — в конце очереди); -1 — никто не ответил
        int BestServer(int exclude = -1)
        {
            int best = -1, bestMs = int.MaxValue;
            for (int i = 0; i < cfg.VpnServers.Count; i++)
            {
                if (i == exclude) continue;
                int ms;
                if (!vpnPing.TryGetValue(cfg.VpnServers[i].Link, out ms) || ms == -1) continue;
                int rank = ms == Vpn.PingUdp ? 100000 : ms;
                if (rank < bestMs) { best = i; bestMs = rank; }
            }
            return best;
        }

        async Task SelectBestServer()
        {
            await PingVpnServers();
            int best = BestServer();
            if (best < 0) { Text("VpnInfo").Text = T("Ни один сервер не ответил — проверьте интернет или подписку"); return; }
            if (best == cfg.VpnSelected) return;
            cfg.VpnSelected = best;
            Log.Write("VPN: выбран самый быстрый сервер — " + CurrentServer.Name);
            await ApplyVpnChanges();
        }

        // Автовыбор: текущий сервер дважды подряд не ответил — переходим на самый быстрый из отвечающих
        async Task VpnHealth()
        {
            if (!cfg.VpnAutoBest || !vpnRunning || busy || CurrentServer == null || DateTime.Now < vpnHealthDue) return;
            vpnHealthDue = DateTime.Now.AddMinutes(1);
            int ms = await Vpn.CurrentDelay();   // сайт через VPN открывается?
            if (ms != -1) { vpnDeadChecks = 0; return; }
            if (++vpnDeadChecks < 2 || cfg.VpnServers.Count < 2) return;
            vpnDeadChecks = 0;
            await PingVpnServers();
            int best = BestServer(cfg.VpnSelected);
            if (best < 0) return;
            string from = CurrentServer.Name;
            cfg.VpnSelected = best;
            Notify("VPN: сервер «" + from + "» не отвечает — переключаюсь на «" + CurrentServer.Name + "»");
            await ApplyVpnChanges();
        }

        // Скорость и объём трафика через VPN — раз в пару секунд, пока это видно
        async Task UpdateVpnTraffic()
        {
            long[] t = vpnRunning ? await Vpn.Traffic() : null;
            if (t == null) { vpnTrafficLast = null; vpnSpeedText = ""; }
            else
            {
                DateTime now = DateTime.Now;
                if (vpnTrafficLast != null)
                {
                    double sec = Math.Max(0.5, (now - vpnTrafficAt).TotalSeconds);
                    double down = Math.Max(0, t[0] - vpnTrafficLast[0]) * 8 / sec / 1e6, up = Math.Max(0, t[1] - vpnTrafficLast[1]) * 8 / sec / 1e6;
                    vpnSpeedText = "↓ " + Mbps(down) + "  ↑ " + Mbps(up) + "  ·  " + T("за сессию") + " " + Bytes(t[0] + t[1]);
                    vpnSpeeds.Add(down);
                    if (vpnSpeeds.Count > 60) vpnSpeeds.RemoveAt(0);
                }
                vpnTrafficLast = t;
                vpnTrafficAt = now;
            }
            TextBlock traffic = Text("VpnTraffic");
            traffic.Text = vpnSpeedText;
            traffic.Visibility = vpnSpeedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateVpnTimer();
            DrawVpnGraph();
            UpdatePowerVpn();
        }

        // Время подключения: «Подключено 1:23:45»
        DateTime? vpnConnectedAt;
        readonly List<double> vpnSpeeds = new List<double>();   // скорость загрузки за последние ~2 минуты, Мбит/с

        void UpdateVpnTimer()
        {
            TextBlock timer = Text("VpnTimer");
            if (!vpnRunning || !vpnConnectedAt.HasValue) { timer.Visibility = Visibility.Collapsed; return; }
            TimeSpan d = DateTime.Now - vpnConnectedAt.Value;
            timer.Text = T("Подключено") + " " + (int)d.TotalHours + ":" + d.ToString(@"mm\:ss");
            timer.Visibility = Visibility.Visible;
        }

        // График скорости загрузки через VPN: линия с заливкой под ней
        void DrawVpnGraph()
        {
            Canvas c = E<Canvas>("VpnGraph");
            c.Visibility = vpnRunning && vpnSpeeds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            c.Children.Clear();
            double w = c.ActualWidth, h = c.Height;
            if (c.Visibility != Visibility.Visible || w <= 0) return;
            double max = Math.Max(1, vpnSpeeds.Max());
            PointCollection pts = new PointCollection();
            for (int i = 0; i < vpnSpeeds.Count; i++)
                pts.Add(new Point(w * i / Math.Max(1, vpnSpeeds.Count - 1), h - 2 - vpnSpeeds[i] / max * (h - 16)));   // сверху место под подпись пика
            PointCollection area = new PointCollection(pts) { new Point(w, h), new Point(0, h) };
            Polygon fill = new Polygon { Points = area, Opacity = 0.18 };
            fill.SetResourceReference(Shape.FillProperty, "Accent");
            Polyline line = new Polyline { Points = pts, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
            line.SetResourceReference(Shape.StrokeProperty, "Accent");
            c.Children.Add(fill);
            c.Children.Add(line);
            TextBlock peak = new TextBlock { Text = Mbps(max), FontSize = 10 };
            peak.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            Canvas.SetRight(peak, 0);
            Canvas.SetTop(peak, -1);
            c.Children.Add(peak);
        }

        static string Mbps(double v) { return (v < 10 ? v.ToString("0.0") : v.ToString("0")) + " " + T("Мбит/с"); }
        static string Bytes(long b)
        {
            return b >= 1L << 30 ? (b / 1073741824.0).ToString("0.0") + " " + T("ГБ") : b >= 1L << 20 ? (b / 1048576.0).ToString("0") + " " + T("МБ") : Math.Max(1, b / 1024) + " " + T("КБ");
        }

        // Строка VPN на карточке-переключателе
        void UpdatePowerVpn()
        {
            bool show = cfg.VpnOn || vpnRunning;
            E<FrameworkElement>("PowerVpn").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            VpnServer s = CurrentServer;
            // на карточке места мало — только скорость загрузки
            string speed = vpnSpeedText.Length > 0 ? " · " + vpnSpeedText.Split(new[] { "  ↑" }, StringSplitOptions.None)[0] : "";
            Text("PowerVpnText").Text = "VPN · " + (s == null ? "" : Vpn.DisplayName(s.Name)) + (vpnRunning ? speed : " · " + T("подключается…"));
            E<Ellipse>("PowerVpnDot").SetResourceReference(Shape.FillProperty, vpnRunning ? "Ok" : "Wait");
        }

        // ---------- Ключ из буфера обмена ----------
        string clipLink;

        void CheckClipboard()
        {
            string link = null;
            try
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText().Trim();
                    string first = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    if (Vpn.IsLink(first) && !cfg.VpnServers.Any(s => s.Link == first)) link = text;
                }
            }
            catch { }   // буфер обмена занят другой программой
            clipLink = link;
            E<FrameworkElement>("VpnClipBar").Visibility = link != null ? Visibility.Visible : Visibility.Collapsed;
            if (link != null)
            {
                string first = link.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                int count = link.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Count(l => Vpn.IsLink(l.Trim()));
                Text("VpnClipText").Text = T("В буфере обмена") + " " + (count > 1 ? count + " " + T("ключа VPN") : T("ключ VPN") + " «" + first.Substring(0, first.IndexOf("://")) + "»") + ". " + T("Добавить?");
            }
        }

        // ---------- Ключ по QR-коду ----------
        async Task AddFromQr(List<string> texts)
        {
            List<string> keys = texts.Where(t => Vpn.IsLink(t.Trim()) || t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToList();
            if (keys.Count == 0)
            {
                Text("VpnInfo").Text = T(texts.Count == 0 ? "QR-код не найден. Сделайте его крупнее на экране или откройте картинку получше" : "В QR-коде нет ключа VPN");
                return;
            }
            E<TextBox>("VpnInput").Text = string.Join("\n", keys);
            await AddVpnServers();
        }

        async Task QrFromFile()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = T("Картинка с QR-кодом ключа"), Filter = T("Картинки") + " (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };
            if (dlg.ShowDialog(window) != true) return;
            List<string> texts;
            try { texts = await Task.Run(() => Qr.FromFile(dlg.FileName)); }
            catch (Exception e) { Text("VpnInfo").Text = T("Не удалось открыть картинку: ") + e.Message; return; }
            await AddFromQr(texts);
        }

        // Окно лаунчера на мгновение прячется, чтобы не закрывать QR-код в другом окне
        async Task QrFromScreen()
        {
            window.Hide();
            List<string> texts;
            try
            {
                await Task.Delay(400);
                texts = await Task.Run(() => Qr.FromScreen());
            }
            finally { ShowWindow(); }
            await AddFromQr(texts);
        }

        async Task AddFromClipboard()
        {
            if (clipLink == null) return;
            E<TextBox>("VpnInput").Text = clipLink;
            clipLink = null;
            E<FrameworkElement>("VpnClipBar").Visibility = Visibility.Collapsed;
            await AddVpnServers();
        }

        async Task StartVpn(bool quiet)
        {
            // автовыбор: перед подключением — самый быстрый сервер
            if (cfg.VpnAutoBest && cfg.VpnServers.Count > 1 && !quiet)
            {
                await PingVpnServers();
                int best = BestServer();
                if (best >= 0) cfg.VpnSelected = best;
            }
            VpnServer server = CurrentServer;
            if (server == null) throw new Exception("Сначала добавьте сервер: вставьте ключ или адрес подписки на странице VPN");
            string other = Conflicts.ActiveVpn();
            if (other != null) throw new Exception(T("Сейчас включён другой VPN") + " (" + other + "). " + T("Выключите его — два VPN одновременно мешают друг другу."));
            vpnLastStart = DateTime.Now;
            await Vpn.Start(server, cfg.VpnMode, cfg.VpnDomains, cfg.VpnApps);
            vpnRunning = true;
            vpnFails = 0;
            vpnConnectedAt = DateTime.Now;
            vpnSpeeds.Clear();
            cfg.VpnHelpDone = true;   // подключились — подсказка «Как начать» больше не нужна
            cfg.VpnOn = true;
            cfg.Save();
            if (!quiet) Log.Write("VPN подключён: " + server.Name + " (" + T(cfg.VpnMode == "all" ? "весь трафик" : "только выбранное") + ")");
        }

        async Task StopVpn()
        {
            cfg.VpnOn = false;
            cfg.Save();
            await Vpn.Stop();
            vpnRunning = false;
            Log.Write("VPN отключён");
        }

        async Task ToggleVpn()
        {
            bool wasOn = vpnRunning || Vpn.Running;
            VpnBusy(true);
            try
            {
                if (wasOn) await RunBusy(StopVpn, "VPN…");
                else await RunBusy(() => StartVpn(false), "VPN…");
            }
            finally { VpnBusy(false); }
            if (!wasOn && vpnRunning) VpnFlash();
            RefreshVpnUi();
        }

        // Пока VPN подключается — по краю карточки бежит полоска
        void VpnBusy(bool on)
        {
            Shape line = E<Shape>("VpnBusyLine");
            if (on) line.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -120, new Duration(TimeSpan.FromSeconds(1.2))) { RepeatBehavior = RepeatBehavior.Forever });
            DoubleAnimation fade = new DoubleAnimation(on ? 1 : 0, new Duration(TimeSpan.FromMilliseconds(200)));
            if (!on) fade.Completed += delegate { line.BeginAnimation(Shape.StrokeDashOffsetProperty, null); };
            line.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // Подключился — карточка коротко вспыхивает цветом акцента
        void VpnFlash()
        {
            E<UIElement>("VpnFlash").BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.35, 0, new Duration(TimeSpan.FromMilliseconds(700))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // Изменились серверы, режим или списки — работающий VPN перезапускаем с новыми настройками
        async Task ApplyVpnChanges()
        {
            cfg.Save();
            RefreshVpnUi();
            if (vpnRunning) { await RunBusy(() => StartVpn(true), "VPN…"); RefreshVpnUi(); }
        }

        // Ядро VPN упало, а VPN должен работать — перезапускаем (не чаще раза в 30 секунд, после двух неудач — сдаёмся)
        async Task VpnGuard()
        {
            vpnRunning = Vpn.Running;
            if (!cfg.VpnOn || vpnRunning || busy || CurrentServer == null || (DateTime.Now - vpnLastStart).TotalSeconds < 30) return;
            Log.Write("VPN: ядро не работает — перезапускаю (" + Vpn.LastLogLine() + ")");
            try { await StartVpn(true); }
            catch (Exception e)
            {
                if (++vpnFails >= 2)
                {
                    cfg.VpnOn = false;
                    cfg.Save();
                    Notify("VPN не удалось перезапустить: " + e.Message);
                }
            }
            RefreshVpnUi();
        }

        async Task AddVpnServers()
        {
            string text = E<TextBox>("VpnInput").Text.Trim();
            if (text.Length == 0) return;
            TextBlock info = Text("VpnInfo");
            bool subscription = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            info.Text = T(subscription ? "Загружаю подписку…" : "Проверяю ключ…");
            List<VpnServer> found;
            try { found = await Vpn.Parse(text); }
            catch (Exception e) { info.Text = T(e.Message); return; }
            if (subscription)
            {
                cfg.VpnSubscription = text;
                cfg.VpnSubInfo = Vpn.LastUserInfo ?? "";
                cfg.VpnSubUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                cfg.VpnServers = found;   // подписка заменяет список целиком
                cfg.VpnSelected = 0;
            }
            else foreach (VpnServer s in found) if (!cfg.VpnServers.Any(x => x.Link == s.Link)) cfg.VpnServers.Add(s);
            info.Text = T("Добавлено серверов") + ": " + found.Count + ". " + T("Ключи хранятся только на этом компьютере, в настройках лаунчера");
            E<TextBox>("VpnInput").Text = "";
            Log.Write("VPN: добавлено серверов — " + found.Count + (subscription ? " (из подписки)" : ""));
            await ApplyVpnChanges();
        }

        async Task RefreshSubscription()
        {
            if (cfg.VpnSubscription.Length == 0) return;
            try
            {
                List<VpnServer> found = await Vpn.Parse(cfg.VpnSubscription);
                string current = CurrentServer == null ? null : CurrentServer.Link;
                cfg.VpnServers = found;
                cfg.VpnSubInfo = Vpn.LastUserInfo ?? "";
                cfg.VpnSubUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                int keep = found.FindIndex(s => s.Link == current);
                cfg.VpnSelected = keep >= 0 ? keep : 0;
                Text("VpnInfo").Text = T("Подписка обновлена, серверов") + ": " + found.Count;
                await ApplyVpnChanges();
            }
            catch (Exception e) { Text("VpnInfo").Text = T(e.Message); }
        }

        async Task AddVpnListItem()
        {
            string raw = E<TextBox>("VpnListInput").Text;
            List<string> list = VpnSitesKind ? cfg.VpnDomains : cfg.VpnApps;
            List<string> added = new List<string>(), bad = new List<string>();
            foreach (string part in raw.Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = VpnSitesKind ? Zapret.NormalizeDomain(part) : NormalizeApp(part);
                if (item == null) bad.Add(part);
                else if (!list.Contains(item) && !added.Contains(item)) added.Add(item);
            }
            Text("VpnListInfo").Text = bad.Count > 0 ? T(VpnSitesKind ? "Не похоже на домен" : "Нужно имя программы, например Spotify.exe") + ": " + string.Join(", ", bad) : " ";
            if (added.Count == 0) return;
            list.AddRange(added);
            E<TextBox>("VpnListInput").Text = "";
            await ApplyVpnChanges();
        }

        // "C:\...\Spotify.exe" или "spotify" → Spotify.exe / spotify.exe
        static string NormalizeApp(string s)
        {
            s = s.Trim().Trim('"');
            if (s.Length == 0) return null;
            s = System.IO.Path.GetFileName(s);
            if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s += ".exe";
            return s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ? null : s;
        }

        // Флаг страны картинкой; если страну не понять — значок глобуса
        static FrameworkElement FlagElement(string code, double w, double h)
        {
            ImageSource img = Vpn.Flag(code);
            if (img == null)
            {
                TextBlock globe = new TextBlock { Text = "", FontSize = h - 1, Width = w, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                globe.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
                globe.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                return globe;
            }
            Border b = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true,
                                    Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill }, BorderThickness = new Thickness(0.5) };
            b.SetResourceReference(Border.BorderBrushProperty, "Line");
            b.ToolTip = T(Vpn.CountryName(code) ?? code);
            return b;
        }

        void ShowSettingsGroup(string group)
        {
            foreach (UIElement child in E<StackPanel>("SettingsRoot").Children)
            {
                FrameworkElement fe = child as FrameworkElement;
                if (fe != null && fe.Tag is string) fe.Visibility = (string)fe.Tag == group ? Visibility.Visible : Visibility.Collapsed;
            }
            E<ScrollViewer>("PageSettings").ScrollToTop();
        }

        string vpnTab;   // открытая вкладка страны в списке серверов ("" — серверы без страны)

        // Вкладка страны: флаг, название, число серверов; открытая подсвечена, у страны текущего сервера — точка
        Border CountryTab(string code, int count, bool open, bool hasCurrent)
        {
            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
            FrameworkElement flag = FlagElement(code == "" ? null : code, 20, 14);
            flag.Margin = new Thickness(0, 0, 7, 0);
            content.Children.Add(flag);
            TextBlock name = new TextBlock { Text = T(Vpn.CountryName(code == "" ? null : code) ?? "Другие"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            if (open) name.FontWeight = FontWeights.SemiBold;
            content.Children.Add(name);
            TextBlock n = new TextBlock { Text = " " + count, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            n.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            content.Children.Add(n);
            if (hasCurrent)
            {
                Ellipse dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = T("Здесь выбранный сервер") };
                dot.SetResourceReference(Shape.FillProperty, "Accent");
                content.Children.Add(dot);
            }
            Border tab = new Border { Child = content, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
            tab.SetResourceReference(Border.BackgroundProperty, open ? "AccentSoft" : "Card");
            tab.SetResourceReference(Border.BorderBrushProperty, open ? "Accent" : "Line");
            tab.MouseLeftButtonUp += delegate { if (vpnTab == code) return; vpnTab = code; RefreshVpnUi(); };
            return tab;
        }

        Border ServerRow(VpnServer s, int index, string code)
        {
            bool selected = index == cfg.VpnSelected;
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FrameworkElement flag = FlagElement(code, 24, 16);
            flag.Margin = new Thickness(0, 0, 11, 0);
            row.Children.Add(flag);
            TextBlock name = new TextBlock { Text = Vpn.DisplayName(s.Name), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                                             ToolTip = s.Link.Substring(0, s.Link.IndexOf("://") + 3) + "…", Margin = new Thickness(0, 0, 70, 0) };
            if (selected) name.FontWeight = FontWeights.SemiBold;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            // качество сервера «полосками сигнала», как у Wi-Fi: 3 — быстро, 1 — медленно, пустые и красный текст — не работает
            int ms;
            if (vpnPing.TryGetValue(s.Link, out ms))
            {
                StackPanel signal = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0),
                                                     ToolTip = ms < 0 ? T("Сайт через этот сервер не открылся") : T("Сайт через сервер открылся за") + " " + ms + " " + T("мс") };
                int level = ms < 0 ? 0 : ms < 400 ? 3 : ms < 900 ? 2 : 1;
                string color = level == 3 ? "Ok" : level == 2 ? "Ok" : level == 1 ? "Wait" : "Fail";
                TextBlock label = new TextBlock { Text = ms < 0 ? T("не работает") : ms + " " + T("мс"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, ms < 0 ? "Fail" : "Muted");
                signal.Children.Add(label);
                for (int b = 0; b < 3; b++)
                {
                    Rectangle bar = new Rectangle { Width = 3.5, Height = 5 + b * 3.5, RadiusX = 1.2, RadiusY = 1.2, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Bottom };
                    bar.SetResourceReference(Shape.FillProperty, b < level ? color : "IconBg");
                    signal.Children.Add(bar);
                }
                Grid.SetColumn(signal, 1);
                row.Children.Add(signal);
            }
            Button del = new Button { Content = "", Width = 28, Height = 28, FontSize = 11, ToolTip = T("Удалить") };
            del.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            del.Click += async (o, e) =>
            {
                e.Handled = true;
                cfg.VpnServers.RemoveAt(index);
                if (cfg.VpnSelected >= cfg.VpnServers.Count) cfg.VpnSelected = Math.Max(0, cfg.VpnServers.Count - 1);
                if (cfg.VpnServers.Count == 0) { cfg.VpnSubscription = ""; cfg.VpnSubInfo = ""; if (vpnRunning) await RunBusy(StopVpn, "VPN…"); }
                await ApplyVpnChanges();
            };
            Grid.SetColumn(del, 2);
            row.Children.Add(del);
            Border card = new Border { Child = row, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, BorderThickness = new Thickness(1.5) };
            card.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSoft" : "Card");
            card.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Card");
            card.MouseLeftButtonUp += async delegate
            {
                if (cfg.VpnSelected == index) return;
                cfg.VpnSelected = index;
                await ApplyVpnChanges();
            };
            return card;
        }

        // Карточка подписки: сколько трафика использовано из скольких и до какого числа оплачено
        void RenderSubscription()
        {
            Vpn.SubscriptionInfo info = cfg.VpnSubscription.Length > 0 ? Vpn.ParseUserInfo(cfg.VpnSubInfo) : null;
            E<FrameworkElement>("VpnSubCard").Visibility = info != null ? Visibility.Visible : Visibility.Collapsed;
            if (info == null) return;
            Text("VpnSubText").Text = info.Total > 0
                ? T("Использовано") + " " + Bytes(info.Used) + " " + T("из") + " " + Bytes(info.Total)
                : T("Использовано") + " " + Bytes(info.Used) + " · " + T("без лимита");
            TextBlock exp = Text("VpnSubExpire");
            if (info.Expire.HasValue)
            {
                double days = (info.Expire.Value - DateTime.Now).TotalDays;
                exp.Text = days < 0 ? T("подписка закончилась") : T("до") + " " + info.Expire.Value.ToString("dd.MM.yyyy") + (days < 7 ? " · " + T("осталось дней") + ": " + (int)Math.Ceiling(days) : "");
                exp.SetResourceReference(TextBlock.ForegroundProperty, days < 0 ? "Fail" : days < 3 ? "Wait" : "Muted");
            }
            else exp.Text = T("бессрочно");
            Border bar = E<Border>("VpnSubBar");
            E<FrameworkElement>("VpnSubBarBg").Visibility = info.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (info.Total > 0)
            {
                double ratio = Math.Min(1, (double)info.Used / info.Total);
                subRatio = ratio;
                bar.Width = Math.Max(0, E<FrameworkElement>("VpnSubBarBg").ActualWidth * ratio);
                bar.SetResourceReference(Border.BackgroundProperty, ratio > 0.9 ? "Fail" : ratio > 0.75 ? "Wait" : "Accent");
            }
        }

        double subRatio;   // доля использованного трафика подписки — для полоски

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
            List<Vpn.Connection> conns = await Vpn.Connections();
            list.Children.Clear();
            // по сайтам: одна строка на сайт, сначала свежие
            var bySite = conns.Where(c => !string.IsNullOrEmpty(c.Host)).GroupBy(c => c.Host)
                .Select(g => new { Host = g.Key, ViaVpn = g.Any(c => c.ViaVpn), Process = g.Select(c => c.Process).FirstOrDefault(p => !string.IsNullOrEmpty(p)), Bytes = g.Sum(c => c.Bytes), Start = g.Max(c => c.Start), Count = g.Count() })
                .OrderByDescending(x => x.Start).Take(40).ToList();
            Text("VpnConnInfo").Text = T("Сейчас соединений") + ": " + conns.Count + " · " + T("через VPN") + ": " + conns.Count(c => c.ViaVpn) + " · " + T("напрямую") + ": " + conns.Count(c => !c.ViaVpn);
            foreach (var c in bySite)
            {
                Grid row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Ellipse dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 9, 0) };
                dot.SetResourceReference(Shape.FillProperty, c.ViaVpn ? "Accent" : "Idle");
                row.Children.Add(dot);
                StackPanel text = new StackPanel { Orientation = Orientation.Horizontal };
                text.Children.Add(new TextBlock { Text = c.Host + (c.Count > 1 ? " ×" + c.Count : ""), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                if (!string.IsNullOrEmpty(c.Process))
                {
                    TextBlock p = new TextBlock { Text = "  " + c.Process, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                    p.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
                    text.Children.Add(p);
                }
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                TextBlock via = new TextBlock { Text = T(c.ViaVpn ? "VPN" : "напрямую") + " · " + Bytes(c.Bytes), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                via.SetResourceReference(TextBlock.ForegroundProperty, c.ViaVpn ? "AccentHover" : "Muted");
                Grid.SetColumn(via, 2);
                row.Children.Add(via);
                list.Children.Add(row);
            }
        }

        static string ModeName(string mode) { return mode == "all" ? "весь трафик" : mode == "ru" ? "Россия напрямую" : "только выбранное"; }

        void RefreshVpnUi()
        {
            if (exiting) return;
            bool on = vpnRunning;
            VpnServer server = CurrentServer;
            Text("VpnStatus").Text = T(on ? "VPN подключён" : cfg.VpnOn ? "VPN переподключается…" : "VPN выключен");
            E<Ellipse>("VpnDot").SetResourceReference(Shape.FillProperty, on ? "Ok" : cfg.VpnOn ? "Wait" : "Idle");
            Text("VpnSub").Text = server == null ? T("Добавьте сервер ниже") : Vpn.DisplayName(server.Name) + " · " + T(ModeName(cfg.VpnMode));
            // карточка подключения: подсветка, переключатель, флаг страны текущего сервера
            TimeSpan anim = TimeSpan.FromMilliseconds(260);
            Fade(E<UIElement>("VpnCardOn"), on ? 1 : 0, anim);
            Fade(E<UIElement>("VpnTrackOn"), on ? 1 : 0, anim);
            Slide(E<Ellipse>("VpnKnob"), on ? 28 : 0, anim);
            E<FrameworkElement>("VpnCard").Opacity = server != null ? 1 : 0.6;
            E<FrameworkElement>("VpnCard").ToolTip = T(server == null ? "Сначала добавьте сервер" : on ? "Нажмите, чтобы отключить VPN" : "Нажмите, чтобы подключить VPN");
            E<Border>("VpnFlagHost").Child = FlagElement(server == null ? null : Vpn.CountryCode(server.Name), 54, 36);
            E<FrameworkElement>("VpnHelp").Visibility = !cfg.VpnHelpDone ? Visibility.Visible : Visibility.Collapsed;
            UpdateVpnTimer();
            filling = true;
            E<ToggleButton>("ChipVpn").IsChecked = on;
            E<RadioButton>(cfg.VpnMode == "all" ? "VpnModeAll" : cfg.VpnMode == "ru" ? "VpnModeRu" : "VpnModeSplit").IsChecked = true;
            E<CheckBox>("SwVpnAuto").IsChecked = cfg.VpnAutoBest;
            filling = false;
            E<Button>("BtnVpnPing").IsEnabled = cfg.VpnServers.Count > 0 && !vpnPinging;
            E<Button>("BtnVpnBest").IsEnabled = cfg.VpnServers.Count > 1 && !vpnPinging;
            UpdatePowerVpn();
            trayState = null;   // значок трея — с меткой VPN или без
            Text("VpnModeText").Text = T(cfg.VpnMode == "all"
                ? "Весь интернет идёт через сервер VPN. Обход zapret в это время не нужен."
                : cfg.VpnMode == "ru"
                ? "Российские сайты и адреса — напрямую, всё остальное — через VPN. Банки и Госуслуги работают как обычно. Ниже можно добавить сайты и программы, которым всегда нужен VPN."
                : "Через VPN — только сайты и программы из списка ниже. Discord, YouTube и остальное идут напрямую и через обход, без потери скорости.");
            E<FrameworkElement>("VpnSplitPanel").Visibility = cfg.VpnMode == "all" ? Visibility.Collapsed : Visibility.Visible;
            E<Button>("BtnVpnRefresh").Visibility = cfg.VpnSubscription.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Text("VpnListHint").Text = T(VpnSitesKind ? "Например: example.com" : "Например: Spotify.exe");
            if (miVpn != null) { miVpn.Text = T(on ? "Выключить VPN" : "Включить VPN"); miVpn.Enabled = server != null; }
            RenderSubscription();

            // серверы по странам: сверху вкладки (флаг, страна, сколько серверов), ниже — серверы выбранной страны.
            // При поиске вкладки не мешают: показываются совпадения по всем странам
            E<FrameworkElement>("VpnSearchBox").Visibility = cfg.VpnServers.Count >= 6 ? Visibility.Visible : Visibility.Collapsed;
            string query = cfg.VpnServers.Count >= 6 ? E<TextBox>("VpnSearch").Text.Trim() : "";
            StackPanel servers = E<StackPanel>("VpnServerList");
            servers.Children.Clear();
            var all = cfg.VpnServers.Select((s, i) => new { s, i, code = Vpn.CountryCode(s.Name) }).ToList();
            var groups = all.GroupBy(x => x.code ?? "").ToList();
            WrapPanel tabs = E<WrapPanel>("VpnTabs");
            tabs.Children.Clear();
            bool useTabs = groups.Count > 1 && query.Length == 0;
            tabs.Visibility = useTabs ? Visibility.Visible : Visibility.Collapsed;
            if (useTabs)
            {
                // открытая вкладка: выбранная вручную, иначе страна текущего сервера
                if (vpnTab == null || !groups.Any(g => g.Key == vpnTab))
                    vpnTab = server != null ? (Vpn.CountryCode(server.Name) ?? "") : groups[0].Key;
                foreach (var g in groups)
                    tabs.Children.Add(CountryTab(g.Key, g.Count(), g.Key == vpnTab, g.Any(x => x.i == cfg.VpnSelected)));
            }
            var shown = all.Where(x => query.Length > 0
                    ? x.s.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                      || (Vpn.CountryName(x.code) ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                      || T(Vpn.CountryName(x.code) ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    : !useTabs || (x.code ?? "") == vpnTab).ToList();
            foreach (var x in shown) servers.Children.Add(ServerRow(x.s, x.i, x.code));
            if (shown.Count == 0 && cfg.VpnServers.Count > 0)
            {
                TextBlock none = new TextBlock { Text = T("Ничего не найдено"), FontSize = 12, Margin = new Thickness(2, 2, 0, 6) };
                none.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                servers.Children.Add(none);
            }

            // сайты или программы через VPN
            StackPanel items = E<StackPanel>("VpnList");
            items.Children.Clear();
            List<string> list = VpnSitesKind ? cfg.VpnDomains : cfg.VpnApps;
            if (list.Count == 0)
            {
                TextBlock empty = new TextBlock { Text = T(VpnSitesKind ? "Список пуст — добавьте сайты, которым нужен VPN" : "Список пуст — добавьте программы, которым нужен VPN"), FontSize = 12, Margin = new Thickness(2, 4, 0, 0) };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                items.Children.Add(empty);
            }
            foreach (string item in list)
            {
                string value = item;
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = value, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                Button del = new Button { Content = "\uE711", Width = 28, Height = 28, FontSize = 11, ToolTip = T("Удалить") };
                del.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
                del.Click += async delegate { list.Remove(value); await ApplyVpnChanges(); };
                Grid.SetColumn(del, 1);
                row.Children.Add(del);
                Border card = new Border { Child = row, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6) };
                card.SetResourceReference(Border.BackgroundProperty, "Card");
                items.Children.Add(card);
            }
        }

        // ---------- Что нового ----------
        // Коротко о главном в каждой крупной версии; показывается один раз после обновления
        static readonly Dictionary<string, string[]> WhatsNew = new Dictionary<string, string[]>
        {
            { "4.0.0", new[]
                {
                    "Встроенный VPN: вставьте ключ или подписку на странице VPN",
                    "Через VPN — только выбранные сайты и программы, остальное идёт напрямую",
                    "Пинг серверов, автовыбор самого быстрого и переход на другой, если сервер упал",
                    "Скорость и трафик через VPN — на странице VPN и на главном экране",
                    "Откат лаунчера на прошлую версию — в настройках",
                    "Новый логотип",
                } },
        };

        bool whatsNewPending;

        void ShowWhatsNew()
        {
            whatsNewPending = false;
            string[] items;
            string key = WhatsNew.Keys.FirstOrDefault(v => v == Consts.Version) ?? WhatsNew.Keys.Where(v => !Util.TestNewer(v, Consts.Version)).OrderByDescending(v => new Version(v)).FirstOrDefault();
            if (key != null && WhatsNew.TryGetValue(key, out items) && Util.TestNewer(key, cfg.LastSeenVersion))
                Dialog.Show(window, T("Лаунчер обновлён до версии") + " " + Consts.Version + "\n\n" + string.Join("\n", items.Select(i => "•  " + T(i))),
                    T("Что нового"), MessageBoxButton.OK, MessageBoxImage.Information);
            cfg.LastSeenVersion = Consts.Version;
            cfg.Save();
        }

        // ---------- Откат лаунчера ----------
        // Прошлая версия — из локальной копии (сохраняется при самообновлении) или предыдущий релиз с GitHub
        async Task RollbackLauncherUi()
        {
            Release remote = null;
            string target = SelfUpdate.PreviousLocal();
            if (target == null)
            {
                await RunBusy(async () => { remote = await SelfUpdate.PreviousRemote(cfg.LauncherRepo); }, "Проверка…");
                if (remote == null) { Ask("Не нашёл версию лаунчера старше текущей ни локально, ни на GitHub."); return; }
                target = remote.Version;
            }
            if (Ask(T("Вернуть лаунчер версии") + " " + target + " " + T("вместо") + " " + Consts.Version + "?\n\n" +
                    T("Перед этим настройки сохранятся в резервную копию: старая версия может не знать новых настроек.") + "\n" +
                    T("Автообновление выключится, иначе лаунчер сам обновится обратно. Включите его, когда захотите.") + "\n" +
                    T("Обход продолжит работать, VPN выключится."),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            bool done = false;
            await RunBusy(async () =>
            {
                string backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SettingsBackup.AutoFile), "before-rollback-" + Consts.Version + ".zlbackup");
                cfg.AutoUpdate = false;
                cfg.VpnOn = false;
                cfg.Save();
                SettingsBackup.Save(backup);
                await Vpn.Stop();   // старые версии не умеют управлять встроенным VPN
                await SelfUpdate.Rollback(remote);
                Log.Write("Перед откатом настройки сохранены в " + backup);
                done = true;
            }, "Откат…");
            if (done) Restart();   // откат прошёл — запускаем прошлую версию
        }

        // ---------- Резервная копия настроек ----------
        bool backupReady;
        void AutoBackup() { if (backupReady) SettingsBackup.Auto(); }

        bool OfferAutoRestore()
        {
            string created = SettingsBackup.Describe(SettingsBackup.AutoFile);
            if (created == null) return false;
            if (Ask(T("Найдены настройки от прошлой установки лаунчера") + " (" + created + "): " +
                    T("стратегия, домены, избранное, профили сетей.") + "\n\n" + T("Восстановить их?"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            try { SettingsBackup.Restore(SettingsBackup.AutoFile); }
            catch (Exception e) { Ask(e.Message, MessageBoxButton.OK, MessageBoxImage.Error); return false; }
            Restart();
            return true;
        }

        void BackupSave()
        {
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = T("Сохранить настройки в файл"), FileName = "ZapretLauncher-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zlbackup",
                Filter = T("Резервная копия Zapret Launcher") + " (*.zlbackup)|*.zlbackup", DefaultExt = ".zlbackup",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(window) != true) return;
            try
            {
                cfg.Save();
                SettingsBackup.Save(dlg.FileName);
                Log.Write("Настройки сохранены в " + dlg.FileName);
                Ask("Настройки сохранены. После переустановки откройте этот файл через «Восстановить настройки из файла».");
            }
            catch (Exception e) { Ask(T("Не удалось сохранить: ") + e.Message, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        void BackupRestore()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = T("Восстановить настройки из файла"),
                Filter = T("Резервная копия Zapret Launcher") + " (*.zlbackup;*.json)|*.zlbackup;*.json",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(window) != true) return;
            string created = SettingsBackup.Describe(dlg.FileName);
            if (created == null) { Ask("Это не файл резервной копии Zapret Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (Ask(T("Заменить текущие настройки копией от") + " " + created + "?\n" + T("Лаунчер перезапустится, обход продолжит работать"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { SettingsBackup.Restore(dlg.FileName); }
            catch (Exception e) { Ask(T("Не удалось восстановить: ") + e.Message, MessageBoxButton.OK, MessageBoxImage.Error); return; }
            Restart();
        }

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

        async Task WaitForInternet(int maxSeconds)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < maxSeconds)
            {
                if (await Task.Run(() => InternetUp()))
                {
                    if (sw.Elapsed.TotalSeconds >= 2) Log.Write("Интернет появился через " + (int)sw.Elapsed.TotalSeconds + " сек.");
                    return;
                }
                Text("PowerStatus").Text = T("Жду интернет…");
                await Task.Delay(2000);
            }
            Log.Write("Интернета нет уже " + maxSeconds + " сек. — включаю обход без него");
        }

        // Есть ли интернет: TCP-соединение с публичными DNS-серверами (их провайдеры не блокируют)
        static bool InternetUp()
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;
            foreach (string ip in new[] { "77.88.8.8", "1.1.1.1", "8.8.8.8" })
            {
                try
                {
                    using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
                        if (c.ConnectAsync(ip, 53).Wait(1500) && c.Connected) return true;
                }
                catch { }
            }
            return false;
        }

        // ---------- Команды из меню в панели задач ----------
        FileSystemWatcher commandWatcher;

        void WatchCommands()
        {
            try
            {
                commandWatcher = new FileSystemWatcher(Paths.Root, "command.txt");
                FileSystemEventHandler h = (s, e) => window.Dispatcher.BeginInvoke(new Action(async () => await RunCommandFile()));
                commandWatcher.Created += h;
                commandWatcher.Changed += h;
                commandWatcher.Renamed += (s, e) => window.Dispatcher.BeginInvoke(new Action(async () => await RunCommandFile()));
                commandWatcher.EnableRaisingEvents = true;
            }
            catch (Exception e) { Log.Write("Команды из панели задач недоступны: " + e.Message); }
            if (File.Exists(TaskbarMenu.CommandFile)) window.Dispatcher.BeginInvoke(new Action(async () => await RunCommandFile()));   // лаунчер запустили ради команды
        }

        async Task RunCommandFile()
        {
            string command = null;
            for (int i = 0; i < 5 && command == null; i++)
            {
                bool busyFile = false;
                try { command = File.ReadAllText(TaskbarMenu.CommandFile).Trim(); File.Delete(TaskbarMenu.CommandFile); }
                catch (FileNotFoundException) { return; }
                catch { busyFile = true; }
                if (busyFile) await Task.Delay(100);   // помощник ещё пишет файл
            }
            if (string.IsNullOrEmpty(command) || exiting) return;
            if (command == "start") { if (!Zapret.Running) await StartBypassUi(); }
            else if (command == "stop") { if (Zapret.Running || pausedByVpn) await StopBypassUi(); }
            else if (command == "pick") { ShowWindow(); await StartAutoPickUi(false); }
            else if (command == "pickfav") { ShowWindow(); await StartAutoPickUi(true); }
            else if (command == "fixdiscord") await FixDiscordUi();
            else if (command.StartsWith("strategy "))
            {
                string file = command.Substring(9);
                if (Zapret.Strategies().Contains(file)) await SelectStrategy(file);   // только существующие стратегии
            }
        }

        void UpdateTaskbarMenu()
        {
            try { TaskbarMenu.Update(cfg.Favorites.Where(f => File.Exists(Paths.InZapret(f)))); } catch { }
        }

        DispatcherTimer Timer(TimeSpan interval, EventHandler tickHandler)
        {
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = interval;
            t.Tick += tickHandler;
            t.Start();
            return t;
        }

        // Окна не видно — лаунчер работает в трее и всё проверяет реже
        bool Idle { get { return !window.IsVisible && (flyout == null || !flyout.IsVisible); } }

        static bool OnBattery()
        {
            try { return Forms.SystemInformation.PowerStatus.PowerLineStatus == Forms.PowerLineStatus.Offline; } catch { return false; }
        }

        // Фоновая проверка сайтов: при открытом окне — раз в 3 мин, в трее — раз в 6, от батареи — раз в 10
        TimeSpan HealthInterval() { return TimeSpan.FromMinutes(OnBattery() ? 10 : Idle ? 6 : 3); }

        // Раз в секунду: интерфейс, сторож по упавшему winws, VPN, сеть, конфликты, hosts, проверка сайтов.
        // Смена сети (событие Windows) проверяется сразу; остальное — по расписанию, в трее реже
        bool ticking;   // проверки идут в фоне — следующий тик не начинается, пока не закончился прошлый

        async Task OnTick()
        {
            if (busy || exiting) return;
            if (Idle) UpdateTray(); else UpdateUi();
            if (ticking) return;
            ticking = true;
            try { await Tick(); }
            finally { ticking = false; }
        }

        async Task Tick()
        {
            tick++;
            bool idle = Idle;
            DateTime now = DateTime.Now, minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            if (minute != historyMinute)   // раз в минуту — отметка в истории за сутки
            {
                historyMinute = minute;
                StateHistory.Record(TrayState(Zapret.Running));
                RenderHistory();
            }
            bool changed = netChanged;
            if (changed || tick % (idle ? 10 : 5) == 1) await CheckVpn();
            if (changed || tick % (idle ? 30 : 10) == 2) { netChanged = false; await CheckNetworkAndApply(); }
            if (tick % (idle ? 60 : 15) == 3) await UpdateConflicts();
            if (tick % 60 == 4) CheckHosts();
            if (tick % (idle ? 15 : 5) == 0)
            {
                bool was = vpnRunning;
                await VpnGuard();
                if (was != vpnRunning) RefreshVpnUi();
                await VpnHealth();
            }
            if (vpnRunning && !idle && tick % 2 == 0) await UpdateVpnTraffic();
            if (vpnConnShown && !idle && tick % 2 == 1 && E<FrameworkElement>("PageVpn").Visibility == Visibility.Visible) await RefreshConnections();
            // подписка обновляется сама раз в сутки — новые серверы, остаток трафика и срок
            DateTime subAt;
            if (tick % 600 == 9 && cfg.VpnSubscription.Length > 0 && (!DateTime.TryParse(cfg.VpnSubUpdated, out subAt) || (DateTime.Now - subAt).TotalHours >= 24)) await RefreshSubscription();
            await CrashGuard();
            if (DateTime.Now >= healthDue)
            {
                healthDue = DateTime.Now.Add(HealthInterval());
                if (Zapret.Running && siteCheckDue == DateTime.MaxValue) siteCheckDue = DateTime.Now;
            }
            if (!busy && !siteChecking && Zapret.Running && DateTime.Now >= siteCheckDue) await SiteCheck(true);
            if (idle && DateTime.Now >= trimDue) { trimDue = DateTime.Now.AddMinutes(15); TrimMemory(); }
        }

        // После сна: сеть могла смениться, соединения — оборваться. Проверяем всё сразу, а сайты — когда сеть поднимется
        void OnResume()
        {
            if (exiting) return;
            Log.Write("Компьютер вышел из сна — проверяю обход");
            netChanged = true;
            failStreak = 0;
            lastRestart = DateTime.MinValue;
            if (wantRunning) siteCheckDue = DateTime.Now.AddSeconds(8);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        // В трее окно не нужно: собираем мусор и отдаём Windows неиспользуемую память (WPF держит её про запас)
        static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
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

        // ---------- Диалоги и уведомления ----------
        MessageBoxResult Ask(string text, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
        {
            return Dialog.Show(window, T(text), "Zapret Launcher", buttons, icon);
        }

        // Уведомление Windows с кнопками (если окно не на виду), иначе — подсказка в трее; всё пишется в журнал
        void Notify(string text, params string[][] actions)
        {
            Log.Write(text);
            if (window.IsVisible && window.IsActive) return;
            string[][] translated = actions.Select(a => new[] { T(a[0]), a[1] }).ToArray();
            if (cfg.Toasts && Toasts.Show("Zapret Launcher", T(text), translated, OnToastAction)) return;
            if (!window.IsVisible) tray.ShowBalloonTip(5000, "Zapret Launcher", T(text), Forms.ToolTipIcon.Info);
        }

        void OnToastAction(string action)
        {
            window.Dispatcher.BeginInvoke(new Action(async () =>
            {
                ShowWindow();
                if (action == "pick") await StartAutoPickUi(false);
                else if (action == "update") await InstallZapretUi();
                else if (action == "launcher-update") await UpdateLauncherUi(false);
                else if (action == "start") await StartBypassUi();
                else if (action == "rollback") RollbackUi(true);
            }));
        }

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
        Task RestartIfRunning() { return Zapret.Running ? RunBusy(() => StartBypass(false), "Применяю…") : Task.FromResult(0); }

        async Task ToggleBypass()
        {
            if (picking) cancelPick = true;
            else if (Zapret.Running || pausedByVpn) await StopBypassUi();
            else if (Zapret.LocalVersion != null) await StartBypassUi();
            else await InstallZapretUi();
        }

        async Task RunBusy(Func<Task> action, string busyText = null)
        {
            if (busy) return;
            busy = true;
            window.Cursor = Cursors.AppStarting;
            if (busyText != null) { Text("PowerStatus").Text = T(busyText); Text("PowerTimer").Text = " "; BusyRing(true); }
            try { await action(); }
            catch (Exception e)
            {
                Log.Write("Ошибка: " + e.Message);
                if (window.IsVisible) Ask(e.Message, MessageBoxButton.OK, MessageBoxImage.Error);
                else tray.ShowBalloonTip(5000, "Zapret Launcher", T(e.Message), Forms.ToolTipIcon.Error);
            }
            finally
            {
                busy = false;
                window.Cursor = null;
                BusyRing(false);
                UpdateUi();
            }
        }

        void BusyRing(bool on)
        {
            // штрих бежит по контуру: смещение на длину штриха+пробела (85) — ровно один оборот
            Shape ring = E<Shape>("BusyRing");
            if (on) ring.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -85, new Duration(TimeSpan.FromSeconds(1.1))) { RepeatBehavior = RepeatBehavior.Forever });
            DoubleAnimation fade = new DoubleAnimation(on ? 1 : 0, new Duration(TimeSpan.FromMilliseconds(200)));
            if (!on) fade.Completed += delegate { if (!busy) ring.BeginAnimation(Shape.StrokeDashOffsetProperty, null); };
            ring.BeginAnimation(UIElement.OpacityProperty, fade);
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

        // Проверка сайтов: обновляет карточки; health — ещё и сторож (две неудачи подряд — подбор новой стратегии)
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
            BuildSitePanel();
            if (control != null && control.Ok) CompareAfterUpdate();
            if (!health || busy || control == null || !control.Ok) return;   // нет интернета — менять стратегию бессмысленно

            int ok = siteResults.Count(r => r.Ok);
            int expected = baseline.HasValue ? Math.Min(baseline.Value, siteResults.Count) : siteResults.Count;
            if (ok >= expected) { failStreak = 0; return; }
            failStreak++;
            Log.Write("Сторож: не открывается " + string.Join(", ", siteResults.Where(r => !r.Ok).Select(r => r.Name)) + " (проверка " + failStreak + " из 2)");
            if (failStreak < 2) { siteCheckDue = DateTime.Now.AddMinutes(1); return; }
            failStreak = 0;
            if (!cfg.Watchdog)
            {
                Notify("Не открывается " + string.Join(", ", siteResults.Where(r => !r.Ok).Select(r => r.Name)), new[] { "Подобрать стратегию", "pick" });
                return;
            }
            if (Conflicts.ActiveVpn() != null) { Log.Write("Сторож: включён VPN — стратегию не меняю"); return; }
            Notify("Стратегия перестала работать — подбираю новую");
            await RunBusy(async () =>
            {
                PickBest best = await AutoPick(true);
                if (best != null && best.Ok > 0) Notify("Новая стратегия: " + Zapret.Title(best.File));
            }, "Подбор…");
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
                    if (stopAtFirstPerfect && okRes.Count == res.Count) break;
                }
            }
            finally { picking = false; HidePickProgress(); }
            Log.Write("Подбор занял " + (int)total.Elapsed.TotalSeconds + " сек.");
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
                    big.Text = r != null && !r.Ok && !siteChecking ? T("недоступен") : https;
                    big.FontSize = 14;
                    big.FontWeight = FontWeights.SemiBold;
                    big.Margin = new Thickness(0, 14, 0, 6);
                    big.SetResourceReference(TextBlock.ForegroundProperty, color == "Fail" ? "Fail" : "TextSub");
                }
                content.Children.Add(big);
                TextBlock t = new TextBlock { Text = tcp, FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
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
                panel.Children.Add(LiftOnHover(card, SiteUrl(site), site));
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
        FrameworkElement LiftOnHover(Border card, string url, string site)
        {
            Grid wrap = new Grid { Margin = card.Margin };
            card.Margin = new Thickness(0);
            Border under = new Border { CornerRadius = card.CornerRadius, Opacity = 0 };
            under.SetResourceReference(Border.BackgroundProperty, "CardRaised");
            under.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = Theme.Dark ? 0.5 : 0.18, Color = Colors.Black };
            wrap.Children.Add(under);
            wrap.Children.Add(card);
            if (url == null) return wrap;
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
            wrap.MouseLeftButtonUp += delegate { Process.Start("explorer.exe", "\"" + url + "\""); };
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
            }, "Обновление…");
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
                    // запоминаем, сколько сайтов открывалось до обновления, — после него сравним
                    okBeforeUpdate = Zapret.Running && siteResults != null ? siteResults.Count(r => r.Ok) : (int?)null;
                    await Zapret.Install(latest, cfg, s => StartBypass(false));
                    UpdateStrategyList();
                    UpdateTitle();
                    Notify("zapret обновлён до версии " + latest.Version);
                    if (okBeforeUpdate.HasValue && Zapret.Running) { updatedFrom = local; siteCheckDue = DateTime.Now.AddSeconds(15); }
                }
                else if (!skipped || !quiet)
                    Notify("Доступна новая версия zapret: " + latest.Version + (skipped ? " (пропущена после отката)" : ""), new[] { "Обновить", "update" });
            }
            else if (!quiet) Log.Write("zapret: установлена последняя версия (" + local + ")");

            if (allowAuto && cfg.AutoUpdate) await AutoUpdateIpset();

            // ядро VPN — только если оно уже установлено (то есть VPN хоть раз включали)
            string sbLocal = Vpn.Version;
            if (sbLocal != null)
            {
                try
                {
                    Release sb = await Vpn.Latest();
                    if (Util.TestNewer(sb.Version, sbLocal) && allowAuto && cfg.AutoUpdate)
                    {
                        bool wasOn = vpnRunning;
                        if (wasOn) await Vpn.Stop();
                        await Vpn.Install(sb);
                        if (wasOn) await StartVpn(true);
                        Log.Write("Ядро VPN обновлено до версии " + sb.Version);
                    }
                    else if (!quiet) Log.Write("Ядро VPN: установлена версия " + sbLocal + (Util.TestNewer(sb.Version, sbLocal) ? ", доступна " + sb.Version : " (последняя)"));
                }
                catch (Exception e) { if (!quiet) Log.Write("Ядро VPN: не удалось проверить обновления: " + e.Message); }
            }
            // Xray — только если уже скачан (был сервер с транспортом xhttp)
            string xrLocal = Vpn.XrayVersion;
            if (xrLocal != null && allowAuto && cfg.AutoUpdate)
            {
                try
                {
                    Release xr = await Vpn.XrayLatest();
                    if (Util.TestNewer(xr.Version.TrimStart('v'), xrLocal))
                    {
                        bool wasOn = vpnRunning && CurrentServer != null && Vpn.NeedsXray(CurrentServer.Link);
                        if (wasOn) await Vpn.Stop();
                        await Vpn.InstallXray(xr);
                        if (wasOn) await StartVpn(true);
                    }
                }
                catch (Exception e) { Log.Write("Ядро Xray: не удалось обновить: " + e.Message); }
            }

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
                            if (allowAuto && cfg.AutoUpdate) { await Telegram.InstallProxy(tg); Notify("tg-ws-proxy обновлён до версии " + tg.Version); }
                            else Notify("Доступна новая версия tg-ws-proxy: " + tg.Version);
                        }
                        else if (!quiet) Log.Write("tg-ws-proxy: установлена последняя версия (" + tgLocal + ")");
                    }
                    catch (Exception e) { Log.Write("tg-ws-proxy: не удалось проверить обновления: " + e.Message); }
                }
            }

            await CheckLauncherUpdate(quiet, allowAuto && cfg.AutoUpdate);
        }

        // Список IP-адресов для IPSet — раз в неделю вместе с проверкой обновлений. Если он изменился и используется,
        // обход перезапускается, чтобы winws перечитал список
        async Task AutoUpdateIpset()
        {
            string mode = Zapret.IpsetMode(Paths.Zapret);
            DateTime last;
            if (mode == null || (DateTime.TryParse(cfg.IpsetUpdated, out last) && (DateTime.Now - last).TotalDays < 7)) return;
            string file = Paths.InZapret(@"lists\ipset-all.txt");
            string target = mode == "loaded" ? file : file + ".backup";
            string before = File.Exists(target) ? File.ReadAllText(target) : "";
            try { await Zapret.UpdateIpsetList(); }
            catch (Exception e) { Log.Write("IPSet: не удалось обновить список: " + e.Message); return; }
            cfg.IpsetUpdated = DateTime.Now.ToString("yyyy-MM-dd");
            cfg.Save();
            bool changed = File.Exists(target) && File.ReadAllText(target) != before;
            if (changed && mode == "loaded" && Zapret.Running && !pausedByVpn) await StartBypass(false);
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
                if (!install) { Notify("Доступна новая версия лаунчера: " + launcherLatest.Version, new[] { "Обновить лаунчер", "launcher-update" }); return; }
                await SelfUpdate.Apply(launcherLatest);
                Restart();
            }
            catch (Exception e) { if (!quiet) Log.Write("Лаунчер: не удалось проверить обновления (" + cfg.LauncherRepo + "): " + e.Message); }
        }

        Task UpdateLauncherUi(bool ask)
        {
            return RunBusy(async () =>
            {
                launcherLatest = await SelfUpdate.Latest(cfg.LauncherRepo);
                if (!Util.TestNewer(launcherLatest.Version, Consts.Version))
                {
                    Dialog.Show(window, T("Установлена последняя версия лаунчера") + " (" + Consts.Version + ").");
                    return;
                }
                if (ask && Dialog.Show(window, T("Доступна версия") + " " + launcherLatest.Version + ". " + T("Обновить лаунчер сейчас? Обход продолжит работать."),
                    "Zapret Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await SelfUpdate.Apply(launcherLatest);
                Restart();
            }, "Проверка…");
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

        // ---------- Оформление ----------
        // Смена темы или акцента: снимок старого вида поверх окна плавно растворяется — без мигания
        void ApplyTheme()
        {
            Image snapshot = window.IsVisible ? Snapshot() : null;
            Grid root = E<Grid>("Root");
            if (snapshot != null) root.Children.Add(snapshot);
            Theme.Apply(cfg.Theme, cfg.Transparent, cfg.Accent);
            Theme.ApplyWindow(window);
            visualActive = null;
            cardState = null;
            UpdateStrategyList();
            BuildSitePanel();
            UpdateUi();
            RenderHistory();
            if (snapshot == null) return;
            DoubleAnimation fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(320))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += delegate { root.Children.Remove(snapshot); };
            snapshot.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        Image Snapshot()
        {
            try
            {
                FrameworkElement content = E<Grid>("Root");
                DpiScale dpi = VisualTreeHelper.GetDpi(window);
                System.Windows.Media.Imaging.RenderTargetBitmap bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                DrawingVisual dv = new DrawingVisual();
                using (DrawingContext dc = dv.RenderOpen())
                {
                    Rect rect = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                    dc.DrawRectangle(Theme.Brush("WindowBg"), null, rect);
                    dc.DrawRectangle(new VisualBrush(content), null, rect);
                }
                bmp.Render(dv);
                Image img = new Image { Source = bmp, IsHitTestVisible = false, Stretch = Stretch.Fill };
                Grid.SetRowSpan(img, 2);
                Grid.SetColumnSpan(img, 3);
                return img;
            }
            catch { return null; }
        }

        // ---------- Размер окна и масштаб ----------
        void RestoreWindowSize()
        {
            double k = cfg.Scale / 100.0;
            double w = cfg.Compact ? 520 * k : cfg.WindowWidth > 0 ? cfg.WindowWidth : 1000 * k, h = cfg.WindowHeight > 0 ? cfg.WindowHeight : 700 * k;
            Rect area = SystemParameters.WorkArea;
            if (cfg.Compact) SetCompact(true, false);
            window.Width = Math.Min(Math.Max(w, window.MinWidth), area.Width);
            window.Height = Math.Min(Math.Max(h, window.MinHeight * k), area.Height);
            if (cfg.WindowMaximized) window.WindowState = WindowState.Maximized;
            window.StateChanged += delegate { OnWindowStateChanged(); };
            OnWindowStateChanged();
        }

        // Развёрнутое окно с WindowChrome выходит за край экрана на ширину рамки — компенсируем отступом
        void OnWindowStateChanged()
        {
            bool max = window.WindowState == WindowState.Maximized;
            Thickness frame = SystemParameters.WindowResizeBorderThickness;
            window.BorderThickness = max ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4) : new Thickness(0);
            Button b = E<Button>("BtnMaximize");
            b.Content = max ? "" : "";
            b.ToolTip = T(max ? "Восстановить" : "Развернуть");
        }

        void SaveWindowSize()
        {
            cfg.WindowMaximized = window.WindowState == WindowState.Maximized;
            Rect r = window.RestoreBounds;
            if (!r.IsEmpty && r.Width > 0)
            {
                if (!cfg.Compact) cfg.WindowWidth = Math.Round(r.Width);   // в компактном виде ширину обычного окна не трогаем
                cfg.WindowHeight = Math.Round(r.Height);
            }
            cfg.Save();
        }

        // Компактный вид: только правая панель (переключатель, сайты, история), без меню и списка стратегий
        void SetCompact(bool on, bool resize)
        {
            double k = cfg.Scale / 100.0;
            if (on && resize && !cfg.Compact) SaveWindowSize();
            cfg.Compact = on;
            if (window.WindowState == WindowState.Maximized && resize) window.WindowState = WindowState.Normal;
            Grid root = E<Grid>("Root");
            root.ColumnDefinitions[0].Width = new GridLength(on ? 0 : 76);
            root.ColumnDefinitions[1].MinWidth = on ? 0 : 400;
            root.ColumnDefinitions[1].Width = on ? new GridLength(0) : new GridLength(5, GridUnitType.Star);
            E<FrameworkElement>("Sidebar").Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            E<FrameworkElement>("MidPanel").Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            window.MinWidth = (on ? 460 : 900) * k;
            if (resize)
            {
                double right = window.Left + window.Width;
                window.Width = on ? 520 * k : Math.Max(window.MinWidth, cfg.WindowWidth > 0 ? cfg.WindowWidth : 1000 * k);
                window.Left = Math.Max(SystemParameters.WorkArea.Left, right - window.Width);   // окно остаётся у того же правого края
            }
            Button b = E<Button>("BtnCompact");
            b.Content = on ? "" : "";
            b.ToolTip = T(on ? "Полный вид" : "Компактный вид");
            cfg.Save();
        }

        void ApplyScale()
        {
            double k = cfg.Scale / 100.0;
            E<Grid>("Root").LayoutTransform = k == 1 ? Transform.Identity : new ScaleTransform(k, k);
            window.MinWidth = (cfg.Compact ? 460 : 900) * k;
            window.MinHeight = 620 * k;
            if (flyout != null) F<FrameworkElement>("FlyCard").LayoutTransform = k == 1 ? Transform.Identity : new ScaleTransform(k, k);
        }

        void SetScale(int scale)
        {
            if (filling || cfg.Scale == scale) return;
            double ratio = (double)scale / cfg.Scale;
            cfg.Scale = scale;
            if (window.WindowState == WindowState.Normal)
            {
                Rect area = SystemParameters.WorkArea;
                window.Width = Math.Min(window.ActualWidth * ratio, area.Width);
                window.Height = Math.Min(window.ActualHeight * ratio, area.Height);
                window.Left = Math.Max(area.Left, Math.Min(window.Left, area.Right - window.Width));
                window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.Height));
            }
            ApplyScale();
            SaveWindowSize();
        }

        void SetAccent(string accent)
        {
            if (filling || cfg.Accent == accent) return;
            cfg.Accent = accent;
            cfg.Save();
            ApplyTheme();
        }

        // ---------- История за сутки ----------
        // Полоса за 24 часа: цвет — состояние обхода в эту минуту, пусто — лаунчер не работал
        void RenderHistory()
        {
            if (!window.IsVisible) return;
            Canvas bar = E<Canvas>("HistoryBar");
            bar.Children.Clear();
            double width = bar.ActualWidth, height = 10;
            if (width <= 0) return;
            DateTime now = DateTime.Now, from = now.AddHours(-24);
            List<KeyValuePair<DateTime, char>> points = StateHistory.Last24h();
            Dictionary<char, int> minutesBy = new Dictionary<char, int> { { 'o', 0 }, { 'p', 0 }, { 'e', 0 }, { 'f', 0 } };
            int i = 0;
            while (i < points.Count)
            {
                // соседние минуты с одинаковым состоянием — один отрезок
                int j = i;
                while (j + 1 < points.Count && points[j + 1].Value == points[i].Value && (points[j + 1].Key - points[j].Key).TotalMinutes <= 2) j++;
                char state = points[i].Value;
                minutesBy[state] += j - i + 1;
                DateTime start = points[i].Key, end = points[j].Key.AddMinutes(1);
                double x1 = (start - from).TotalMinutes / 1440 * width, x2 = (end - from).TotalMinutes / 1440 * width;
                Rectangle seg = new Rectangle { Width = Math.Max(1.5, x2 - x1), Height = height };
                seg.SetResourceReference(Shape.FillProperty, HistoryColor(state));
                seg.ToolTip = start.ToString("HH:mm") + "–" + end.ToString("HH:mm") + " · " + T(HistoryName(state));
                Canvas.SetLeft(seg, Math.Max(0, x1));
                bar.Children.Add(seg);
                i = j + 1;
            }
            Text("HistorySummary").Text = points.Count == 0 ? T("данных пока нет") : T("работал") + " " + Span(minutesBy['o']);
            WrapPanel legend = E<WrapPanel>("HistoryLegend");
            legend.Children.Clear();
            foreach (char c in new[] { 'o', 'p', 'e', 'f' })
            {
                if (minutesBy[c] == 0 && c != 'o') continue;
                StackPanel item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
                Ellipse dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                dot.SetResourceReference(Shape.FillProperty, HistoryColor(c));
                item.Children.Add(dot);
                TextBlock t = new TextBlock { Text = T(HistoryName(c)) + " " + Span(minutesBy[c]), FontSize = 11 };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                item.Children.Add(t);
                legend.Children.Add(item);
            }
        }

        static string HistoryColor(char c) { return c == 'o' ? "Accent" : c == 'p' ? "Wait" : c == 'e' ? "Fail" : "Idle"; }
        static string HistoryName(char c) { return c == 'o' ? "работал" : c == 'p' ? "пауза (VPN)" : c == 'e' ? "сбой" : "выключен"; }

        static string Span(int minutes)
        {
            return minutes >= 60 ? (minutes / 60) + " " + T("ч") + (minutes % 60 > 0 ? " " + (minutes % 60) + " " + T("мин") : "") : minutes + " " + T("мин");
        }

        // Карточка-переключатель: плавно подсвечивается бирюзовым, ползунок едет вправо
        void SetActive(bool on)
        {
            if (visualActive == on) return;
            bool first = !visualActive.HasValue;
            visualActive = on;
            TimeSpan d = TimeSpan.FromMilliseconds(first ? 0 : 260);
            Fade(E<UIElement>("ActiveLayer"), on ? 1 : 0, d);
            Fade(E<UIElement>("PowerIconOn"), on ? 1 : 0, d);
            Fade(E<UIElement>("ToggleTrackOn"), on ? 1 : 0, d);
            Fade(E<UIElement>("GlowBg"), on ? 0.9 : 0, d);
            Slide(E<Ellipse>("ToggleKnob"), on ? 28 : 0, d);
            if (on && !first) Ripple();
            animateCards = true;
            // пока обход работает, подсветка карточки медленно «дышит»
            UIElement glow = E<UIElement>("GlowBg");
            if (on)
            {
                DoubleAnimation breathe = new DoubleAnimation(0.9, 0.5, new Duration(TimeSpan.FromSeconds(2.6)))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = d,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                glow.BeginAnimation(UIElement.OpacityProperty, breathe, HandoffBehavior.Compose);
            }
        }

        // Волна от значка щита при включении
        void Ripple()
        {
            Ellipse r = E<Ellipse>("Ripple");
            ScaleTransform s = new ScaleTransform(1, 1);
            r.RenderTransform = s;
            Duration d = new Duration(TimeSpan.FromMilliseconds(700));
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 9, d) { EasingFunction = ease });
            s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 9, d) { EasingFunction = ease });
            r.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 0, d));
        }

        // Карточка-переключатель окрашивается по состоянию: жёлтая — пауза из-за VPN, красная — ошибка; значок щита меняется на паузу или предупреждение
        void UpdateCardState(string state)
        {
            if (state == cardState) return;
            cardState = state;
            bool tinted = state == "pause" || state == "error";
            Border layer = E<Border>("StateLayer");
            Border fly = F<Border>("FlyState");
            if (tinted)
            {
                Color c = Theme.Color(state == "pause" ? "Wait" : "Fail");
                Brush bg = new LinearGradientBrush(Color.FromArgb(0x38, c.R, c.G, c.B), Color.FromArgb(0x0C, c.R, c.G, c.B), 45);
                Brush border = new SolidColorBrush(Color.FromArgb(0x90, c.R, c.G, c.B));
                foreach (Border b in new[] { layer, fly }) if (b != null) { b.Background = bg; b.BorderBrush = border; }
            }
            TimeSpan d = TimeSpan.FromMilliseconds(260);
            Fade(layer, tinted ? 1 : 0, d);
            Fade(fly, tinted ? 1 : 0, d);
            Text("PowerIcon").Text = state == "pause" ? "" : state == "error" ? "" : "";
            Text("PowerIcon").SetResourceReference(TextBlock.ForegroundProperty,
                state == "on" ? "AccentText" : state == "pause" ? "Wait" : state == "error" ? "Fail" : "Muted");
        }

        static void Fade(UIElement e, double to, TimeSpan d)
        {
            if (e == null) return;
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, new Duration(d)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        static void Slide(FrameworkElement e, double x, TimeSpan d)
        {
            if (e == null) return;
            TranslateTransform tr = e.RenderTransform as TranslateTransform;
            if (tr == null || tr.IsFrozen) { tr = new TranslateTransform(); e.RenderTransform = tr; }
            tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, new Duration(d)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        }

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
            foreach (string p in new[] { "Strategies", "Domains", "Vpn", "Settings", "Log" })
            {
                FrameworkElement page = E<FrameworkElement>("Page" + p);
                page.Visibility = p == name ? Visibility.Visible : Visibility.Collapsed;
                if (p != name) continue;
                // смена страницы: лёгкое затухание и сдвиг снизу
                TranslateTransform tr = new TranslateTransform(0, 10);
                page.RenderTransform = tr;
                Duration d = new Duration(TimeSpan.FromMilliseconds(220));
                IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
                tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, d) { EasingFunction = ease });
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

        // ---------- Приветствие ----------
        static readonly string[][] OnboardingSteps =
        {
            new[] { "\uEA18", "Добро пожаловать в Zapret Launcher",
                "Лаунчер включает zapret одной кнопкой, следит, чтобы Discord, YouTube и Telegram открывались, и сам обновляется.", "Пропустить", "Дальше" },
            new[] { "\uE7F4", "Работает в фоне",
                "Закрытое окно сворачивается в трей. Клик по значку открывает мини-окно с переключателем и пингом. Включите «Автозапуск», чтобы обход запускался вместе с Windows.", "Пропустить", "Дальше" },
            new[] { "", "Встроенный VPN",
                "Есть свой VPN-сервер или подписка? Вставьте ключ на странице VPN: через него пойдут только выбранные сайты и программы, а Discord, YouTube и остальное — напрямую, на полной скорости.", "Пропустить", "Дальше" },
            new[] { "\uEC4A", "Найдём рабочую стратегию",
                "У каждого провайдера свои блокировки. Лаунчер по очереди проверит все стратегии и выберет лучшую — это займёт пару минут. Если включён VPN, выключите его на время проверки.", "Позже", "Подобрать сейчас" },
        };

        void ShowOnboarding()
        {
            onboardingStep = 0;
            FrameworkElement overlay = E<FrameworkElement>("Onboarding");
            overlay.Visibility = Visibility.Visible;
            overlay.Opacity = 0;
            Fade(overlay, 1, TimeSpan.FromMilliseconds(220));
            RenderOnboarding();
        }

        void RenderOnboarding()
        {
            string[] s = OnboardingSteps[onboardingStep];
            Text("OnbIcon").Text = s[0];
            Text("OnbTitle").Text = T(s[1]);
            Text("OnbText").Text = T(s[2]);
            E<Button>("OnbSkip").Content = T(s[3]);
            E<Button>("OnbNext").Content = T(s[4]);
            StackPanel dots = E<StackPanel>("OnbDots");
            dots.Children.Clear();
            for (int i = 0; i < OnboardingSteps.Length; i++)
            {
                Border dot = new Border { Width = i == onboardingStep ? 20 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0) };
                dot.SetResourceReference(Border.BackgroundProperty, i == onboardingStep ? "Accent" : "Line");
                dots.Children.Add(dot);
            }
            FrameworkElement card = E<FrameworkElement>("OnbCard");
            TranslateTransform tr = new TranslateTransform(16, 0);
            card.RenderTransform = tr;
            tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(16, 0, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        async Task OnboardingNext(bool skip)
        {
            bool last = onboardingStep == OnboardingSteps.Length - 1;
            if (!skip && !last) { onboardingStep++; RenderOnboarding(); return; }
            cfg.OnboardingDone = true;
            cfg.Save();
            E<FrameworkElement>("Onboarding").Visibility = Visibility.Collapsed;
            if (!skip && last) await StartAutoPickUi(false);
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
            F<Image>("FlyIcon").Source = imageIcons[trayState ?? "off"];
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

        string DomainKind { get { return E<RadioButton>("DomGeneral").IsChecked == true ? "general" : "exclude"; } }

        void UpdateDomainUi()
        {
            if (!Directory.Exists(Paths.Zapret)) return;
            List<string> domains = Zapret.UserDomains(DomainKind);
            E<ListBox>("DomainList").ItemsSource = domains;
            E<FrameworkElement>("DomainEmpty").Visibility = domains.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            Text("DomainEmptyText").Text = T(DomainKind == "general" ? "Своих доменов пока нет — добавьте сайт, который нужно разблокировать"
                                                                      : "Исключений нет — сюда добавляют сайты, которые ломаются от обхода");
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
            info.Text = bad.Count > 0 ? T("Не похоже на домен") + ": " + string.Join(", ", bad)
                      : fresh.Count == 0 && added.Count > 0 ? T("Этот домен уже есть в списке")
                      : T("Поддомены учитываются автоматически. Изменения применяются сразу.");
            if (fresh.Count == 0) return;
            Zapret.SaveUserDomains(DomainKind, current.Concat(fresh));
            Log.Write("Добавлено в «" + (DomainKind == "general" ? "Обходить" : "Не трогать") + "»: " + string.Join(", ", fresh));
            E<TextBox>("DomainInput").Text = "";
            UpdateDomainUi();
            AutoBackup();
            await RestartIfRunning();
        }

        async Task RemoveDomain(string domain)
        {
            Zapret.SaveUserDomains(DomainKind, Zapret.UserDomains(DomainKind).Where(d => d != domain));
            Log.Write("Удалён домен: " + domain);
            UpdateDomainUi();
            AutoBackup();
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
            Click("BtnVpnQrFile", QrFromFile);
            Click("BtnVpnQrScreen", QrFromScreen);
            ClickSync("SwVpnAuto", () => { cfg.VpnAutoBest = E<CheckBox>("SwVpnAuto").IsChecked == true; cfg.Save(); });
            window.Activated += delegate { if (E<FrameworkElement>("PageVpn").Visibility == Visibility.Visible) CheckClipboard(); };
            Click("BtnVpnListAdd", AddVpnListItem);
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
                    foreach (string q in new[] { "Servers", "Routes", "Conn" })
                        E<FrameworkElement>("VpnPane" + q).Visibility = q == p ? Visibility.Visible : Visibility.Collapsed;
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
            TextBox input = E<TextBox>("DomainInput");
            input.KeyDown += async (s, e) => { if (e.Key == Key.Return) await AddDomain(); };
            input.TextChanged += delegate { Text("DomainHint").Visibility = input.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible; };
            E<ListBox>("DomainList").AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(async (s, e) =>
            {
                Button b = e.OriginalSource as Button;
                if (b != null && b.Tag is string) await RemoveDomain((string)b.Tag);
            }));

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

        // «Выход» из меню трея: обход и Telegram-прокси выключаются вместе с лаунчером.
        // Перезапуск после самообновления и смены языка идёт через Exit() напрямую — там обход не прерывается
        async Task QuitAndStop()
        {
            if (exiting || quitting) return;
            quitting = true;
            if (picking) cancelPick = true;
            wantRunning = false;   // чтобы сторож не перезапустил winws, пока лаунчер закрывается
            pausedByVpn = false;
            if (statusTimer != null) statusTimer.Stop();
            HideFlyout();
            tray.Text = "Zapret: " + T("выключаю обход…");
            try
            {
                await Zapret.Stop(false);
                await Telegram.StopProxy();
                await Vpn.Stop();   // VPN тоже выключается вместе с лаунчером; при следующем запуске включится снова, если был включён
            }
            catch (Exception e) { Log.Write("Не удалось остановить обход при выходе: " + e.Message); }
            Log.Write("Лаунчер закрыт, обход выключен");
            Exit();
        }

        bool quitting;

        public void Exit()
        {
            if (!exiting) { SaveWindowSize(); StateHistory.Save(); AutoBackup(); }
            exiting = true;
            foreach (DispatcherTimer t in new[] { statusTimer, updateTimer }) if (t != null) t.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (flyout != null) flyout.Close();
            window.Close();
            Application.Current.Shutdown();
        }
    }
}
