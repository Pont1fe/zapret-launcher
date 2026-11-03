// Страница «Домены»: список, наборы сайтов, поиск, добавление списком и из файла (общие части — и для VPN)
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
        string DomainKind { get { return E<RadioButton>("DomGeneral").IsChecked == true ? "general" : "exclude"; } }

        void UpdateDomainUi()
        {
            if (!Directory.Exists(Paths.Zapret)) return;
            List<string> domains = Zapret.UserDomains(DomainKind);
            E<FrameworkElement>("DomainSearchBox").Visibility = domains.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
            string query = domains.Count > 8 ? E<TextBox>("DomainSearch").Text.Trim() : "";
            // наборы — только в «Обходить»; в исключениях домены идут по одному
            FillItemList(E<StackPanel>("DomainItems"), domains, query, false, RemoveDomain, DomainKind == "general" ? (Func<SitePreset, Task>)RemoveDomainPreset : null);
            E<FrameworkElement>("DomainEmpty").Visibility = domains.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            Text("DomainEmptyText").Text = T(DomainKind == "general" ? "Своих доменов пока нет — добавьте сайт, который нужно разблокировать, или выберите готовый набор"
                                                                      : "Исключений нет — сюда добавляют сайты, которые ломаются от обхода");
            // наборы — только для списка «Обходить»: исключать целые сервисы незачем
            E<FrameworkElement>("DomainPresetBox").Visibility = DomainKind == "general" ? Visibility.Visible : Visibility.Collapsed;
            if (DomainKind == "general") FillPresets(E<StackPanel>("DomainPresets"), domains, ToggleDomainPreset);
        }

        async Task AddDomain()
        {
            if (await AddDomains(E<TextBox>("DomainInput").Text)) E<TextBox>("DomainInput").Text = "";
        }

        async Task<bool> AddDomains(string raw)
        {
            List<string> added = new List<string>(), bad = new List<string>();
            foreach (string part in SitePresets.SplitItems(raw))
            {
                string d = Zapret.NormalizeDomain(part);
                if (d != null) added.Add(d); else bad.Add(part);
            }
            if (added.Count == 0 && bad.Count == 0) return false;
            List<string> current = Zapret.UserDomains(DomainKind);
            List<string> fresh = added.Where(d => !current.Contains(d)).Distinct().ToList();
            Text("DomainInfo").Text = AddedInfo(fresh.Count, added.Count, bad, "Не похоже на домен", "Этот домен уже есть в списке",
                                                "Можно сразу несколько — через пробел, запятую или вставить списком. Поддомены учитываются автоматически.");
            if (fresh.Count == 0) return false;
            Zapret.SaveUserDomains(DomainKind, current.Concat(fresh));
            Log.Write("Добавлено в «" + (DomainKind == "general" ? "Обходить" : "Не трогать") + "»: " + string.Join(", ", fresh));
            UpdateDomainUi();
            AutoBackup();
            await RestartIfRunning();
            return true;
        }

        // Итог добавления: сколько добавлено, что уже было, что не распознано (первые несколько)
        static string AddedInfo(int fresh, int found, List<string> bad, string badText, string dupText, string normalText)
        {
            string s = fresh > 1 ? T("Добавлено") + ": " + fresh : fresh == 0 && found > 0 ? T(dupText) : "";
            if (bad.Count > 0)
                s += (s.Length > 0 ? ". " : "") + T(badText) + ": " + string.Join(", ", bad.Take(5)) + (bad.Count > 5 ? " (+" + (bad.Count - 5) + ")" : "");
            return s.Length > 0 ? s : T(normalText);
        }

        async Task ToggleDomainPreset(SitePreset p)
        {
            List<string> current = Zapret.UserDomains("general");
            if (SitePresets.IsOn(p, current))
            {
                HashSet<string> remove = SitePresets.RemovalSet(p, current, false);
                Zapret.SaveUserDomains("general", current.Where(d => !remove.Contains(d)));
                Log.Write("Набор «" + p.Name + "» убран из «Обходить»");
            }
            else
            {
                Zapret.SaveUserDomains("general", current.Concat(p.Domains.Where(d => !current.Contains(d))));
                Log.Write("Набор «" + p.Name + "» добавлен в «Обходить»");
            }
            UpdateDomainUi();
            AutoBackup();
            await RestartIfRunning();
        }

        async Task RemoveDomainPreset(SitePreset p)
        {
            if (SitePresets.IsOn(p, Zapret.UserDomains("general"))) await ToggleDomainPreset(p);
        }

        // Список доменов или программ: включённые наборы — одной карточкой (значок, название, домены мелко), остальное — по строке.
        // query — поиск по названию набора, доменам и программам
        void FillItemList(StackPanel host, List<string> items, string query, bool vpn, Func<string, Task> removeOne, Func<SitePreset, Task> removeSet)
        {
            host.Children.Clear();
            Func<string, bool> match = s => query.Length == 0 || s.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            List<SitePreset> sets = removeSet == null ? new List<SitePreset>()
                : SitePresets.All.Where(p => (vpn || !p.VpnOnly) && SitePresets.IsOn(p, items)).ToList();
            HashSet<string> grouped = new HashSet<string>(sets.SelectMany(p => p.Domains), StringComparer.OrdinalIgnoreCase);
            int shown = 0;
            foreach (SitePreset preset in sets)
            {
                SitePreset p = preset;
                if (!match(p.Name) && !p.Domains.Any(match) && !(vpn && p.Apps.Any(match))) continue;
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(PresetBadge(p, 28));
                StackPanel text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = p.Name, FontSize = 13, FontWeight = FontWeights.SemiBold });
                TextBlock sub = new TextBlock
                {
                    Text = string.Join(", ", p.Domains) + (vpn && p.Apps.Length > 0 ? "  ·  " + string.Join(", ", p.Apps) : ""),
                    FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                text.Children.Add(sub);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                Button del = new Button { Content = "", Width = 28, Height = 28, FontSize = 11, ToolTip = T("Убрать набор"), VerticalAlignment = VerticalAlignment.Center };
                del.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
                del.Click += async delegate { await removeSet(p); };
                Grid.SetColumn(del, 2);
                row.Children.Add(del);
                Border card = new Border { Child = row, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 9, 8, 9), Margin = new Thickness(0, 0, 0, 6) };
                card.SetResourceReference(Border.BackgroundProperty, "Card");
                host.Children.Add(card);
                shown++;
            }
            List<string> singles = items.Where(d => !grouped.Contains(d) && match(d)).ToList();
            const int MaxRows = 300;   // после импорта большого списка тысячи строк подвесили бы окно — остальное находится поиском
            foreach (string item in singles.Take(MaxRows))
            {
                string value = item;
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = value, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                Button del = new Button { Content = "", Width = 28, Height = 28, FontSize = 11, ToolTip = T("Удалить") };
                del.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
                del.Click += async delegate { await removeOne(value); };
                Grid.SetColumn(del, 1);
                row.Children.Add(del);
                Border card = new Border { Child = row, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6) };
                card.SetResourceReference(Border.BackgroundProperty, "Card");
                host.Children.Add(card);
                shown++;
            }
            if (singles.Count > MaxRows)
            {
                TextBlock more = new TextBlock { Text = T("Всего в списке") + ": " + singles.Count + ". " + T("Показаны первые 300 — остальные найдёт поиск"), FontSize = 12, Margin = new Thickness(2, 2, 0, 6), TextWrapping = TextWrapping.Wrap };
                more.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                host.Children.Add(more);
            }
            if (shown == 0 && query.Length > 0)
            {
                TextBlock none = new TextBlock { Text = T("Ничего не найдено"), FontSize = 12, Margin = new Thickness(2, 2, 0, 6) };
                none.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                host.Children.Add(none);
            }
        }

        readonly Dictionary<string, string> presetTabs = new Dictionary<string, string>();   // открытая вкладка наборов на каждой странице

        // Готовые наборы: сверху вкладки (Общение, Медиа…) с числом добавленных, ниже — кнопки наборов открытой вкладки.
        // Кнопка подсвечена, если домены набора уже все в списке; клик добавляет набор или убирает его
        void FillPresets(StackPanel host, ICollection<string> domains, Func<SitePreset, Task> toggle)
        {
            bool vpn = host.Name == "VpnPresets";
            List<SitePreset> all = SitePresets.All.Where(x => !x.VpnOnly || vpn).ToList();
            List<string> groups = SitePresets.Groups.Where(g => all.Any(p => p.Group == g)).ToList();
            string open;
            if (!presetTabs.TryGetValue(host.Name, out open) || !groups.Contains(open))
                presetTabs[host.Name] = open = groups.FirstOrDefault(g => all.Any(p => p.Group == g && SitePresets.IsOn(p, domains))) ?? groups[0];
            host.Children.Clear();
            WrapPanel tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (string group in groups)
            {
                string g = group;
                int count = all.Count(p => p.Group == g && SitePresets.IsOn(p, domains));
                StackPanel label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(new TextBlock { Text = T(g) });
                if (count > 0) label.Children.Add(new TextBlock { Text = " " + count, Opacity = 0.65 });
                RadioButton tab = new RadioButton { Content = label, GroupName = host.Name + "Tabs", IsChecked = g == open, Margin = new Thickness(0, 0, 4, 4) };
                tab.SetResourceReference(FrameworkElement.StyleProperty, "Segment");
                tab.Click += delegate { presetTabs[host.Name] = g; FillPresets(host, domains, toggle); };
                tabs.Children.Add(tab);
            }
            host.Children.Add(tabs);
            WrapPanel panel = new WrapPanel();
            host.Children.Add(panel);
            foreach (SitePreset preset in all.Where(p => p.Group == open))
            {
                SitePreset p = preset;
                bool on = SitePresets.IsOn(p, domains);
                StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(PresetBadge(p, 18));
                content.Children.Add(new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) });
                ToggleButton chip = new ToggleButton { Content = content, IsChecked = on };
                chip.SetResourceReference(FrameworkElement.StyleProperty, "PresetChip");
                chip.ToolTip = string.Join(", ", p.Domains) + (p.Apps.Length > 0 && vpn ? ", " + string.Join(", ", p.Apps) : "") +
                               "\n" + T(on ? "Нажмите, чтобы убрать набор" : "Нажмите, чтобы добавить набор");
                chip.Click += async delegate { chip.IsChecked = !on; await toggle(p); };
                panel.Children.Add(chip);
            }
        }

        // Значок сервиса: буква на скруглённой плашке его фирменного цвета
        static Border PresetBadge(SitePreset p, double size)
        {
            TextBlock mark = new TextBlock
            {
                Text = p.Mark ?? p.Name.Substring(0, 1), Foreground = Brushes.White, FontWeight = FontWeights.Bold,
                FontSize = size * (p.Mark != null && p.Mark.Length > 1 ? 0.48 : 0.58),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            Border b = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), Child = mark, VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p.Color ?? "#808080"))
            };
            return b;
        }

        // Список из текстового файла: по элементу на строку, комментарии после # пропускаются
        string ReadListFile()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = T("Добавить списком из файла"), Filter = T("Текстовые списки") + " (*.txt;*.lst)|*.txt;*.lst|" + T("Все файлы") + " (*.*)|*.*"
            };
            if (dlg.ShowDialog(window) != true) return null;
            try { return File.ReadAllText(dlg.FileName); }
            catch (Exception e) { Ask(T("Не удалось прочитать файл: ") + e.Message, MessageBoxButton.OK, MessageBoxImage.Error); return null; }
        }

        // Поле поиска: подсказка прячется, пока есть текст; список перерисовывается на каждый символ
        void SearchBox(string box, string hint, Action refresh)
        {
            TextBox input = E<TextBox>(box);
            input.TextChanged += delegate
            {
                Text(hint).Visibility = input.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
                refresh();
            };
        }

        // Вставка нескольких строк в однострочное поле: строки склеиваются через запятую, а не обрезаются до первой.
        // Комментарии после # убираются сразу — в одной строке они съели бы всё, что после них
        static void PasteAsOneLine(TextBox box)
        {
            DataObject.AddPastingHandler(box, (s, e) =>
            {
                string text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
                if (text == null || text.IndexOfAny(new[] { '\r', '\n' }) < 0) return;
                IEnumerable<string> lines = text.Split('\n')
                    .Select(l => { int c = l.IndexOf('#'); return (c >= 0 ? l.Substring(0, c) : l).Trim(); })
                    .Where(l => l.Length > 0);
                DataObject one = new DataObject();
                one.SetData(DataFormats.UnicodeText, string.Join(", ", lines));
                e.DataObject = one;
            });
        }

        async Task RemoveDomain(string domain)
        {
            Zapret.SaveUserDomains(DomainKind, Zapret.UserDomains(DomainKind).Where(d => d != domain));
            Log.Write("Удалён домен: " + domain);
            UpdateDomainUi();
            AutoBackup();
            await RestartIfRunning();
        }

        void UpdateFilterUi()
        {
            if (!Directory.Exists(Paths.Zapret)) return;
            string gf = Zapret.ReadGameFilter().Mode;
            E<RadioButton>("GfOff").IsChecked = gf != "tcp" && gf != "udp" && gf != "all";
            E<RadioButton>("GfTcp").IsChecked = gf == "tcp";
            E<RadioButton>("GfUdp").IsChecked = gf == "udp";
            E<RadioButton>("GfAll").IsChecked = gf == "all";
            string ip = Zapret.IpsetMode(Paths.Zapret);
            E<RadioButton>("IpNone").IsChecked = ip == "none";
            E<RadioButton>("IpLoaded").IsChecked = ip == "loaded";
            E<RadioButton>("IpAny").IsChecked = ip == "any";
        }
    }
}
