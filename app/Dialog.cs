// Диалоги в стиле лаунчера вместо системных MessageBox (те белые и выбиваются из оформления)
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace ZapretLauncher
{
    static class Dialog
    {
        public static MessageBoxResult Show(Window owner, string text, string title = "Zapret Launcher",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
        {
            MessageBoxResult result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK
                : buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;

            Window w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = owner == null || !owner.IsVisible,
                Title = title, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Topmost = owner == null || !owner.IsVisible
            };
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);   // мелкий текст — по пиксельной сетке, без мыла
            if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (owner != null && owner.Icon != null) w.Icon = owner.Icon;

            string glyph = icon == MessageBoxImage.Error ? "" : icon == MessageBoxImage.Warning ? "" : icon == MessageBoxImage.Question ? "" : "";
            string glyphColor = icon == MessageBoxImage.Error ? "Fail" : icon == MessageBoxImage.Warning ? "WarnText" : "Accent";

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            Border iconBg = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top };
            iconBg.SetResourceReference(Border.BackgroundProperty, "IconBg");
            TextBlock iconText = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            iconText.SetResourceReference(TextBlock.ForegroundProperty, glyphColor);
            iconBg.Child = iconText;
            head.Children.Add(iconBg);

            StackPanel texts = new StackPanel { Margin = new Thickness(14, 0, 0, 0), MaxWidth = 420 };
            TextBlock titleText = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            TextBlock body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), LineHeight = 19 };
            body.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            texts.Children.Add(titleText);
            texts.Children.Add(body);
            Grid.SetColumn(texts, 1);
            head.Children.Add(texts);

            StackPanel buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            Action<string, MessageBoxResult, bool> add = (label, value, primary) =>
            {
                Button b = new Button { Content = I18n.T(label), Height = 34, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary };
                b.SetResourceReference(FrameworkElement.StyleProperty, primary ? "AccentButton" : "GhostButton");
                b.Click += delegate { result = value; w.Close(); };
                buttonRow.Children.Add(b);
            };
            if (buttons == MessageBoxButton.OK) add("ОК", MessageBoxResult.OK, true);
            else if (buttons == MessageBoxButton.OKCancel) { add("Отмена", MessageBoxResult.Cancel, false); add("ОК", MessageBoxResult.OK, true); }
            else if (buttons == MessageBoxButton.YesNo) { add("Нет", MessageBoxResult.No, false); add("Да", MessageBoxResult.Yes, true); }
            else { add("Отмена", MessageBoxResult.Cancel, false); add("Нет", MessageBoxResult.No, false); add("Да", MessageBoxResult.Yes, true); }

            StackPanel content = new StackPanel();
            content.Children.Add(head);
            content.Children.Add(buttonRow);
            Border card = new Border
            {
                Child = content, CornerRadius = new CornerRadius(16), Padding = new Thickness(22, 20, 22, 18), Margin = new Thickness(24),
                BorderThickness = new Thickness(1), MinWidth = 380,
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.35, Color = Colors.Black }
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardRaised");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            // перетаскивание за любое место карточки
            card.MouseLeftButtonDown += (s, e) => { try { w.DragMove(); } catch { } };
            w.Content = card;

            // появление: лёгкий масштаб и затухание
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            ScaleTransform scale = new ScaleTransform(0.96, 0.96);
            card.RenderTransform = scale;
            card.Opacity = 0;
            w.Loaded += delegate
            {
                Duration d = new Duration(TimeSpan.FromMilliseconds(160));
                IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, d) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, d) { EasingFunction = ease });
            };
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.ShowDialog();
            return result;
        }
    }
}
