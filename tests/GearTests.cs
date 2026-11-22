// Hallow Gear unit tests. No real device is ever opened: HID++ and A50 messages are built and parsed from fixture bytes, a fake
// HID++ mouse answers on a fake port, and the family pipe test runs a test copy with made-up gear (HALLOW_GEAR_FAKE=1).
//   tests\GearTests.exe
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

namespace HallowGear.Tests
{
    public static class Run
    {
        static int pass, fail;
        static void Ok(bool c, string name) { if (c) { pass++; Console.WriteLine("ok   " + name); } else { fail++; Console.WriteLine("FAIL " + name); } }
        static string Hex(byte[] b, int n = -1) { return b == null ? "null" : string.Join(" ", b.Take(n < 0 ? b.Length : n).Select(x => x.ToString("X2"))); }
        static byte[] B(string hex) { return hex.Split(' ').Where(x => x.Length > 0).Select(x => Convert.ToByte(x, 16)).ToArray(); }
        static string Dir;

        public static int Main(string[] args)
        {
            Dir = Path.Combine(Path.GetTempPath(), "HallowGearTest" + Process.GetCurrentProcess().Id);
            Environment.SetEnvironmentVariable("HALLOW_GEAR_TESTDIR", Dir);
            // suites: "unit" (everything in-process) and "pipe" (a test copy of the app over the family pipe); none = both
            bool unit = args.Length == 0 || args.Contains("unit"), pipe = args.Length == 0 || args.Contains("pipe");
            if (unit)
            {
                try { Building(); Parsing(); AllowList(); NoOtherWrites(); FakeMouse(); A50(); Merge(); States(); Alerts(); GameHold(); HistoryTests(); LookTests(); SettingsTests(); }
                catch (Exception e) { fail++; Console.WriteLine("FAIL exception: " + e); }
                try { MoreLogitech(); BtParsing(); BtKinds(); BtCards(); BtAlerts(); }
                catch (Exception e) { fail++; Console.WriteLine("FAIL exception: " + e); }
            }
            if (pipe) { try { Pipe(); } catch (Exception e) { fail++; Console.WriteLine("FAIL exception: " + e); } }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
            Console.WriteLine(pass + " passed, " + fail + " failed");
            return fail == 0 ? 0 : 1;
        }

        // ---------------- HID++ messages
        static void Building()
        {
            var r = SafeReport.ForHidpp(true, 1, Hidpp.IRoot, 0, 0, Hidpp.SwId, 0x10, 0x04);
            Ok(r != null && Hex(r.Bytes) == "11 01 00 0D 10 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00", "IRoot getFeature(0x1004) on slot 1, long: " + Hex(r == null ? null : r.Bytes));
            r = SafeReport.ForHidpp(false, 2, Hidpp.IRoot, 0, 1, Hidpp.SwId, 0, 0, 0x5A);
            Ok(r != null && Hex(r.Bytes) == "10 02 00 1D 00 00 5A", "ping, short report: " + Hex(r == null ? null : r.Bytes));
            r = SafeReport.ForHidpp(true, 1, Hidpp.UnifiedBattery, 5, 1, Hidpp.SwId);
            Ok(r != null && Hex(r.Bytes, 4) == "11 01 05 1D" && r.Length == 20, "0x1004 getStatus at index 5");
            r = SafeReport.ForHidpp(true, 0xFF, Hidpp.DeviceName, 2, 1, Hidpp.SwId, 16);
            Ok(r != null && Hex(r.Bytes, 5) == "11 FF 02 1D 10", "getDeviceName(16) on a corded device (index 0xFF)");
            Ok(r.What.Contains("0x0005") && r.What.Contains("getDeviceName"), "the request says what it is: " + r.What);
            var bytes = r.Bytes; bytes[2] = 0x80; Ok(r.Bytes[2] == 0x02, "Bytes is a copy - changing it can't change what is sent");
        }
        static void Parsing()
        {
            var reply = B("11 01 00 0D 06 00 01 00 00 00 00 00 00 00 00 00 00 00 00 00");
            Ok(Hidpp.IsReply(reply, 1, 0, 0) && Hidpp.Params(reply)[0] == 6, "getFeature reply -> index 6");
            Ok(!Hidpp.IsReply(reply, 2, 0, 0) && !Hidpp.IsReply(reply, 1, 0, 1), "another device or function isn't our reply");
            Ok(!Hidpp.IsReply(B("11 01 00 0A 06 00 01"), 1, 0, 0), "someone else's software id (G HUB's) isn't our reply");
            Ok(Hidpp.ErrorFor(B("11 01 FF 05 1D 05 00 00"), 1, 5, 1) == 5, "HID++ 2.0 error code");
            Ok(Hidpp.ErrorFor(B("10 03 8F 00 1D 08 00"), 3, 0, 1) == 8 && Hidpp.ErrorName(8).Contains("empty"), "receiver HID++ 1.0 error 0x08 = empty slot");
            Ok(Hidpp.ErrorFor(B("10 02 8F 00 1D 09 00"), 2, 0, 1) == 9 && Hidpp.ErrorName(9).Contains("asleep"), "receiver error 0x09 = asleep / off");
            Ok(Hidpp.ErrorFor(reply, 1, 0, 0) == 0, "a good reply isn't an error");
            var u = Hidpp.Unified(B("42 04 00 00"), true);
            Ok(u.Percent == 66 && !u.Charging && !u.Full && !u.External && u.Level == "good", "0x1004: 66 %, good, discharging");
            u = Hidpp.Unified(B("4E 04 01 01"), true); Ok(u.Percent == 78 && u.Charging && u.External, "0x1004: charging on a cable");
            u = Hidpp.Unified(B("64 08 03 01"), true); Ok(u.Percent == 100 && u.Full && !u.Charging, "0x1004: full");
            u = Hidpp.Unified(B("00 02 00 00"), false); Ok(u.Percent == 20 && u.Estimated && u.Level == "low", "0x1004 without percent: level 'low' -> about 20 % (estimated)");
            u = Hidpp.Unified(B("2A 04 02 01"), true); Ok(u.Charging, "0x1004: slow charging counts as charging");
            var s = Hidpp.Status(B("50 32 01")); Ok(s.Percent == 80 && s.Charging, "0x1000: 80 % recharging");
            s = Hidpp.Status(B("64 00 03")); Ok(s.Percent == 100 && s.Full, "0x1000: charged");
            s = Hidpp.Status(B("0F 05 00")); Ok(s.Percent == 15 && !s.Charging && !s.External, "0x1000: 15 % discharging");
            var v = Hidpp.Voltage(B("0F 3C 00")); Ok(v.Millivolts == 3900 && v.Estimated && v.Percent > 55 && v.Percent < 75 && !v.Charging, "0x1001: 3900 mV -> about " + v.Percent + " % (estimated)");
            v = Hidpp.Voltage(B("10 2E 80")); Ok(v.Charging && v.External, "0x1001: external power + charging flag");
            v = Hidpp.Voltage(B("10 5A 81")); Ok(v.Full && v.Percent == 100, "0x1001: end of charge = full");
            Ok(Hidpp.PercentFromMillivolts(4200) == 100 && Hidpp.PercentFromMillivolts(3400) == 0 && Hidpp.PercentFromMillivolts(3800) > Hidpp.PercentFromMillivolts(3700), "voltage table runs the right way");
            Ok(Hidpp.LinkUp(B("10 01 41 10 40 9D 40"), 1) == false && Hidpp.LinkUp(B("10 01 41 10 00 9D 40"), 1) == true && Hidpp.LinkUp(B("10 02 41 10 00 9D 40"), 1) == null, "0x41 connection notification: link down / up, other slot ignored");
            var ev = Hidpp.Passive(B("11 01 05 00 3C 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00"), 1, Hidpp.UnifiedBattery, 5, true);
            Ok(ev != null && ev.Percent == 60, "a battery event the mouse sends by itself is heard (no request)");
            Ok(Hidpp.Passive(B("11 01 06 00 3C 04 00 00"), 1, Hidpp.UnifiedBattery, 5, true) == null && Hidpp.Passive(B("11 01 05 20 3C 04 00 00"), 1, Hidpp.UnifiedBattery, 5, true) == null, "other features / functions aren't mistaken for battery reports");
            Ok(Hidpp.TypeName(3) == "mouse" && Hidpp.TypeName(0) == "keyboard" && Hidpp.TypeName(8) == "headset", "device types");
        }

        // ---------------- the read-only allow-lists
        static void AllowList()
        {
            int allowed = 0; var bad = new List<string>();
            for (int f = 0; f <= 0xFFFF; f++)
                for (byte fn = 0; fn < 16; fn++)
                {
                    var args = f == 0 && fn == 0 ? new byte[] { 0x10, 0x04 } : f == 0 && fn == 1 ? new byte[] { 0, 0, 0x5A } : new byte[0];
                    var name = Hidpp.AllowedName((ushort)f, fn, (byte)(f == 0 ? 0 : 5), args);
                    if (name == null) continue; allowed++;
                    Dictionary<byte, string> fns; if (!Hidpp.Allowed.TryGetValue((ushort)f, out fns) || !fns.ContainsKey(fn)) bad.Add(f.ToString("X4") + "/" + fn);
                    if (!name.StartsWith("get")) bad.Add(f.ToString("X4") + "/" + fn + " " + name);
                }
            Ok(bad.Count == 0 && allowed == Hidpp.Allowed.Values.Sum(x => x.Count), "of all 65536 features x 16 functions only the " + allowed + " listed reads can be built (all 'get...')" + (bad.Count > 0 ? ": " + string.Join(", ", bad) : ""));
            var writes = new[] {
                new object[] { 0x2201, 3, "Adjustable DPI setSensorDpi" }, new object[] { 0x2202, 4, "Extended DPI setSensorDpiParameters" }, new object[] { 0x8060, 2, "Report rate setReportRate" },
                new object[] { 0x8061, 3, "Extended report rate set" }, new object[] { 0x8070, 1, "Color LED effects setZoneEffect" }, new object[] { 0x8071, 1, "RGB effects" },
                new object[] { 0x8100, 2, "Onboard profiles setMode" }, new object[] { 0x8100, 6, "Onboard profiles memoryWrite" }, new object[] { 0x1814, 1, "Change host setCurrentHost" },
                new object[] { 0x1815, 4, "Hosts info" }, new object[] { 0x1B04, 3, "Special keys setCidReporting" }, new object[] { 0x2205, 1, "Pointer speed set" },
                new object[] { 0x00C2, 1, "DFU control" }, new object[] { 0x00D0, 1, "DFU" }, new object[] { 0x1802, 0, "Device reset" }, new object[] { 0x1830, 1, "Power modes set" },
                new object[] { 0x0007, 2, "Friendly name setFriendlyName" }, new object[] { 0x1004, 2, "Unified battery showBatteryStatus" }, new object[] { 0x1001, 1, "Battery voltage fn1" },
                new object[] { 0x1000, 2, "Battery status fn2" }, new object[] { 0x0005, 3, "Device name fn3" }, new object[] { 0x0000, 2, "IRoot fn2" }, new object[] { 0x0001, 1, "Feature set" } };
            foreach (var w in writes) Ok(SafeReport.ForHidpp(true, 1, (ushort)(int)w[0], 5, (byte)(int)w[1], Hidpp.SwId) == null, "refused: 0x" + ((int)w[0]).ToString("X4") + " fn" + w[1] + " (" + w[2] + ")");
            Ok(SafeReport.ForHidpp(true, 1, Hidpp.UnifiedBattery, 0x80, 1, Hidpp.SwId) == null && SafeReport.ForHidpp(true, 1, Hidpp.UnifiedBattery, 0x82, 1, Hidpp.SwId) == null, "refused: feature index 0x80 / 0x82 (HID++ 1.0 SET_REGISTER / SET_LONG_REGISTER - receiver writes, pairing)");
            Ok(SafeReport.ForHidpp(true, 0xFF, Hidpp.UnifiedBattery, 0, 1, Hidpp.SwId) == null && SafeReport.ForHidpp(true, 1, Hidpp.IRoot, 3, 0, Hidpp.SwId, 0x10, 0x04) == null, "refused: a feature at the wrong index (IRoot only at 0, others never at 0)");
            Ok(SafeReport.ForHidpp(true, 1, Hidpp.IRoot, 0, 0, Hidpp.SwId, 0x22, 0x01) == null && SafeReport.ForHidpp(true, 1, Hidpp.IRoot, 0, 0, Hidpp.SwId, 0x81, 0x00) == null, "refused: looking up features that aren't on the list (DPI 0x2201, onboard profiles 0x8100)");
            Ok(SafeReport.ForHidpp(true, 1, Hidpp.UnifiedBattery, 5, 1, Hidpp.SwId, 1) == null && SafeReport.ForHidpp(true, 1, Hidpp.IRoot, 0, 1, Hidpp.SwId, 1, 0, 0) == null, "refused: arguments on calls that take none");
            Ok(SafeReport.ForHidpp(true, 1, Hidpp.UnifiedBattery, 5, 1, 0) == null, "refused: software id 0 (that's for device events)");
            // the A50: only the battery GET, never a payload
            var a50 = Enumerable.Range(0, 256).Where(c => SafeReport.ForA50((byte)c, 0x0C, 64) != null).ToList();
            Ok(a50.Count == 1 && a50[0] == 0x06, "A50: of all 256 commands only 0x06 (battery GET) can be built");
            Ok(SafeReport.ForA50(0x06, 0x0C, 64, 0x01) == null, "A50: the battery command with a payload (a SET) is refused");
            foreach (var w in new[] { new object[] { 0x08, new byte[] { 0xFF, 0x10 }, "volume SET" }, new object[] { 0x09, new byte[] { 0x01, 0xFF, 0x03 }, "sidetone SET" }, new object[] { 0x0F, new byte[] { 0 }, "lights SET" },
                                     new object[] { 0x14, new byte[] { 1 }, "noise gate SET" }, new object[] { 0x0D, new byte[] { 1, 3, 0 }, "EQ SET" } })
                Ok(SafeReport.ForA50((byte)(int)w[0], 0x1B, 64, (byte[])w[1]) == null, "A50 refused: " + w[2]);
            // nothing else can make a SafeReport
            var t = typeof(SafeReport);
            var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Ok(ctors.Length == 1 && ctors[0].IsPrivate, "SafeReport has one constructor, and it's private");
            var makers = typeof(Program).Assembly.GetTypes().SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.ReturnType == t && m.DeclaringType == t).Select(m => m.Name).OrderBy(x => x).ToList();
            Ok(string.Join(",", makers) == "ForA50,ForHidpp", "only ForHidpp / ForA50 return a SafeReport: " + string.Join(",", makers));
            Ok(typeof(IReportPort).GetMethod("Write").GetParameters().Single().ParameterType == t, "a port's Write takes a SafeReport and nothing else");
        }
        // the only WriteFile call is HidPort.Write(SafeReport); HidD_SetFeature / SetOutputReport / SetConfiguration aren't even declared
        static void NoOtherWrites()
        {
            var asm = typeof(Program).Assembly; var wf = typeof(HidNative).GetMethod("WriteFile", BindingFlags.NonPublic | BindingFlags.Static);
            Ok(wf != null, "found the WriteFile import");
            var callers = new HashSet<string>(); var setters = new List<string>();
            // 1.1: every WinRT (Windows.*) method the code can call or hook, and every SetupAPI / CfgMgr / Bluetooth API it imports
            var winrt = new SortedSet<string>(); var badWinrt = new SortedSet<string>(); var imports = new SortedSet<string>(); var badImports = new SortedSet<string>();
            var okWinrt = new HashSet<string> { "FindAllAsync", "GetDeviceSelectorFromPairingState", "FromIdAsync", "GetGattServicesForUuidAsync", "GetCharacteristicsForUuidAsync",
                "ReadValueAsync", "FromBuffer", "ReadBytes", "GetResults", "Cancel", "Close", "Dispose", "add_ValueChanged", "remove_ValueChanged" };
            var okSetupApi = new HashSet<string> { "SetupDiGetClassDevs", "SetupDiGetClassDevsW", "SetupDiEnumDeviceInterfaces", "SetupDiGetDeviceInterfaceDetail", "SetupDiGetDeviceInterfaceDetailW",
                "SetupDiDestroyDeviceInfoList", "SetupDiEnumDeviceInfo", "SetupDiGetDeviceInstanceIdW", "SetupDiGetDevicePropertyW" };
            foreach (var type in asm.GetTypes())
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                                 .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
                {
                    if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0)
                    {
                        if (m.Name.StartsWith("HidD_Set") || m.Name == "DeviceIoControl") setters.Add(m.Name);
                        var di = m.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false).Cast<System.Runtime.InteropServices.DllImportAttribute>().FirstOrDefault();
                        var dll = di == null ? "" : di.Value.ToLowerInvariant(); var entry = di != null && !string.IsNullOrEmpty(di.EntryPoint) ? di.EntryPoint : m.Name;
                        imports.Add(dll + "!" + entry);
                        if (dll.StartsWith("setupapi") && !okSetupApi.Contains(entry)) badImports.Add(dll + "!" + entry);
                        if (dll.StartsWith("cfgmgr32") || dll.StartsWith("bluetoothapis") || dll.StartsWith("bthprops") || dll.StartsWith("irprops") || entry.StartsWith("CM_Set") || entry.StartsWith("SetupDiSet") || entry.StartsWith("SetupDiCall") || entry.StartsWith("SetupDiChange") || entry.StartsWith("Bluetooth")) badImports.Add(dll + "!" + entry);
                    }
                    MethodBody body; try { body = m.GetMethodBody(); } catch { continue; } if (body == null) continue;
                    var il = body.GetILAsByteArray();
                    for (int i = 0; i + 4 < il.Length; i++)
                    {
                        int at;
                        if (il[i] == 0x28 || il[i] == 0x6F || il[i] == 0x73) at = i + 1;                                         // call, callvirt, newobj
                        else if (il[i] == 0xFE && (il[i + 1] == 0x06 || il[i + 1] == 0x07) && i + 5 < il.Length) at = i + 2;     // ldftn, ldvirtftn (WinRT event hooks)
                        else continue;
                        MethodBase target;
                        try { target = type.Module.ResolveMethod(BitConverter.ToInt32(il, at), type.IsGenericType ? type.GetGenericArguments() : null, m.IsGenericMethod ? m.GetGenericArguments() : null); } catch { continue; }
                        if (target == wf) callers.Add(type.Name + "." + m.Name);
                        var ns = target.DeclaringType == null ? "" : target.DeclaringType.Namespace ?? "";
                        if (ns == "Windows" || ns.StartsWith("Windows."))
                        {
                            var nm = target.DeclaringType.Name.Split('`')[0] + "." + target.Name; winrt.Add(nm);
                            bool ok = okWinrt.Contains(target.Name) || target.Name.StartsWith("get_") || (target.Name == ".ctor" && target.DeclaringType.Name.StartsWith("TypedEventHandler"));
                            if (!ok) badWinrt.Add(nm + " (in " + type.Name + "." + m.Name + ")");
                        }
                    }
                }
            Ok(callers.Count == 1 && callers.Contains("HidPort.Write"), "WriteFile is called from HidPort.Write only: " + string.Join(", ", callers));
            Ok(setters.Count == 0, "no HidD_Set* / DeviceIoControl imports" + (setters.Count > 0 ? ": " + string.Join(", ", setters) : ""));
            Ok(winrt.Contains("BluetoothLEDevice.GetGattServicesForUuidAsync") && winrt.Contains("GattCharacteristic.ReadValueAsync") && winrt.Contains("DeviceInformation.FindAllAsync") && winrt.Contains("GattCharacteristic.add_ValueChanged"),
                "the IL scan sees the Bluetooth code (" + winrt.Count + " WinRT members, e.g. GattCharacteristic.ReadValueAsync, add_ValueChanged)");
            Ok(badWinrt.Count == 0, "Bluetooth: every WinRT call is a read (find, open by id, get services / characteristics, read value, listen) - no write, pair, connect, CCCD, session or advertising call" + (badWinrt.Count > 0 ? ": " + string.Join(", ", badWinrt) : ""));
            Ok(!winrt.Any(x => x.Contains(".Write") || x.EndsWith("PairAsync") || x.Contains("Unpair") || x.Contains("MaintainConnection") || x.Contains("OpenAsync") || x.Contains("ClientCharacteristicConfiguration")), "no WriteValue / WriteClientCharacteristicConfigurationDescriptor / Pair / MaintainConnection / OpenAsync anywhere");
            Ok(badImports.Count == 0 && imports.Contains("setupapi.dll!SetupDiGetDevicePropertyW"), "Bluetooth device properties: only SetupDi Get / Enum imports - no SetupDiSet* / CM_Set* / Bluetooth* API (" + imports.Count(x => x.StartsWith("setupapi")) + " SetupAPI imports)" + (badImports.Count > 0 ? ": " + string.Join(", ", badImports) : ""));
        }

        // ---------------- a fake HID++ mouse on slot 1 (asleep mouse on slot 2, empty 3..6), through the real HidppDevice / LogiSource code paths
        class FakePort : IReportPort
        {
            public event Action<byte[]> Report; public int OutLength { get { return 20; } }
            public readonly List<byte[]> Written = new List<byte[]>(); public Func<byte[], byte[]> Answer;
            public bool Write(SafeReport r) { var b = r.Bytes; Written.Add(b); var a = Answer(b); var h = Report; if (a != null && h != null) h(a); return true; }
            public void Push(byte[] r) { var h = Report; if (h != null) h(r); }
            public void Dispose() { }
        }
        static byte[] Mouse(byte[] q)
        {
            byte dev = q[1], idx = q[2], fs = q[3]; int fn = fs >> 4;
            Func<byte[], byte[]> L = p => { var r = new byte[20]; r[0] = 0x11; r[1] = dev; r[2] = idx; r[3] = fs; Array.Copy(p, 0, r, 4, Math.Min(16, p.Length)); return r; };
            if (dev == 2) return new byte[] { 0x10, dev, 0x8F, idx, fs, 0x09, 0 };
            if (dev != 1) return new byte[] { 0x10, dev, 0x8F, idx, fs, 0x08, 0 };
            if (idx == 0 && fn == 1) return L(new byte[] { 4, 5, q[6] });
            if (idx == 0 && fn == 0) { int f = q[4] << 8 | q[5]; return L(new byte[] { (byte)(f == 0x0005 ? 2 : f == 0x1004 ? 5 : 0) }); }
            var name = System.Text.Encoding.ASCII.GetBytes("PRO X 2");
            if (idx == 2 && fn == 0) return L(new byte[] { (byte)name.Length });
            if (idx == 2 && fn == 1) return L(name.Skip(q[4]).ToArray());
            if (idx == 2 && fn == 2) return L(new byte[] { 3 });
            if (idx == 5 && fn == 0) return L(new byte[] { 0x0F, 0x02 });
            if (idx == 5 && fn == 1) return L(new byte[] { 66, 4, 0, 0 });
            return new byte[] { 0x11, dev, 0xFF, idx, fs, 0x02 };
        }
        static void FakeMouse()
        {
            var port = new FakePort { Answer = Mouse };
            var d = new HidppDevice(port, null, 1) { ReplyMs = 300 };
            Ok(d.Ping() == 4, "ping: HID++ 4");
            Ok(d.Name() == "PRO X 2", "name read over getDeviceNameCount / getDeviceName");
            Ok(d.DeviceType() == 3, "type: mouse");
            var b = d.Battery(); Ok(b != null && b.Percent == 66 && !b.Charging && d.BatteryFeature == Hidpp.UnifiedBattery && d.BatteryIndex == 5, "battery 66 % via Unified Battery at index 5");
            var asleep = new HidppDevice(port, null, 2) { ReplyMs = 300 }; Ok(asleep.Ping() == 0 && asleep.LastError == 9, "an asleep mouse: error 0x09");
            var empty = new HidppDevice(port, null, 4) { ReplyMs = 300 }; Ok(empty.Ping() == 0 && empty.LastError == 8, "an empty slot: error 0x08");
            var silent = new HidppDevice(new FakePort { Answer = q => null }, null, 1) { ReplyMs = 100 }; Ok(silent.Ping() == 0 && silent.LastError == -1, "no answer: -1, and no feature is cached as missing");
            // every byte that went out was an allow-listed read
            var bad = port.Written.Where(w => { int fn = w[3] >> 4; return !(w[2] == 0 && (fn == 0 || fn == 1)) && !(w[2] == 2 && fn <= 2) && !(w[2] == 5 && fn <= 1); }).ToList();
            Ok(port.Written.Count > 5 && bad.Count == 0 && port.Written.All(w => (w[3] & 0x0F) == Hidpp.SwId), port.Written.Count + " requests sent, all of them reads with Hallow Gear's software id");
            Ok(d.Sent.All(x => x.Contains(" get")), "the log names each request: " + string.Join("; ", d.Sent.Distinct()));
        }
        static void A50()
        {
            var r = SafeReport.ForA50(A50Proto.CmdBattery, A50Proto.HandleBattery, 64);
            Ok(r != null && Hex(r.Bytes, 6) == "02 0C 03 00 06 0C" && r.Length == 64 && r.Bytes.Skip(6).All(x => x == 0), "A50 battery GET = 02 0C 03 00 06 0C, padded to 64");
            var b = A50Proto.Battery(B("02 0C 06 00 06 00 55 00 01"));
            Ok(b != null && b.Percent == 85 && b.Charging && b.External, "A50 reply: 85 %, docked = charging");
            b = A50Proto.Battery(B("02 0C 06 00 06 00 1A 00 00")); Ok(b != null && b.Percent == 26 && !b.Charging && !b.External, "A50 reply: 26 %, off the dock");
            b = A50Proto.Battery(B("02 0C 06 00 06 00 64 00 01")); Ok(b != null && b.Full && !b.Charging, "A50 reply: 100 % on the dock = full");
            Ok(A50Proto.Battery(B("02 0C 03 00 06 0C 00 00 00")) == null, "a bare ack (no payload) isn't a battery reading");
            Ok(A50Proto.Battery(B("02 0C 06 00 08 00 10 00 05")) == null, "a volume push isn't a battery reading");
            Ok(A50Proto.Battery(B("02 0C 06 00 06 00 C8 00 00")) == null, "nonsense percent (200) is ignored");
            var port = new FakePort { Answer = q => q[4] == 6 ? B("02 0C 06 00 06 00 47 00 00") : null };
            var rd = new A50Reader(port) { ReplyMs = 300 }; var got = rd.Battery();
            Ok(got != null && got.Percent == 71 && port.Written.Count == 1 && port.Written[0][4] == 6, "A50 reader: one GET, 71 %");
            Ok(A50Proto.IsControl(new HidInfo { Vid = 0x046D, Pid = 0x0B1C, UsagePage = 0xFF32, Usage = 0x74, Interface = 8, OutLen = 64 }) && !A50Proto.IsControl(new HidInfo { Vid = 0x046D, Pid = 0x0B1C, UsagePage = 0x000C, Interface = 3, OutLen = 0 }), "the A50's control interface is page 0xFF32 / interface 8");
        }

        // ---------------- devices
        static void Merge()
        {
            var t = DateTime.Now;
            var rx = new GearDevice { Key = "mouse:pro x 2", Kind = "mouse", Name = "PRO X 2", Via = "LIGHTSPEED receiver", Battery = 66, Updated = t.AddMinutes(-3) };
            var cable = new GearDevice { Key = "mouse:pro x 2", Kind = "mouse", Name = "PRO X 2", Via = "USB cable", Wired = true, Battery = 67, Charging = true, Updated = t };
            var kb = new GearDevice { Key = "keyboard:g713 keyboard", Kind = "keyboard", Name = "G713 Keyboard", Wired = true, HasBattery = false };
            var m = GearMerge.Merge(new[] { rx, kb, cable });
            Ok(m.Count == 2 && m[0].Kind == "mouse" && m[0].Wired && m[0].Charging && m[0].Battery == 67 && m[0].State == "charging" && m[0].StateLabel.Contains("wired"), "receiver + cable = one mouse card, wired (charging): " + m[0].StateLabel);
            var asleepCable = cable.Clone(); asleepCable.Battery = null; asleepCable.Charging = false; asleepCable.Updated = null;
            m = GearMerge.Merge(new[] { rx, asleepCable }); Ok(m.Count == 1 && m[0].Wired && m[0].Battery == 66, "cable without a battery answer keeps the receiver's reading");
            Ok(GearMerge.KeyFor("mouse", " PRO X 2 ") == "mouse:pro x 2", "keys ignore case and spaces");
            var off = LogiSource.Offline(rx, "mouse", "PRO X 2");
            Ok(off.State == "asleep" && off.Connected && off.Battery == 66 && off.StateLabel == "Asleep" && rx.State == "on-battery", "1.0.1: a paired mouse the receiver can't reach (0x08 / 0x09) is asleep, not \"not connected\", and keeps its last battery");
            Ok(LogiSource.Offline(null, "mouse", "M").State == "asleep", "...also before it was ever read");
        }
        static void States()
        {
            Func<Action<GearDevice>, GearDevice> D = f => { var d = new GearDevice { Key = "k", Kind = "mouse", Name = "M", Battery = 50 }; f(d); return d; };
            Ok(D(d => { }).State == "on-battery" && D(d => { }).StateLabel.Contains("wireless"), "on battery, wireless");
            Ok(D(d => d.Charging = true).State == "charging" && D(d => d.Full = true).State == "full", "charging / full");
            Ok(D(d => d.Asleep = true).State == "asleep" && D(d => d.Connected = false).State == "not-connected", "asleep / not connected");
            Ok(D(d => { d.HasBattery = false; d.Wired = true; }).State == "wired" && D(d => d.Wired = true).State == "wired", "wired");
            Ok(D(d => d.Battery = null).State == "wireless", "connected, battery not known yet: wireless");
            var h = new GearDevice { Kind = "headset", Battery = 90, Charging = true, External = true }; Ok(h.StateLabel == "Charging \u00B7 docked", "headset on its base: charging, docked");
        }

        // ---------------- alerts
        static List<string> Feed(BatteryAlerts a, AlertRules r, GearDevice d, params int[] levels)
        {
            var o = new List<string>(); foreach (var p in levels) { d.Battery = p; o.AddRange(a.Update(d, r, DateTime.Now).Select(x => x.Kind)); } return o;
        }
        static void Alerts()
        {
            var r = new AlertRules(); var a = new BatteryAlerts();
            var d = new GearDevice { Key = "mouse:m", Kind = "mouse", Name = "M", Battery = 30 };
            Ok(string.Join(",", Feed(a, r, d, 30, 25, 21)) == "", "no alert above 20 %");
            Ok(string.Join(",", Feed(a, r, d, 20)) == "low", "20 %: low, once");
            Ok(string.Join(",", Feed(a, r, d, 19, 18, 20, 15, 11)) == "", "...and not again while it drains (or wobbles)");
            Ok(string.Join(",", Feed(a, r, d, 10)) == "critical", "10 %: very low, once");
            Ok(string.Join(",", Feed(a, r, d, 9, 5, 2)) == "", "...and not again");
            d.Charging = true; Ok(string.Join(",", Feed(a, r, d, 12)) == "charging", "charging started");
            Ok(string.Join(",", Feed(a, r, d, 40, 80)) == "", "no repeats while charging");
            d.Charging = false; d.Full = true; Ok(string.Join(",", Feed(a, r, d, 100)) == "full", "fully charged");
            d.Full = false; Ok(string.Join(",", Feed(a, r, d, 99, 50, 21, 20)) == "low", "a new discharge: low again (once per discharge)");
            var a2 = new BatteryAlerts(); var d2 = new GearDevice { Key = "h", Kind = "headset", Name = "ASTRO A50", Battery = 8 };
            Ok(string.Join(",", Feed(a2, r, d2, 8)) == "critical" && string.Join(",", Feed(a2, r, d2, 7, 15)) == "", "first seen at 8 %: only 'very low', not both");
            Ok(string.Join(",", Feed(a2, r, d2, 40, 20)) == "low", "jumping 10+ points above a level (a fresh battery) re-arms it");
            var a3 = new BatteryAlerts(); var d3 = new GearDevice { Key = "w", Kind = "mouse", Name = "M", Battery = 70 };
            Feed(a3, r, d3, 70); d3.Wired = true; Ok(Feed(a3, r, d3, 70).Count == 0, "wired / wireless: off by default");
            var rw = new AlertRules { Wired = true }; d3.Wired = false; Ok(string.Join(",", Feed(a3, rw, d3, 70)) == "wireless", "...and on when asked");
            var off = new AlertRules { Low = false }; var a4 = new BatteryAlerts(); Ok(Feed(a4, off, new GearDevice { Key = "x", Kind = "mouse", Battery = 5 }, 5).Count == 0, "low alerts can be switched off");
            var a5 = new BatteryAlerts(); Ok(Feed(a5, r, new GearDevice { Key = "k", Kind = "keyboard", HasBattery = false }, 0).Count == 0 && Feed(a5, r, new GearDevice { Key = "s", Kind = "mouse", Asleep = true }, 3).Count == 0, "no alerts for a keyboard without battery or an asleep mouse");
            var custom = new AlertRules { Low1 = 30, Low2 = 15 }; var a6 = new BatteryAlerts(); var d6 = new GearDevice { Key = "c", Kind = "mouse", Battery = 50 };
            Ok(string.Join(",", Feed(a6, custom, d6, 50, 30, 16, 15)) == "low,critical", "custom levels 30 / 15");
            var al = new BatteryAlerts().Update(new GearDevice { Key = "e", Kind = "mouse", Name = "M", Battery = 19, Estimated = true }, r, DateTime.Now).Single();
            Ok(al.Title.Contains("19 %") && al.Text.Contains("estimated"), "the alert says the level and if it's an estimate: " + al.Text);
        }
        static void GameHold()
        {
            var router = new AlertRouter(); var t = DateTime.Now;
            Func<string, string, GearAlert> A = (k, kind) => new GearAlert { Key = k, Kind = kind, Title = k + " " + kind, Text = "", At = t = t.AddSeconds(1) };
            Ok(router.Route(new[] { A("mouse", "low") }, false).Count == 1, "no game: shown at once");
            Ok(router.Route(new[] { A("mouse", "low") }, true).Count == 0 && router.Held.Count == 1 && router.OverlayLine != null && router.OverlayLine.Kind == "low", "during a game: nothing shown, held, low battery goes to Overlay");
            router.Route(new[] { A("mouse", "critical") }, true);
            Ok(router.Held.Count == 1 && router.Held[0].Kind == "critical" && router.OverlayLine.Kind == "critical", "a newer low alert for the same device replaces the older one");
            router.Route(new[] { A("headset", "charging") }, true); router.Route(new[] { A("headset", "full") }, true);
            Ok(router.Held.Count == 2 && router.Held.Any(x => x.Kind == "full") && router.OverlayLine.Kind == "critical", "charging -> full keeps only 'full'; not an Overlay line");
            var after = router.GameEnded(DateTime.Now);
            Ok(after.Count == 2 && router.Held.Count == 0 && router.OverlayLine == null, "after the game: the held alerts are shown, the Overlay line goes");
            foreach (var k in new[] { "a", "b", "c", "d", "e" }) router.Route(new[] { A(k, k == "a" ? "low" : "charging") }, true);
            after = router.GameEnded(DateTime.Now);
            Ok(after.Count == 2 && after[0].Kind == "low" && after[1].Kind == "summary" && after[1].Text.Contains("b charging"), "more than 3 held: the low ones plus one summary");
        }
        static void HistoryTests()
        {
            var h = new History(); var t = new DateTime(2026, 11, 22, 12, 0, 0);
            h.Add("m", 80, t); h.Add("m", 80, t.AddMinutes(3)); h.Add("m", 79, t.AddMinutes(4)); h.Add("m", 79, t.AddMinutes(15));
            Ok(h.For("m").Count == 3, "a point when it changes, and at least every 10 min");
            h.Add("m", 70, t.AddHours(25)); Ok(h.For("m").Count == 1, "older than 24 h is dropped");
            var back = History.FromJson(h.ToJson(), t.AddHours(25)); Ok(back.For("m").Count == 1 && back.For("m")[0].P == 70, "saved and read back");
            Ok(History.FromJson("{not json", t).Points.Count == 0 && History.FromJson("{\"m\":[[\"x\",5],[\"2026-11-22T12:00:00\",500]]}", t).For("m").Single().P == 100, "a broken file doesn't crash; values are clamped");
        }
        static void LookTests()
        {
            Ok(!Look.Resolve(null, "hallow", () => true) && Look.Resolve("light", "hallow", () => false) && Look.Resolve("system", "hallow", () => true) && !Look.Resolve("system", "hallow", () => null), "mode: missing = dark, light, system follows Windows");
            Ok(Look.Resolve("dark", "light", () => false) && !Look.Resolve("light", "dark", () => true), "the app's own Look wins");
            Ok(Look.ModeOf("{\"accent\":\"#8B5CF6\",\"mode\":\"Light\"}") == "light" && Look.ModeOf("garbage") == "dark", "theme.json mode parsing");
            foreach (var accent in new[] { "#F07820", "#8B5CF6", "#22C55E", "#FACC15" })
            {
                var bad = Look.LightPairs(accent).Where(p => Look.Contrast((string)p[1], (string)p[2]) < (double)p[3]).Select(p => (string)p[0]).ToList();
                Ok(bad.Count == 0, "light palette contrast with accent " + accent + (bad.Count > 0 ? ": " + string.Join(", ", bad) : ""));
            }
        }
        static void SettingsTests()
        {
            var s = new GearSettings { Low1 = 90, Low2 = 95, PollSeconds = 5 }; s.Fix();
            Ok(s.Low1 == 50 && s.Low2 < s.Low1 && s.PollSeconds == 60, "settings are clamped (low <= 50 %, very low below low, at least 60 s)");
            var d = new GearSettings(); d.Fix(); Ok(d.AlertLow && d.Low1 == 20 && d.Low2 == 10 && d.AlertCharging && d.AlertFull && !d.AlertWired && !d.ReadMouseInGames && d.PollSeconds == 60, "defaults: 20 / 10 %, charging + full on, wired switch off, mouse not asked in games, 60 s");
        }

        // ---------------- 1.1: other Logitech HID++ 2.0 devices - a keyboard and a second mouse on other receiver slots, a Bluetooth mouse
        // device -> (feature id -> index), name, type, battery answers. Everything goes through the real LogiSource / HidppDevice code.
        class FakeDev { public string Name; public int Type; public Dictionary<int, int> Features; public Dictionary<int, byte[]> Battery; public bool Asleep; }
        static Func<byte[], byte[]> Answers(Dictionary<int, FakeDev> devs)
        {
            return q =>
            {
                byte dev = q[1], idx = q[2], fs = q[3]; int fn = fs >> 4;
                Func<byte[], byte[]> L = p => { var r = new byte[20]; r[0] = 0x11; r[1] = dev; r[2] = idx; r[3] = fs; Array.Copy(p, 0, r, 4, Math.Min(16, p.Length)); return r; };
                FakeDev d; if (!devs.TryGetValue(dev, out d)) return new byte[] { 0x10, dev, 0x8F, idx, fs, 0x08, 0 };   // empty slot
                if (d.Asleep) return new byte[] { 0x10, dev, 0x8F, idx, fs, 0x09, 0 };
                if (idx == 0 && fn == 1) return L(new byte[] { 4, 5, q[6] });
                if (idx == 0 && fn == 0) { int f = q[4] << 8 | q[5], i; return L(new byte[] { (byte)(d.Features.TryGetValue(f, out i) ? i : 0) }); }
                var feat = d.Features.Where(kv => kv.Value == idx).Select(kv => kv.Key).DefaultIfEmpty(-1).First();
                var name = System.Text.Encoding.ASCII.GetBytes(d.Name);
                if (feat == 0x0005 && fn == 0) return L(new byte[] { (byte)name.Length });
                if (feat == 0x0005 && fn == 1) return L(name.Skip(q[4]).ToArray());
                if (feat == 0x0005 && fn == 2) return L(new byte[] { (byte)d.Type });
                if (feat == 0x1004 && fn == 0) return L(new byte[] { 0x0F, 0x02 });
                byte[] b; if (feat > 0 && d.Battery.TryGetValue(feat * 16 + fn, out b)) return L(b);
                return new byte[] { 0x11, dev, 0xFF, idx, fs, 0x02 };
            };
        }
        static void MoreLogitech()
        {
            var rxDevs = new Dictionary<int, FakeDev> {
                { 1, new FakeDev { Name = "PRO X 2", Type = 3, Features = new Dictionary<int, int> { { 0x0005, 2 }, { 0x1004, 5 } }, Battery = new Dictionary<int, byte[]> { { 0x1004 * 16 + 1, new byte[] { 66, 4, 0, 0 } } } } },
                { 3, new FakeDev { Name = "G915 TKL", Type = 0, Features = new Dictionary<int, int> { { 0x0005, 2 }, { 0x1000, 4 } }, Battery = new Dictionary<int, byte[]> { { 0x1000 * 16 + 0, new byte[] { 55, 50, 0 } } } } },
                { 5, new FakeDev { Name = "G502 X PLUS", Type = 3, Features = new Dictionary<int, int> { { 0x0005, 3 }, { 0x1001, 6 } }, Battery = new Dictionary<int, byte[]> { { 0x1001 * 16 + 0, new byte[] { 0x0F, 0x3C, 0x00 } } } } } };
            var btDevs = new Dictionary<int, FakeDev> { { 0xFF, new FakeDev { Name = "MX Master 3S", Type = 3, Features = new Dictionary<int, int> { { 0x0005, 2 }, { 0x1004, 7 } }, Battery = new Dictionary<int, byte[]> { { 0x1004 * 16 + 1, new byte[] { 90, 4, 0, 0 } } } } } };
            var rxPort = new FakePort { Answer = Answers(rxDevs) }; var btPort = new FakePort { Answer = Answers(btDevs) }; var silent = new FakePort { Answer = q => null };
            const string btPath = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b034_rev&0006_0a1b2c3d4e05&col03#9&1&0&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var infos = new List<HidInfo> {
                new HidInfo { Path = @"\\?\hid#vid_046d&pid_c547&mi_02&col02#8&1&0&0001#{x}", Vid = 0x046D, Pid = 0xC547, UsagePage = 0xFF00, Usage = 2, InLen = 20, OutLen = 20, Interface = 2, Product = "USB Receiver" },
                new HidInfo { Path = @"\\?\hid#vid_046d&pid_c547&mi_02&col01#8&1&0&0000#{x}", Vid = 0x046D, Pid = 0xC547, UsagePage = 0xFF00, Usage = 1, InLen = 7, OutLen = 7, Interface = 2, Product = "USB Receiver" },
                new HidInfo { Path = btPath, Vid = 0x046D, Pid = 0xB034, UsagePage = 0xFF43, Usage = 0x0202, InLen = 20, OutLen = 20, Product = "MX Master 3S" },
                new HidInfo { Path = btPath.Replace("col03", "col01"), Vid = 0x046D, Pid = 0xB034, UsagePage = 1, Usage = 2, InLen = 8, OutLen = 0, Product = "MX Master 3S" } };
            var st = new GearSettings(); st.Fix();
            var src = new LogiSource(st) { Enumerator = () => infos, Opener = i => i.Path == btPath ? btPort : i.InLen == 20 ? rxPort : i.InLen == 7 ? silent : null };
            var list = src.Read(true, true, true);
            Func<string, GearDevice> D = n => list.FirstOrDefault(x => x.Name == n);
            var m1 = D("PRO X 2"); var kb = D("G915 TKL"); var m2 = D("G502 X PLUS"); var bt = D("MX Master 3S");
            Ok(list.Count == 4, "receiver slots 1 / 3 / 5 + a Bluetooth Logitech mouse = 4 cards: " + string.Join(", ", list.Select(x => x.Name)));
            Ok(m1 != null && m1.Kind == "mouse" && m1.Battery == 66 && m1.Via == "LIGHTSPEED receiver", "slot 1: the PRO X 2, 66 % (Unified Battery)");
            Ok(kb != null && kb.Kind == "keyboard" && kb.Battery == 55 && kb.HasBattery && kb.Key == "keyboard:g915 tkl" && kb.State == "on-battery", "slot 3: a wireless keyboard gets a keyboard card, 55 % (Battery Status 0x1000)");
            Ok(m2 != null && m2.Kind == "mouse" && m2.Estimated && m2.Battery > 55 && m2.Battery < 75 && m2.Key != m1.Key, "slot 5: a second mouse gets its own card, ~" + (m2 == null ? 0 : m2.Battery) + " % estimated (Battery Voltage 0x1001)");
            Ok(bt != null && bt.Via == "Bluetooth" && !bt.Wired && bt.Battery == 90 && bt.State == "on-battery", "a Logitech mouse straight over Bluetooth: HID++ at index 0xFF, 90 %, not wired");
            var all = rxPort.Written.Concat(btPort.Written).ToList();
            var bad = all.Where(w =>
            {
                var devs = w[1] == 0xFF ? btDevs : rxDevs; FakeDev fd; int fn = w[3] >> 4;
                if (w[2] == 0) return !(fn == 0 || fn == 1);
                if (!devs.TryGetValue(w[1], out fd)) return true;
                var feat = fd.Features.Where(kv => kv.Value == w[2]).Select(kv => kv.Key).DefaultIfEmpty(-1).First();
                Dictionary<byte, string> fns; return feat < 0 || !Hidpp.Allowed.TryGetValue((ushort)feat, out fns) || !fns.ContainsKey((byte)fn);
            }).ToList();
            Ok(all.Count > 10 && bad.Count == 0 && all.All(w => (w[3] & 0x0F) == Hidpp.SwId), all.Count + " requests to 4 devices and 3 empty slots, every one an allow-listed read" + (bad.Count > 0 ? ": " + string.Join(" | ", bad.Select(b => Hex(b, 6))) : ""));
            Ok(st.KnownSlots.Count == 3 && st.KnownSlotKinds["C547:3"] == "keyboard" && st.KnownSlotKinds["C547:5"] == "mouse", "slot names and kinds remembered (C547:1 / 3 / 5)");
            // a game starts: nothing is asked, cards keep their kinds
            int before = rxPort.Written.Count + btPort.Written.Count;
            var paused = src.Read(false, false, false);
            Ok(rxPort.Written.Count + btPort.Written.Count == before && paused.Count == 4 && paused.Where(x => x.HasBattery).All(x => x.Paused) && paused.Any(x => x.Kind == "keyboard" && x.Name == "G915 TKL"), "during a game: nothing sent to any slot, 4 paused cards, the keyboard still a keyboard");
            // next session, game already running: the remembered slots come back with their kinds
            var src2 = new LogiSource(st) { Enumerator = () => infos, Opener = i => i.Path == btPath ? btPort : i.InLen == 20 ? rxPort : i.InLen == 7 ? silent : null };
            var cold = src2.Read(false, false, true);
            Ok(cold.Any(x => x.Kind == "keyboard" && x.Name == "G915 TKL" && x.Paused) && cold.Count(x => x.Kind == "mouse" && x.Via == "LIGHTSPEED receiver") == 2 && rxPort.Written.Count + btPort.Written.Count == before, "a new session during a game: remembered slots shown with their kinds, still nothing sent");
            // the keyboard goes to sleep
            rxDevs[3].Asleep = true; var later = src.Read(true, true, false); var ks = later.FirstOrDefault(x => x.Name == "G915 TKL");
            Ok(ks != null && ks.Kind == "keyboard" && ks.State == "asleep" && ks.Battery == 55 && ks.Note.Contains("press a key"), "an asleep keyboard: asleep, keeps 55 %, 'press a key to wake it'");
            src.Dispose(); src2.Dispose();
            // paths and receivers
            Ok(LogiSource.IsBluetoothPath(btPath) && !LogiSource.IsBluetoothPath(infos[0].Path) && LogiSource.IsBluetoothPath(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002046d_pid&b01e#8&1&0&0000#{x}"), "Bluetooth HID paths (LE 0x1812, classic 0x1124) are told from USB ones");
            Ok(HidNative.VidInPath(btPath, 0x046D) && HidNative.VidInPath(infos[0].Path, 0x046D) && !HidNative.VidInPath(@"\\?\hid#vid_1532&pid_0099#x", 0x046D), "Logitech collections are found by USB and Bluetooth vendor id (Razer's aren't)");
            Ok(LogiSource.ReceiverName(0xC548, null) == "Bolt receiver" && LogiSource.ReceiverName(0xC52B, "USB Receiver") == "Unifying receiver" && LogiSource.ReceiverName(0xC547, "USB Receiver") == "LIGHTSPEED receiver", "receiver names: Bolt, Unifying, LIGHTSPEED");
            // two identical mice (another receiver): two cards
            var twin = GearMerge.Merge(new[] { new GearDevice { Key = "mouse:g305", Kind = "mouse", Name = "G305", Battery = 40 }, new GearDevice { Key = "mouse:g305", Kind = "mouse", Name = "G305", Battery = 90 } });
            Ok(twin.Count == 2 && twin.Any(x => x.Name == "G305 (2)" && x.Key == "mouse:g305#2"), "two identical wireless mice: two cards (\"G305\" and \"G305 (2)\")");
            var multi = GearMerge.Merge(new[] { new GearDevice { Key = "mouse:mx", Kind = "mouse", Name = "MX", Asleep = true, Battery = 10 }, new GearDevice { Key = "mouse:mx", Kind = "mouse", Name = "MX", Battery = 70, Via = "Bluetooth" } });
            Ok(multi.Count == 1 && multi[0].Battery == 70 && multi[0].Via == "Bluetooth", "a multi-host mouse seen on two links: the one that answers wins");
        }

        // ---------------- 1.1: Bluetooth
        static void BtParsing()
        {
            Ok(BtProto.Level(B("55")) == 85 && BtProto.Level(B("00")) == 0 && BtProto.Level(B("64")) == 100, "Battery Level 0x2A19: one byte, 85 / 0 / 100 %");
            Ok(BtProto.Level(B("C8")) == null && BtProto.Level(new byte[0]) == null && BtProto.Level(null) == null, "Battery Level: 200 / empty / missing = unknown");
            Ok(BtProto.Level(B("2A 00 01")) == 42, "Battery Level: extra bytes are ignored");
            Ok(BtProto.PnpBattery(0x03, B("50"), 1) == 80, "DEVPKEY_Bluetooth_Battery (DEVPROP_TYPE_BYTE): 80 %");
            Ok(BtProto.PnpBattery(0x07, B("3C 00 00 00"), 4) == 60, "...a UINT32 value is read too: 60 %");
            Ok(BtProto.PnpBattery(0x03, B("FF"), 1) == null && BtProto.PnpBattery(0x12, B("35 00"), 2) == null && BtProto.PnpBattery(0x03, B("50"), 0) == null, "...255, a string, or no value = unknown");
            Ok(BtProto.Address(@"BTHENUM\DEV_A0B1C2D3E4F5\7&2E1B3A0&0&BLUETOOTHDEVICE_A0B1C2D3E4F5") == "a0b1c2d3e4f5", "address from a classic device node (BTHENUM\\DEV_...)");
            Ok(BtProto.Address(@"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_LOCALMFG&0002\7&36D1D1A&0&A0B1C2D3E4F5_C00000000") == "a0b1c2d3e4f5", "...from a classic service node (hands-free)");
            Ok(BtProto.Address(@"BTHLE\DEV_C1D2E3F4A5B6\8&1F2E3D4C&0&C1D2E3F4A5B6") == "c1d2e3f4a5b6", "...from an LE device node (BTHLE\\DEV_...)");
            Ok(BtProto.Address("BluetoothLE#BluetoothLE00:1a:7d:da:71:13-c1:d2:e3:f4:a5:b6") == "c1d2e3f4a5b6" && BtProto.Address(0xC1D2E3F4A5B6UL) == "c1d2e3f4a5b6", "...from a WinRT id and a WinRT address");
            Ok(BtProto.Address(@"USB\VID_0489&PID_E13A\5&1") == null && BtProto.Address(@"BTH\MS_BTHLE\A&29623A3&0&3") == null, "the adapter and enumerators have no device address");
            Ok(BtProto.Key("a0b1c2d3e4f5") == "bt:a0b1c2d3e4f5", "device key: bt:<address>");
        }
        static void BtKinds()
        {
            Ok(BtProto.KindFromCod(0x240404, "WH-CH720N") == "headset" && BtProto.KindFromCod(0x240418, "Arctis") == "headset", "class of device: wearable headset / headphones = headset");
            Ok(BtProto.KindFromCod(0x240404, "Galaxy Buds2 Pro") == "earbuds" && BtProto.KindFromCod(0x240418, "AirPods Pro") == "earbuds" && BtProto.KindFromCod(0x240404, "WF-1000XM5") == "earbuds", "...earbuds are told apart by name (Buds, AirPods, WF-)");
            Ok(BtProto.KindFromCod(0x000508, "Wireless Controller") == "controller" && BtProto.KindFromCod(0x000504, "Stick") == "controller", "peripheral gamepad / joystick = controller");
            Ok(BtProto.KindFromCod(0x000540, "K380") == "keyboard" && BtProto.KindFromCod(0x0005C0, "Combo") == "keyboard" && BtProto.KindFromCod(0x000580, "M720") == "mouse", "peripheral keyboard / combo / pointing");
            Ok(BtProto.KindFromCod(0x5A020C, "Pixel 8") == "other" && BtProto.KindFromCod(0x240414, "Speaker") == "other", "a phone or a speaker = other");
            Ok(BtProto.KindFromAppearance(0x03C1, "K") == "keyboard" && BtProto.KindFromAppearance(0x03C2, "M") == "mouse" && BtProto.KindFromAppearance(0x03C4, "Pad") == "controller" && BtProto.KindFromAppearance(0x03C3, "J") == "controller", "LE appearance: keyboard, mouse, gamepad, joystick");
            Ok(BtProto.KindFromAppearance(0x0941, "x") == "earbuds" && BtProto.KindFromAppearance(0x0942, "x") == "headset" && BtProto.KindFromAppearance(0x0943, "x") == "headset", "LE appearance: earbud, headset, headphones");
            Ok(BtProto.KindFromAppearance(0, "Xbox Wireless Controller") == "controller" && BtProto.KindFromAppearance(0, "DualSense Wireless Controller") == "controller" && BtProto.KindFromAppearance(0x0040, "Pixel 8") == "other", "no appearance: the name decides (Xbox / DualSense = controller), a phone = other");
            Ok(BatteryAlerts.Word(new GearDevice { Kind = "earbuds" }) == "Earbuds" && BatteryAlerts.Word(new GearDevice { Kind = "controller" }) == "Controller" && BatteryAlerts.Word(new GearDevice { Kind = "other" }) == "Device", "kind words: Earbuds, Controller, Device");
            Ok(MainWindow.KindIcon(new GearDevice { Kind = "controller" }) == "gamepad" && MainWindow.KindIcon(new GearDevice { Kind = "earbuds" }) == "headphones" && Icons.Get(MainWindow.KindIcon(new GearDevice { Kind = "other", Bluetooth = true })) != null, "card icons: gamepad, headphones, and an icon for other Bluetooth devices");
        }
        static void BtCards()
        {
            var now = new DateTime(2026, 11, 22, 12, 0, 0);
            var buds = new BtEntry { Address = "a0b1c2d3e4f5", Name = "Galaxy Buds2 Pro", Kind = "earbuds", Classic = true, Connected = true, Battery = 80, Source = BtProto.WindowsSource };
            var d = BtProto.ToDevice(buds, null, now);
            Ok(d != null && d.Key == "bt:a0b1c2d3e4f5" && d.Kind == "earbuds" && d.Battery == 80 && d.Connected && d.Bluetooth && !d.Wired && d.Via == "Bluetooth" && d.Updated == now && d.LastSeen == now, "a connected device with a battery: a card, 80 %, connected, seen now");
            Ok(d.State == "on-battery" && d.StateLabel == "Connected \u00B7 Bluetooth" && !d.Charging && !d.Full, "state on-battery (for Overlay), label 'Connected \u00B7 Bluetooth', never charging (not reported)");
            Ok(BtProto.ToDevice(new BtEntry { Address = "a1", Name = "Living Room TV", Kind = "other", Connected = true }, null, now) == null, "a device that never reported a battery: no card");
            var rem = BtProto.Remember(d);
            var away = BtProto.ToDevice(new BtEntry { Address = "a0b1c2d3e4f5", Name = "Galaxy Buds2 Pro", Classic = true, Connected = false }, rem, now.AddHours(3));
            Ok(away != null && !away.Connected && away.State == "not-connected" && away.Battery == 80 && away.LastSeen == now && away.Source == null && away.Note.Contains("last one"), "not connected now: a card with the last battery and when it was last seen (" + rem + ")");
            var lastConn = BtProto.ToDevice(new BtEntry { Address = "a2", Name = "WH-1000XM5", Kind = "headset", Connected = false, Battery = 60, LastSeen = now.AddHours(-2) }, null, now);
            Ok(lastConn != null && lastConn.LastSeen == now.AddHours(-2) && lastConn.Battery == 60, "...last seen from Windows' last-connected time");
            var unknown = BtProto.ToDevice(new BtEntry { Address = "a3", Name = "Pad", Kind = "controller", Connected = true }, "controller|Pad|33|2026-11-22T10:00:00", now);
            Ok(unknown != null && unknown.Battery == 33 && unknown.Note.Contains("hasn't told"), "connected but no battery yet: remembered value, and a note saying so");
            Ok(BtProto.ToDevice(new BtEntry { Address = "a4", Name = "", Connected = true, Battery = 50 }, null, now).Name == "Bluetooth device", "a nameless device is 'Bluetooth device'");
            // a Logitech device read over HID++ on Bluetooth isn't listed twice
            var logi = new List<GearDevice> { new GearDevice { Key = "mouse:mx master 3s", Kind = "mouse", Name = "MX Master 3S", Via = "Bluetooth", Battery = 90 } };
            var both = GearMerge.WithBluetooth(logi, new[] { new GearDevice { Key = "bt:a5", Kind = "mouse", Name = "MX Master 3S", Bluetooth = true, Via = "Bluetooth", Battery = 85 }, d });
            Ok(both.Count == 2 && both[0].Name == "MX Master 3S" && !both[0].Bluetooth && both[1].Bluetooth, "a Logitech mouse on Bluetooth is read over HID++ and not shown twice; Bluetooth cards come after Logitech gear");
            Ok(Controller.DeviceJson(d, false).ContainsKey("lastSeen") && (bool)Controller.DeviceJson(d)["bluetooth"] && !(bool)Controller.DeviceJson(d, false)["alerts"], "status.json device: 1.0 fields plus bluetooth / lastSeen / alerts");
            var fb = new FakeBtSource(); var entries = fb.Read();
            Ok(entries.Count == 4 && entries.Count(e => BtProto.ToDevice(e, null, now) != null) == 3 && entries[1].Kind == "controller" && entries[0].Kind == "earbuds", "made-up Bluetooth devices: earbuds, a controller, headphones (not connected), a TV without battery");
        }
        static void BtAlerts()
        {
            var r = new AlertRules(); var a = new BatteryAlerts();
            var d = new GearDevice { Key = "bt:a0", Kind = "earbuds", Name = "Galaxy Buds2 Pro", Bluetooth = true, Via = "Bluetooth", Battery = 40 };
            Ok(string.Join(",", Feed(a, r, d, 40, 30, 21)) == "", "Bluetooth: nothing above 20 %");
            var low = a.Update(new GearDevice { Key = "bt:a0", Kind = "earbuds", Name = "Galaxy Buds2 Pro", Bluetooth = true, Battery = 20 }, r, DateTime.Now);
            Ok(low.Count == 1 && low[0].Kind == "low" && low[0].Title == "Earbuds battery low: 20 %" && low[0].Text.Contains("case"), "20 %: 'Earbuds battery low: 20 %' - put them in their case");
            Ok(string.Join(",", Feed(a, r, d, 19, 15, 12)) == "" && string.Join(",", Feed(a, r, d, 10)) == "critical" && string.Join(",", Feed(a, r, d, 8, 5)) == "", "once per discharge: low once, very low once");
            Ok(string.Join(",", Feed(a, r, d, 85, 60, 20)) == "low", "no charging report: charged in its case (back to 85 %) re-arms the alerts");
            var a2 = new BatteryAlerts(); var pad = new GearDevice { Key = "bt:a1", Kind = "controller", Name = "Xbox Wireless Controller", Bluetooth = true, Battery = 9 };
            var c = a2.Update(pad, r, DateTime.Now); Ok(c.Count == 1 && c[0].Kind == "critical" && c[0].Text.Contains("batteries"), "a controller at 9 %: very low - plug it in or change its batteries");
            var off = new GearDevice { Key = "bt:a2", Kind = "headset", Name = "WH", Bluetooth = true, Battery = 5, Connected = false };
            Ok(new BatteryAlerts().Update(off, r, DateTime.Now).Count == 0, "a Bluetooth device that isn't connected: no alert for its old battery");
            var router = new AlertRouter(); router.Route(low, true);
            Ok(router.Held.Count == 1 && router.OverlayLine != null && router.OverlayLine.Key == "bt:a0", "during a game a Bluetooth low battery is held and goes to Overlay like the rest");
        }

        // ---------------- 1.1: Hallow Overlay 1.22's own reader (its src\Gear.cs, compiled here as it is, read-only) parses Gear's status.json
        static void OverlayCompat(string json)
        {
            var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Run).Assembly.Location), ".."));
            var ov = Environment.GetEnvironmentVariable("HALLOW_OVERLAY_SRC") ?? Path.Combine(Path.GetDirectoryName(repo), "HallowOverlay", "src", "Gear.cs");
            if (!File.Exists(ov)) { Ok(true, "Overlay's reader not on this PC (" + ov + ") - compatibility not checked"); return; }
            var prov = new Microsoft.CSharp.CSharpCodeProvider();
            var cp = new System.CodeDom.Compiler.CompilerParameters(new[] { "System.dll", "System.Core.dll", "System.Web.Extensions.dll" }) { GenerateInMemory = true };
            var res = prov.CompileAssemblyFromSource(cp, File.ReadAllText(ov), "namespace HallowOverlay { public static class Snap { public static bool On; } }");
            Ok(!res.Errors.HasErrors, "Overlay 1.22's Gear reader compiles here (" + Path.GetFileName(ov) + ")" + (res.Errors.HasErrors ? ": " + res.Errors[0] : ""));
            if (res.Errors.HasErrors) return;
            var link = res.CompiledAssembly.GetType("HallowOverlay.GearLink");
            var g = link.GetMethod("Parse").Invoke(null, new object[] { json });
            Ok(g != null && (bool)g.GetType().GetField("Running").GetValue(g) && (int)g.GetType().GetField("LowAt").GetValue(g) == 20, "Overlay parses Gear 1.1's status.json: running, low at 20 %");
            var chips = g == null ? new List<object>() : ((System.Collections.IEnumerable)g.GetType().GetField("Devices").GetValue(g)).Cast<object>().ToList();
            Func<object, string, object> F = (o, f) => o.GetType().GetField(f).GetValue(o);
            var desc = string.Join(", ", chips.Select(c => F(c, "Kind") + " " + F(c, "Name") + " " + F(c, "Pct") + "%"));
            Ok(chips.Count == 4 && chips.Any(c => (string)F(c, "Kind") == "earbuds" && (int)F(c, "Pct") == 80) && chips.Any(c => (string)F(c, "Kind") == "controller" && (int)F(c, "Pct") == 45) && !chips.Any(c => (string)F(c, "Name") == "WH-1000XM5"),
                "Overlay's chips: mouse, headset, earbuds 80 %, controller 45 % - not the keyboard (no battery) nor the headphones that aren't connected: " + desc);
            Ok(link.GetMethod("Icon").Invoke(null, new object[] { "earbuds" }) != null && link.GetMethod("Usable").Invoke(null, new object[] { g, DateTime.Now }) is bool, "Overlay shows the new kinds with its fallback icon, and the status counts as fresh");
        }

        // ---------------- the family pipe of a test copy (made-up gear, no windows)
        static string Cmd(string exe, string line)
        {
            var psi = new ProcessStartInfo(exe, "--cmd \"" + line + "\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_GEAR_TESTDIR"] = Dir;
            using (var p = Process.Start(psi)) { var o = p.StandardOutput.ReadToEnd().Trim(); if (!p.WaitForExit(15000)) p.Kill(); return o; }
        }
        static Dictionary<string, object> J(string reply) { return reply.StartsWith("ok {") ? new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reply.Substring(3)) : null; }
        static void Pipe()
        {
            var exe = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Run).Assembly.Location), "..", "HallowGear.exe"));
            Ok(File.Exists(exe), "the app is built: " + exe);
            Directory.CreateDirectory(Dir);
            var psi = new ProcessStartInfo(exe, "--tray") { UseShellExecute = false, CreateNoWindow = true };
            psi.EnvironmentVariables["HALLOW_GEAR_TESTDIR"] = Dir; psi.EnvironmentVariables["HALLOW_GEAR_FAKE"] = "1"; psi.EnvironmentVariables["HALLOW_GEAR_HEADLESS"] = "1";
            var app = Process.Start(psi);
            try
            {
                string ping = null; for (int i = 0; i < 50 && (ping == null || !ping.StartsWith("ok")); i++) { Thread.Sleep(200); ping = Cmd(exe, "ping"); }
                Ok(ping == "ok {\"app\":\"Gear\",\"version\":\"" + Program.Version + "\"}", "ping: " + ping);
                Cmd(exe, "test-read");
                var st = J(Cmd(exe, "status"));
                Ok(st != null && (string)st["app"] == "Gear" && st.ContainsKey("updated") && (bool)st["running"] && st.ContainsKey("alert") && st.ContainsKey("game") && st.ContainsKey("lastCrash") && st.ContainsKey("lowAt"), "status: app, version, updated, running, alert, game, lowAt, lastCrash");
                var devs = st == null ? null : ((System.Collections.ArrayList)st["devices"]).Cast<Dictionary<string, object>>().ToList();
                Ok(devs != null && devs.Count == 6 && devs.All(x => new[] { "kind", "name", "battery", "state", "wired", "charging", "full", "connected", "asleep", "estimated", "paused", "updated" }.All(x.ContainsKey)), "status.devices (3 Logitech + 3 Bluetooth): every 1.0 field is still there - kind, name, battery, state, wired, charging, full, connected, asleep, estimated, paused, updated");
                Ok(devs != null && devs.Any(x => (string)x["kind"] == "keyboard" && x["battery"] == null && (string)x["state"] == "wired") && devs.Any(x => (string)x["kind"] == "mouse" && x["battery"] is int), "keyboard: wired, no battery; mouse: a number");
                Ok(devs != null && devs.All(x => x.ContainsKey("bluetooth") && x.ContainsKey("lastSeen") && x.ContainsKey("alerts")) && st.ContainsKey("bluetooth") && st.ContainsKey("hiddenDevices"), "1.1 adds bluetooth / lastSeen / alerts per device and bluetooth / hiddenDevices at the top (nothing renamed or removed)");
                var buds = devs == null ? null : devs.FirstOrDefault(x => (string)x["name"] == "Galaxy Buds2 Pro"); var xm = devs == null ? null : devs.FirstOrDefault(x => (string)x["name"] == "WH-1000XM5");
                Ok(buds != null && (string)buds["kind"] == "earbuds" && (int)buds["battery"] == 80 && (bool)buds["bluetooth"] && (bool)buds["connected"] && (string)buds["state"] == "on-battery" && !(bool)buds["charging"] && !(bool)buds["wired"], "Bluetooth earbuds: kind earbuds, 80 %, connected, on-battery, not charging");
                Ok(xm != null && !(bool)xm["connected"] && (string)xm["state"] == "not-connected" && xm["lastSeen"] != null && !devs.Any(x => (string)x["name"] == "Living Room TV"), "headphones not connected: not-connected with a last-seen time; a TV without battery has no card");
                var file = Path.Combine(Dir, "status.json"); Ok(File.Exists(file) && J("ok " + File.ReadAllText(file)) != null, "status.json written (and valid JSON)");
                OverlayCompat(File.ReadAllText(file));
                var menu = J(Cmd(exe, "menu")); Ok(menu != null && (string)menu["app"] == "Gear" && ((System.Collections.ArrayList)menu["items"]).Count >= 3, "menu");
                Ok(Cmd(exe, "invoke refresh") == "ok" && Cmd(exe, "invoke nope").StartsWith("error") && Cmd(exe, "open settings") == "ok" && Cmd(exe, "open nowhere").StartsWith("error"), "invoke / open");
                Ok(Cmd(exe, "frobnicate") == "error unknown command", "unknown command");
                Ok(Cmd(exe, "test-autostart on") == "ok True" && Cmd(exe, "test-autostart off") == "ok False", "start with Windows on / off (test file, not the real Run key)");
                // alerts: low -> a notification; during a game nothing pops up, the alert goes to status for Overlay; after the game it's shown
                Cmd(exe, "test-battery 0 19");
                var notes = Cmd(exe, "test-notes"); Ok(notes.Contains("Mouse battery low: 19 %"), "19 %: a low battery notification: " + notes);
                Cmd(exe, "test-game Delta Force"); Cmd(exe, "test-battery 0 9");
                var during = Cmd(exe, "test-notes"); st = J(Cmd(exe, "status"));
                var alert = st == null ? null : st["alert"] as Dictionary<string, object>;
                Ok(!during.Contains("very low") && alert != null && ((string)alert["text"]).Contains("9 %") && (string)alert["level"] == "critical" && (string)st["game"] == "Delta Force" && (int)st["held"] == 1, "in a game: no pop-up; status.alert for Overlay: " + (alert == null ? "none" : (string)alert["text"]));
                var asks = J(Cmd(exe, "test-asks")); Cmd(exe, "invoke refresh"); Thread.Sleep(600);
                Cmd(exe, "test-game off"); var after = Cmd(exe, "test-notes"); st = J(Cmd(exe, "status"));
                Ok(after.Contains("Mouse battery very low: 9 %") && st["alert"] == null && (int)st["held"] == 0, "after the game: the held alert is shown, Overlay's line is gone");
                // 1.1: Bluetooth alerts, per-device alerts off, hide / show, nothing new during a game, the switch
                Cmd(exe, "test-bt 1 19"); notes = Cmd(exe, "test-notes");
                Ok(notes.Contains("Controller battery low: 19 %"), "a Bluetooth controller at 19 %: 'Controller battery low: 19 %'");
                Cmd(exe, "test-bt 1 17"); Ok(Cmd(exe, "test-notes").Split(new[] { "Controller battery low" }, StringSplitOptions.None).Length == 2, "...once per discharge");
                Cmd(exe, "test-bt-alerts bt:0a1b2c3d4e01 off"); Cmd(exe, "test-bt 0 9"); notes = Cmd(exe, "test-notes"); st = J(Cmd(exe, "status"));
                var b2 = st == null ? null : ((System.Collections.ArrayList)st["devices"]).Cast<Dictionary<string, object>>().FirstOrDefault(x => (string)x["name"] == "Galaxy Buds2 Pro");
                Ok(!notes.Contains("Earbuds battery") && b2 != null && (int)b2["battery"] == 9 && !(bool)b2["alerts"], "alerts off for the earbuds: 9 %, no notification, status says alerts false");
                Cmd(exe, "test-bt-hide bt:0a1b2c3d4e02 on"); st = J(Cmd(exe, "status"));
                var names = st == null ? new List<string>() : ((System.Collections.ArrayList)st["devices"]).Cast<Dictionary<string, object>>().Select(x => (string)x["name"]).ToList();
                Ok(names.Count == 5 && !names.Contains("Xbox Wireless Controller") && (int)st["hiddenDevices"] == 1 && !((System.Collections.ArrayList)st["low"]).Contains("Xbox Wireless Controller"), "a hidden device isn't in status.json (so not on Overlay either), nor in 'low'");
                Cmd(exe, "test-bt-hide bt:0a1b2c3d4e02 off"); st = J(Cmd(exe, "status")); Ok(((System.Collections.ArrayList)st["devices"]).Count == 6 && (int)st["hiddenDevices"] == 0, "...and back when shown again");
                Cmd(exe, "test-game Delta Force"); var btReads = Cmd(exe, "test-bt-reads");
                Cmd(exe, "invoke refresh"); Thread.Sleep(800); Cmd(exe, "test-read");
                Ok(Cmd(exe, "test-bt-reads") == btReads, "during a game Bluetooth devices aren't read, even on Refresh (" + btReads + ")");
                Cmd(exe, "test-bt-hear bt:0a1b2c3d4e02 8"); Thread.Sleep(300); st = J(Cmd(exe, "status"));
                var pad = ((System.Collections.ArrayList)st["devices"]).Cast<Dictionary<string, object>>().FirstOrDefault(x => (string)x["name"] == "Xbox Wireless Controller");
                var al2 = st["alert"] as Dictionary<string, object>;
                Ok(pad != null && (int)pad["battery"] == 8 && !(bool)pad["paused"] && !Cmd(exe, "test-notes").Contains("Controller battery very low") && al2 != null && ((string)al2["text"]).StartsWith("Controller battery very low: 8 %"), "...but a battery notification it sends by itself is heard (8 %): no pop-up, the alert goes to Overlay's line");
                Cmd(exe, "test-game off");
                Ok(Cmd(exe, "test-notes").Contains("Controller battery very low: 8 %"), "...and is shown after the game");
                Cmd(exe, "test-bt-switch off"); st = J(Cmd(exe, "status"));
                Ok(!(bool)st["bluetooth"] && ((System.Collections.ArrayList)st["devices"]).Count == 3, "the Bluetooth switch off: only the Logitech gear");
                Cmd(exe, "test-bt-switch on"); st = J(Cmd(exe, "status"));
                Ok((bool)st["bluetooth"] && ((System.Collections.ArrayList)st["devices"]).Count == 6, "...and on again: the Bluetooth cards are back");
                Ok(Cmd(exe, "quit") == "ok", "quit");
                Ok(app.WaitForExit(10000), "it exits");
                var last = J("ok " + File.ReadAllText(file)); Ok(last != null && !(bool)last["running"], "status.json says not running after quit");
                Ok(Cmd(exe, "ping").StartsWith("error") , "--cmd when it isn't running: error (exit 2)");
            }
            finally { if (!app.HasExited) app.Kill(); }
        }
    }
}
