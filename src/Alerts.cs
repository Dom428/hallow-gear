// Hallow Gear - the battery alert rules and the 24-hour history.
//   low (Low1, default 20 %) and very low (Low2, default 10 %): once per discharge each (charging re-arms them; so does the level
//   climbing 10 points above a threshold, e.g. a fresh battery); charging started; fully charged; wired <-> wireless (off by default).
//   While a game runs nothing pops up: alerts are held and shown after the game; a low battery goes to Hallow Overlay as a quiet line.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HallowGear
{
    public class GearAlert
    {
        public string Key, Kind, Title, Text; public DateTime At; public bool Low { get { return Kind == "low" || Kind == "critical"; } }
        public override string ToString() { return Kind + ": " + Text; }
    }
    public class AlertRules
    {
        public bool Low = true, Charging = true, Full = true, Wired; public int Low1 = 20, Low2 = 10;
        public static AlertRules From(GearSettings s) { return new AlertRules { Low = s.AlertLow, Charging = s.AlertCharging, Full = s.AlertFull, Wired = s.AlertWired, Low1 = s.Low1, Low2 = s.Low2 }; }
    }

    public class BatteryAlerts
    {
        class St { public bool Known, Charging, Full, Wired; public HashSet<int> Fired = new HashSet<int>(); }
        readonly Dictionary<string, St> st = new Dictionary<string, St>();
        public static string Word(GearDevice d) { return BtProto.Word(d.Kind); }   // Headset, Mouse, Keyboard, Earbuds, Controller, Device

        public List<GearAlert> Update(GearDevice d, AlertRules r, DateTime now)
        {
            var list = new List<GearAlert>();
            if (d == null || !d.HasBattery || !d.Connected || d.Asleep) return list;
            St s; if (!st.TryGetValue(d.Key, out s)) st[d.Key] = s = new St();
            Func<string, string, string, GearAlert> A = (k, t, x) => new GearAlert { Key = d.Key, Kind = k, Title = t, Text = x, At = now };
            var name = d.Name ?? Word(d);
            bool charging = d.Charging, full = d.Full;
            if (s.Known)
            {
                if (charging && !s.Charging && r.Charging) list.Add(A("charging", Word(d) + " is charging", name + " started charging" + (d.Battery.HasValue ? " at " + d.Battery + " %." : ".")));
                if (full && !s.Full && r.Full) list.Add(A("full", Word(d) + " is fully charged", name + " is fully charged" + (d.Kind == "headset" ? " - ready to go." : ".")));
                if (d.Wired != s.Wired && r.Wired) list.Add(A(d.Wired ? "wired" : "wireless", Word(d) + (d.Wired ? " switched to wired" : " switched to wireless"), name + (d.Wired ? " is on its USB cable now." : " is wireless again.")));
            }
            if (charging || full) s.Fired.Clear();
            else if (d.Battery.HasValue)
            {
                int p = d.Battery.Value;
                s.Fired.RemoveWhere(t => p >= t + 10);   // a fresh battery / it was charged while we weren't looking
                var levels = new[] { r.Low1, r.Low2 }.Where(t => t > 0).Distinct().OrderByDescending(t => t).ToList();
                var hit = levels.Where(t => p <= t && !s.Fired.Contains(t)).ToList();
                if (hit.Count > 0)
                {
                    int worst = hit.Min(); bool critical = worst == levels.Min() && levels.Count > 1;
                    foreach (var t in levels.Where(t => p <= t)) s.Fired.Add(t);
                    if (r.Low) list.Add(A(critical ? "critical" : "low", Word(d) + " battery " + (critical ? "very low" : "low") + ": " + p + " %", name + " is at " + p + " %" + (d.Estimated ? " (estimated)" : "") + ". " + Advice(d)));
                }
            }
            s.Known = true; s.Charging = charging; s.Full = full; s.Wired = d.Wired;
            return list;
        }
        static string Advice(GearDevice d)
        {
            if (d.Bluetooth) return d.Kind == "earbuds" ? "Put them in their case to charge." : d.Kind == "controller" ? "Plug it in, or change its batteries." : "Charge it soon.";
            return d.Kind == "headset" ? "Put it on the base station to charge." : d.Kind == "mouse" ? "Plug in its cable or put it on its charger." : "Charge it soon.";
        }
    }

    // nothing pops up during a game: alerts wait, low battery becomes Overlay's quiet line; after the game they're shown (at most 3)
    public class AlertRouter
    {
        public readonly List<GearAlert> Held = new List<GearAlert>();
        public GearAlert OverlayLine;   // the newest low-battery alert held during this game (for Overlay), null otherwise
        // returns what to show now
        public List<GearAlert> Route(IEnumerable<GearAlert> alerts, bool gameRunning)
        {
            var now = new List<GearAlert>();
            foreach (var a in alerts)
            {
                if (!gameRunning) { now.Add(a); continue; }
                Held.RemoveAll(x => x.Key == a.Key && (x.Kind == a.Kind || (x.Low && a.Low) || (IsCharge(x) && IsCharge(a))));
                Held.Add(a);
                if (a.Low) OverlayLine = a;
            }
            return now;
        }
        static bool IsCharge(GearAlert a) { return a.Kind == "charging" || a.Kind == "full" || a.Kind == "wired" || a.Kind == "wireless"; }
        // the game closed: what to show now (at most 3 toasts - more become one summary)
        public List<GearAlert> GameEnded(DateTime now)
        {
            OverlayLine = null;
            var all = Held.OrderBy(a => a.At).ToList(); Held.Clear();
            if (all.Count <= 3) return all;
            var low = all.Where(a => a.Low).ToList(); var keep = low.Take(2).ToList();
            var rest = all.Except(keep).ToList();
            keep.Add(new GearAlert { Key = "summary", Kind = "summary", Title = "While you played", Text = string.Join(" \u00B7 ", rest.Select(a => a.Title)), At = now });
            return keep;
        }
    }

    // the last 24 hours of each device's battery: a point when it changes, and at least every 10 minutes; %APPDATA%\HallowGear\history.json
    public class History
    {
        public class Pt { public DateTime T; public int P; }
        public readonly Dictionary<string, List<Pt>> Points = new Dictionary<string, List<Pt>>();
        public bool Dirty;
        public void Add(string key, int pct, DateTime t)
        {
            List<Pt> l; if (!Points.TryGetValue(key, out l)) Points[key] = l = new List<Pt>();
            var last = l.LastOrDefault();
            if (last != null && last.P == pct && (t - last.T).TotalMinutes < 10) return;
            if (last != null && t < last.T) return;
            l.Add(new Pt { T = t, P = pct }); Dirty = true;
            Trim(t);
        }
        public void Trim(DateTime now) { foreach (var l in Points.Values) l.RemoveAll(p => (now - p.T).TotalHours > 24); }
        public List<Pt> For(string key) { List<Pt> l; return Points.TryGetValue(key, out l) ? l.ToList() : new List<Pt>(); }
        public string ToJson()
        {
            var d = Points.ToDictionary(kv => kv.Key, kv => kv.Value.Select(p => new object[] { p.T.ToString("yyyy-MM-ddTHH:mm:ss"), p.P }).ToList());
            return new JavaScriptSerializer().Serialize(d);
        }
        public static History FromJson(string json, DateTime now)
        {
            var h = new History();
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json ?? "");
                if (d != null)
                    foreach (var kv in d)
                    {
                        var arr = kv.Value as System.Collections.ArrayList; if (arr == null) continue;
                        var l = new List<Pt>();
                        foreach (var o in arr)
                        {
                            var pair = o as System.Collections.ArrayList; DateTime t;
                            if (pair == null || pair.Count < 2 || !DateTime.TryParse(pair[0] as string, out t) || !(pair[1] is int)) continue;
                            l.Add(new Pt { T = t, P = Math.Max(0, Math.Min(100, (int)pair[1])) });
                        }
                        h.Points[kv.Key] = l.OrderBy(p => p.T).ToList();
                    }
            }
            catch { }
            h.Trim(now); return h;
        }
        public static string FilePath { get { return Path.Combine(GearSettings.Dir, "history.json"); } }
        public static History Load() { try { return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath), DateTime.Now) : new History(); } catch { return new History(); } }
        public void Save()
        {
            if (GearSettings.Frozen) return;
            try
            {
                Directory.CreateDirectory(GearSettings.Dir); var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
                Dirty = false;
            }
            catch { }
        }
        // made-up history for --snap: a day of slow discharge with a charge in the middle
        public static History Fake(DateTime now)
        {
            var h = new History(); var t0 = now.AddHours(-24);
            for (int i = 0; i <= 144; i++)
            {
                var t = t0.AddMinutes(i * 10);
                int m = i < 60 ? 92 - i / 2 : i < 72 ? 62 + (i - 60) * 3 : 98 - (i - 72) / 2;
                h.Points.GetOrAdd("mouse:pro x superlight 2").Add(new Pt { T = t, P = Math.Max(5, Math.Min(100, m - 30)) });
                int a = i < 40 ? 100 - i * 2 : i < 50 ? 20 + (i - 40) * 8 : 100;
                h.Points.GetOrAdd("headset:astro a50").Add(new Pt { T = t, P = Math.Max(0, Math.Min(100, a)) });
            }
            return h;
        }
    }
    static class DictExt
    {
        public static List<History.Pt> GetOrAdd(this Dictionary<string, List<History.Pt>> d, string k) { List<History.Pt> l; if (!d.TryGetValue(k, out l)) d[k] = l = new List<History.Pt>(); return l; }
    }
}
