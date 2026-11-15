// Живой талисман — маленький робот в строке с датой: тёмная голова-экран, светящиеся глаза и огонёк на антенне
// цвета акцента. Настроение показывают глаза: спокоен — два «окошка»; всё открывается — «^ ^»; проверяет — смотрит
// по сторонам, бегут точки; что-то сломалось — глаза опущены и жёлтые; обход выключен — тусклые полоски и «z».
// Открытые глаза моргают. Без анимаций Windows — только смена глаз
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ZapretLauncher
{
    partial class Launcher
    {
        // sleep / busy / sad / calm / happy
        public static string MascotMood(string state, bool checking, bool busy, List<SiteResult> res)
        {
            if (busy || checking) return "busy";
            if (state == "off") return "sleep";
            if (state == "error") return "sad";
            if (state == "pause" || res == null || res.Count == 0) return "calm";
            return res.All(r => r.Ok) ? "happy" : "sad";
        }

        public static string MascotTip(string mood)
        {
            return mood == "sleep" ? "Сплю — обход выключен" : mood == "busy" ? "Секунду, проверяю…" : mood == "sad" ? "Что-то не открывается"
                 : mood == "happy" ? "Всё открывается!" : "Всё спокойно";
        }

        // Корпус одинаково тёмный в обеих темах — светится только экран
        static readonly Color RobotShell = Color.FromRgb(0x2B, 0x2C, 0x35), RobotPart = Color.FromRgb(0x3A, 0x3B, 0x45), RobotScreen = Color.FromRgb(0x0C, 0x0D, 0x12);

        Path robotEyes, robotGlow, robotHead;
        Ellipse robotLamp, robotLampGlow;
        Ellipse[] robotDots;
        TextBlock mascotZ;
        Canvas robotFace;
        ScaleTransform mascotBlink;
        TranslateTransform robotLook, mascotHop, mascotZMove;
        DispatcherTimer mascotBlinkTimer;
        string mascotMood, mascotLook;
        readonly Random mascotRnd = new Random();

        static Color Mix(Color a, Color b, double t) { return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t)); }

        static Geometry Pair(double x1, double x2, double y, double w, double h, double r)
        {
            GeometryGroup g = new GeometryGroup();
            g.Children.Add(new RectangleGeometry(new Rect(x1, y, w, h), r, r));
            g.Children.Add(new RectangleGeometry(new Rect(x2, y, w, h), r, r));
            return g;
        }

        static Ellipse Dot(double cx, double cy, double r)
        {
            Ellipse e = new Ellipse { Width = r * 2, Height = r * 2 };
            Canvas.SetLeft(e, cx - r);
            Canvas.SetTop(e, cy - r);
            return e;
        }

        static Rectangle Box(double x, double y, double w, double h, double r, Color c)
        {
            Rectangle b = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = new SolidColorBrush(c) };
            Canvas.SetLeft(b, x);
            Canvas.SetTop(b, y);
            return b;
        }

        void BuildMascot()
        {
            // робот в сетке 100×100 занимает x 8…92, y 2…80 — холст ровно по этим границам, левый край вровень с текстом
            Canvas frame = new Canvas { Width = 84, Height = 78 };
            Canvas art = new Canvas { Width = 100, Height = 100, RenderTransform = new TranslateTransform(-8, -2) };
            Canvas body = new Canvas { Width = 100, Height = 100 };
            mascotHop = new TranslateTransform();
            body.RenderTransform = mascotHop;

            body.Children.Add(new Line { X1 = 50, Y1 = 19, X2 = 50, Y2 = 9, Stroke = new SolidColorBrush(RobotPart), StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            robotLampGlow = Dot(50, 7, 7.5);
            robotLampGlow.Opacity = 0.3;
            robotLamp = Dot(50, 7, 4.5);
            body.Children.Add(robotLampGlow);
            body.Children.Add(robotLamp);
            body.Children.Add(Box(8, 38, 7, 20, 3.5, RobotPart));
            body.Children.Add(Box(85, 38, 7, 20, 3.5, RobotPart));
            robotHead = new Path { Data = new RectangleGeometry(new Rect(13, 18, 74, 62), 20, 20), Fill = new SolidColorBrush(RobotShell),
                                   Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Direction = 270, Opacity = 0.3 } };
            body.Children.Add(robotHead);
            Rectangle shine = Box(17, 21, 38, 6, 3, Colors.White);
            shine.Opacity = 0.07;
            body.Children.Add(shine);
            body.Children.Add(Box(21, 26, 58, 46, 13, RobotScreen));

            // экран: свечение под глазами, сами глаза, точки «проверяю»; лицо смотрит по сторонам и моргает целиком
            robotFace = new Canvas();
            robotLook = new TranslateTransform();
            mascotBlink = new ScaleTransform(1, 1, 50, 49);
            TransformGroup faceMove = new TransformGroup();
            faceMove.Children.Add(mascotBlink);
            faceMove.Children.Add(robotLook);
            robotFace.RenderTransform = faceMove;
            // ореол: та же форма глаз, размытая — экран «светится», а не обведён рамкой
            robotGlow = new Path { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Opacity = 0.75,
                                   Effect = new BlurEffect { Radius = 6, KernelType = KernelType.Gaussian } };
            robotEyes = new Path { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            robotFace.Children.Add(robotGlow);
            robotFace.Children.Add(robotEyes);
            body.Children.Add(robotFace);
            robotDots = new[] { Dot(60, 65, 1.8), Dot(66, 65, 1.8), Dot(72, 65, 1.8) };
            foreach (Ellipse d in robotDots) body.Children.Add(d);
            art.Children.Add(body);

            mascotZ = new TextBlock { Text = "z", FontSize = 15, FontWeight = FontWeights.Bold };
            mascotZ.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            mascotZMove = new TranslateTransform();
            mascotZ.RenderTransform = mascotZMove;
            Canvas.SetLeft(mascotZ, 60);
            Canvas.SetTop(mascotZ, -2);
            art.Children.Add(mascotZ);
            frame.Children.Add(art);

            E<Border>("Mascot").Child = new Viewbox { Child = frame };
            mascotBlinkTimer = new DispatcherTimer();
            mascotBlinkTimer.Tick += delegate { Blink(); mascotBlinkTimer.Interval = TimeSpan.FromMilliseconds(2500 + mascotRnd.Next(3500)); };
        }

        void UpdateMascot(string mood)
        {
            if (robotEyes == null) BuildMascot();
            E<Border>("Mascot").ToolTip = T(MascotTip(mood));
            // цвет глаз — акцент темы (грусть — жёлтый); перекрашиваем и при смене темы, даже если настроение то же
            Color glow = Theme.Color(mood == "sad" ? "Wait" : "Accent");
            Color eye = Mix(glow, Colors.White, 0.18);
            string look = mood + glow + Theme.Dark;
            bool moodChanged = mood != mascotMood;
            if (look == mascotLook && !moodChanged) return;
            mascotLook = look;
            ((DropShadowEffect)robotHead.Effect).Color = Theme.Dark ? Colors.Black : Color.FromRgb(0x30, 0x2A, 0x50);
            ((DropShadowEffect)robotHead.Effect).Opacity = Theme.Dark ? 0.5 : 0.25;
            Color lamp = Theme.Color("Accent");
            robotLamp.Fill = new SolidColorBrush(mood == "sleep" ? RobotPart : lamp);
            robotLampGlow.Fill = new SolidColorBrush(lamp);
            robotLampGlow.Visibility = mood == "sleep" ? Visibility.Collapsed : Visibility.Visible;
            foreach (Ellipse d in robotDots) { d.Fill = new SolidColorBrush(eye); d.Visibility = mood == "busy" ? Visibility.Visible : Visibility.Collapsed; }

            // глаза: «окошки», дуги «^ ^», опущенные трапеции, тусклые полоски
            SolidColorBrush eyeBrush = new SolidColorBrush(eye), glowBrush = new SolidColorBrush(glow);
            robotEyes.Fill = robotEyes.Stroke = robotGlow.Fill = robotGlow.Stroke = null;
            robotEyes.Opacity = 1;
            if (mood == "happy")
            {
                Geometry arcs = Geometry.Parse("M29,51 Q35,41 41,51 M59,51 Q65,41 71,51");
                robotGlow.Data = arcs; robotGlow.Stroke = glowBrush; robotGlow.StrokeThickness = 9;
                robotEyes.Data = arcs; robotEyes.Stroke = eyeBrush; robotEyes.StrokeThickness = 5;
            }
            else if (mood == "sad")
            {
                Geometry droop = Geometry.Parse("M29,47 L41,43 L41,56 Q41,58 39,58 L31,58 Q29,58 29,56 Z M59,43 L71,47 L71,56 Q71,58 69,58 L61,58 Q59,58 59,56 Z");
                robotGlow.Data = droop; robotGlow.Fill = glowBrush; robotGlow.Stroke = glowBrush; robotGlow.StrokeThickness = 4;
                robotEyes.Data = droop; robotEyes.Fill = eyeBrush;
            }
            else if (mood == "sleep")
            {
                robotEyes.Data = Geometry.Parse("M29,51 L41,51 M59,51 L71,51");
                robotEyes.Stroke = eyeBrush; robotEyes.StrokeThickness = 4; robotEyes.Opacity = 0.45;
            }
            else
            {
                robotGlow.Data = Pair(28, 58, 40, 14, 18, 6); robotGlow.Fill = glowBrush;
                robotEyes.Data = Pair(29, 59, 41, 12, 16, 5); robotEyes.Fill = eyeBrush;
            }
            mascotZ.Visibility = mood == "sleep" ? Visibility.Visible : Visibility.Collapsed;
            if (!moodChanged) return;
            bool first = mascotMood == null, anim = SystemParameters.ClientAreaAnimation;
            mascotMood = mood;
            bool open = mood == "calm" || mood == "busy" || mood == "sad";

            // движения: прошлые — снять, новые — по настроению
            robotLook.BeginAnimation(TranslateTransform.XProperty, null);
            robotLook.Y = mood == "sad" ? 1.5 : 0;
            robotLampGlow.BeginAnimation(UIElement.OpacityProperty, null);
            foreach (Ellipse d in robotDots) d.BeginAnimation(UIElement.OpacityProperty, null);
            mascotZMove.BeginAnimation(TranslateTransform.YProperty, null);
            mascotZ.BeginAnimation(UIElement.OpacityProperty, null);
            mascotBlinkTimer.Stop();
            if (!anim) return;
            if (mood == "sleep")
            {
                mascotZMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, -10, new Duration(TimeSpan.FromSeconds(2.4))) { RepeatBehavior = RepeatBehavior.Forever });
                mascotZ.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(2.4))) { RepeatBehavior = RepeatBehavior.Forever });
            }
            if (mood == "busy")
            {
                // смотрит по сторонам, огонёк мигает, точки загораются по очереди
                robotLook.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-4, 4, new Duration(TimeSpan.FromMilliseconds(900))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                robotLampGlow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.1, 0.55, new Duration(TimeSpan.FromMilliseconds(500))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                for (int i = 0; i < robotDots.Length; i++)
                    robotDots[i].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.2, new Duration(TimeSpan.FromMilliseconds(450))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(150 * i) });
            }
            if (open) { mascotBlinkTimer.Interval = TimeSpan.FromMilliseconds(1800); mascotBlinkTimer.Start(); }
            if (first) return;
            // смена настроения заметна: радость — подпрыгнуть, огорчение — вздрогнуть
            if (mood == "happy")
                mascotHop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -8, new Duration(TimeSpan.FromMilliseconds(200))) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            else if (mood == "sad")
                mascotHop.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 3, new Duration(TimeSpan.FromMilliseconds(60))) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) });
        }

        void Blink()
        {
            if (!window.IsVisible) return;
            mascotBlink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.1, new Duration(TimeSpan.FromMilliseconds(80))) { AutoReverse = true });
        }
    }
}
