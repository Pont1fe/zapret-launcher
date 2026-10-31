// Уведомления Windows 10/11 с кнопками (WinRT через отражение — без SDK) и оформление окна (скругление, тёмная рамка)
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security;

namespace ZapretLauncher
{
    static class Toasts
    {
        public const string AppId = "Pont1fe.ZapretLauncher";

        [DllImport("shell32.dll")]
        static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindCtx, int flags, ref Guid iid, out IPropertyStore store);

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            int GetCount(out uint count);
            int GetAt(uint index, out PropertyKey key);
            int GetValue(ref PropertyKey key, out PropVariant value);
            int SetValue(ref PropertyKey key, ref PropVariant value);
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PropertyKey { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Sequential)]
        struct PropVariant { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

        static Type managerType, xmlType, toastType;
        static bool available;

        // Готовит уведомления: идентификатор процесса и тот же идентификатор у ярлыка в «Пуске» (без него Windows их не покажет)
        public static void Init()
        {
            try
            {
                SetCurrentProcessExplicitAppUserModelID(AppId);
                if (!File.Exists(Paths.StartMenuShortcut) || !Shell.ShortcutPointsHere(Paths.StartMenuShortcut)) return;
                SetShortcutAppId(Paths.StartMenuShortcut);
                managerType = Type.GetType("Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime");
                xmlType = Type.GetType("Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime")
                       ?? Type.GetType("Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType=WindowsRuntime");
                toastType = Type.GetType("Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType=WindowsRuntime");
                available = managerType != null && xmlType != null && toastType != null;
            }
            catch (Exception e) { Log.Write("Уведомления Windows недоступны: " + e.Message); available = false; }
        }

        static void SetShortcutAppId(string path)
        {
            Guid iid = typeof(IPropertyStore).GUID;
            IPropertyStore store;
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2 /* GPS_READWRITE */, ref iid, out store) != 0) return;
            try
            {
                PropertyKey key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };   // System.AppUserModel.ID
                PropVariant current;
                if (store.GetValue(ref key, out current) == 0 && current.vt == 31 && Marshal.PtrToStringUni(current.p) == AppId) return;
                PropVariant v = new PropVariant { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(AppId) };
                store.SetValue(ref key, ref v);
                store.Commit();
                Marshal.FreeCoTaskMem(v.p);
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        // Показывает уведомление; actions — пары «подпись, код». false — показать не удалось (тогда используем трей)
        public static bool Show(string title, string text, string[][] actions, Action<string> onAction)
        {
            if (!available) return false;
            try
            {
                string xml = "<toast launch=\"open\"><visual><binding template=\"ToastGeneric\"><text>" + SecurityElement.Escape(title) + "</text><text>" +
                    SecurityElement.Escape(text) + "</text></binding></visual>";
                if (actions != null && actions.Length > 0)
                {
                    xml += "<actions>";
                    foreach (string[] a in actions)
                        xml += "<action content=\"" + SecurityElement.Escape(a[0]) + "\" arguments=\"" + SecurityElement.Escape(a[1]) + "\"/>";
                    xml += "</actions>";
                }
                xml += "</toast>";

                object doc = Activator.CreateInstance(xmlType);
                xmlType.GetMethod("LoadXml", new[] { typeof(string) }).Invoke(doc, new object[] { xml });
                object toast = Activator.CreateInstance(toastType, doc);
                if (onAction != null)
                {
                    // ev.AddEventHandler на событиях WinRT в .NET Framework не работает — подписываемся через WindowsRuntimeMarshal
                    EventInfo ev = toastType.GetEvent("Activated");
                    Delegate handler = Delegate.CreateDelegate(ev.EventHandlerType, new Activation(onAction), typeof(Activation).GetMethod("Handle"));
                    Type token = typeof(EventRegistrationToken);
                    Delegate add = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(ev.EventHandlerType, token), toast, ev.GetAddMethod());
                    Delegate remove = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(token), toast, ev.GetRemoveMethod());
                    typeof(WindowsRuntimeMarshal).GetMethod("AddEventHandler").MakeGenericMethod(ev.EventHandlerType).Invoke(null, new object[] { add, remove, handler });
                }
                object notifier = managerType.GetMethod("CreateToastNotifier", new[] { typeof(string) }).Invoke(null, new object[] { AppId });
                notifier.GetType().GetMethod("Show").Invoke(notifier, new[] { toast });
                return true;
            }
            catch (Exception e)
            {
                Log.Write("Не удалось показать уведомление Windows: " + (e.InnerException ?? e).Message);
                available = false;
                return false;
            }
        }

        public class Activation
        {
            readonly Action<string> callback;
            public Activation(Action<string> callback) { this.callback = callback; }

            // Вызывается Windows в фоновом потоке: sender — ToastNotification, args — ToastActivatedEventArgs
            public void Handle(object sender, object args)
            {
                string arguments = "open";
                try { arguments = (string)args.GetType().GetProperty("Arguments").GetValue(args); } catch { }
                callback(arguments);
            }
        }
    }

    static class WindowStyleHelper
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Windows 11: скруглённые углы и тёмная рамка; на Windows 10 атрибуты просто игнорируются
        public static void Apply(IntPtr hwnd)
        {
            int dark = 1, round = 2 /* DWMWCP_ROUND */, border = 0x00352E2C /* BGR #2C2E35 */;
            try
            {
                DwmSetWindowAttribute(hwnd, 20, ref dark, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);    // DWMWA_WINDOW_CORNER_PREFERENCE
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);   // DWMWA_BORDER_COLOR
            }
            catch { }
        }
    }
}
