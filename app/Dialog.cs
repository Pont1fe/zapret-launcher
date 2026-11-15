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
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information, string yes = null, string no = null)   // yes, no — свои подписи кнопок «Да» и «Нет»
        {
            MessageBoxResult result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK
                : buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;

            Window w = null;   // окно создаётся ниже, кнопки закрывают его по ссылке

            string glyph = icon == MessageBoxImage.Error ? "" : icon == MessageBoxImage.Warning ? "" : icon == MessageBoxImage.Question ? "" : "";
            string glyphColor = icon == MessageBoxImage.Error ? "Fail" : icon == MessageBoxImage.Warning ? "WarnText" : "Accent";

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            Border iconBg = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top };
            iconBg.SetResourceReference(Border.BackgroundProperty, icon == MessageBoxImage.Error || icon == MessageBoxImage.Warning ? "WarnBg" : "AccentSoft");   // мягкая плитка в цвет смысла, как значки на главной
            TextBlock iconText = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            iconText.SetResourceReference(TextBlock.ForegroundProperty, glyphColor);
            iconBg.Child = iconText;
            head.Children.Add(iconBg);

            StackPanel texts = new StackPanel { Margin = new Thickness(14, 0, 0, 0), MaxWidth = 420 };
            TextBlock titleText = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
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
                Button b = new Button { Content = I18n.T(label), Height = 38, MinWidth = 104, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary };
                b.SetResourceReference(FrameworkElement.StyleProperty, primary ? "AccentButton" : "GhostButton");
                b.Click += delegate { result = value; w.Close(); };
                buttonRow.Children.Add(b);
            };
            if (buttons == MessageBoxButton.OK) add("ОК", MessageBoxResult.OK, true);
            else if (buttons == MessageBoxButton.OKCancel) { add("Отмена", MessageBoxResult.Cancel, false); add("ОК", MessageBoxResult.OK, true); }
            else if (buttons == MessageBoxButton.YesNo) { add(no ?? "Нет", MessageBoxResult.No, false); add(yes ?? "Да", MessageBoxResult.Yes, true); }
            else { add("Отмена", MessageBoxResult.Cancel, false); add(no ?? "Нет", MessageBoxResult.No, false); add(yes ?? "Да", MessageBoxResult.Yes, true); }

            StackPanel content = new StackPanel();
            content.Children.Add(head);
            content.Children.Add(buttonRow);
            w = Host(owner, title, content);
            w.ShowDialog();
            return result;
        }

        // Окно в стиле лаунчера вокруг любого содержимого: карточка с тенью, появление, перетаскивание, Esc — закрыть.
        // Показывает вызывающий (ShowDialog)
        public static Window Host(Window owner, string title, UIElement content)
        {
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
            Border card = new Border
            {
                Child = content, CornerRadius = new CornerRadius(24), Padding = new Thickness(24, 22, 24, 20), Margin = new Thickness(28),
                MinWidth = 400,
                // мягкая тень как у карточек: в светлой теме — с оттенком акцента, в тёмной — чёрная
                Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 8, Direction = 270, Opacity = Theme.Dark ? 0.45 : 0.18, Color = Theme.Color("Shadow") }
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardRaised");
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
            return w;
        }
    }
}
