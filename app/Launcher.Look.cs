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
            window.MinWidth = (on ? 460 : MinFullWidth) * k;
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

        // полный вид: боковое меню 76 + колонки 400 и 440 из ui.xaml и запас на рамку окна
        const double MinFullWidth = 940;

        void ApplyScale()
        {
            double k = cfg.Scale / 100.0;
            E<Grid>("Root").LayoutTransform = k == 1 ? Transform.Identity : new ScaleTransform(k, k);
            window.MinWidth = (cfg.Compact ? 460 : MinFullWidth) * k;
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
