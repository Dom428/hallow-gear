// Hallow Gear - Windows' own HID APIs (SetupDi + CreateFile + HidD_* / HidP_*, overlapped ReadFile / WriteFile). No driver, no
// third-party library. Devices are opened SHARED (read and write sharing), so Logitech G HUB keeps working next to it.
//
// READ-ONLY BY CONSTRUCTION: the only thing that can ever be written to a device is a SafeReport, and a SafeReport can only be made by
// SafeReport.ForHidpp(...) / SafeReport.ForA50(...) / (1.3.1) SafeReport.ForReceiver(...), which refuse every function that isn't on the
// read-only allow-lists in Hidpp.cs and A50.cs. There is no other WriteFile call in Hallow Gear, and no HidD_SetFeature / HidD_SetOutputReport at all (the tests check both).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HallowGear
{
    // what Windows says about one HID collection
    public class HidInfo
    {
        public string Path; public ushort Vid, Pid; public ushort UsagePage, Usage; public int InLen, OutLen, FeatureLen; public int Interface = -1; public string Product;
        public override string ToString() { return string.Format("{0:X4}:{1:X4} mi{2} page {3:X4} usage {4:X4} in {5} out {6} '{7}'", Vid, Pid, Interface, UsagePage, Usage, InLen, OutLen, Product); }
    }

    // the one and only kind of buffer that can be written to a device (see the header). The constructor is private.
    public sealed class SafeReport
    {
        readonly byte[] bytes;
        public readonly string What;   // "HID++ 0x1004 fn1 getStatus" - for the log
        SafeReport(byte[] b, string what) { bytes = b; What = what; }
        public byte[] Bytes { get { return (byte[])bytes.Clone(); } }
        public int Length { get { return bytes.Length; } }
        internal byte[] Raw { get { return bytes; } }

        // HID++ 2.0: [0x10 short | 0x11 long, device, featureIndex, function << 4 | swId, params...]. null = refused (not read-only).
        public static SafeReport ForHidpp(bool longReport, byte device, ushort featureId, byte featureIndex, byte function, byte swId, params byte[] args)
        {
            var name = Hidpp.AllowedName(featureId, function, featureIndex, args);
            if (name == null || swId == 0 || swId > 0x0F) return null;
            var b = new byte[longReport ? 20 : 7];
            b[0] = longReport ? Hidpp.LongId : Hidpp.ShortId; b[1] = device; b[2] = featureIndex; b[3] = (byte)((function << 4) | swId);
            if (args != null) for (int i = 0; i < args.Length && 4 + i < b.Length; i++) b[4 + i] = args[i];
            return new SafeReport(b, string.Format("HID++ 0x{0:X4} fn{1} {2}", featureId, function, name));
        }
        // 1.3.1: HID++ 1.0 to the receiver itself (device index 0xFF), short report [0x10, 0xFF, sub id, register, args...]. Only the receiver
        // reads on Hidpp.ReceiverReadName's list (GET_LONG_REGISTER 0xB5, pairing info / name pages); null = refused.
        public static SafeReport ForReceiver(byte subId, byte register, params byte[] args)
        {
            var name = Hidpp.ReceiverReadName(subId, register, args);
            if (name == null) return null;
            var b = new byte[7];
            b[0] = Hidpp.ShortId; b[1] = Hidpp.ReceiverIndex; b[2] = subId; b[3] = register;
            for (int i = 0; i < args.Length && 4 + i < b.Length; i++) b[4 + i] = args[i];
            return new SafeReport(b, string.Format("HID++ 1.0 receiver 0x{0:X2} reg 0x{1:X2} page 0x{2:X2} {3}", subId, register, args[0], name));
        }
        // ASTRO A50 (Gen 5) base station: [0x02, 0x0C, 3 + payload, 0x00, cmd, handle, payload...] padded to the output report size
        public static SafeReport ForA50(byte cmd, byte handle, int frameSize, params byte[] payload)
        {
            var name = A50Proto.AllowedName(cmd, payload);
            if (name == null || frameSize < 6 || frameSize > 1024) return null;
            var b = new byte[frameSize];
            b[0] = A50Proto.ReportId; b[1] = A50Proto.Marker; b[2] = (byte)(A50Proto.HeaderLen + (payload == null ? 0 : payload.Length)); b[3] = 0; b[4] = cmd; b[5] = handle;
            return new SafeReport(b, string.Format("A50 0x{0:X2} {1}", cmd, name));
        }
    }

    public static class HidNative
    {
        const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);
        [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int need, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out int n, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr CreateEvent(IntPtr a, bool manual, bool initial, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ResetEvent(IntPtr h);
        [DllImport("kernel32.dll")] internal static extern int WaitForSingleObject(IntPtr h, int ms);

        // every present HID collection of one vendor (attributes and caps are read with a no-access handle: nothing is sent to the device
        // except the product-string request Windows answers from the USB descriptor)
        public static List<HidInfo> Enumerate(ushort vid)
        {
            var list = new List<HidInfo>();
            Guid g; HidD_GetHidGuid(out g);
            var set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
            try
            {
                for (int i = 0; i < 512; i++)
                {
                    var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                    int need; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0) continue;
                    var buf = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out need, IntPtr.Zero)) continue;
                        var path = Marshal.PtrToStringUni(buf + 4);
                        // USB: "vid_046d"; Bluetooth HID: "vid&02046d" (LE) / "vid&0002046d" (classic) - the attributes decide
                        if (path == null || !VidInPath(path, vid)) continue;
                        var info = Describe(path); if (info != null && info.Vid == vid) list.Add(info);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }
        public static bool VidInPath(string path, ushort vid)
        {
            var v = vid.ToString("x4"); var p = path.ToLowerInvariant();
            return p.Contains("vid_" + v) || p.Contains("vid&02" + v) || p.Contains("vid&0002" + v) || p.Contains("vid&01" + v) || p.Contains("vid&0001" + v);
        }
        static HidInfo Describe(string path)
        {
            using (var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var a = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                if (!HidD_GetAttributes(h, ref a)) return null;
                var info = new HidInfo { Path = path, Vid = a.VendorID, Pid = a.ProductID };
                IntPtr pp;
                if (HidD_GetPreparsedData(h, out pp))
                {
                    try { var c = new HIDP_CAPS(); if (HidP_GetCaps(pp, ref c) == 0x110000) { info.UsagePage = c.UsagePage; info.Usage = c.Usage; info.InLen = c.InputReportByteLength; info.OutLen = c.OutputReportByteLength; info.FeatureLen = c.FeatureReportByteLength; } }
                    finally { HidD_FreePreparsedData(pp); }
                }
                try { var b = new byte[256]; if (HidD_GetProductString(h, b, b.Length)) info.Product = Encoding.Unicode.GetString(b).TrimEnd('\0').Trim(); } catch { }
                var m = System.Text.RegularExpressions.Regex.Match(path, "&mi_([0-9a-f]{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) info.Interface = Convert.ToInt32(m.Groups[1].Value, 16);
                return info;
            }
        }
        internal static SafeFileHandle OpenShared(string path)
        {
            return CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        }
    }

    // anything that carries reports both ways: a real HID collection, or a fake in tests / --snap
    public interface IReportPort : IDisposable
    {
        bool Write(SafeReport r);                 // false = couldn't send
        event Action<byte[]> Report;              // every input report the device sends (to anyone: G HUB's answers too)
        int OutLength { get; }
    }

    // one opened HID collection: a background reader (Below Normal) hands every input report to Report; Write sends a SafeReport
    public sealed class HidPort : IReportPort
    {
        readonly SafeFileHandle h; readonly int inLen, outLen; volatile bool closed; readonly Thread reader;
        public event Action<byte[]> Report;
        public int OutLength { get { return outLen; } }
        public string Path { get; private set; }
        HidPort(SafeFileHandle handle, string path, int inLen, int outLen)
        {
            h = handle; Path = path; this.inLen = inLen; this.outLen = outLen;
            reader = new Thread(ReadLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "hid reader" }; reader.Start();
        }
        public static HidPort Open(HidInfo info)
        {
            if (info == null || info.InLen <= 0) return null;
            var handle = HidNative.OpenShared(info.Path);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            return new HidPort(handle, info.Path, info.InLen, info.OutLen);
        }
        void ReadLoop()
        {
            var ev = HidNative.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            var ov = Marshal.AllocHGlobal(32); var buf = Marshal.AllocHGlobal(Math.Max(inLen, 8));
            try
            {
                while (!closed)
                {
                    for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(ov, i, 0);
                    HidNative.ResetEvent(ev); Marshal.WriteIntPtr(ov, IntPtr.Size * 2 + 8, ev);
                    if (!HidNative.ReadFile(h, buf, inLen, IntPtr.Zero, ov) && Marshal.GetLastWin32Error() != 997) { Thread.Sleep(1000); if (closed) break; continue; }
                    int n = 0; bool got = false;
                    while (!closed)
                    {
                        if (HidNative.WaitForSingleObject(ev, 500) == 0) { got = HidNative.GetOverlappedResult(h, ov, out n, false); break; }
                    }
                    if (closed) { HidNative.CancelIoEx(h, ov); HidNative.GetOverlappedResult(h, ov, out n, true); break; }
                    if (!got || n <= 0) { Thread.Sleep(200); continue; }
                    var r = new byte[n]; Marshal.Copy(buf, r, 0, n);
                    var cb = Report; if (cb != null) try { cb(r); } catch { }
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(ov); Marshal.FreeHGlobal(buf); HidNative.CloseHandle(ev); }
        }
        // the ONLY write in Hallow Gear: a SafeReport, padded to the collection's output report size, sent with a 1 s limit
        public bool Write(SafeReport r)
        {
            if (r == null || closed || outLen <= 0) return false;
            var data = new byte[outLen]; var src = r.Raw; Array.Copy(src, data, Math.Min(src.Length, outLen));
            var ev = HidNative.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            var ov = Marshal.AllocHGlobal(32); var buf = Marshal.AllocHGlobal(outLen);
            try
            {
                for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(ov, i, 0);
                Marshal.WriteIntPtr(ov, IntPtr.Size * 2 + 8, ev); Marshal.Copy(data, 0, buf, outLen);
                int n;
                if (HidNative.WriteFile(h, buf, outLen, IntPtr.Zero, ov)) return HidNative.GetOverlappedResult(h, ov, out n, true);
                if (Marshal.GetLastWin32Error() != 997) return false;
                if (HidNative.WaitForSingleObject(ev, 1000) != 0) { HidNative.CancelIoEx(h, ov); HidNative.GetOverlappedResult(h, ov, out n, true); return false; }
                return HidNative.GetOverlappedResult(h, ov, out n, false) && n > 0;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(ov); Marshal.FreeHGlobal(buf); HidNative.CloseHandle(ev); }
        }
        public void Dispose()
        {
            if (closed) return; closed = true;
            try { reader.Join(1500); } catch { }
            try { h.Dispose(); } catch { }
        }
    }

    // waits for one matching input report (from one or more ports) after a request
    public sealed class ReplyWaiter : IDisposable
    {
        readonly Func<byte[], bool> match; readonly ManualResetEvent got = new ManualResetEvent(false); byte[] reply; readonly List<IReportPort> ports;
        readonly Action<byte[]> handler;
        public ReplyWaiter(Func<byte[], bool> match, params IReportPort[] ports)
        {
            this.match = match; this.ports = new List<IReportPort>();
            handler = r => { if (reply == null && match(r)) { reply = r; got.Set(); } };
            foreach (var p in ports) if (p != null) { p.Report += handler; this.ports.Add(p); }
        }
        public byte[] Wait(int ms) { return got.WaitOne(ms) ? reply : null; }
        public void Dispose() { foreach (var p in ports) p.Report -= handler; got.Close(); }
    }
}

namespace HallowGear
{
    // every request actually sent this session, counted (for the probe's log and the status page)
    public static class SentLog
    {
        static readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        public static void Add(string what) { lock (counts) { int n; counts.TryGetValue(what, out n); counts[what] = n + 1; } }
        public static List<string> Summary() { lock (counts) return counts.OrderBy(kv => kv.Key).Select(kv => kv.Key + " x" + kv.Value).ToList(); }
        public static int Total { get { lock (counts) return counts.Values.Sum(); } }
    }
}
