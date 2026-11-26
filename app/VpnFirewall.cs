// Persistent WFP policy. It belongs to Windows, not to the lifetime of sing-box or the launcher.
// API/identifiers: Microsoft WinSDK fwpmu.h and FWPM_FILTER0 on learn.microsoft.com.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ZapretLauncher
{
    static class VpnFirewall
    {
        static readonly Guid SubLayer = new Guid("cb2a008d-6b8f-4ed0-a227-10a1a7114801");
        static readonly Guid[] Layers = { new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82"), new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4") };
        static readonly Guid AppId = new Guid("d78e1e87-8644-4ea5-9437-d809ecefc971");
        static readonly Guid LocalInterface = new Guid("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
        const uint NotFound = 0x80320003, AlreadyExists = 0x80320009;
        static readonly object PolicyLock = new object();
        sealed class AppliedPolicy
        {
            public readonly string Signature;
            public readonly string[] Apps;
            public readonly ulong Tunnel;

            public AppliedPolicy(string signature, string[] apps, ulong tunnel)
            {
                Signature = signature;
                Apps = apps;
                Tunnel = tunnel;
            }
        }

        static volatile AppliedPolicy applied;
        internal const int MaxApps = 31;
        const int FilterSlots = 4 * MaxApps; // Two address families, up to two filters per app.
        public static bool Enabled { get { var policy = applied; return policy != null && policy.Apps.Length > 0; } }
        internal static bool AllowsTunnel { get { var policy = applied; return policy != null && policy.Tunnel != 0; } }

        internal static bool CoversApps(string[] paths)
        {
            var policy = applied;
            return policy != null && paths.Length > 0 && paths.All(path => policy.Apps.Contains(path, StringComparer.OrdinalIgnoreCase));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Display { [MarshalAs(UnmanagedType.LPWStr)] public string name, description; }
        [StructLayout(LayoutKind.Sequential)]
        struct Blob { public uint size; public IntPtr data; }
        [StructLayout(LayoutKind.Explicit, Size = 8)]
        struct Data { [FieldOffset(0)] public IntPtr pointer; [FieldOffset(0)] public uint number; [FieldOffset(0)] public ulong alignment; }
        [StructLayout(LayoutKind.Sequential)]
        struct Value { public uint type; public Data data; }
        [StructLayout(LayoutKind.Sequential)]
        struct Condition { public Guid field; public uint match; public Value value; }
        [StructLayout(LayoutKind.Sequential)]
        struct ActionType { public uint type; public Guid key; }
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        struct Context { [FieldOffset(0)] public ulong raw; [FieldOffset(0)] public Guid key; }
        [StructLayout(LayoutKind.Sequential)]
        struct Filter
        {
            public Guid key; public Display display; public uint flags; public IntPtr provider;
            public Blob providerData; public Guid layer, subLayer; public Value weight;
            public uint count; public IntPtr conditions; public ActionType action; public Context context;
            public IntPtr reserved; public ulong id; public Value effectiveWeight;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Sublayer
        {
            public Guid key; public Display display; public uint flags; public IntPtr provider;
            public Blob providerData; public ushort weight;
        }

        [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] static extern uint FwpmEngineOpen0(string server, uint authentication, IntPtr identity, IntPtr session, out IntPtr engine);
        [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineClose0(IntPtr engine);
        [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);
        [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionCommit0(IntPtr engine);
        [DllImport("fwpuclnt.dll")] static extern uint FwpmTransactionAbort0(IntPtr engine);
        [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] static extern uint FwpmSubLayerAdd0(IntPtr engine, ref Sublayer layer, IntPtr security);
        [DllImport("fwpuclnt.dll")] static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine, ref Guid key);
        [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr security, out ulong id);
        [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterDeleteByKey0(IntPtr engine, ref Guid key);
        [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] static extern uint FwpmGetAppIdFromFileName0(string file, out IntPtr app);
        [DllImport("fwpuclnt.dll")] static extern void FwpmFreeMemory0(ref IntPtr memory);
        [DllImport("iphlpapi.dll")] static extern uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);

        static void Check(uint code) { if (code != 0) throw new Win32Exception(unchecked((int)code), "Windows VPN firewall: 0x" + code.ToString("X8")); }
        static Guid Key(int index) { return new Guid("cb2a008d-6b8f-4ed0-a228-" + index.ToString("x12")); }
        static Value Number(uint type, uint value) { return new Value { type = type, data = new Data { number = value } }; }


        public static ulong TunnelLuid()
        {
            NetworkInterface ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == Vpn.InterfaceName && n.OperationalStatus == OperationalStatus.Up);
            if (ni == null) return 0;
            ulong luid;
            Check(ConvertInterfaceIndexToLuid((uint)ni.GetIPProperties().GetIPv4Properties().Index, out luid));
            return luid;
        }

        // Expose ABI sizes for a read-only test before any policy is installed.
        public static string Layout() { return Marshal.SizeOf(typeof(Value)) + "/" + Marshal.SizeOf(typeof(Condition)) + "/" + Marshal.SizeOf(typeof(Filter)) + "/" + Marshal.SizeOf(typeof(Sublayer)); }

        public static void Clear() { ApplyScoped(false, 0, null); }

        internal static string[] AppScope(IEnumerable<string> apps)
        {
            return apps.Where(path => Routes.IsAppPath(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static bool SameVersionedInstall(string previous, string active)
        {
            if (!Routes.IsAppPath(previous) || !Routes.IsAppPath(active) ||
                !Path.GetFileName(previous).Equals(Path.GetFileName(active), StringComparison.OrdinalIgnoreCase)) return false;
            string oldDir = Path.GetDirectoryName(previous), newDir = Path.GetDirectoryName(active);
            return !string.IsNullOrEmpty(oldDir) && !string.IsNullOrEmpty(newDir) &&
                VersionedAppFolder(oldDir) && VersionedAppFolder(newDir) &&
                string.Equals(Path.GetDirectoryName(oldDir), Path.GetDirectoryName(newDir), StringComparison.OrdinalIgnoreCase);
        }

        internal static string[] VersionedAppPaths(string original)
        {
            if (!Routes.IsAppPath(original)) return new string[0];
            string dir = Path.GetDirectoryName(original);
            if (!VersionedAppFolder(dir)) return new string[0];
            string root = Path.GetDirectoryName(dir);
            if (!Directory.Exists(root)) return new string[0];
            try
            {
                return AppScope(Directory.EnumerateDirectories(root, "app-*")
                    .Where(VersionedAppFolder).Select(folder => Path.Combine(folder, Path.GetFileName(original))));
            }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        static bool VersionedAppFolder(string dir)
        {
            string name = Path.GetFileName(dir);
            return System.Text.RegularExpressions.Regex.IsMatch(name, @"^app-\d+(?:\.\d+)+(?:[-+][\w.-]+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static void ApplyScoped(bool enabled, ulong tunnel, string[] scope)
        {
            lock (PolicyLock) ApplyCore(enabled, tunnel, scope);
        }

        static void ApplyCore(bool enabled, ulong tunnel, string[] scope)
        {
            if (enabled && (scope == null || scope.Length == 0 || scope.Length > MaxApps || scope.Any(path => !Routes.IsAppPath(path))))
                throw new ArgumentException("Защите нужны полные пути к 1–31 программе (.exe).", "scope");
            string signature = enabled ? "True:" + tunnel + ":" + string.Join("|", scope) : "False";
            if (tunnel == 0 && applied != null && signature == applied.Signature) return;
            IntPtr engine;
            Check(FwpmEngineOpen0(null, 10, IntPtr.Zero, IntPtr.Zero, out engine));
            try
            {
                Check(FwpmTransactionBegin0(engine, 0));
                try
                {
                    for (int i = 0; i < FilterSlots; i++) { Guid key = Key(i); uint rc = FwpmFilterDeleteByKey0(engine, ref key); if (rc != NotFound) Check(rc); }
                    if (enabled)
                    {
                        Sublayer layer = new Sublayer { key = SubLayer, display = new Display { name = "Zapret Launcher VPN protection" }, flags = 1, weight = ushort.MaxValue };
                        uint rc = FwpmSubLayerAdd0(engine, ref layer, IntPtr.Zero);
                        if (rc != AlreadyExists) Check(rc);
                        int id = 0;
                        foreach (Guid addressFamily in Layers)
                        {
                            foreach (string path in scope) AddApp(engine, ref id, addressFamily, path, tunnel);
                        }
                    }
                    else { Guid key = SubLayer; uint rc = FwpmSubLayerDeleteByKey0(engine, ref key); if (rc != 0x80320007) Check(rc); }
                    Check(FwpmTransactionCommit0(engine));
                    applied = new AppliedPolicy(signature, enabled ? (string[])scope.Clone() : new string[0], enabled ? tunnel : 0);
                }
                catch { FwpmTransactionAbort0(engine); throw; }
            }
            finally { FwpmEngineClose0(engine); }
        }

        static void AddApp(IntPtr engine, ref int id, Guid layer, string file, ulong tunnel)
        {
            IntPtr app;
            Check(FwpmGetAppIdFromFileName0(file, out app));
            try
            {
                var application = new Condition { field = AppId, value = new Value { type = 12, data = new Data { pointer = app } } };
                if (tunnel != 0)
                {
                    IntPtr luid = Marshal.AllocHGlobal(8);
                    try
                    {
                        Marshal.WriteInt64(luid, unchecked((long)tunnel));
                        Add(engine, ref id, layer, true, 10, application, new Condition { field = LocalInterface, value = new Value { type = 4, data = new Data { pointer = luid } } });
                    }
                    finally { Marshal.FreeHGlobal(luid); }
                }
                Add(engine, ref id, layer, false, 0, application);
            }
            finally { FwpmFreeMemory0(ref app); }
        }

        static void Add(IntPtr engine, ref int index, Guid layer, bool permit, uint priority, params Condition[] conditions)
        {
            int size = Marshal.SizeOf(typeof(Condition));
            IntPtr memory = conditions.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(size * conditions.Length);
            try
            {
                for (int i = 0; i < conditions.Length; i++) Marshal.StructureToPtr(conditions[i], IntPtr.Add(memory, i * size), false);
                // Only blocks survive a reboot or a BFE restart. A new tunnel must earn its own permit.
                Filter filter = new Filter { key = Key(index++), display = new Display { name = "Zapret Launcher VPN protection" }, flags = permit ? 0u : 1u,
                    layer = layer, subLayer = SubLayer, weight = Number(1, priority), count = (uint)conditions.Length, conditions = memory,
                    action = new ActionType { type = permit ? 0x1002u : 0x1001u } };
                ulong id;
                Check(FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out id));
            }
            finally { if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory); }
        }
    }
}
