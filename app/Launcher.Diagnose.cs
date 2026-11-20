// Мастер «Почему не работает?»: по шагам проверяет интернет, DNS, мешающие программы и драйвер WinDivert, обход и сайты,
// а в конце говорит, что не так, и предлагает исправить — тем же действием, что и кнопки в других местах лаунчера
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ZapretLauncher
{
    // Итог: заголовок, объяснение и действие кнопки «Исправить» ("" — исправлять нечего или это решает сам пользователь)
    public class Verdict { public string Title, Text, Fix; }

    partial class Launcher
    {

        // ---------- Окно мастера ----------
        System.Threading.CancellationTokenSource connectionCancellation;

        Task DiagnoseUi() { return ConnectionUi(false); }
        Task RepairConnectionUi() { return ConnectionUi(true); }

        async Task ConnectionUi(bool repair)
        {
            if (busy || picking || siteChecking || ticking) { Ask("Дождитесь, пока закончится текущее действие, и запустите проверку снова."); return; }
            ShowWindow();
            string[] labels = { "Интернет", "DNS", "Мешающие программы и драйвер WinDivert", "Обход zapret", "Сайты и стратегия" };
            List<Action<string, string>> marks = new List<Action<string, string>>();
            StackPanel steps = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (string label in labels) steps.Children.Add(StepRow(label, marks));

            StackPanel content = new StackPanel { Width = 440 };
            TextBlock title = new TextBlock { Text = T(repair ? "Исправить соединение" : "Почему не работает?"), FontSize = 17, FontWeight = FontWeights.Bold };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            TextBlock sub = new TextBlock { Text = T("Проверяю по шагам — это займёт несколько секунд"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            content.Children.Add(title);
            content.Children.Add(sub);
            content.Children.Add(steps);

            // итог — карточка под шагами, появляется в конце
            Border verdictBox = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
            StackPanel verdictText = new StackPanel();
            TextBlock vTitle = new TextBlock { FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            vTitle.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            TextBlock vText = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
            vText.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            verdictText.Children.Add(vTitle);
            verdictText.Children.Add(vText);
            verdictBox.Child = verdictText;
            content.Children.Add(verdictBox);

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            Button close = new Button { Content = T("Закрыть"), Height = 38, MinWidth = 104 };
            close.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
            Button fix = new Button { Content = T("Исправить"), Height = 38, MinWidth = 104, Margin = new Thickness(8, 0, 0, 0), IsDefault = true, Visibility = Visibility.Collapsed };
            fix.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
            buttons.Children.Add(close);
            buttons.Children.Add(fix);
            content.Children.Add(buttons);

            Window w = Dialog.Host(window, title.Text, content);
            Verdict verdict = null;
            bool doFix = false, finished = false, cancelled = false;
            var cancellation = new System.Threading.CancellationTokenSource();
            connectionCancellation = cancellation;
            close.Click += delegate { w.Close(); };
            fix.Click += delegate { doFix = true; w.Close(); };
            close.Content = T("Остановить");
            w.Closing += (sender, e) =>
            {
                if (finished) return;
                e.Cancel = true;
                cancelled = true;
                cancellation.Cancel();
                cancelPick = true;
                close.IsEnabled = false;
                sub.Text = T(repair ? "Останавливаю и возвращаю настройки…" : "Останавливаю проверку…");
            };
            w.Loaded += async delegate
            {
                busy = true;
                BusyRing(true);
                try
                {
                    verdict = repair
                        ? await RepairConnection(marks, text => sub.Text = T(text), () => cancelled, w, cancellation.Token)
                        : await RunDiagnosis(marks, cancellation.Token);
                    if (cancelled) throw new OperationCanceledException();
                    sub.Text = T(repair ? "Исправление закончено" : "Проверка закончена");
                }
                catch (OperationCanceledException)
                {
                    verdict = new Verdict { Title = repair ? "Исправление остановлено" : "Проверка остановлена", Text = "Можно запустить исправление снова или открыть журнал.", Fix = "" };
                    sub.Text = T("Остановлено");
                }
                catch (Exception e)
                {
                    Log.Write("Не удалось исправить: " + e.Message);
                    verdict = new Verdict { Title = "Не удалось исправить", Text = T(e.Message), Fix = "" };
                    sub.Text = T("Подробности — в журнале");
                }
                finally
                {
                    finished = true;
                    busy = false;
                    BusyRing(false);
                    close.Content = T("Закрыть");
                    close.IsEnabled = true;
                    UpdateUi();
                    RunPendingRestarts();
                }
                vTitle.Text = T(verdict.Title);
                vText.Text = T(verdict.Text);
                bool good = ConnectionRepair.Succeeded(verdict);
                verdictBox.SetResourceReference(Border.BackgroundProperty, good ? "AccentSoft" : "WarnBg");
                verdictBox.Visibility = Visibility.Visible;
                fix.Visibility = repair ? (good ? Visibility.Collapsed : Visibility.Visible) : (verdict.Fix.Length > 0 ? Visibility.Visible : Visibility.Collapsed);
                fix.Content = T(repair ? (verdict.Fix == "vpn" ? "Открыть VPN" : "Открыть журнал") : "Исправить");
                close.Focus();
            };
            w.ShowDialog();
            cancellation.Dispose();
            connectionCancellation = null;
            if (!doFix || verdict == null) return;
            if (repair)
            {
                if (verdict.Fix == "vpn") OpenVpnRoutes();
                else E<RadioButton>("NavLog").IsChecked = true;
                return;
            }
            Log.Write("Почему не работает: исправляю — " + verdict.Title);
            try
            {
                switch (verdict.Fix)
                {
                    case "install": await InstallZapretUi(); break;
                    case "conflicts": await FixConflicts(); break;
                    case "start": await StartBypassUi(); break;
                    case "dns": await SetSecureDns(true, DnsChoice()); break;
                    case "vpn": OpenVpnRoutes(); break;
                    case "pick": await StartAutoPickUi(false); break;
                }
            }
            catch (Exception e) { Log.Write("Не удалось исправить: " + e.Message); Ask(T("Не удалось исправить") + ": " + T(e.Message), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // Проверки по очереди; mark(состояние, подробность) — ok / warn / fail
        async Task<Verdict> RunDiagnosis(List<Action<string, string>> marks, System.Threading.CancellationToken cancellation = default(System.Threading.CancellationToken))
        {
            Log.Write("Почему не работает: проверяю");
            List<SiteResult> results = await Sites.Test(cfg.Telegram, true, 6, cfg.MySites, cancellation);
            SiteResult control = results.FirstOrDefault(r => r.Name == Sites.Control);
            bool internet = control != null && control.Ok;
            marks[0](internet ? "ok" : "fail", internet ? T("есть") : T("сайты не открываются вообще"));

            bool dnsOk = await Diagnosis.DnsLooksRight(cancellation);
            marks[1](dnsOk ? "ok" : cfg.SecureDns.Length > 0 ? "warn" : "fail", dnsOk ? T("адреса правильные") + (cfg.SecureDns.Length > 0 ? " · " + T("защищённый") : "") : T("адреса заблокированных сайтов подменены"));

            List<Conflict> conflicts;
            Task<List<Conflict>> conflictTask = Task.Run(() => Conflicts.All());
            if (await Task.WhenAny(conflictTask, Task.Delay(10000, cancellation)) != conflictTask)
            {
                cancellation.ThrowIfCancellationRequested();
                throw new Exception("Проверка конфликтов не ответила вовремя");
            }
            conflicts = await conflictTask;
            cancellation.ThrowIfCancellationRequested();
            if (cfg.VpnPause) conflicts = conflicts.Where(c => c.Kind != "vpn").ToList();   // с паузой VPN — штатная ситуация
            marks[2](conflicts.Count == 0 ? "ok" : conflicts.Any(c => c.Kind != "vpn") ? "fail" : "warn",
                     conflicts.Count == 0 ? T("не найдены") : string.Join(" · ", conflicts.Select(c => T(c.Text))));

            bool installed = Zapret.LocalVersion != null, running = Zapret.Running;
            marks[3](running || pausedByVpn ? "ok" : "fail",
                     !installed ? T("не установлен") : pausedByVpn ? T("пауза на время VPN") : running ? T("работает") + " · " + T(Zapret.Title(cfg.Strategy)) : T("выключен"));

            // свои сайты на главной в итог не берём: проблема может быть у самого сайта, а не у обхода
            List<string> failing = results.Where(r => !r.Ok && r.Name != Sites.Control && !cfg.MySites.Contains(r.Name)).Select(r => r.Name).ToList();
            marks[4](failing.Count == 0 ? "ok" : "fail", failing.Count == 0 ? T("открываются") : T("не открывается") + ": " + string.Join(", ", failing.Select(T)));

            // карточки на главной — по свежей проверке
            siteResults = results.Where(r => r.Name != Sites.Control).ToList();
            BuildSitePanel();
            Verdict verdict = Diagnosis.Diagnose(internet, dnsOk, cfg.SecureDns.Length > 0, installed, conflicts, running, pausedByVpn, failing, cfg.BlockedSites);
            if ((cfg.VpnOn && !Vpn.Running) || ((verdict.Fix == "pick" || verdict.Fix == "start") && (pausedByVpn || vpnRunning || vpnLocked || cfg.VpnOn)))
                verdict = new Verdict { Title = "Нужно проверить VPN", Text = "VPN включён или ожидает подключения. Проверьте сервер и маршруты VPN, затем повторите исправление.", Fix = "vpn" };
            Log.Write("Почему не работает: " + verdict.Title);
            return verdict;
        }

        // Строка шага: крутится, пока идёт проверка; потом — значок итога и короткая подробность
        static FrameworkElement StepRow(string label, List<Action<string, string>> marks)
        {
            Grid row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Grid icon = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            Ellipse spin = new Ellipse { StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 3, 3 }, RenderTransformOrigin = new Point(0.5, 0.5) };
            spin.SetResourceReference(Shape.StrokeProperty, "Accent");
            RotateTransform r = new RotateTransform();
            spin.RenderTransform = r;
            r.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.2))) { RepeatBehavior = RepeatBehavior.Forever });
            Ellipse dot = new Ellipse { Visibility = Visibility.Collapsed };
            TextBlock glyph = new TextBlock { FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
            icon.Children.Add(spin);
            icon.Children.Add(dot);
            icon.Children.Add(glyph);
            row.Children.Add(icon);
            StackPanel text = new StackPanel();
            TextBlock name = new TextBlock { Text = T(label), FontWeight = FontWeights.SemiBold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            TextBlock detail = new TextBlock { Text = T("проверяю…"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(name);
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            marks.Add((state, info) =>
            {
                r.BeginAnimation(RotateTransform.AngleProperty, null);
                spin.Visibility = Visibility.Collapsed;
                dot.Visibility = Visibility.Visible;
                dot.SetResourceReference(Shape.FillProperty, state == "ok" ? "Ok" : state == "warn" ? "Wait" : "Fail");
                glyph.Text = state == "ok" ? "" : state == "warn" ? "!" : "";
                if (state == "warn") glyph.FontFamily = new FontFamily("Segoe UI");
                detail.Text = info;
            });
            return row;
        }
    }
}
