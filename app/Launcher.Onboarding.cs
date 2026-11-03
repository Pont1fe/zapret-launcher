// Приветствие: шаги тура и картинки к ним
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
        // ---------- Приветствие ----------
        // значок, заголовок, текст, кнопки, картинка шага (рисуется в OnboardingPicture)
        static readonly string[][] OnboardingSteps =
        {
            new[] { "", "Добро пожаловать в Zapret Launcher",
                "Лаунчер включает zapret одной кнопкой, следит, чтобы Discord, YouTube и Telegram открывались, и сам обновляется.", "Пропустить", "Дальше", "welcome" },
            new[] { "", "Работает в фоне",
                "Закрытое окно сворачивается в трей. Клик по значку открывает мини-окно с переключателем и пингом. Включите «Автозапуск», чтобы обход запускался вместе с Windows.", "Пропустить", "Дальше", "" },
            new[] { "", "Встроенный VPN",
                "Есть свой VPN-сервер или подписка? Вставьте ключ или отсканируйте QR-код на странице VPN. Через VPN пойдёт только то, что нужно, остальное — напрямую и через обход, на полной скорости.", "Пропустить", "Дальше", "vpn" },
            new[] { "", "Три режима маршрутов",
                "Выберите на вкладке «Маршруты», что пускать через VPN. Режим можно поменять в любой момент — VPN переподключится сам.", "Пропустить", "Дальше", "modes" },
            new[] { "", "Найдём рабочую стратегию",
                "У каждого провайдера свои блокировки. Лаунчер по очереди проверит все стратегии и выберет лучшую — это займёт пару минут. Если включён VPN, выключите его на время проверки.", "Позже", "Подобрать сейчас", "pick" },
        };

        // Картинки приветствия — из тех же элементов, что и в окне, поэтому сами подстраиваются под тему и цвет акцента
        FrameworkElement OnboardingPicture(string kind)
        {
            if (kind == "welcome")
            {
                StackPanel p = new StackPanel();
                Grid card = new Grid();
                card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                card.ColumnDefinitions.Add(new ColumnDefinition());
                card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                card.Children.Add(PicBadge("", "Accent", Brushes.White, 40));
                StackPanel t = PicText(T("Обход работает"), T("Стратегия") + ": ALT2", 15);
                Grid.SetColumn(t, 1);
                card.Children.Add(t);
                FrameworkElement toggle = PicToggle(true);
                Grid.SetColumn(toggle, 2);
                card.Children.Add(toggle);
                p.Children.Add(PicCard(card, true));
                UniformGrid sites = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, -8, 0) };
                foreach (string[] s in new[] { new[] { "Discord", "742" }, new[] { "YouTube", "688" }, new[] { "Telegram", "455" } })
                {
                    string[] look = SiteLook(s[0]);
                    Grid row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.Children.Add(PicBadge(look[0], null, Brushes.White, 22, look[1]));
                    StackPanel st = PicText(s[0], s[1] + " " + T("мс"), 12);
                    Grid.SetColumn(st, 1);
                    row.Children.Add(st);
                    Border c = PicCard(row, false);
                    c.Margin = new Thickness(0, 0, 8, 0);
                    sites.Children.Add(c);
                }
                p.Children.Add(sites);
                return p;
            }
            if (kind == "vpn")
            {
                // компьютер → две дороги: нужное — через VPN, остальное — напрямую и через обход
                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                FrameworkElement pc = PicBadge("", "IconBg", null, 44);
                g.Children.Add(pc);
                TextBlock arrow = new TextBlock { Text = "", FontSize = 14, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                arrow.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
                arrow.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                Grid.SetColumn(arrow, 1);
                g.Children.Add(arrow);
                StackPanel routes = new StackPanel();
                routes.Children.Add(PicRoute("", T("Через VPN"), "chatgpt.com · Spotify · " + T("ваши сайты"), true));
                Border direct = PicRoute("", T("Напрямую и через обход"), "Discord · YouTube · " + T("всё остальное"), false);
                direct.Margin = new Thickness(0, 8, 0, 0);
                routes.Children.Add(direct);
                Grid.SetColumn(routes, 2);
                g.Children.Add(routes);
                return g;
            }
            if (kind == "modes")
            {
                UniformGrid g = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 0) };
                g.Children.Add(PicMode("", null, "Только выбранное", "Ваши сайты и программы — через VPN", true));
                g.Children.Add(PicMode(null, "ru", "Россия напрямую", "Российское — напрямую, остальное — через VPN", false));
                g.Children.Add(PicMode("", null, "Весь трафик", "Весь интернет — через VPN", false));
                return g;
            }
            if (kind == "pick")
            {
                StackPanel p = new StackPanel();
                WrapPanel chips = new WrapPanel();
                foreach (string[] s in new[] { new[] { "ALT", "" }, new[] { "ALT2", "742 " + T("мс") }, new[] { "ALT3", "" }, new[] { "FAKE TLS", "910 " + T("мс") } })
                {
                    bool ok = s[1].Length > 0;
                    StackPanel c = new StackPanel { Orientation = Orientation.Horizontal };
                    TextBlock mark = new TextBlock { Text = ok ? "" : "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                    mark.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
                    mark.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Ok" : "Fail");
                    c.Children.Add(mark);
                    c.Children.Add(new TextBlock { Text = s[0], FontSize = 12, FontWeight = s[0] == "ALT2" ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
                    if (ok)
                    {
                        TextBlock ms = new TextBlock { Text = "  " + s[1], FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                        ms.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                        c.Children.Add(ms);
                    }
                    Border chip = new Border { Child = c, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1) };
                    chip.SetResourceReference(Border.BackgroundProperty, s[0] == "ALT2" ? "AccentSoft" : "Card");
                    chip.SetResourceReference(Border.BorderBrushProperty, s[0] == "ALT2" ? "Accent" : "Line");
                    chips.Children.Add(chip);
                }
                p.Children.Add(chips);
                Grid bar = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 0) };
                Border bg = new Border { CornerRadius = new CornerRadius(3) };
                bg.SetResourceReference(Border.BackgroundProperty, "IconBg");
                bar.Children.Add(bg);
                Border fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 120 };
                fill.SetResourceReference(Border.BackgroundProperty, "Accent");
                bar.Children.Add(fill);
                p.Children.Add(bar);
                TextBlock note = new TextBlock { Text = T("Подбор") + " 5 / 19 · " + T("Лучшая пока") + ": ALT2", FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
                note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                p.Children.Add(note);
                return p;
            }
            return null;
        }

        // Квадратный значок: глиф на фоне (ресурс темы или фирменный цвет сервиса)
        static FrameworkElement PicBadge(string glyph, string bgKey, Brush fg, double size, string color = null)
        {
            Border b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), Margin = new Thickness(0, 0, size > 30 ? 12 : 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (color != null) b.Background = B(color); else b.SetResourceReference(Border.BackgroundProperty, bgKey);
            TextBlock t = new TextBlock { Text = glyph, FontSize = size * 0.45, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
            if (fg != null) t.Foreground = fg; else t.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            b.Child = t;
            return b;
        }

        static StackPanel PicText(string title, string sub, double size)
        {
            StackPanel p = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            p.Children.Add(new TextBlock { Text = title, FontSize = size, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            TextBlock s = new TextBlock { Text = sub, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            s.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            p.Children.Add(s);
            return p;
        }

        static Border PicCard(UIElement child, bool accent)
        {
            Border b = new Border { Child = child, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BackgroundProperty, accent ? "AccentSoft" : "Card");
            b.SetResourceReference(Border.BorderBrushProperty, accent ? "AccentBorder" : "Line");
            return b;
        }

        static FrameworkElement PicToggle(bool on)
        {
            Border track = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Center };
            track.SetResourceReference(Border.BackgroundProperty, on ? "Accent" : "TrackOff");
            track.Child = new Ellipse { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(3, 0, 3, 0) };
            return track;
        }

        static Border PicRoute(string glyph, string title, string sub, bool vpn)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(PicBadge(glyph, vpn ? "Accent" : "IconBg", vpn ? Brushes.White : null, 28));
            StackPanel t = PicText(title, sub, 12.5);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return PicCard(g, vpn);
        }

        static Border PicMode(string glyph, string flag, string title, string sub, bool selected)
        {
            StackPanel p = new StackPanel();
            FrameworkElement icon = flag != null ? FlagElement(flag, 28, 20) : PicBadge(glyph, selected ? "Accent" : "IconBg", selected ? Brushes.White : null, 28);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.Margin = new Thickness(0, 0, 0, 8);
            p.Children.Add(icon);
            p.Children.Add(new TextBlock { Text = T(title), FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            TextBlock s = new TextBlock { Text = T(sub), FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
            s.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            p.Children.Add(s);
            Border b = PicCard(p, selected);
            b.Margin = new Thickness(0, 0, 8, 0);
            return b;
        }

        void ShowOnboarding()
        {
            onboardingStep = 0;
            FrameworkElement overlay = E<FrameworkElement>("Onboarding");
            overlay.Visibility = Visibility.Visible;
            overlay.Opacity = 0;
            Fade(overlay, 1, TimeSpan.FromMilliseconds(220));
            RenderOnboarding();
        }

        void RenderOnboarding()
        {
            string[] s = OnboardingSteps[onboardingStep];
            Text("OnbIcon").Text = s[0];
            Text("OnbTitle").Text = T(s[1]);
            Text("OnbText").Text = T(s[2]);
            E<Button>("OnbSkip").Content = T(s[3]);
            E<Button>("OnbNext").Content = T(s[4]);
            FrameworkElement picture = OnboardingPicture(s[5]);
            Border host = E<Border>("OnbPicture");
            host.Child = picture;
            host.Visibility = picture != null ? Visibility.Visible : Visibility.Collapsed;
            StackPanel dots = E<StackPanel>("OnbDots");
            dots.Children.Clear();
            for (int i = 0; i < OnboardingSteps.Length; i++)
            {
                Border dot = new Border { Width = i == onboardingStep ? 20 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0) };
                dot.SetResourceReference(Border.BackgroundProperty, i == onboardingStep ? "Accent" : "Line");
                dots.Children.Add(dot);
            }
            FrameworkElement card = E<FrameworkElement>("OnbCard");
            TranslateTransform tr = new TranslateTransform(16, 0);
            card.RenderTransform = tr;
            tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(16, 0, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        async Task OnboardingNext(bool skip)
        {
            bool last = onboardingStep == OnboardingSteps.Length - 1;
            if (!skip && !last) { onboardingStep++; RenderOnboarding(); return; }
            cfg.OnboardingDone = true;
            cfg.Save();
            E<FrameworkElement>("Onboarding").Visibility = Visibility.Collapsed;
            if (!skip && last) await StartAutoPickUi(false);
        }
    }
}
