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
        string VpnKey() { return CurrentServer == null ? null : CurrentServer.Link + "|" + cfg.VpnMode; }
        bool VpnSwitching { get { return vpnRunning && vpnActive != null && vpnActive != VpnKey(); } }

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
            bool ready = vpnRunning && !VpnSwitching;   // пока подключён не выбранный сервер — «подключается», а не его скорость
            Text("PowerVpnText").Text = "VPN · " + (s == null ? "" : Vpn.DisplayName(s.Name)) +
                                        (ready ? speed : " · " + T(vpnLocked ? "не работает — блокировка" : "подключается…"));
            E<Ellipse>("PowerVpnDot").SetResourceReference(Shape.FillProperty, ready ? "Ok" : vpnLocked ? "Fail" : "Wait");
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
                        string key = Awg.FromConf(text, null).Link.Split('#')[0];
                        if (!cfg.VpnServers.Any(s => s.Link.Split('#')[0] == key)) link = text;
                    }
                }
            }
            catch { }   // буфер обмена занят другой программой
            clipLink = link;
            E<FrameworkElement>("VpnClipBar").Visibility = link != null ? Visibility.Visible : Visibility.Collapsed;
            if (link != null && Awg.LooksLikeConf(link)) Text("VpnClipText").Text = T("В буфере обмена настройки AmneziaWG. Добавить?");
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
                Title = T("Файл настроек AmneziaWG"), Filter = T("Файлы настроек") + " (*.conf)|*.conf|" + T("Все файлы") + " (*.*)|*.*",
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
            try { server = Awg.FromConf(conf, fileName); }
            catch (Exception e) { info.Text = T(e.Message); return; }
            // тот же файл скачали заново (старый Amnezia отключает) — заменяем, а не добавляем второй
            int same = cfg.VpnServers.FindIndex(s => Awg.IsAwg(s.Link) && s.Name == server.Name);
            if (same >= 0) cfg.VpnServers[same] = server;
            else if (!cfg.VpnServers.Any(s => s.Link == server.Link)) cfg.VpnServers.Add(server);
            info.Text = T(same >= 0 ? "Сервер AmneziaWG обновлён" : "Добавлен сервер AmneziaWG") + ": " + Vpn.DisplayName(server.Name) + ". " +
                        T("Ключи хранятся только на этом компьютере, в настройках лаунчера");
            Log.Write("VPN: " + (same >= 0 ? "обновлён" : "добавлен") + " сервер AmneziaWG — " + server.Name);
            await ApplyVpnChanges();
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
            string key = VpnKey();
            await Vpn.Start(server, cfg.VpnMode, cfg.VpnDomains, cfg.VpnApps);
            vpnActive = key;
            vpnRunning = true;
            vpnLocked = false;
            vpnLockShown = false;
            vpnFails = 0;
            vpnConnectedAt = DateTime.Now;
            vpnSpeeds.Clear();
            cfg.VpnHelpDone = true;   // подключились — подсказка «Как начать» больше не нужна
            cfg.VpnOn = true;
            cfg.Save();
            Log.Write("VPN подключён: " + server.Name + " (" + ModeName(cfg.VpnMode) + ")");   // и при переподключении — видно, через какой сервер идёт трафик
            if (Awg.IsAwg(server.Link)) { Task check = AwgSelfCheck(); }   // в фоне: VPN уже подключён, ждать проверку незачем
        }

        // После подключения к AmneziaWG: открывается ли сайт через VPN, а если нет — где обрыв (до туннеля, на сервере или дальше); итог — в журнал
        async Task AwgSelfCheck()
        {
            try
            {
                await Task.Delay(1500);
                string tunnel = Awg.ActiveTunnel;
                if (tunnel == null) return;
                // в режимах «Только выбранное» и «Россия напрямую» — запрос через выход sing-box в туннель, как у сайтов из списка
                int ms = tunnel == Awg.ChainTunnel ? await Vpn.CurrentDelay() : await Awg.Delay();
                Awg.TunnelStats st = await Task.Run(() => Awg.Stats(tunnel));
                string bytes = st == null ? "" : " (" + T("на сервер") + " " + st.Tx + " " + T("Б") + ", " + T("от сервера") + " " + st.Rx + " " + T("Б") + ")";
                if (ms >= 0) { Log.Write("AmneziaWG: проверка — сайт через VPN открылся за " + ms + " мс" + bytes); return; }
                string why = st == null ? "AmneziaWG не ответил на запрос статистики"
                    : st.Tx == 0 ? "трафик не попал в туннель AmneziaWG"
                    : st.Rx == 0 ? "сервер AmneziaWG не отвечает — файл настроек мог устареть: скачайте его в приложении Amnezia заново"
                    : "связь с сервером AmneziaWG есть, но сайт через VPN не открылся";
                Log.Write("AmneziaWG: проверка не прошла — " + why + bytes);
                string tail = await Task.Run(() => Awg.LogTail(12));
                if (tail != null) Log.Write("AmneziaWG, журнал ядра: " + tail);
                Notify("AmneziaWG: " + T(why));
            }
            catch (Exception e) { Log.Write("AmneziaWG: проверка не удалась — " + e.Message); }
        }

        async Task StopVpn()
        {
            cfg.VpnOn = false;
            cfg.Save();
            await Vpn.Stop();
            vpnRunning = false;
            vpnActive = null;
            vpnLocked = false;   // выключили сами — блокировка тоже снимается
            vpnLockShown = false;
            Log.Write("VPN отключён");
        }

        async Task ToggleVpn()
        {
            bool wasOn = vpnRunning || Vpn.Running || vpnLocked;   // при блокировке нажатие выключает VPN вместе с ней
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
            // по cfg.VpnOn, а не vpnRunning: пока VPN переподключается, vpnRunning на мгновение false — и смена сервера терялась
            if (!cfg.VpnOn) return;
            if (busy) vpnRestartPending = true;
            else { await RunBusy(StartVpnOrLock, "VPN…"); RefreshVpnUi(); }
        }

        // Ядро VPN упало, а VPN должен работать — перезапускаем (не чаще раза в 30 секунд, после двух неудач — сдаёмся)
        async Task VpnGuard()
        {
            vpnRunning = Vpn.Running;
            vpnLocked = !vpnRunning && Vpn.Locked;
            if (cfg.VpnOn && VpnSwitching && !busy && !vpnRestartPending)
            {
                // подключён не тот сервер или режим, что выбран (смену не успели применить) — переподключаемся
                Log.Write("VPN: подключён не выбранный сервер — переподключаюсь к " + CurrentServer.Name);
                await RunBusy(() => StartVpn(true), "VPN…");
                RefreshVpnUi();
                return;
            }
            // при блокировке пробуем подключиться реже — каждая попытка ненадолго её снимает
            if (!cfg.VpnOn || vpnRunning || busy || CurrentServer == null || (DateTime.Now - vpnLastStart).TotalSeconds < (vpnLocked ? 120 : 30)) return;
            bool wasLocked = vpnLocked;
            if (!wasLocked) Log.Write("VPN: ядро не работает — перезапускаю (" + Vpn.LastLogLine() + ")");
            Exception error = null;
            try { await StartVpn(true); }
            catch (Exception e) { error = e; }
            if (error == null) { if (wasLocked) Notify("VPN снова работает — блокировка снята"); }
            else if (cfg.VpnKillSwitch) await LockVpn(error.Message);
            else if (++vpnFails >= 2)
            {
                cfg.VpnOn = false;
                cfg.Save();
                Notify("VPN не удалось перезапустить: " + error.Message);
            }
            RefreshVpnUi();
        }

        // Переподключение после смены сервера, режима или списков: не вышло, а включено «не пускать мимо VPN» — блокировка
        async Task StartVpnOrLock()
        {
            Exception error = null;
            try { await StartVpn(true); }
            catch (Exception e) { error = e; }
            if (error == null) return;
            if (!cfg.VpnKillSwitch) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            await LockVpn(error.Message);
        }

        // ---------- «Не пускать мимо VPN» ----------
        bool vpnLocked;      // вместо VPN работает блокировка (Vpn.StartLock)
        bool vpnLockShown;   // о блокировке уже сообщили — при каждой новой попытке подключиться не повторяем

        // VPN не поднимается — вместо него ядро-блокировка: сайты и программы из списка не уходят напрямую
        async Task LockVpn(string reason)
        {
            try { await Vpn.StartLock(cfg.VpnMode, cfg.VpnDomains, cfg.VpnApps); }
            catch (Exception e)
            {
                // без блокировки остаётся только выключить VPN, как раньше, и честно сказать об этом
                Log.Write("VPN: не удалось включить блокировку — " + e.Message);
                cfg.VpnOn = false;
                cfg.Save();
                Notify("VPN не удалось перезапустить: " + reason);
                return;
            }
            vpnRunning = false;
            vpnLocked = true;
            if (vpnLockShown) return;
            vpnLockShown = true;
            Log.Write("VPN не работает (" + reason + ") — " + LockText());
            Notify(T("VPN не работает") + " — " + T(LockText()));
        }

        string LockText()
        {
            return cfg.VpnMode == "split" ? "сайты и программы из списка заблокированы, пока VPN не заработает"
                                          : "всё, что шло через VPN, заблокировано, пока он не заработает";
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
                cfg.VpnServers = found.Concat(cfg.VpnServers.Where(s => Awg.IsAwg(s.Link))).ToList();   // подписка заменяет список (серверы AmneziaWG остаются)
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
                cfg.VpnServers = found.Concat(cfg.VpnServers.Where(s => Awg.IsAwg(s.Link))).ToList();   // серверы AmneziaWG — не из подписки, их не трогаем
                cfg.VpnSubInfo = Vpn.LastUserInfo ?? "";
                cfg.VpnSubUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                int keep = found.FindIndex(s => s.Link == current);
                cfg.VpnSelected = keep >= 0 ? keep : 0;
                Text("VpnInfo").Text = T("Подписка обновлена, серверов") + ": " + found.Count;
                await ApplyVpnChanges();
                await CheckSubscription();
            }
            catch (Exception e) { Text("VpnInfo").Text = T(e.Message); }
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

        // Что напомнить о подписке сейчас: ключ этапа → текст. Этапы: за 3 дня, за сутки, закончилась; трафик 90% и 100%.
        // Ключ содержит дату окончания и размер тарифа: продлили подписку — ключ новый, и напоминание придёт снова, когда подойдёт срок
        static Dictionary<string, string> SubscriptionWarnings(Vpn.SubscriptionInfo info, DateTime now)
        {
            Dictionary<string, string> w = new Dictionary<string, string>();
            if (info == null) return w;
            if (info.Expire.HasValue)
            {
                double days = (info.Expire.Value - now).TotalDays;
                string date = info.Expire.Value.ToString("dd.MM.yyyy HH:mm");
                string key = "expire " + info.Expire.Value.ToString("yyyyMMddHHmm") + " ";
                if (days <= 0) w[key + "0"] = "Подписка VPN закончилась: " + date + ". Продлите её у своего провайдера";
                else if (days <= 3)
                    w[key + (days <= 1 ? "1" : "3")] = "Подписка VPN заканчивается: " + date + " (осталось дней: " + (int)Math.Ceiling(days) + "). Продлите её у своего провайдера";
            }
            if (info.Total > 0)
            {
                double ratio = (double)info.Used / info.Total;
                string key = "traffic " + info.Total + " ";
                string used = Bytes(info.Used) + " из " + Bytes(info.Total);
                if (ratio >= 1) w[key + "100"] = "Трафик подписки VPN закончился: использовано " + used;
                else if (ratio >= 0.9) w[key + "90"] = "Трафик подписки VPN почти закончился: использовано " + used;
            }
            return w;
        }

        // Раз в час и после обновления подписки: новый этап — одно уведомление. Перед ним — свежие сведения о подписке
        // (только заголовок со сроком и трафиком; серверы и VPN не трогаем), если они старше часа: вдруг её уже продлили
        async Task CheckSubscription()
        {
            if (cfg.VpnSubscription.Length == 0) return;
            HashSet<string> warned = new HashSet<string>((cfg.VpnSubWarned ?? "").Split('|').Where(k => k.Length > 0));
            Dictionary<string, string> now = SubscriptionWarnings(Vpn.ParseUserInfo(cfg.VpnSubInfo), DateTime.Now);
            DateTime updated;
            bool stale = !DateTime.TryParse(cfg.VpnSubUpdated, out updated) || (DateTime.Now - updated).TotalHours >= 1;
            if (stale && now.Keys.Any(k => !warned.Contains(k)))
            {
                try
                {
                    await Vpn.Parse(cfg.VpnSubscription);
                    if (Vpn.LastUserInfo != null) { cfg.VpnSubInfo = Vpn.LastUserInfo; RenderSubscription(); }
                }
                catch { }
                now = SubscriptionWarnings(Vpn.ParseUserInfo(cfg.VpnSubInfo), DateTime.Now);
            }
            foreach (KeyValuePair<string, string> kv in now)
                if (!warned.Contains(kv.Key)) Notify(kv.Value, new[] { "Открыть VPN", "vpn-page" });
            string joined = string.Join("|", now.Keys);
            if (joined != (cfg.VpnSubWarned ?? "")) { cfg.VpnSubWarned = joined; cfg.Save(); }
        }
    }
}
