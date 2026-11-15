// Перетаскивание в окно: ярлык или .exe — программа пойдёт через VPN; ссылка или адрес сайта — на странице «Домены» в обход,
// на странице VPN — через VPN, в остальных местах лаунчер спросит. Добавляет тот же код, что и поле ввода на этих страницах
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ZapretLauncher
{
    partial class Launcher
    {
        Border dropHint;
        TextBlock dropHintText;
        List<string> dropApps, dropSites;   // разобраны один раз на входе в окно: ярлыки читаются не мгновенно

        void WireDrop()
        {
            window.AllowDrop = true;
            window.PreviewDragEnter += (s, e) => OnDragOver(e);
            window.PreviewDragOver += (s, e) => OnDragOver(e);
            window.PreviewDragLeave += (s, e) =>
            {
                // DragLeave приходит и при переходе между элементами окна — прячем, только когда курсор действительно ушёл
                Point p = e.GetPosition(window);
                if (p.X <= 0 || p.Y <= 0 || p.X >= window.ActualWidth || p.Y >= window.ActualHeight) EndDrag();
            };
            window.PreviewDrop += async (s, e) =>
            {
                if (OverTextBox(e.OriginalSource) || dropApps == null) { EndDrag(); return; }   // в поле ввода — обычная вставка текста
                e.Handled = true;
                List<string> apps = dropApps, sites = dropSites;
                EndDrag();
                await AddDropped(apps, sites);
            };
        }

        void OnDragOver(DragEventArgs e)
        {
            if (OverTextBox(e.OriginalSource)) { HideDropHint(); return; }
            if (dropApps == null) ReadDrop(e.Data);
            e.Handled = true;
            if (busy || dropApps.Count + dropSites.Count == 0) { e.Effects = DragDropEffects.None; HideDropHint(); return; }
            e.Effects = DragDropEffects.Copy;
            ShowDropHint(DropHintText(dropApps, dropSites, CurrentPage()));
        }

        void ReadDrop(IDataObject data)
        {
            List<string> files = new List<string>();
            string text = "";
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop))
                    foreach (string f in (string[])data.GetData(DataFormats.FileDrop))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".lnk") { string target = ShortcutTarget(f); if (target != null) files.Add(target); }
                        else if (ext == ".url") text += " " + UrlFileTarget(f);   // ярлык сайта, перетащенный из браузера на рабочий стол
                        else files.Add(f);
                    }
                if (data.GetDataPresent(DataFormats.UnicodeText)) text += " " + (string)data.GetData(DataFormats.UnicodeText);
            }
            catch { }
            dropApps = new List<string>();
            dropSites = new List<string>();
            Routes.ParseDrop(files, text, dropApps, dropSites);
        }

        static string ShortcutTarget(string lnk)
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                string target = (string)shell.CreateShortcut(lnk).TargetPath;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch { return null; }
        }

        static string UrlFileTarget(string file)
        {
            try { return File.ReadLines(file).Where(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)).Select(l => l.Substring(4)).FirstOrDefault() ?? ""; }
            catch { return ""; }
        }

        static bool OverTextBox(object source)
        {
            DependencyObject d = source as DependencyObject;
            while (d != null)
            {
                if (d is TextBoxBase) return true;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        string CurrentPage()
        {
            if (E<FrameworkElement>("PageVpn").Visibility == Visibility.Visible) return "Vpn";
            if (E<FrameworkElement>("PageDomains").Visibility == Visibility.Visible) return DomainKind == "general" ? "Domains" : "Exclude";
            return "";
        }

        // «Отпустите — Spotify.exe пойдёт через VPN» и т. п.
        public static string DropHintText(List<string> apps, List<string> sites, string page)
        {
            Func<IEnumerable<string>, string> names = list => list.Count() <= 2 ? string.Join(", ", list) : string.Join(", ", list.Take(2)) + " " + T("и ещё") + " " + (list.Count() - 2);
            List<string> parts = new List<string>();
            if (apps.Count > 0) parts.Add(names(apps.Select(Path.GetFileName)) + " — " + T("через VPN"));
            if (sites.Count > 0)
                parts.Add(names(sites) + " — " + T(page == "Domains" ? "в обход" : page == "Exclude" ? "в «Не трогать»" : page == "Vpn" || apps.Count > 0 ? "через VPN" : "спрошу: в обход или через VPN"));
            return T("Отпустите") + ": " + string.Join("; ", parts);
        }

        async Task AddDropped(List<string> apps, List<string> sites)
        {
            if (busy) return;
            string page = CurrentPage();
            if (apps.Count > 0) await AddToVpn(apps, false);
            if (sites.Count == 0) return;
            bool vpn = page == "Vpn" || (page == "" && apps.Count > 0);   // вместе с программой — туда же, в VPN
            if (page == "")
            {
                MessageBoxResult r = apps.Count > 0 ? MessageBoxResult.No
                    : Dialog.Show(window, T("Куда добавить") + " " + string.Join(", ", sites) + "?\n\n" +
                                  T("В обход — сайт откроется через zapret, как Discord и YouTube. Через VPN — пойдёт через ваш VPN-сервер."),
                                  T("Добавить сайт"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, "В обход", "Через VPN");
                if (r == MessageBoxResult.Cancel) return;
                vpn = r == MessageBoxResult.No;
            }
            if (vpn) { await AddToVpn(sites, true); return; }
            if (page == "")
            {
                E<RadioButton>("NavDomains").IsChecked = true;
                E<RadioButton>("DomGeneral").IsChecked = true;
                UpdateDomainUi();
            }
            await AddDomains(string.Join("\n", sites));
        }

        // Страница VPN, вкладка «Маршруты», нужный список — и тот же разбор, что у поля ввода
        async Task AddToVpn(List<string> items, bool sites)
        {
            E<RadioButton>("NavVpn").IsChecked = true;
            E<RadioButton>("VpnTabRoutes").IsChecked = true;
            E<RadioButton>(sites ? "VpnKindSites" : "VpnKindApps").IsChecked = true;
            RefreshVpnUi();
            await AddVpnListItems(string.Join("\n", items));
        }

        void ShowDropHint(string text)
        {
            if (dropHint == null)
            {
                dropHintText = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460, Margin = new Thickness(0, 12, 0, 0) };
                dropHintText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
                Grid icon = new Grid { Width = 56, Height = 56 };
                System.Windows.Shapes.Ellipse circle = new System.Windows.Shapes.Ellipse();
                circle.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
                TextBlock glyph = new TextBlock { Text = "", FontSize = 22, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                glyph.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
                icon.Children.Add(circle);
                icon.Children.Add(glyph);
                StackPanel card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                card.Children.Add(icon);
                card.Children.Add(dropHintText);
                Border plate = new Border { Child = card, Padding = new Thickness(28, 22, 28, 22), CornerRadius = new CornerRadius(22), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                plate.SetResourceReference(Border.BackgroundProperty, "CardRaised");
                // всё окно — «зона приёма»: рамка цвета акцента и мягкая подложка, карточка с подсказкой по центру
                dropHint = new Border { Child = plate, CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(2.5), Margin = new Thickness(14), IsHitTestVisible = false };
                dropHint.SetResourceReference(Border.BorderBrushProperty, "Accent");
                Grid.SetRowSpan(dropHint, 10);
                Grid.SetColumnSpan(dropHint, 10);
                Panel.SetZIndex(dropHint, 1000);
                E<Grid>("Root").Children.Add(dropHint);
            }
            Color a = Theme.Color("Accent");   // окно просвечивает сквозь лёгкую заливку цвета акцента; тема могла смениться — берём цвет каждый раз
            dropHint.Background = new SolidColorBrush(Color.FromArgb(0x38, a.R, a.G, a.B));
            dropHintText.Text = text;
            if (dropHint.Visibility != Visibility.Visible) { dropHint.Opacity = 0; dropHint.Visibility = Visibility.Visible; Fade(dropHint, 1, TimeSpan.FromMilliseconds(120)); }
        }

        void HideDropHint() { if (dropHint != null) { dropHint.BeginAnimation(UIElement.OpacityProperty, null); dropHint.Opacity = 0; dropHint.Visibility = Visibility.Collapsed; } }

        void EndDrag() { HideDropHint(); dropApps = dropSites = null; }
    }
}
