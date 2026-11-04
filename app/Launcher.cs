// Главное окно: общие поля, запуск, таймер, фоновые проверки, выход. Остальное — в Launcher.*.cs
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
        int tick, onboardingStep;
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
            E<ListBox>("LogList").ItemsSource = logView;
            Log.Line += line => window.Dispatcher.BeginInvoke(new Action(() => AddLogLine(line)));
            try { foreach (string line in File.ReadLines(Paths.Log).Reverse().Take(600).Reverse()) AddLogLine(line); } catch { }

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
        // Демо-режим для скриншотов (переменная окружения ZAPRETLAUNCHER_DEMO=1): только окно. Без автозапуска, ярлыков, записей
        // в реестре, обновлений, обхода, VPN и фоновых таймеров — копия лаунчера ничего не меняет в системе и не трогает настоящий
        static readonly bool DemoMode = Environment.GetEnvironmentVariable("ZAPRETLAUNCHER_DEMO") == "1";

        async Task Startup()
        {
            if (DemoMode) { RefreshVpnUi(); return; }
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
            vpnLocked = !vpnRunning && Vpn.Locked;   // лаунчер перезапустился во время блокировки
            if (vpnRunning) { Vpn.ReadApiEndpoint(); vpnConnectedAt = Vpn.StartedAt(); vpnActive = VpnKey(); }   // лаунчер перезапустился, а VPN работал
            if (cfg.VpnOn && !vpnRunning && CurrentServer != null)
            {
                Exception error = null;
                try { await StartVpn(true); }
                catch (Exception e) { error = e; }
                if (error != null)
                {
                    Log.Write("VPN не подключился: " + error.Message);
                    if (cfg.VpnKillSwitch) await LockVpn(error.Message);   // и после запуска Windows сайты из списка не уйдут напрямую
                }
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
            watchdog.Reset();
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
                RunPendingRestarts();
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
