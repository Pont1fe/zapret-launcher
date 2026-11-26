// Встроенный VPN: подключение, серверы, пинг, трафик, подписки, ключи из буфера обмена и QR-кода
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
        // ---------- Встроенный VPN ----------
        bool vpnRunning;                 // обновляется в тике: опрос процессов ядра недешёвый
        int vpnFails;
        DateTime vpnLastStart = DateTime.MinValue;

        VpnServer CurrentServer { get { return cfg.VpnServers.Count == 0 ? null : cfg.VpnServers[Math.Min(cfg.VpnSelected, cfg.VpnServers.Count - 1)]; } }

        // Сервер AmneziaWG в режиме «Весь трафик» работает без sing-box: журнала соединений нет, sing-box ему не нужен
        bool AwgDirect { get { return CurrentServer != null && Awg.IsAwg(CurrentServer.Link) && cfg.VpnMode == "all"; } }

        // С каким сервером и в каком режиме VPN подключён на самом деле. Выбранный в списке сервер может отличаться
        // (выбрали, пока VPN переподключался) — тогда сторож VPN переподключает, а карточка пишет «переподключается»
        string vpnActive;
        int vpnRouteRevision;
        int vpnAppliedRevision;
        string VpnKey() { return CurrentServer == null ? null : CurrentServer.Link + "|" + cfg.VpnMode; }
        bool VpnSwitching { get { return vpnRunning && (vpnAppliedRevision != vpnRouteRevision || vpnActive != VpnKey()); } }

        // ---------- Пинг, автовыбор сервера, трафик ----------
        readonly Dictionary<string, int> vpnPing = new Dictionary<string, int>();   // ссылка → мс (-1 нет ответа, -2 UDP)
        readonly Dictionary<string, double> vpnSpeed = new Dictionary<string, double>();   // ссылка → Мбит/с по последнему замеру (-1 не скачалось)
        bool vpnPinging, vpnSpeedTesting;

        // Скорость: выбранный сервер и самые быстрые по проверке (всего до 5), по 5 секунд на каждый. Качаем через тестовое ядро —
        // работающий VPN не переподключается. AmneziaWG так не проверить (как и пингом)
        async Task SpeedTestServers()
        {
            if (vpnSpeedTesting || vpnPinging || cfg.VpnServers.Count == 0) return;
            if (!cfg.VpnServers.Any(s => vpnPing.ContainsKey(s.Link))) await PingVpnServers();
            List<VpnServer> servers = ServerList.SpeedCandidates(cfg.VpnServers, vpnPing, cfg.VpnSelected, 5).Select(i => cfg.VpnServers[i]).ToList();
            if (servers.Count == 0) { Text("VpnInfo").Text = T("Скорость проверить не на чем: серверы не отвечают (AmneziaWG так не проверить)"); return; }
            vpnSpeedTesting = true;
            RefreshVpnUi();
            int done = 0;
            Text("VpnInfo").Text = T("Замеряю скорость") + ": 0 / " + servers.Count + "…";
            try
            {
                await Vpn.Speeds(servers, 5, (i, mbps) =>
                {
                    vpnSpeed[servers[i].Link] = mbps;
                    Text("VpnInfo").Text = T("Замеряю скорость") + ": " + (++done) + " / " + servers.Count + "…";
                    RefreshVpnUi();
                });
                VpnServer best = servers.Where(s => vpnSpeed.ContainsKey(s.Link) && vpnSpeed[s.Link] > 0).OrderByDescending(s => vpnSpeed[s.Link]).FirstOrDefault();
                Text("VpnInfo").Text = best == null ? T("Ни через один сервер не скачался тестовый файл")
                                                    : T("Быстрее всех") + ": " + Vpn.DisplayName(best.Name) + " — " + Formats.Mbps(vpnSpeed[best.Link]);
                Log.Write("VPN: скорость — " + string.Join(", ", servers.Select(s => Vpn.DisplayName(s.Name) + " " + (vpnSpeed.ContainsKey(s.Link) && vpnSpeed[s.Link] > 0 ? Formats.Mbps(vpnSpeed[s.Link]) : "—"))));
            }
            catch (Exception e) { Text("VpnInfo").Text = T("Не удалось замерить скорость: ") + T(e.Message); }
            finally { vpnSpeedTesting = false; RefreshVpnUi(); }
        }
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

        int BestServer(int exclude = -1) { return ServerList.Best(cfg.VpnServers, vpnPing, exclude); }

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

        bool vpnHealthRecovery;

        async Task VpnHealth()
        {
            if (!cfg.VpnOn || !vpnRunning || busy || vpnHealthChecking || VpnSwitching || quitting ||
                CurrentServer == null || DateTime.Now < vpnHealthDue) return;

            vpnHealthDue = DateTime.Now.AddSeconds(30);
            int generation = vpnHealthGeneration;
            int revision = vpnRouteRevision;
            int ms = await Vpn.VerifyTraffic();
            if (generation != vpnHealthGeneration || revision != vpnRouteRevision || VpnSwitching || busy || quitting) return;

            vpnVerifiedMs = ms;
            vpnHealthIssue = ms >= 0 ? null : ZapretLauncher.VpnHealth.Advice(null, false);
            RefreshVpnHealthUi();
            if (ms >= 0) { vpnDeadChecks = 0; return; }
            if (++vpnDeadChecks < 2) return;

            await RunBusy(async () =>
            {
                // Закрываем доступ перед заменой ядра и ждём успешную проверку нового туннеля.
                ApplyVpnFirewall(cfg.VpnKillSwitch);
                vpnHealthRecovery = true;
                vpnRunning = false;
                vpnLocked = VpnFirewall.Enabled;
                vpnHealthIssue = ZapretLauncher.VpnHealth.Unresponsive;
                SetConnectionEvent(vpnHealthIssue);
                RefreshVpnUi();

                if (cfg.VpnAutoBest && cfg.VpnServers.Count > 1)
                {
                    await PingVpnServers();
                    if (revision != vpnRouteRevision) return;
                    int best = BestServer(cfg.VpnSelected);
                    if (best >= 0) cfg.VpnSelected = best;
                }

                await StartVpnOrLock(true, background: true);
            }, "VPN…");
            RefreshVpnUi();
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
                    vpnSpeedText = "↓ " + Formats.Mbps(down) + "  ↑ " + Formats.Mbps(up) + "  ·  " + T("за сессию") + " " + Formats.Bytes(t[0] + t[1]);
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
            TextBlock peak = new TextBlock { Text = Formats.Mbps(max), FontSize = 10 };
            peak.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            Canvas.SetRight(peak, 0);
            Canvas.SetTop(peak, -1);
            c.Children.Add(peak);
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
                    else if (Awg.LooksLikeConf(text))   // скопировали содержимое файла AmneziaWG — если таких настроек ещё нет
                    {
                        string key = WireGuard.Import(text, null).Link.Split('#')[0];
                        if (!cfg.VpnServers.Any(s => s.Link.Split('#')[0] == key)) link = text;
                    }
                }
            }
            catch { }   // буфер обмена занят другой программой
            clipLink = link;
            E<FrameworkElement>("VpnClipBar").Visibility = link != null ? Visibility.Visible : Visibility.Collapsed;
            if (link != null && Awg.LooksLikeConf(link)) Text("VpnClipText").Text = T("В буфере обмена файл настроек VPN. Добавить?");
            else if (link != null)
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
            string text = clipLink;
            clipLink = null;
            E<FrameworkElement>("VpnClipBar").Visibility = Visibility.Collapsed;
            if (Awg.LooksLikeConf(text)) { await AddAwgConf(text, null); return; }   // многострочный текст в однострочное поле не поместится
            E<TextBox>("VpnInput").Text = text;
            await AddVpnServers();
        }

        // ---------- Файл настроек AmneziaWG (в приложении Amnezia: подписка → «Файлы конфигурации») ----------
        async Task AwgFromFile()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = T("Импортировать WireGuard или AmneziaWG"), Filter = T("Файлы настроек") + " (*.conf)|*.conf|" + T("Все файлы") + " (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(window) != true) return;
            string text;
            try { text = System.IO.File.ReadAllText(dlg.FileName); }
            catch (Exception e) { Text("VpnInfo").Text = T("Не удалось открыть файл: ") + e.Message; return; }
            await AddAwgConf(text, dlg.FileName);
        }

        async Task AddAwgConf(string conf, string fileName)
        {
            TextBlock info = Text("VpnInfo");
            VpnServer server;
            try { server = WireGuard.Import(conf, fileName); }
            catch (Exception e) { info.Text = T(e.Message); return; }
            // тот же файл скачали заново (старый Amnezia отключает) — заменяем, а не добавляем второй
            int same = cfg.VpnServers.FindIndex(s => string.IsNullOrEmpty(s.SubscriptionId)
                && (Awg.IsAwg(s.Link) || WireGuard.IsLink(s.Link)) && s.Name == server.Name);
            if (same >= 0) cfg.VpnServers[same] = server;
            else if (!cfg.VpnServers.Any(s => s.Link == server.Link)) cfg.VpnServers.Add(server);
            info.Text = T(same >= 0 ? "Сервер обновлён" : "Сервер добавлен") + ": " + Vpn.DisplayName(server.Name) + ". " +
                        T("Ключи хранятся только на этом компьютере, в настройках лаунчера");
            Log.Write("VPN: " + (same >= 0 ? "обновлён" : "добавлен") + " сервер из файла");
            await ApplyVpnChanges();
        }

        async Task StartVpn(bool quiet)
        {
            vpnDeadChecks = 0;
            vpnHealthDue = DateTime.Now.AddSeconds(30);
            ResetVpnHealth();
            try { await StartVpnCore(quiet); }
            catch (Exception e)
            {
                vpnRunning = Vpn.Running;
                vpnHealthIssue = ZapretLauncher.VpnHealth.Advice(e.ToString(), true);
                Log.Write("VPN: " + Report.SanitizeVpn(e.Message, cfg));
                RefreshVpnHealthUi();
                throw new Exception(T(vpnHealthIssue), e);
            }
        }

        async Task StartVpnCore(bool quiet)
        {
            bool reconnecting = cfg.VpnOn && vpnConnectedAt.HasValue;
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
            string key = VpnKey();
            int revision = vpnRouteRevision;
            string mode = cfg.VpnMode;
            string[] domains = cfg.VpnDomains.ToArray(), apps = cfg.VpnApps.ToArray();
            string[] scope = cfg.VpnKillSwitch ? VpnFirewall.AppScope(cfg.VpnProtectedApps) : new string[0];
            bool protect = scope.Length > 0;
            apps = apps.Union(scope, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!protect && VpnFirewall.Enabled) VpnFirewall.Clear();
            if (protect)
            {
                VpnFirewall.ApplyScoped(true, 0, scope);
                cfg.VpnOn = true;
                cfg.Save();
                if (!(mode == "all" && Awg.IsAwg(server.Link)) && Vpn.Version == null) await Vpn.Install(await Vpn.Latest());
                if (Awg.IsAwg(server.Link) && Awg.Version == null) await Awg.Install(await Awg.Latest());
                if (Vpn.NeedsXray(server.Link) && Vpn.XrayVersion == null) await Vpn.InstallXray(await Vpn.XrayLatest());
            }
            if (revision != vpnRouteRevision) { vpnRestartPending = true; return; }
            if (protect) VpnFirewall.ApplyScoped(true, 0, scope);
            await Vpn.Start(server, mode, domains, apps);
            int recoveryMs = -1;
            if (vpnHealthRecovery)
            {
                recoveryMs = await Vpn.VerifyTraffic();
                if (recoveryMs < 0)
                {
                    await Vpn.Stop();
                    throw new InvalidOperationException(ZapretLauncher.VpnHealth.Unresponsive);
                }
            }
            if (revision != vpnRouteRevision)
            {
                cfg.VpnOn = true;
                cfg.Save();
                vpnRestartPending = true;
                vpnRunning = false;
                await Vpn.Stop();
                return;
            }
            if (protect)
            {
                Exception failure = null;
                try
                {
                    ulong tunnel = VpnFirewall.TunnelLuid();
                    if (tunnel == 0) throw new InvalidOperationException("Адаптер VPN не появился — подключение отменено");
                    VpnFirewall.ApplyScoped(true, tunnel, scope);
                }
                catch (Exception e) { failure = e; }
                if (failure != null)
                {
                    try { await Vpn.Stop(); }
                    catch (Exception e) { Log.Write("VPN: " + e.Message); }
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }
            }
            vpnActive = key;
            vpnAppliedRevision = revision;
            vpnRunning = true;
            vpnLocked = false;
            vpnLockShown = false;
            vpnFails = 0;
            vpnConnectedAt = DateTime.Now;
            vpnSpeeds.Clear();
            cfg.VpnHelpDone = true;   // подключились — подсказка «Как начать» больше не нужна
            cfg.VpnOn = true;
            cfg.Save();
            SetConnectionEvent(reconnecting ? "VPN переподключился" : "VPN подключён", Vpn.DisplayName(server.Name) + " · " + T(VpnInfo.ModeName(cfg.VpnMode)));
            vpnHealthRecovery = false;
            if (recoveryMs >= 0)
            {
                vpnVerifiedMs = recoveryMs;
                vpnHealthIssue = null;
                RefreshVpnHealthUi();
            }
            else
            {
                Task check = CheckVpnTraffic();
            }
        }

        int vpnHealthGeneration;
        bool vpnHealthChecking;
        int vpnVerifiedMs = -1;
        string vpnHealthIssue;

        void ResetVpnHealth()
        {
            vpnHealthGeneration++;
            vpnHealthChecking = false;
            vpnVerifiedMs = -1;
            vpnHealthIssue = vpnHealthRecovery ? ZapretLauncher.VpnHealth.Unresponsive : null;
            RefreshVpnHealthUi();
        }

        async Task CheckVpnTraffic()
        {
            if (!vpnRunning || vpnHealthChecking || quitting) return;
            ResetVpnHealth();
            int generation = vpnHealthGeneration;
            vpnHealthChecking = true;
            RefreshVpnHealthUi();
            // Сравниваем записи до и после проверки: старая ошибка могла относиться к другому запросу.
            string before = await Task.Run(() => Vpn.LastLogLine());
            int ms = -1;
            try { ms = await Vpn.VerifyTraffic(); }
            catch (Exception) { }
            string after = ms < 0 ? await Task.Run(() => Vpn.LastLogLine()) : null;
            if (generation != vpnHealthGeneration) return;
            vpnHealthChecking = false;
            if (quitting) return;
            vpnVerifiedMs = Vpn.Running ? ms : -1;
            if (vpnVerifiedMs >= 0) vpnDeadChecks = 0;
            vpnHealthIssue = vpnVerifiedMs < 0 ? ZapretLauncher.VpnHealth.Advice(before == after || AwgDirect ? null : after, false) : null;
            Log.Write(vpnHealthIssue ?? ("Проверочный запрос прошёл через VPN: " + vpnVerifiedMs + " " + T("мс")));
            RefreshVpnHealthUi();
        }

        async Task RetryVpnHealth()
        {
            if (busy || quitting) return;
            if (vpnRunning && !VpnSwitching) await CheckVpnTraffic();
            else
            {
                await RunBusy(StartVpnOrLock, "VPN…");
                RefreshVpnUi();
            }
        }

        void RefreshVpnHealthUi()
        {
            bool ready = vpnRunning && !VpnSwitching;
            bool issue = vpnHealthIssue != null;
            Text("VpnStatus").Text = T(ready ? (vpnVerifiedMs >= 0 ? "Подключён" : "Туннель запущен")
                : VpnProtectionBlocking ? "Трафик приостановлен" : cfg.VpnOn ? "VPN переподключается…" : "VPN выключен");
            Text("VpnHealthText").Text = ready && vpnHealthChecking ? T("Проверяю трафик через VPN…")
                : ready && vpnVerifiedMs >= 0 ? T("Проверочный запрос прошёл через VPN") + " · " + vpnVerifiedMs + " " + T("мс")
                : issue ? T(vpnHealthIssue) : T("Трафик через VPN ещё не проверен");
            E<FrameworkElement>("VpnHealthPanel").Visibility = ready || issue ? Visibility.Visible : Visibility.Collapsed;
            Button retry = E<Button>("BtnVpnHealth");
            retry.Content = T(ready ? "Повторить проверку" : "Повторить подключение");
            retry.IsEnabled = !vpnHealthChecking && !busy;
            E<Button>("BtnVpnHealthServers").Visibility = issue && cfg.VpnServers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            E<Ellipse>("VpnDot").SetResourceReference(Shape.FillProperty,
                ready && vpnVerifiedMs >= 0 ? "Ok" : vpnLocked || issue ? "Fail" : ready || cfg.VpnOn ? "Wait" : "Idle");
        }

        async Task StopVpn()
        {
            vpnHealthRecovery = false;
            vpnDeadChecks = 0;
            ResetVpnHealth();
            cfg.VpnOn = false;
            cfg.Save();
            await Task.Run(() => VpnFirewall.Clear());
            await Vpn.Stop();
            vpnRunning = false;
            vpnActive = null;
            vpnLocked = false;   // выключили сами — блокировка тоже снимается
            vpnLockShown = false;
            SetConnectionEvent("VPN отключён");
        }

        async Task ToggleVpn()
        {
            bool wasOn = vpnRunning || Vpn.Running || vpnLocked;   // при блокировке нажатие выключает VPN вместе с ней
            VpnBusy(true);
            try
            {
                if (wasOn) await RunBusy(StopVpn, "VPN…");
                else await RunBusy(() => StartVpnOrLock(false), "VPN…");
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

        // Подключился — карточка (уже цвета акцента) коротко вспыхивает белым
        void VpnFlash()
        {
            E<UIElement>("VpnFlash").BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.35, 0, new Duration(TimeSpan.FromMilliseconds(700))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // Изменились серверы, режим или списки — работающий VPN перезапускаем с новыми настройками
        async Task ApplyVpnChanges()
        {
            vpnRouteRevision++;
            cfg.Save();
            RefreshVpnUi();
            // по cfg.VpnOn, а не vpnRunning: пока VPN переподключается, vpnRunning на мгновение false — и смена сервера терялась
            if (!cfg.VpnOn) return;
            if (busy) vpnRestartPending = true;
            else { await RunBusy(StartVpnOrLock, "VPN…"); RefreshVpnUi(); }
        }

        // Ядро VPN упало, а VPN должен работать — перезапускаем (не чаще раза в 30 секунд, после двух неудач — сдаёмся)
        async Task VpnGuard()
        {
            if (busy || quitting) return;
            vpnRunning = Vpn.Running;
            vpnLocked = !vpnRunning && (Vpn.Locked || (cfg.VpnOn && VpnFirewall.Enabled));
            if (cfg.VpnOn && VpnSwitching && !busy && !vpnRestartPending)
            {
                // подключён не тот сервер или режим, что выбран (смену не успели применить) — переподключаемся
                await RunBusy(StartVpnOrLock, "VPN…");
                RefreshVpnUi();
                return;
            }
            // При блокировке переподключаемся реже. Политика Windows остаётся включённой.
            if (!cfg.VpnOn || CurrentServer == null) { vpnCheckAfterResume = false; return; }
            if (vpnCheckAfterResume && vpnRunning)
            {
                vpnCheckAfterResume = false;
                int revision = vpnRouteRevision;
                int generation = vpnHealthGeneration;
                int result = await Vpn.VerifyTraffic();
                if (quitting || busy || !cfg.VpnOn || revision != vpnRouteRevision || generation != vpnHealthGeneration || result >= 0) return;
                await RunBusy(StartVpnOrLock, "VPN…");
                return;
            }
            if (vpnRunning || (!vpnCheckAfterResume && (DateTime.Now - vpnLastStart).TotalSeconds < (vpnLocked ? 120 : 30))) return;
            vpnCheckAfterResume = false;
            bool wasLocked = vpnLocked;
            await RunBusy(async () =>
            {
                if (!wasLocked) Log.Write("VPN: ядро не работает — перезапускаю (" + Vpn.LastLogLine() + ")");
                Exception error = null;
                try { await StartVpn(true); }
                catch (Exception e) { error = e; }
                if (error == null) { if (wasLocked && vpnRunning) Notify("VPN снова работает — блокировка снята"); }
                else if (VpnFirewall.Enabled) LockVpn(error.Message);
                else if (++vpnFails >= 2)
                {
                    cfg.VpnOn = false;
                    cfg.Save();
                    Notify("VPN не удалось перезапустить: " + error.Message);
                }
                RefreshVpnUi();
            }, "VPN…");
        }

        // Переподключение после смены сервера, режима или списков: не вышло, а включено «не пускать мимо VPN» — блокировка
        Task StartVpnOrLock() { return StartVpnOrLock(true); }

        async Task StartVpnOrLock(bool quiet, bool background = false)
        {
            Exception error = null;
            try { await StartVpn(quiet); }
            catch (Exception e) { error = e; }
            if (error == null) return;
            if (!cfg.VpnKillSwitch || !VpnFirewall.Enabled)
            {
                if (background) return; // StartVpn has already recorded the failure; VpnGuard will retry.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
            LockVpn(error.Message);
        }

        // ---------- «Не пускать мимо VPN» ----------
        bool vpnLocked;      // Windows продолжает блокировать прямые подключения
        bool vpnLockShown;   // о блокировке уже сообщили — при каждой новой попытке подключиться не повторяем
        bool vpnCheckAfterResume;

        void ApplyVpnFirewall(bool enabled)
        {
            string[] scope = VpnFirewall.AppScope(cfg.VpnProtectedApps);
            bool active = enabled && scope.Length > 0;
            VpnFirewall.ApplyScoped(active, 0, scope);
        }

        void LockVpn(string reason)
        {
            try
            {
                ApplyVpnFirewall(true);
            }
            catch (Exception e)
            {
                // WFP остаётся включённым даже при отказе запасного ядра блокировки.
                Log.Write("VPN: не удалось включить блокировку — " + e.Message);
                cfg.VpnOn = VpnFirewall.Enabled;
                cfg.Save();
                vpnRunning = false;
                vpnLocked = VpnFirewall.Enabled;
                Notify("VPN не удалось перезапустить: " + reason);
                return;
            }
            vpnRunning = false;
            vpnLocked = VpnFirewall.Enabled;
            if (!vpnLocked || vpnLockShown) return;
            vpnLockShown = true;
            Log.Write("VPN не работает (" + reason + ") — " + LockText());
            SetConnectionEvent(LockText());
            Notify(T("VPN не работает") + " — " + T(LockText()));
        }

        bool VpnProtectionBlocking { get { return VpnFirewall.Enabled && !vpnRunning; } }

        string VpnProtectionStatus()
        {
            if (VpnProtectionBlocking) return T(LockText());
            if (VpnFirewall.Enabled)
                return T("Защита активна для выбранных программ");
            return T(!cfg.VpnKillSwitch ? "Защита выключена" : cfg.VpnProtectedApps.Count == 0 ? "Выберите программы для защиты." : cfg.VpnOn ? "Защита ещё не активна" : "Защита включится при запуске VPN");
        }

        string LockText()
        {
            return "Интернет для выбранных программ на паузе, пока VPN не заработает";
        }

        async Task AddVpnServers()
        {
            string text = E<TextBox>("VpnInput").Text.Trim();
            if (text.Length == 0) return;
            TextBlock info = Text("VpnInfo");
            bool subscription = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            info.Text = T(subscription ? "Загружаю подписку…" : "Проверяю ключ…");
            if (subscription)
            {
                var feed = cfg.VpnSubscriptions.FirstOrDefault(item => item.Url == text)
                    ?? new VpnSubscription { Id = Guid.NewGuid().ToString("N"), Url = text };
                if (!await RefreshSubscription(feed, true)) return;
            }
            else
            {
                List<VpnServer> found;
                try { found = await Vpn.Parse(text); }
                catch (Exception e) { info.Text = T(e.Message); return; }
                VpnServer selected = CurrentServer;
                foreach (VpnServer server in found)
                    if (!cfg.VpnServers.Any(s => s.Link == server.Link && string.IsNullOrEmpty(s.SubscriptionId))) cfg.VpnServers.Add(server);
                info.Text = T("Добавлено серверов") + ": " + found.Count;
                await SaveSubscriptionChanges(selected);
            }
            if (E<TextBox>("VpnInput").Text.Trim() == text) E<TextBox>("VpnInput").Text = "";
        }

        readonly HashSet<string> subscriptionDownloads = new HashSet<string>(StringComparer.Ordinal);
        bool maintainingSubscriptions;

        async Task RunSubscriptionMaintenance(bool refresh)
        {
            if (maintainingSubscriptions || quitting || exiting) return;
            maintainingSubscriptions = true;
            try
            {
                if (refresh) await RefreshSubscriptions();
                else await CheckSubscription();
            }
            catch (Exception e) { Log.Write("VPN: " + Report.SanitizeVpn(e.Message, cfg)); }
            finally { maintainingSubscriptions = false; }
        }

        async Task<bool> RefreshSubscription(VpnSubscription feed, bool add = false)
        {
            if (!subscriptionDownloads.Add(feed.Url)) return false;
            try
            {
                string info = null;
                List<VpnServer> found = await Vpn.Parse(feed.Url, value => info = value);
                if (quitting || exiting) return false;
                if (!cfg.VpnSubscriptions.Contains(feed))
                {
                    if (!add) return false;
                    cfg.VpnSubscriptions.Add(feed);
                }
                VpnServer selected = CurrentServer;
                feed.ReplaceServers(cfg, found);
                feed.Info = info;
                feed.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                Text("VpnInfo").Text = feed.Host + " · " + T("Подписка обновлена, серверов") + ": " + found.Count;
                await SaveSubscriptionChanges(selected);
                if (cfg.VpnSubscriptions.Contains(feed)) WarnSubscription(feed);
                return true;
            }
            catch (Exception e)
            {
                Text("VpnInfo").Text = feed.Host + " · " + Report.Sanitize(e.Message, feed.Url, Report.ServerHosts(cfg));
                return false;
            }
            finally { subscriptionDownloads.Remove(feed.Url); }
        }

        async Task SaveSubscriptionChanges(VpnServer previous)
        {
            VpnServer selected = CurrentServer;
            if ((previous == null ? null : previous.Link) == (selected == null ? null : selected.Link))
            {
                cfg.Save();
                RefreshVpnUi();
            }
            else await ApplyVpnChanges();
        }

        async Task RefreshSubscriptions()
        {
            foreach (VpnSubscription feed in cfg.VpnSubscriptions.ToList())
                if (!quitting && !exiting && cfg.VpnSubscriptions.Contains(feed) && feed.IsStale(DateTime.Now, 24))
                    await RefreshSubscription(feed);
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

        string vpnSource;
        bool vpnFavoritesOnly;
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
            VpnSubscription source = cfg.VpnSubscriptions.FirstOrDefault(feed => feed.Id == s.SubscriptionId);
            TextBlock name = new TextBlock { Text = Vpn.DisplayName(s.Name), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                                             ToolTip = source == null ? s.Link.Substring(0, s.Link.IndexOf("://") + 3) + "…" : SubscriptionTitle(source),
                                             Margin = new Thickness(0, 0, SpeedOf(s) > 0 ? 150 : vpnPing.ContainsKey(s.Link) ? 70 : 4, 0) };
            if (selected) name.FontWeight = FontWeights.SemiBold;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            // качество сервера «полосками сигнала», как у Wi-Fi: 3 — быстро, 1 — медленно, пустые и красный текст — не работает
            int ms;
            if (vpnPing.TryGetValue(s.Link, out ms) && ms == Vpn.PingUdp)
            {
                // AmneziaWG проверить без подключения нельзя — это не «не работает»
                TextBlock udp = new TextBlock { Text = T("без проверки"), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0),
                                                ToolTip = T("Такой сервер можно проверить, только подключившись к нему") };
                udp.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
                Grid.SetColumn(udp, 1);
                row.Children.Add(udp);
            }
            else if (vpnPing.ContainsKey(s.Link))
            {
                double speed = SpeedOf(s);
                StackPanel signal = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0),
                                                     ToolTip = (ms < 0 ? T("Сайт через этот сервер не открылся") : T("Сайт через сервер открылся за") + " " + ms + " " + T("мс")) +
                                                               (speed > 0 ? "\n" + T("Скорость загрузки") + ": " + Formats.Mbps(speed) : speed < 0 && vpnSpeed.ContainsKey(s.Link) ? "\n" + T("Тестовый файл через этот сервер не скачался") : "") };
                int level = ms < 0 ? 0 : ms < 400 ? 3 : ms < 900 ? 2 : 1;
                string color = level == 3 ? "Ok" : level == 2 ? "Ok" : level == 1 ? "Wait" : "Fail";
                TextBlock label = new TextBlock { Text = (ms < 0 ? T("не работает") : ms + " " + T("мс")) + (speed > 0 ? "  ·  " + Formats.Mbps(speed) : ""), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
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
                await RemoveVpnServers(s, null);
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            Button star = new Button { Content = s.Favorite ? "★" : "☆", Width = 28, Height = 28, FontSize = 17, FontFamily = SystemFonts.MessageFontFamily,
                                       ToolTip = T(s.Favorite ? "Убрать из избранного" : "Добавить в избранное") };
            star.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
            if (s.Favorite) star.SetResourceReference(Control.ForegroundProperty, "Accent");
            System.Windows.Automation.AutomationProperties.SetName(star, (string)star.ToolTip);
            star.Click += (o, e) =>
            {
                e.Handled = true;
                s.Favorite = !s.Favorite;
                cfg.Save();
                RefreshVpnUi();
            };
            actions.Children.Add(star);
            actions.Children.Add(del);
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            Border card = new Border { Child = row, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 7, 8, 7), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, BorderThickness = new Thickness(1.5) };
            card.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSoft" : "Card");
            card.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Card");
            card.MouseLeftButtonUp += async delegate
            {
                if (actions.IsMouseOver || cfg.VpnSelected == index) return;
                cfg.VpnSelected = index;
                await ApplyVpnChanges();
            };
            return card;
        }

        async Task RemoveVpnServers(VpnServer server, VpnSubscription feed)
        {
            VpnServer selected = CurrentServer;
            if (feed == null) cfg.VpnServers.Remove(server);
            else
            {
                cfg.VpnSubscriptions.Remove(feed);
                cfg.VpnServers.RemoveAll(s => s.SubscriptionId == feed.Id);
            }
            cfg.VpnSelected = VpnSubscription.Selection(cfg.VpnServers, selected);
            if (cfg.VpnServers.Count == 0 && cfg.VpnOn) await RunBusy(StopVpn, "VPN…");
            await SaveSubscriptionChanges(selected);
        }

        Button VpnSourceButton(string name, bool selected, Action choose)
        {
            var button = new Button { Content = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap },
                HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 32, Margin = new Thickness(0, 0, 0, 4) };
            button.SetResourceReference(FrameworkElement.StyleProperty, selected ? "AccentButton" : "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += delegate { choose(); RefreshVpnUi(); };
            return button;
        }

        void RenderSubscription()
        {
            var list = E<StackPanel>("VpnSubscriptionList");
            list.Children.Clear();
            bool manual = cfg.VpnServers.Any(s => string.IsNullOrEmpty(s.SubscriptionId));
            if (vpnSource == "" ? !manual : vpnSource != null && !cfg.VpnSubscriptions.Any(s => s.Id == vpnSource))
                vpnSource = null;
            list.Children.Add(VpnSourceButton(T("Все серверы"), vpnSource == null, () => { vpnSource = null; vpnTab = null; }));
            if (manual && cfg.VpnSubscriptions.Count > 0)
                list.Children.Add(VpnSourceButton(T("Ручные ключи"), vpnSource == "", () => { vpnSource = ""; vpnTab = null; }));
            foreach (VpnSubscription feed in cfg.VpnSubscriptions)
            {
                string title = (cfg.VpnSubscriptions.Count(f => f.Host == feed.Host) > 1 ? SubscriptionTitle(feed) : feed.Host)
                    + " · " + cfg.VpnServers.Count(s => s.SubscriptionId == feed.Id);
                list.Children.Add(VpnSourceButton(title, vpnSource == feed.Id, () => { vpnSource = feed.Id; vpnTab = null; }));
                if (vpnSource != feed.Id) continue;
                var panel = new StackPanel();
                Vpn.SubscriptionInfo info = Vpn.ParseUserInfo(feed.Info);
                if (info != null)
                {
                    string traffic = T("Использовано") + " " + Formats.Bytes(info.Used) + (info.Total > 0
                        ? " " + T("из") + " " + Formats.Bytes(info.Total) : " · " + T("без лимита"));
                    string expires = info.Expire.HasValue ? T("до") + " " + info.Expire.Value.ToString("dd.MM.yyyy") : T("бессрочно");
                    var details = new TextBlock { Text = traffic + " · " + expires, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
                    details.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    panel.Children.Add(details);
                    if (info.Total > 0)
                    {
                        double ratio = Math.Max(0, Math.Min(1, (double)info.Used / info.Total));
                        var bar = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
                        bar.SetResourceReference(Border.BackgroundProperty, ratio > 0.9 ? "Fail" : ratio > 0.75 ? "Wait" : "Accent");
                        var track = new Border { Child = bar, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 6, 0, 0) };
                        track.SetResourceReference(Border.BackgroundProperty, "IconBg");
                        track.SizeChanged += (sender, e) => bar.Width = e.NewSize.Width * ratio;
                        panel.Children.Add(track);
                    }
                }

                var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                var refresh = new Button { Content = T("Обновить"), Margin = new Thickness(0, 0, 4, 0) };
                refresh.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
                refresh.Click += async delegate { if (cfg.VpnSubscriptions.Contains(feed)) await RefreshSubscription(feed); };
                var delete = new Button { Content = T("Удалить") };
                delete.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
                delete.Click += async delegate
                {
                    if (subscriptionDownloads.Contains(feed.Url)) return;
                    if (MessageBox.Show(T("Удалить подписку и её серверы?") + "\n" + SubscriptionTitle(feed),
                        "VPN", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes && !subscriptionDownloads.Contains(feed.Url))
                        await RemoveVpnServers(null, feed);
                };
                buttons.Children.Add(refresh);
                buttons.Children.Add(delete);
                panel.Children.Add(buttons);
                var card = new Border { Child = panel, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 8) };
                card.SetResourceReference(Border.BackgroundProperty, "Card");
                list.Children.Add(card);
            }
        }

        string SubscriptionTitle(VpnSubscription feed)
        {
            return T("Подписка") + " " + (cfg.VpnSubscriptions.IndexOf(feed) + 1) + " · " + feed.Host;
        }

        void WarnSubscription(VpnSubscription feed)
        {
            var warnings = VpnInfo.SubscriptionWarnings(Vpn.ParseUserInfo(feed.Info), DateTime.Now);
            var warned = new HashSet<string>((feed.Warned ?? "").Split('|'));
            foreach (var warning in warnings)
                if (!warned.Contains(warning.Key)) Notify(SubscriptionTitle(feed) + "\n" + warning.Value, new[] { "Открыть VPN", "vpn-page" });
            string joined = string.Join("|", warnings.Keys);
            if (joined != (feed.Warned ?? "")) { feed.Warned = joined; cfg.Save(); }
        }

        async Task CheckSubscription()
        {
            foreach (VpnSubscription feed in cfg.VpnSubscriptions.ToList())
            {
                if (!cfg.VpnSubscriptions.Contains(feed)) continue;
                var warnings = VpnInfo.SubscriptionWarnings(Vpn.ParseUserInfo(feed.Info), DateTime.Now);
                if (feed.IsStale(DateTime.Now, 1) && warnings.Keys.Except((feed.Warned ?? "").Split('|')).Any())
                    await RefreshSubscription(feed);
                if (cfg.VpnSubscriptions.Contains(feed)) WarnSubscription(feed);
            }
        }
    }
}
