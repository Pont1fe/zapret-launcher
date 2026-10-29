// Чтение QR-кодов с ключами VPN: из картинки или прямо с экрана. Библиотека ZXing.Net встроена в exe (см. build.ps1)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ZapretLauncher
{
    static class Qr
    {
        // zxing.dll лежит ресурсом внутри лаунчера — подгружаем её, когда .NET попросит
        public static void RegisterResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                if (!e.Name.StartsWith("zxing,", StringComparison.OrdinalIgnoreCase)) return null;
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("zxing.dll"))
                {
                    if (st == null) return null;
                    byte[] bytes = new byte[st.Length];
                    st.Read(bytes, 0, bytes.Length);
                    return Assembly.Load(bytes);
                }
            };
        }

        // Все QR-коды на картинке (тексты); пустой список — не нашлось
        public static List<string> Decode(Bitmap bmp)
        {
            ZXing.BarcodeReader reader = new ZXing.BarcodeReader { AutoRotate = true };
            reader.Options.TryHarder = true;
            reader.Options.PossibleFormats = new List<ZXing.BarcodeFormat> { ZXing.BarcodeFormat.QR_CODE };
            ZXing.Result[] many = reader.DecodeMultiple(bmp);
            if (many != null && many.Length > 0) return many.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
            ZXing.Result one = reader.Decode(bmp);
            return one == null ? new List<string>() : new List<string> { one.Text };
        }

        public static List<string> FromFile(string path)
        {
            using (Bitmap bmp = new Bitmap(path)) return Decode(bmp);
        }

        // Снимок всех мониторов
        public static List<string> FromScreen()
        {
            Rectangle area = System.Windows.Forms.SystemInformation.VirtualScreen;
            using (Bitmap bmp = new Bitmap(area.Width, area.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
                return Decode(bmp);
            }
        }
    }
}
