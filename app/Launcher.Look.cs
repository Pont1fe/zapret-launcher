// Оформление: тема, масштаб, размер окна, анимации, история за сутки
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
        // ---------- Оформление ----------
        // Смена темы или акцента: снимок старого вида остаётся поверх окна, а новый вид расходится из точки клика кругом.
        // Мышь вне окна (сменили с клавиатуры или сама система) — круг из центра; анимация в Windows выключена — сразу
        void ApplyTheme()
        {
            Image snapshot = window.IsVisible ? Snapshot() : null;
            Grid root = E<Grid>("Root");
            if (snapshot != null) root.Children.Add(snapshot);
            Theme.Apply(cfg.Theme, cfg.Transparent, cfg.Accent, File.Exists(Paths.Wallpaper));
            Theme.ApplyWindow(window, TopBarHeight * cfg.Scale / 100.0);
            ShowWallpaper();
            visualActive = null;
            cardState = null;
            UpdateStrategyList();
            BuildSitePanel();
            UpdateUi();
            RenderHistory();
            if (snapshot == null) return;
            if (!SystemParameters.ClientAreaAnimation) { root.Children.Remove(snapshot); return; }
            Size size = new Size(root.ActualWidth, root.ActualHeight);
            Point mouse = Mouse.GetPosition(root);
            Point from = new Rect(size).Contains(mouse) ? mouse : new Point(size.Width / 2, size.Height / 2);
            EllipseGeometry hole = new EllipseGeometry(from, 0, 0);
            snapshot.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(size)), hole);
            DoubleAnimation grow = new DoubleAnimation(0, WaveRadius(from, size), new Duration(TimeSpan.FromMilliseconds(560))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            grow.Completed += delegate { root.Children.Remove(snapshot); };
            hole.BeginAnimation(EllipseGeometry.RadiusXProperty, grow);
            hole.BeginAnimation(EllipseGeometry.RadiusYProperty, grow);
        }

        // ---------- Картинка за окном ----------
        // С прозрачным фоном (Mica) не показывается: сквозь окно и так видно обои Windows
        void ShowWallpaper()
        {
            Image img = E<Image>("Wallpaper");
            ImageSource src = Theme.Transparent || !File.Exists(Paths.Wallpaper) ? null : LoadWallpaper(Paths.Wallpaper);
            img.Source = src;
            img.Visibility = src == null ? Visibility.Collapsed : Visibility.Visible;
            img.Opacity = Theme.Dark ? 0.55 : 0.7;   // в тёмной теме приглушённее — светлое фото не спорит с текстом
            E<FrameworkElement>("BtnWallpaperOff").Visibility = File.Exists(Paths.Wallpaper) ? Visibility.Visible : Visibility.Collapsed;
            Text("WallpaperSub").Text = T(Theme.Transparent && File.Exists(Paths.Wallpaper) ? "Не видна, пока включён прозрачный фон (Mica)" : "Своё фото или обои за окном — размываются, чтобы текст читался");
        }

        // Уменьшенная копия (ширина 1600): окно не больше экрана, а память за огромное фото платить незачем; битый файл — null
        public static ImageSource LoadWallpaper(string path)
        {
            // через свой поток, а не по адресу файла: битая картинка иначе оставляла файл занятым — ни удалить, ни заменить
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    System.Windows.Media.Imaging.BitmapImage b = new System.Windows.Media.Imaging.BitmapImage();
                    b.BeginInit();
                    b.StreamSource = fs;
                    b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // всё прочитано сразу — поток можно закрыть
                    b.DecodePixelWidth = 1600;
                    b.EndInit();
                    b.Freeze();
                    return b;
                }
            }
            catch { return null; }
        }

        const long WallpaperMaxBytes = 30L * 1024 * 1024;

        void PickWallpaper()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog { Filter = T("Картинки") + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.heic;*.jxr;*.tif;*.tiff" };
            if (dlg.ShowDialog(window) != true) return;
            if (new FileInfo(dlg.FileName).Length > WallpaperMaxBytes) { Ask("Картинка больше 30 МБ — выберите поменьше.", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (LoadWallpaper(dlg.FileName) == null) { Ask("Не получилось открыть эту картинку — выберите другую (JPG или PNG).", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            try { File.Copy(dlg.FileName, Paths.Wallpaper, true); }   // копия в папке лаунчера: оригинал можно перенести или удалить
            catch (Exception e) { Ask(T("Не удалось сохранить картинку") + ": " + e.Message, MessageBoxButton.OK, MessageBoxImage.Error); return; }
            if (cfg.Transparent) { cfg.Transparent = false; cfg.Save(); filling = true; E<CheckBox>("SwMica").IsChecked = false; filling = false; }   // Mica закрыл бы картинку
            Log.Write("Картинка на фоне: " + System.IO.Path.GetFileName(dlg.FileName));
            ApplyTheme();
        }

        void RemoveWallpaper()
        {
            try { File.Delete(Paths.Wallpaper); } catch (Exception e) { Log.Write("Картинка на фоне: " + e.Message); }
            ApplyTheme();
        }

        // Радиус, при котором круг из точки p закрывает всё окно, — расстояние до самого дальнего угла
        static double WaveRadius(Point p, Size s)
        {
            double dx = Math.Max(p.X, s.Width - p.X), dy = Math.Max(p.Y, s.Height - p.Y);
            return Math.Sqrt(dx * dx + dy * dy);
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
            double w = cfg.WindowWidth > 0 ? cfg.WindowWidth : 1240 * k, h = cfg.WindowHeight > 0 ? cfg.WindowHeight : 760 * k;
            Rect area = SystemParameters.WorkArea;
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
                cfg.WindowWidth = Math.Round(r.Width);
                cfg.WindowHeight = Math.Round(r.Height);
            }
            cfg.Save();
        }

        const double TopBarHeight = 68;   // высота верхней панели в ui.xaml: за неё окно перетаскивается

        void ApplyScale()
        {
            double k = cfg.Scale / 100.0;
            E<Grid>("Root").LayoutTransform = k == 1 ? Transform.Identity : new ScaleTransform(k, k);
            // как MinWidth/MinHeight в ui.xaml, но не больше экрана: иначе кнопки окна уйдут за край (главная прокручивается)
            Rect area = SystemParameters.WorkArea;
            window.MinWidth = Math.Min(1080 * k, area.Width);
            window.MinHeight = Math.Min(680 * k, area.Height);
            System.Windows.Shell.WindowChrome chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
            if (chrome != null && !chrome.IsFrozen) chrome.CaptionHeight = TopBarHeight * k;
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
        // Дуга кольца от верхней точки по часовой стрелке: share — доля круга (0..1), r — радиус, c — центр по обеим осям
        static Geometry RingArc(double share, double r, double c)
        {
            share = Math.Max(0, Math.Min(1, share));
            if (share <= 0) return Geometry.Empty;
            if (share >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);
            double a = share * 2 * Math.PI;
            PathFigure f = new PathFigure { StartPoint = new Point(c, c - r) };
            f.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, share > 0.5, SweepDirection.Clockwise, true));
            return new PathGeometry(new[] { f });
        }

        // ---------- Плавные числа ----------
        // Значение «доезжает» до нового с замедлением в конце; новый запуск по тому же ключу перебивает старый.
        // Если в Windows выключена анимация — сразу конечное значение
        readonly Dictionary<string, DispatcherTimer> tweens = new Dictionary<string, DispatcherTimer>();

        void Tween(string key, double from, double to, Action<double> apply, double ms = 600)
        {
            DispatcherTimer old;
            if (tweens.TryGetValue(key, out old)) { old.Stop(); tweens.Remove(key); }
            if (from == to || !SystemParameters.ClientAreaAnimation) { apply(to); return; }
            DateTime start = DateTime.Now;
            DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            t.Tick += delegate
            {
                double p = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / ms);
                apply(from + (to - from) * EaseOut(p));
                if (p >= 1) { t.Stop(); tweens.Remove(key); }
            };
            tweens[key] = t;
            apply(from);
            t.Start();
        }

        static double EaseOut(double p) { return 1 - Math.Pow(1 - p, 3); }

        double shownShare;   // докуда кольцо уже дорисовано; при скрытии окна — ноль, чтобы при открытии оно выросло заново
        readonly Dictionary<string, int> shownMs = new Dictionary<string, int>();

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
            // кольцо: доля работы среди минут, когда лаунчер был запущен
            double share = points.Count == 0 ? 0 : (double)minutesBy['o'] / points.Count;
            System.Windows.Shapes.Path ring = E<System.Windows.Shapes.Path>("HistoryRing");
            TextBlock percent = Text("HistoryPercent");
            bool empty = points.Count == 0;
            Tween("ring", shownShare, share, v =>
            {
                ring.Data = RingArc(v, 28.5, 32);   // по центру толщины дорожки: 64/2 − 7/2
                percent.Text = empty ? "—" : Math.Round(v * 100) + "%";
            }, 900);
            shownShare = share;
            Text("HistoryMonth").Text = I18n.English ? DateTime.Now.ToString("MMMM", new System.Globalization.CultureInfo("en-US")) : DateTime.Now.ToString("MMMM", new System.Globalization.CultureInfo("ru-RU")).ToLower();
            WrapPanel legend = E<WrapPanel>("HistoryLegend");
            legend.Children.Clear();
            foreach (char c in new[] { 'o', 'p', 'e', 'f' })
            {
                if (minutesBy[c] == 0 && c != 'o') continue;
                StackPanel item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
                Ellipse dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                dot.SetResourceReference(Shape.FillProperty, HistoryColor(c));
                item.Children.Add(dot);
                TextBlock t = new TextBlock { Text = T(HistoryName(c)) + " " + Formats.Span(minutesBy[c]), FontSize = 11 };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                item.Children.Add(t);
                legend.Children.Add(item);
            }
        }

        static string HistoryColor(char c) { return c == 'o' ? "Accent" : c == 'p' ? "Wait" : c == 'e' ? "Fail" : "Idle"; }
        static string HistoryName(char c) { return c == 'o' ? "работал" : c == 'p' ? "пауза (VPN)" : c == 'e' ? "сбой" : "выключен"; }

        // Карточка обхода: плавно заливается цветом акцента, кнопка-щит светлеет
        void SetActive(bool on)
        {
            if (visualActive == on) return;
            bool first = !visualActive.HasValue;
            visualActive = on;
            TimeSpan d = TimeSpan.FromMilliseconds(first ? 0 : 260);
            Fade(E<UIElement>("ActiveLayer"), on ? 1 : 0, d);
            Fade(E<UIElement>("PowerIconOn"), on ? 1 : 0, d);
            Fade(E<UIElement>("GlowBg"), on ? 0.9 : 0, d);
            // включён — карточка цвета акцента, текст на ней светлый (в тёмной теме акцент яркий — текст тёмный, это AccentText)
            Text("PowerStatus").SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentText" : "Text");
            Text("PowerTimer").SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentText" : "TextSub");
            Text("PowerHint").SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentText" : "Muted");
            Text("PowerCaption").SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentText" : "Muted");
            if (on && !first) Ripple();
            Pulse(on);
            animateCards = true;
            UpdateGreeting();
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

        // Пока обход работает, от кнопки «питания» раз в 2,6 с расходится и гаснет мягкое кольцо; выключен — кольца нет
        void Pulse(bool on)
        {
            Border ring = E<Border>("PowerPulse");
            ScaleTransform s = new ScaleTransform(1, 1);
            ring.RenderTransform = s;
            if (!on) { ring.BeginAnimation(UIElement.OpacityProperty, null); ring.Opacity = 0; return; }
            TimeSpan grow = TimeSpan.FromSeconds(1.8), cycle = TimeSpan.FromSeconds(2.6);
            Func<double, double, DoubleAnimationUsingKeyFrames> wave = (from, to) =>
            {
                DoubleAnimationUsingKeyFrames a = new DoubleAnimationUsingKeyFrames { Duration = cycle, RepeatBehavior = RepeatBehavior.Forever };
                a.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(grow), new CubicEase { EasingMode = EasingMode.EaseOut }));
                a.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(cycle)));   // пауза до следующей волны
                return a;
            };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, wave(1, 1.5));
            s.BeginAnimation(ScaleTransform.ScaleYProperty, wave(1, 1.5));
            ring.BeginAnimation(UIElement.OpacityProperty, wave(0.6, 0));
        }

        // Волна от кнопки при включении
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
            Text("PowerIcon").Text = state == "pause" ? "" : state == "error" ? "" : "";   // включён и выключен — значок «питания», как у кнопки включения
            Text("PowerIcon").SetResourceReference(TextBlock.ForegroundProperty,
                state == "on" ? "Accent" : state == "pause" ? "Wait" : state == "error" ? "Fail" : "Muted");   // «on» — на белой плитке
        }

        static void Fade(UIElement e, double to, TimeSpan d)
        {
            if (e == null) return;
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, new Duration(d)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // Смена вкладки: содержимое въезжает сбоку (справа — если вкладка правее прежней) и проявляется; delay — для появления по очереди
        static void SlideIn(FrameworkElement e, double fromX, int delayMs = 0)
        {
            Duration d = new Duration(TimeSpan.FromMilliseconds(240));
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            TimeSpan begin = TimeSpan.FromMilliseconds(delayMs);
            TranslateTransform tr = new TranslateTransform(fromX, 0);
            e.RenderTransform = tr;
            e.Opacity = 0;
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease, BeginTime = begin });
            tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, d) { EasingFunction = ease, BeginTime = begin });
        }

        static void Slide(FrameworkElement e, double x, TimeSpan d)
        {
            if (e == null) return;
            TranslateTransform tr = e.RenderTransform as TranslateTransform;
            if (tr == null || tr.IsFrozen) { tr = new TranslateTransform(); e.RenderTransform = tr; }
            tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, new Duration(d)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        }
    }
}
