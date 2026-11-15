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
            { "WindowBg", "#0D0E11" }, { "MidBg", "#121317" },   // глубже: карточки отделяются от фона, а не тонут в нём
            { "Card", "#1C1E24" }, { "CardHover", "#24272E" }, { "CardRaised", "#191B20" }, { "Line", "#2A2C33" }, { "CardEdge", "#14FFFFFF" },
            { "Text", "#ECEEF2" }, { "TextSub", "#C3C6CE" }, { "Muted", "#8B8E98" }, { "Faint", "#6B6E78" },
            { "Accent", "#1FB89A" }, { "AccentHover", "#2CD3B1" }, { "AccentSoft", "#1A2F2B" }, { "AccentBorder", "#24564C" }, { "AccentText", "#0B1F1B" },
            { "Input", "#1E2025" }, { "InputBorder", "#2C2E35" }, { "SegmentBg", "#25272E" }, { "TrackOff", "#33363E" }, { "KnobOff", "#C9CCD4" },
            { "IconBg", "#26282F" }, { "Thumb", "#3A3D46" }, { "Hover", "#22242A" }, { "ButtonHover", "#262830" },
            { "WarnBg", "#33261A" }, { "WarnBorder", "#7A5420" }, { "WarnText", "#FFC67A" }, { "Danger", "#FF8A8A" },
            { "Overlay", "#B3000000" }, { "Glow", "#551FB89A" },
            { "Ok", "#3DD68C" }, { "Fail", "#FF6B6B" }, { "Wait", "#E0B040" }, { "Idle", "#4A4D55" },
            { "Shadow", "#000000" },
        };

        static readonly Dictionary<string, string> LightColors = new Dictionary<string, string>
        {
            // светлая — «воздушная»: лиловатые фоны, белые карточки с мягкой тенью, почти без рамок
            { "WindowBg", "#F3F2FA" }, { "MidBg", "#F8F7FD" },
            { "Card", "#FFFFFF" }, { "CardHover", "#F4F2FD" }, { "CardRaised", "#FFFFFF" }, { "Line", "#ECEAF6" }, { "CardEdge", "#00FFFFFF" },
            { "Text", "#16152B" }, { "TextSub", "#3B3955" }, { "Muted", "#7E7C98" }, { "Faint", "#A9A7C0" },
            { "Accent", "#12A386" }, { "AccentHover", "#0E8E75" }, { "AccentSoft", "#DDF4EE" }, { "AccentBorder", "#9ED9CA" }, { "AccentText", "#FFFFFF" },
            { "Input", "#FFFFFF" }, { "InputBorder", "#E4E1F2" }, { "SegmentBg", "#EFEDF8" }, { "TrackOff", "#D3D0E4" }, { "KnobOff", "#FFFFFF" },
            { "IconBg", "#EEECF8" }, { "Thumb", "#CFCCE0" }, { "Hover", "#F0EEF9" }, { "ButtonHover", "#E9E6F7" },
            { "WarnBg", "#FFF4E0" }, { "WarnBorder", "#E8B15A" }, { "WarnText", "#8A5300" }, { "Danger", "#D64545" },
            { "Overlay", "#80000000" }, { "Glow", "#4412A386" },
            { "Ok", "#1FA866" }, { "Fail", "#E04848" }, { "Wait", "#C99A1C" }, { "Idle", "#B5BAC2" },
            { "Shadow", "#3B2F8F" },   // тень карточек: лиловая, слабая
        };

        // В прозрачном режиме фоны окна полупрозрачные, чтобы сквозь них было видно Mica
        static readonly Dictionary<string, string> DarkGlass = new Dictionary<string, string>
        {
            { "WindowBg", "#00000000" }, { "MidBg", "#33000000" },
            { "Card", "#B31E2025" }, { "CardRaised", "#B31C1E23" }, { "Input", "#991E2025" }, { "SegmentBg", "#AA25272E" },
        };

        static readonly Dictionary<string, string> LightGlass = new Dictionary<string, string>
        {
            { "WindowBg", "#00000000" }, { "MidBg", "#55FFFFFF" },
            { "Card", "#D9FFFFFF" }, { "CardRaised", "#D9FFFFFF" }, { "Input", "#CCFFFFFF" }, { "SegmentBg", "#CCEEF0F3" },
        };

        // Готовые темы: тёмная основа со своими фонами, карточками и линиями; [0] — родной акцент (ставится при выборе темы, потом его можно сменить)
        public static readonly Dictionary<string, string> PresetAccents = new Dictionary<string, string> { { "night", "violet" }, { "ocean", "teal" }, { "sunset", "orange" } };
        static readonly Dictionary<string, Dictionary<string, string>> Presets = new Dictionary<string, Dictionary<string, string>>
        {
            // Ночь — чёрный, как экран OLED: карточки чуть светлее фона
            { "night", new Dictionary<string, string> {
                { "WindowBg", "#000000" }, { "MidBg", "#050507" }, { "Card", "#101116" }, { "CardHover", "#17181F" }, { "CardRaised", "#0C0D11" }, { "Line", "#1D1E25" },
                { "Input", "#101116" }, { "InputBorder", "#1D1E25" }, { "SegmentBg", "#14151B" }, { "TrackOff", "#26272F" }, { "IconBg", "#17181E" }, { "Thumb", "#2C2D36" },
                { "Hover", "#121319" }, { "ButtonHover", "#17181F" } } },
            // Океан — глубокий сине-зелёный, приглушённые надписи с холодным оттенком
            { "ocean", new Dictionary<string, string> {
                { "WindowBg", "#06141C" }, { "MidBg", "#0A1B25" }, { "Card", "#10252F" }, { "CardHover", "#152E3A" }, { "CardRaised", "#0E212B" }, { "Line", "#1B3542" },
                { "Input", "#10252F" }, { "InputBorder", "#1B3542" }, { "SegmentBg", "#122A36" }, { "TrackOff", "#1F3C4A" }, { "IconBg", "#13303D" }, { "Thumb", "#27495A" },
                { "Hover", "#0F2633" }, { "ButtonHover", "#143140" }, { "TextSub", "#BFD0D8" }, { "Muted", "#7F98A6" }, { "Faint", "#5E7682" } } },
            // Закат — тёплый сливовый, надписи с розоватым оттенком
            { "sunset", new Dictionary<string, string> {
                { "WindowBg", "#160C14" }, { "MidBg", "#1D1019" }, { "Card", "#271621" }, { "CardHover", "#301C29" }, { "CardRaised", "#23131E" }, { "Line", "#37212F" },
                { "Input", "#271621" }, { "InputBorder", "#37212F" }, { "SegmentBg", "#2C1925" }, { "TrackOff", "#3E2634" }, { "IconBg", "#2F1B28" }, { "Thumb", "#4B2E3F" },
                { "Hover", "#261521" }, { "ButtonHover", "#2E1A27" }, { "TextSub", "#DCC6D0" }, { "Muted", "#A58A98" }, { "Faint", "#7D6472" } } },
        };

        public static bool IsPreset(string theme) { return theme != null && Presets.ContainsKey(theme); }

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

        // Цвета акцента; бирюзовый — родная палитра выше, остальные выводятся из базового цвета
        public static readonly Dictionary<string, string> Accents = new Dictionary<string, string>
        {
            { "teal", "#1FB89A" }, { "blue", "#3B82F6" }, { "violet", "#7558F7" }, { "green", "#22C55E" }, { "orange", "#F59E0B" }, { "pink", "#EC4899" },
        };

        // Цвет акцента Windows (Параметры → Персонализация → Цвета)
        public static Color SystemAccent()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int)
                    {
                        int abgr = (int)v;
                        return System.Windows.Media.Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                    }
                }
            }
            catch { }
            return (Color)ColorConverter.ConvertFromString(Accents["teal"]);
        }

        static Color Mix(Color a, Color b, double t)
        {
            return System.Windows.Media.Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
        static string Hex(byte alpha, Color c) { return "#" + alpha.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        // Оттенки акцента для тёмной и светлой темы — по тем же пропорциям, что у бирюзовой палитры
        static void ApplyAccent(Dictionary<string, string> colors, Color a, bool dark)
        {
            Color black = Colors.Black, white = Colors.White;
            if (dark)
            {
                Color bg = (Color)ColorConverter.ConvertFromString(colors["MidBg"]);   // мягкие оттенки акцента — на фоне именно этой темы
                colors["Accent"] = Hex(a);
                colors["AccentHover"] = Hex(Mix(a, white, 0.18));
                colors["AccentSoft"] = Hex(Mix(bg, a, 0.14));
                colors["AccentBorder"] = Hex(Mix(bg, a, 0.38));
                colors["AccentText"] = Hex(Mix(a, black, 0.85));
                colors["Glow"] = Hex(0x55, a);
            }
            else
            {
                Color deep = Mix(a, black, 0.12);
                colors["Accent"] = Hex(deep);
                colors["AccentHover"] = Hex(Mix(a, black, 0.24));
                colors["AccentSoft"] = Hex(Mix(white, a, 0.14));
                colors["AccentBorder"] = Hex(Mix(white, a, 0.42));
                colors["AccentText"] = "#FFFFFF";
                colors["Glow"] = Hex(0x44, deep);
            }
        }

        // theme: dark / light / system или готовая тема (night / ocean / sunset); accent: ключ из Accents или system;
        // wallpaper — за окном своя картинка: панели и карточки становятся полупрозрачными, чтобы она просвечивала
        public static void Apply(string theme, bool transparent, string accent = "teal", bool wallpaper = false)
        {
            Dark = theme == "light" ? false : theme == "system" ? SystemPrefersDark() : true;
            Transparent = transparent && MicaSupported;
            Dictionary<string, string> colors = Palette(theme, Dark, Transparent, accent, wallpaper);
            ResourceDictionary res = Application.Current.Resources;
            foreach (KeyValuePair<string, string> kv in colors)
            {
                Color c = (Color)ColorConverter.ConvertFromString(kv.Value);
                res[kv.Key] = new SolidColorBrush(c);
                res[kv.Key + "Color"] = c;
            }
            res["SystemAccent"] = new SolidColorBrush(SystemAccent());
        }

        public static Dictionary<string, string> Palette(string theme, bool dark, bool transparent, string accent, bool wallpaper)
        {
            Dictionary<string, string> colors = new Dictionary<string, string>(dark ? DarkColors : LightColors);
            bool preset = IsPreset(theme);
            if (preset) foreach (KeyValuePair<string, string> kv in Presets[theme]) colors[kv.Key] = kv.Value;
            // бирюзовый — родная палитра тёмной и светлой тем; у готовой темы оттенки акцента считаются под её фон
            if (accent == "system") ApplyAccent(colors, SystemAccent(), dark);
            else if ((accent != "teal" || preset) && Accents.ContainsKey(accent)) ApplyAccent(colors, (Color)ColorConverter.ConvertFromString(Accents[accent]), dark);
            if (transparent) foreach (KeyValuePair<string, string> kv in dark ? DarkGlass : LightGlass) colors[kv.Key] = kv.Value;
            else if (wallpaper)
                foreach (KeyValuePair<string, string> kv in new Dictionary<string, string> { { "MidBg", "99" }, { "Card", "D9" }, { "CardRaised", "D9" }, { "Input", "CC" }, { "SegmentBg", "CC" } })
                    colors[kv.Key] = "#" + kv.Value + colors[kv.Key].TrimStart('#');
            return colors;
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
        public static void ApplyWindow(Window window, double caption = 38)
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
                CaptionHeight = caption, ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.CanResize ? 6 : 0), CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false, GlassFrameThickness = Transparent ? new Thickness(-1) : new Thickness(0)
            };
            WindowChrome.SetWindowChrome(window, chrome);
            window.Background = Transparent ? Brushes.Transparent : Brush("WindowBg");
        }
    }
}
