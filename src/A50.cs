// Hallow Gear - the ASTRO A50 (Gen 5) base station (USB 046d:0b1c, HID interface 8, vendor page 0xFF32), READ-ONLY.
// The protocol is the one the open-source HeadsetControl project documents for "Logitech ASTRO A50 Gen 5"
// (lib/devices/logitech_astro_a50.hpp, reverse-engineered there - not from Logitech): 64-byte report 0x02 frames
//   [0x02, 0x0C, 3 + payload length, 0x00, command, handle (echoed), payload...]
// and the battery GET (command 0x06, no payload); its reply is [0x02, 0x0C, len, 0x00, 0x06, handle, level %, level2, docked/charging].
// That GET is the ONLY thing Hallow Gear sends to the A50. Volume, sidetone, EQ, lights and noise gate are SETs and are not on the list.
// The base station also sends these frames by itself when something changes; those are read too (nothing sent).
// Not known: the A50 X (a different protocol), whether the headset is switched on (the base answers either way), chat-mix is not read.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HallowGear
{
    public static class A50Proto
    {
        public const ushort Vid = 0x046D, PidGen5 = 0x0B1C;
        public const byte ReportId = 0x02, Marker = 0x0C, HeaderLen = 3, CmdBattery = 0x06, HandleBattery = 0x0C;
        // ---- THE ALLOW-LIST: command -> name. GETs without a payload only.
        public static readonly Dictionary<byte, string> Allowed = new Dictionary<byte, string> { { CmdBattery, "battery GET" } };
        public static string AllowedName(byte cmd, byte[] payload)
        {
            string n; if (!Allowed.TryGetValue(cmd, out n)) return null;
            return payload == null || payload.Length == 0 ? n : null;   // a payload would make it a SET
        }
        public static bool IsBatteryFrame(byte[] r)
        {
            return r != null && r.Length >= 9 && r[0] == ReportId && r[1] == Marker && r[4] == CmdBattery && r[2] > HeaderLen;
        }
        public static BatteryReading Battery(byte[] r)
        {
            if (!IsBatteryFrame(r) || r[6] > 100) return null;
            bool docked = r[8] != 0;
            return new BatteryReading { Percent = r[6], Charging = docked && r[6] < 100, Full = docked && r[6] >= 100, External = docked, Source = "ASTRO A50 base station (battery GET 0x06)" };
        }
        public static bool IsControl(HidInfo i) { return i.Vid == Vid && i.Pid == PidGen5 && (i.UsagePage == 0xFF32 || i.Interface == 8) && i.OutLen >= 6; }
    }

    public class A50Reader
    {
        readonly IReportPort port; public readonly List<string> Sent = new List<string>(); public int ReplyMs = 1500;
        public A50Reader(IReportPort port) { this.port = port; }
        public BatteryReading Battery()
        {
            var req = SafeReport.ForA50(A50Proto.CmdBattery, A50Proto.HandleBattery, port.OutLength > 0 ? port.OutLength : 64);
            if (req == null) return null;
            lock (Sent) Sent.Add(req.What); SentLog.Add(req.What);
            byte[] reply;
            using (var w = new ReplyWaiter(A50Proto.IsBatteryFrame, port))
            {
                if (!port.Write(req)) return null;
                reply = w.Wait(ReplyMs);
            }
            return A50Proto.Battery(reply);
        }
    }
}
