// Темы оформления: тёмная, светлая, как в Windows; прозрачный фон Mica (Windows 11 22H2+).
// Цвета лежат в ресурсах приложения, разметка берёт их через DynamicResource — поэтому тема меняется на лету.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;

namespace ZapretLauncher
{
    static class Theme
    {
        public static bool Dark { get; private set; }
        public static bool Transparent { get; private set; }

        // Mica официально работает с Windows 11 22H2 (сборка 22621)
        public static bool MicaSupported { get { return Environment.OSVersion.Version.Build >= 22621; } }

        static readonly Dictionary<string, string> DarkColors = new Dictionary<string, string>
        {
            { "WindowBg", "#121316" }, { "TitleBg", "#16171B" }, { "SidebarBg", "#16171B" }, { "MidBg", "#191A1F" }, { "RightBg", "#131418" },
            { "Card", "#1E2025" }, { "CardHover", "#252830" }, { "CardRaised", "#1C1E23" }, { "Line", "#2C2E35" },
            { "Text", "#ECEEF2" }, { "TextSub", "#C3C6CE" }, { "Muted", "#8B8E98" }, { "Faint", "#6B6E78" },
            { "Accent", "#1FB89A" }, { "AccentHover", "#2CD3B1" }, { "AccentSoft", "#1A2F2B" }, { "AccentBorder", "#24564C" }, { "AccentText", "#0B1F1B" },
            { "Input", "#1E2025" }, { "InputBorder", "#2C2E35" }, { "SegmentBg", "#25272E" }, { "TrackOff", "#33363E" }, { "KnobOff", "#C9CCD4" },
            { "IconBg", "#26282F" }, { "Thumb", "#3A3D46" }, { "Hover", "#22242A" }, { "NavHover", "#1F2127" }, { "ButtonHover", "#262830" },
            { "WarnBg", "#33261A" }, { "WarnBorder", "#7A5420" }, { "WarnText", "#FFC67A" }, { "Danger", "#FF8A8A" },
            { "Overlay", "#B3000000" }, { "ActiveFrom", "#16443B" }, { "ActiveTo", "#1B2A2A" }, { "ActiveBorder", "#2A7A69" }, { "Glow", "#551FB89A" },
            { "Ok", "#3DD68C" }, { "Fail", "#FF6B6B" }, { "Wait", "#E0B040" }, { "Idle", "#4A4D55" },
        };

        static readonly Dictionary<string, string> LightColors = new Dictionary<string, string>
        {
            { "WindowBg", "#F3F4F6" }, { "TitleBg", "#EBEDF0" }, { "SidebarBg", "#EBEDF0" }, { "MidBg", "#F7F8FA" }, { "RightBg", "#F1F3F5" },
            { "Card", "#FFFFFF" }, { "CardHover", "#F0F2F5" }, { "CardRaised", "#FFFFFF" }, { "Line", "#E1E4E8" },
            { "Text", "#16181D" }, { "TextSub", "#3B3F47" }, { "Muted", "#6B707A" }, { "Faint", "#9AA0A8" },
            { "Accent", "#12A386" }, { "AccentHover", "#0E8E75" }, { "AccentSoft", "#DDF4EE" }, { "AccentBorder", "#9ED9CA" }, { "AccentText", "#FFFFFF" },
            { "Input", "#FFFFFF" }, { "InputBorder", "#D9DCE1" }, { "SegmentBg", "#EEF0F3" }, { "TrackOff", "#C9CDD3" }, { "KnobOff", "#FFFFFF" },
            { "IconBg", "#E9ECEF" }, { "Thumb", "#C3C7CE" }, { "Hover", "#EEF0F3" }, { "NavHover", "#E2E5E9" }, { "ButtonHover", "#E4E7EB" },
            { "WarnBg", "#FFF4E0" }, { "WarnBorder", "#E8B15A" }, { "WarnText", "#8A5300" }, { "Danger", "#D64545" },
            { "Overlay", "#80000000" }, { "ActiveFrom", "#C9F0E6" }, { "ActiveTo", "#E9F7F3" }, { "ActiveBorder", "#7FCFBB" }, { "Glow", "#4412A386" },
            { "Ok", "#1FA866" }, { "Fail", "#E04848" }, { "Wait", "#C99A1C" }, { "Idle", "#B5BAC2" },
        };

        // В прозрачном режиме фоны окна полупрозрачные, чтобы сквозь них было видно Mica
        static readonly Dictionary<string, string> DarkGlass = new Dictionary<string, string>
        {
            { "WindowBg", "#00000000" }, { "TitleBg", "#00000000" }, { "SidebarBg", "#22000000" }, { "MidBg", "#33000000" }, { "RightBg", "#11000000" },
            { "Card", "#B31E2025" }, { "CardRaised", "#B31C1E23" }, { "Input", "#991E2025" }, { "SegmentBg", "#AA25272E" },
        };

        static readonly Dictionary<string, string> LightGlass = new Dictionary<string, string>
        {
            { "WindowBg", "#00000000" }, { "TitleBg", "#00000000" }, { "SidebarBg", "#22FFFFFF" }, { "MidBg", "#55FFFFFF" }, { "RightBg", "#33FFFFFF" },
            { "Card", "#D9FFFFFF" }, { "CardRaised", "#D9FFFFFF" }, { "Input", "#CCFFFFFF" }, { "SegmentBg", "#CCEEF0F3" },
        };

        public static bool SystemPrefersDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return !(v is int) || (int)v == 0;
                }
            }
            catch { return true; }
        }

        // theme: dark / light / system
        public static void Apply(string theme, bool transparent)
        {
            Dark = theme == "light" ? false : theme == "dark" ? true : SystemPrefersDark();
            Transparent = transparent && MicaSupported;
            Dictionary<string, string> colors = new Dictionary<string, string>(Dark ? DarkColors : LightColors);
            if (Transparent) foreach (KeyValuePair<string, string> kv in Dark ? DarkGlass : LightGlass) colors[kv.Key] = kv.Value;
            ResourceDictionary res = Application.Current.Resources;
            foreach (KeyValuePair<string, string> kv in colors)
            {
                Color c = (Color)ColorConverter.ConvertFromString(kv.Value);
                res[kv.Key] = new SolidColorBrush(c);
                res[kv.Key + "Color"] = c;
            }
        }

        public static Brush Brush(string key) { return (Brush)Application.Current.Resources[key]; }
        public static Color Color(string key) { return (Color)Application.Current.Resources[key + "Color"]; }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [StructLayout(LayoutKind.Sequential)]
        struct Margins { public int Left, Right, Top, Bottom; }

        // Оформление окна в DWM: тёмная/светлая рамка, скруглённые углы (Windows 11), Mica или обычный фон
        public static void ApplyWindow(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = Dark ? 1 : 0, round = 2, border = Dark ? 0x00352E2C : 0x00E8E4E1;
            int backdrop = Transparent ? 2 /* DWMSBT_MAINWINDOW (Mica) */ : 1 /* DWMSBT_NONE */;
            try
            {
                DwmSetWindowAttribute(hwnd, 20, ref dark, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);    // DWMWA_WINDOW_CORNER_PREFERENCE
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);   // DWMWA_BORDER_COLOR
                if (MicaSupported) DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);   // DWMWA_SYSTEMBACKDROP_TYPE
                Margins m = Transparent ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new Margins();
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                HwndSource src = HwndSource.FromHwnd(hwnd);
                if (src != null && src.CompositionTarget != null)
                    src.CompositionTarget.BackgroundColor = Transparent ? Colors.Transparent : Color("WindowBg");
            }
            catch { }
            WindowChrome chrome = new WindowChrome
            {
                CaptionHeight = 38, ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false, GlassFrameThickness = Transparent ? new Thickness(-1) : new Thickness(0)
            };
            WindowChrome.SetWindowChrome(window, chrome);
            window.Background = Transparent ? Brushes.Transparent : Brush("WindowBg");
        }
    }
}
