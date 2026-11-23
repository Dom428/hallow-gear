// Hallow Gear 1.3.1 - remembered gear: %APPDATA%\HallowGear\devices.json keeps each Logitech / A50 device's kind, name, last battery,
// last state and when it was last seen, so a restart (even in the middle of a game) shows every device at once - "last known: 66 % - 12 min
// ago" - until fresh data arrives. Bluetooth devices have their own memory (settings.json BtKnown). Nothing secret, nothing sent anywhere.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HallowGear
{
    [DataContract]
    public class RememberedDevice
    {
        [DataMember] public string Key, Kind, Name, Via, State, SlotKey, Source;
        [DataMember] public int? Battery;
        [DataMember] public bool HasBattery = true, Wired, Charging, Full, Estimated, Provisional;
        [DataMember] public string Seen;      // last time it was there (ISO 8601, local)
        [DataMember] public string Updated;   // when its battery was last read / heard
        [OnDeserializing] void Defaults(StreamingContext c) { HasBattery = true; }
    }

    public static class RememberedGear
    {
        public static string FilePath { get { return Path.Combine(GearSettings.Dir, "devices.json"); } }
        static DataContractJsonSerializer Ser() { return new DataContractJsonSerializer(typeof(List<RememberedDevice>)); }
        public static string Time(DateTime t) { return t.ToString("s"); }
        static DateTime? Parse(string s) { DateTime t; return s != null && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out t) ? t : (DateTime?)null; }

        public static List<RememberedDevice> FromJson(string json)
        {
            try
            {
                using (var m = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
                {
                    var l = Ser().ReadObject(m) as List<RememberedDevice>;
                    return (l ?? new List<RememberedDevice>()).Where(r => r != null && !string.IsNullOrEmpty(r.Key) && !string.IsNullOrEmpty(r.Name) && !r.Key.StartsWith("bt:"))
                        .Select(r => { if (r.Battery.HasValue) r.Battery = Math.Max(0, Math.Min(100, r.Battery.Value)); return r; }).GroupBy(r => r.Key).Select(g => g.First()).Take(32).ToList();
                }
            }
            catch { return new List<RememberedDevice>(); }
        }
        public static string ToJson(List<RememberedDevice> list)
        {
            using (var m = new MemoryStream()) { Ser().WriteObject(m, list ?? new List<RememberedDevice>()); return Encoding.UTF8.GetString(m.ToArray()); }
        }
        public static List<RememberedDevice> Load() { try { return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath)) : new List<RememberedDevice>(); } catch { return new List<RememberedDevice>(); } }
        public static void Save(List<RememberedDevice> list)
        {
            if (GearSettings.Frozen) return;
            try { Directory.CreateDirectory(GearSettings.Dir); var tmp = FilePath + ".tmp"; File.WriteAllText(tmp, ToJson(list)); if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath); } catch { }
        }

        // a device worth remembering: Logitech / A50 gear (not Bluetooth), connected and not a "last known" copy of itself
        public static RememberedDevice From(GearDevice d, DateTime now)
        {
            return new RememberedDevice { Key = d.Key, Kind = d.Kind, Name = d.Name, Via = d.Via, State = d.State, SlotKey = d.SlotKey, Source = d.Source, Battery = d.HasBattery ? d.Battery : null,
                HasBattery = d.HasBattery, Wired = d.Wired, Charging = d.Charging, Full = d.Full, Estimated = d.Estimated, Provisional = d.Provisional,
                Seen = Time(now), Updated = d.Updated.HasValue ? Time(d.Updated.Value) : null };
        }
        // folds what is shown now into what is remembered; true when something worth saving changed (a device, its battery, its state)
        public static bool Update(List<RememberedDevice> mem, IEnumerable<GearDevice> shown, DateTime now)
        {
            bool changed = false;
            foreach (var d in shown)
            {
                if (d.Bluetooth || d.LastKnown || d.Waiting || !d.Connected || string.IsNullOrEmpty(d.Key) || string.IsNullOrEmpty(d.Name)) continue;
                // the device itself named this slot now: the receiver's provisional name for it goes
                if (!d.Provisional && d.SlotKey != null) changed |= mem.RemoveAll(r => r.Provisional && r.SlotKey == d.SlotKey && r.Key != d.Key) > 0;
                var old = mem.FirstOrDefault(r => r.Key == d.Key); var n = From(d, now);
                if (old != null && !n.Battery.HasValue && old.Battery.HasValue) { n.Battery = old.Battery; n.Updated = old.Updated; n.Source = old.Source; n.Estimated = old.Estimated; }   // asleep: keep the last battery
                if (old == null || old.Battery != n.Battery || old.State != n.State || old.Name != n.Name || old.Kind != n.Kind || old.Provisional != n.Provisional) changed = true;
                if (old != null) mem.Remove(old);
                mem.Add(n);
            }
            return changed;
        }
        public static bool Forget(List<RememberedDevice> mem, string key) { return mem.RemoveAll(r => r.Key == key) > 0; }

        // the card shown for a remembered device until fresh data arrives
        public static GearDevice ToCard(RememberedDevice r)
        {
            var d = new GearDevice { Key = r.Key, Kind = r.Kind ?? "device", Name = r.Name, Via = r.Via, Source = r.Source, SlotKey = r.SlotKey, Provisional = r.Provisional,
                HasBattery = r.HasBattery, Battery = r.HasBattery ? r.Battery : null, Wired = r.Wired, Estimated = r.Estimated, LastKnown = true, Paused = true,
                Charging = r.Charging && r.State == "charging", Full = r.Full && r.State == "full", Asleep = r.State == "asleep", Connected = true };
            d.Updated = Parse(r.Updated); d.LastSeen = Parse(r.Seen);
            return d;
        }
        // "last known: 66 % \u00B7 12 min ago" (the age of the battery reading, else of the last sighting)
        public static string LastKnownText(GearDevice d, DateTime now)
        {
            var when = d.Updated ?? d.LastSeen;
            return "last known: " + (d.HasBattery && d.Battery.HasValue ? d.Battery + " %" : d.StateLabel.ToLowerInvariant()) + (when.HasValue ? " \u00B7 " + MainWindow.Ago(when, now) : "");
        }
    }
}
