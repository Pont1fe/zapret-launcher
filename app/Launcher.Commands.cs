using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ZapretLauncher
{
    partial class Launcher
    {
        void OpenSiteSettings()
        {
            E<RadioButton>("NavSettings").IsChecked = true;
            E<RadioButton>("SetTabBypass").IsChecked = true;
            TextBox box = E<TextBox>("MySitesBox");
            box.BringIntoView();
            box.Focus();
        }

        async Task ShowCommands()
        {
            if (quitting) return;
            var commands = new[]
            {
                new { Label = T("Исправить соединение"), Run = (Func<Task>)RepairConnectionUi },
                new { Label = T("Проверить сайты сейчас"), Run = (Func<Task>)(() =>
                {
                    E<RadioButton>("NavHome").IsChecked = true;
                    if (pageIndex != 0) ShowPage("Home");
                    return PingNow();
                }) },
                new { Label = T("Свои сайты для проверки"), Run = (Func<Task>)(() => { OpenSiteSettings(); return Task.CompletedTask; }) },
                new { Label = T("Стратегии"), Run = (Func<Task>)(() => { ShowStrategyList(); return Task.CompletedTask; }) },
                new { Label = T("Открыть VPN"), Run = (Func<Task>)(() => { E<RadioButton>("NavVpn").IsChecked = true; return Task.CompletedTask; }) },
                new { Label = T("Открыть журнал"), Run = (Func<Task>)(() => { E<RadioButton>("NavLog").IsChecked = true; return Task.CompletedTask; }) },
                new { Label = T("Сохранить события сбоя"), Run = (Func<Task>)(() => { SaveIncident(); return Task.CompletedTask; }) }
            };

            string title = T("Команды");
            TextBox search = new TextBox { Height = 36 };
            search.SetResourceReference(FrameworkElement.StyleProperty, "InputBox");
            System.Windows.Automation.AutomationProperties.SetName(search, T("Найти команду"));
            Border input = new Border { Child = search, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 10, 0) };
            input.SetResourceReference(Border.BackgroundProperty, "Input");
            ListBox results = new ListBox
            {
                DisplayMemberPath = "Label", Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), MaxHeight = 250, Margin = new Thickness(0, 10, 0, 0)
            };
            results.SetResourceReference(Control.ForegroundProperty, "Text");
            System.Windows.Automation.AutomationProperties.SetName(results, title);
            TextBlock empty = new TextBlock { Text = T("Ничего не найдено"), Margin = new Thickness(0, 10, 0, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            StackPanel panel = new StackPanel { Width = 360 };
            panel.Children.Add(input);
            panel.Children.Add(results);
            panel.Children.Add(empty);

            Window dialog = Dialog.Host(window, title, panel);
            Func<Task> run = null;
            var matches = commands;
            Action accept = () =>
            {
                if (results.SelectedIndex < 0) return;
                run = matches[results.SelectedIndex].Run;
                dialog.Close();
            };
            Action filter = () =>
            {
                string[] words = search.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                matches = commands.Where(command => words.All(word =>
                    command.Label.IndexOf(word, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToArray();
                results.ItemsSource = matches;
                results.SelectedIndex = matches.Length == 0 ? -1 : 0;
                empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            search.TextChanged += delegate { filter(); };
            results.MouseDoubleClick += (s, e) =>
            {
                if (ItemsControl.ContainerFromElement(results, e.OriginalSource as DependencyObject) is ListBoxItem) accept();
            };
            dialog.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; accept(); }
                else if ((e.Key == Key.Down || e.Key == Key.Up) && results.Items.Count > 0)
                {
                    e.Handled = true;
                    results.SelectedIndex = Math.Max(0, Math.Min(results.Items.Count - 1,
                        results.SelectedIndex + (e.Key == Key.Down ? 1 : -1)));
                    results.ScrollIntoView(results.SelectedItem);
                }
            };
            dialog.Loaded += delegate { search.Focus(); };
            filter();
            dialog.ShowDialog();
            if (run != null) await run();
        }
    }
}
