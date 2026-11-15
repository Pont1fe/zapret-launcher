// «Гонка стратегий» на главной во время подбора: у каждой стратегии своя дорожка. Проверяемая «бежит», проверенная
// заполняется по доле открытых сайтов, рядом — точка на каждый сайт; лидер отмечен звёздочкой. После финиша карточка
// несколько секунд показывает победителя и уступает место обычному ряду (стратегии, VPN, последние сутки)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ZapretLauncher
{
    partial class Launcher
    {
        class RaceLane { public ScaleTransform Scale; public Border Fill; public StackPanel Dots; public TextBlock Name, Result; }
        readonly List<RaceLane> raceLanes = new List<RaceLane>();
        int raceLeader = -1;
        DispatcherTimer raceHide;

        // Заполнение дорожки: доля открытых сайтов; ни одного — короткий отрезок, чтобы дорожка не выглядела непроверенной
        public static double RaceFill(int ok, int total) { return total <= 0 || ok <= 0 ? 0.04 : Math.Min(1, (double)ok / total); }

        public static string RaceLeaderText(string title, int ok, int total, int avgMs)
        {
            return T("Лидер") + ": " + T(title) + " · " + ok + "/" + total + (avgMs > 0 ? " · " + avgMs + " " + T("мс") : "");
        }

        void RaceStart(List<string> strategies)
        {
            if (raceHide != null) raceHide.Stop();
            UniformGrid host = E<UniformGrid>("RaceLanes");
            host.Children.Clear();
            raceLanes.Clear();
            raceLeader = -1;
            host.Columns = strategies.Count > 6 ? 2 : 1;
            foreach (string s in strategies) host.Children.Add(RaceLaneRow(s));
            Text("RaceTitle").Text = T("Гонка стратегий");
            Text("RaceLeader").Text = T("Проверяю, какая стратегия откроет больше сайтов");
            Text("RaceCount").Text = "0 / " + strategies.Count;
            E<FrameworkElement>("HomeRow3").Visibility = Visibility.Collapsed;
            FrameworkElement race = E<FrameworkElement>("PickRace");
            race.Opacity = 0;
            race.Visibility = Visibility.Visible;
            Fade(race, 1, TimeSpan.FromMilliseconds(220));
            // карточка ниже края окна — главная прокручивается к ней, когда она уже разложена
            if (E<FrameworkElement>("PageHome").Visibility == Visibility.Visible) race.Dispatcher.BeginInvoke(new Action(() => race.BringIntoView()), DispatcherPriority.Loaded);
        }

        FrameworkElement RaceLaneRow(string file)
        {
            Grid row = new Grid { Height = 28, Margin = new Thickness(0, 3, 22, 3) };
            foreach (GridLength w in new[] { new GridLength(28), new GridLength(96), new GridLength(1, GridUnitType.Star), new GridLength(56), new GridLength(58) })   // точки — постоянной ширины: иначе непроверенные дорожки длиннее
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            Border badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(7), Background = B(Zapret.Color(file)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            badge.Child = new TextBlock { Text = Zapret.Badge(file), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(badge);
            TextBlock name = new TextBlock { Text = T(Zapret.Title(file)), ToolTip = T(Zapret.Title(file)) + " · " + file, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            Grid track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center };
            Border bed = new Border { CornerRadius = new CornerRadius(4) };
            bed.SetResourceReference(Border.BackgroundProperty, "IconBg");
            ScaleTransform scale = new ScaleTransform(0, 1);
            Border fill = new Border { CornerRadius = new CornerRadius(4), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = scale };
            fill.SetResourceReference(Border.BackgroundProperty, "Accent");
            track.Children.Add(bed);
            track.Children.Add(fill);
            Grid.SetColumn(track, 2);
            row.Children.Add(track);
            StackPanel dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            Grid.SetColumn(dots, 3);
            row.Children.Add(dots);
            TextBlock result = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            result.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Grid.SetColumn(result, 4);
            row.Children.Add(result);
            raceLanes.Add(new RaceLane { Scale = scale, Fill = fill, Dots = dots, Name = name, Result = result });
            return row;
        }

        // Проверяемая стратегия «бежит»: заливка ходит туда-обратно, имя выделено
        void RaceRunning(int i)
        {
            if (i >= raceLanes.Count) return;
            RaceLane l = raceLanes[i];
            l.Name.FontWeight = FontWeights.SemiBold;
            l.Name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            l.Result.Text = T("бежит…");
            if (!SystemParameters.ClientAreaAnimation) { l.Scale.ScaleX = 0.35; return; }
            l.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.08, 0.6, new Duration(TimeSpan.FromMilliseconds(700)))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }

        void RaceResult(int i, List<SiteResult> res, bool leader, int done, int count)
        {
            if (i >= raceLanes.Count) return;
            RaceLane l = raceLanes[i];
            int ok = res.Count(r => r.Ok);
            Finish(l, RaceFill(ok, res.Count), ok == 0);
            foreach (SiteResult r in res)
            {
                Ellipse dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(2, 0, 2, 0), ToolTip = T(r.Name) + ": " + (r.Ok ? r.Ms + " " + T("мс") : T("не открылся")) };
                dot.SetResourceReference(Shape.FillProperty, r.Ok ? "Ok" : "Fail");
                l.Dots.Children.Add(dot);
            }
            l.Result.Text = ok + "/" + res.Count;
            if (leader)
            {
                if (raceLeader >= 0 && raceLeader != i) MarkLeader(raceLanes[raceLeader], false);
                raceLeader = i;
                MarkLeader(l, true);
                List<SiteResult> okRes = res.Where(r => r.Ok).ToList();
                Text("RaceLeader").Text = RaceLeaderText(l.Name.Text, ok, res.Count, okRes.Count > 0 ? (int)okRes.Average(r => r.Ms) : 0);
            }
            Text("RaceCount").Text = done + " / " + count;
        }

        void RaceFailed(int i, int done, int count)
        {
            if (i >= raceLanes.Count) return;
            Finish(raceLanes[i], RaceFill(0, 0), true);
            raceLanes[i].Result.Text = T("сошла");
            raceLanes[i].Result.ToolTip = T("Стратегия не запустилась");
            Text("RaceCount").Text = done + " / " + count;
        }

        void Finish(RaceLane l, double to, bool failed)
        {
            l.Name.FontWeight = FontWeights.Normal;
            l.Name.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            l.Fill.SetResourceReference(Border.BackgroundProperty, failed ? "Fail" : "Accent");
            l.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 450 : 0)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        void MarkLeader(RaceLane l, bool on)
        {
            string score = l.Result.Text.TrimStart('★', ' ');
            l.Result.Text = on ? "★ " + score : score;
            l.Result.SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "Muted");
            l.Result.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        // Финиш: победитель крупно, через несколько секунд — обычный ряд главной
        void RaceEnd(string winner, bool stopped)
        {
            foreach (RaceLane l in raceLanes.Where(x => x.Result.Text == T("бежит…"))) { Finish(l, 0, false); l.Result.Text = ""; }
            Text("RaceTitle").Text = T(stopped ? "Подбор остановлен" : "Финиш");
            Text("RaceLeader").Text = winner != null ? T("Победила") + " " + T(Zapret.Title(winner)) + " — " + T("включаю её") : T("Ни одна стратегия не открыла сайты — оставляю прежнюю");
            if (raceHide == null)
            {
                raceHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                raceHide.Tick += delegate
                {
                    raceHide.Stop();
                    E<FrameworkElement>("PickRace").Visibility = Visibility.Collapsed;
                    FrameworkElement row = E<FrameworkElement>("HomeRow3");
                    row.Opacity = 0;
                    row.Visibility = Visibility.Visible;
                    Fade(row, 1, TimeSpan.FromMilliseconds(220));
                };
            }
            raceHide.Start();
        }
    }
}
