// Hallow Gear settings: %APPDATA%\HallowGear\settings.json. Nothing secret is kept here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace HallowGear
{
    [DataContract]
    public class GearSettings
    {
        // developer tests: HALLOW_GEAR_TESTDIR = data folder (and a separate pipe); HALLOW_GEAR_FAKE=1 = made-up devices (no device is
        // ever opened); HALLOW_GEAR_HEADLESS=1 = no windows or tray.
        public static readonly string TestDir = Env("HALLOW_GEAR_TESTDIR");
        public static readonly bool Fake = TestDir != null && Env("HALLOW_GEAR_FAKE") == "1";
        public static readonly bool Headless = TestDir != null && Env("HALLOW_GEAR_HEADLESS") == "1";
        public static bool Frozen;   // --snap: never saved
        static string Env(string n) { var v = Environment.GetEnvironmentVariable(n); return string.IsNullOrWhiteSpace(v) ? null : v; }
        public static string Dir { get { return TestDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HallowGear"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        [DataMember] public bool UseHallowIcon = true;
        [DataMember] public bool UseHallowAccent = true;
        [DataMember] public string Look = "hallow";        // "hallow" (follow theme.json's mode), "light", "dark"
        // alerts
        [DataMember] public bool AlertLow = true;
        [DataMember] public int Low1 = 20, Low2 = 10;       // percent: "low" and "very low", once per discharge each
        [DataMember] public bool AlertCharging = true;      // charging started
        [DataMember] public bool AlertFull = true;          // fully charged
        [DataMember] public bool AlertWired;                // switched to wired / wireless - off by default
        // reading
        [DataMember] public int PollSeconds = 60;           // on mains power; on battery it's at least 5 minutes
        [DataMember] public bool ReadMouseInGames;          // off: while a game runs the mouse is only listened to, never asked
        [DataMember] public List<string> ExtraGames;
        // remembered
        [DataMember] public Dictionary<string, string> KnownSlots;   // "receiverPid:slot" -> device name (to say "asleep" instead of nothing)
        [DataMember] public Dictionary<string, string> KnownGear;    // device key -> "kind|name" (shown as "not connected" while it is away; Forget removes it)
        [DataMember] public Dictionary<string, string> KnownSlotKinds;   // 1.1: "receiverPid:slot" -> kind (mouse, keyboard...) of a device read before
        // 1.1: Bluetooth devices (what Windows knows of them)
        [DataMember] public bool Bluetooth = true;          // the "Bluetooth devices" switch
        [DataMember] public List<string> BtHidden;          // device keys ("bt:<address>") not shown (nor sent to Overlay, nor alerted)
        [DataMember] public List<string> BtNoAlerts;        // device keys whose alerts are off
        [DataMember] public Dictionary<string, string> BtKnown;   // device key -> "kind|name|percent|last seen" (shown as not connected while away)
        [DataMember] public string LastUpdateCheck;
        [DataMember] public string LastCrashShown;
        [DataMember] public bool TrayHintShown;

        [OnDeserializing] void Defaults(StreamingContext c) { UseHallowIcon = true; UseHallowAccent = true; Look = "hallow"; AlertLow = true; Low1 = 20; Low2 = 10; AlertCharging = true; AlertFull = true; PollSeconds = 60; Bluetooth = true; }

        public static GearSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var f = File.OpenRead(FilePath))
                    {
                        var s = (GearSettings)new DataContractJsonSerializer(typeof(GearSettings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(f);
                        if (s != null) { s.Fix(); return s; }
                    }
            }
            catch { }
            var d = new GearSettings(); d.Fix(); return d;
        }
        public void Fix()
        {
            if (ExtraGames == null) ExtraGames = new List<string>();
            ExtraGames.RemoveAll(string.IsNullOrWhiteSpace);
            if (KnownSlots == null) KnownSlots = new Dictionary<string, string>();
            if (KnownGear == null) KnownGear = new Dictionary<string, string>();
            if (KnownSlotKinds == null) KnownSlotKinds = new Dictionary<string, string>();
            if (BtHidden == null) BtHidden = new List<string>(); if (BtNoAlerts == null) BtNoAlerts = new List<string>();
            if (BtKnown == null) BtKnown = new Dictionary<string, string>();
            if (Look != "light" && Look != "dark") Look = "hallow";
            Low1 = Math.Max(5, Math.Min(50, Low1)); Low2 = Math.Max(1, Math.Min(Low1 - 1, Low2));
            PollSeconds = Math.Max(60, Math.Min(900, PollSeconds));
        }

        static readonly object saveLock = new object();
        public void Save()
        {
            if (Frozen) return;
            lock (saveLock)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var tmp = FilePath + ".tmp";
                    using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(GearSettings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).WriteObject(f, this);
                    for (int i = 0; ; i++) { try { if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath); break; } catch { if (i >= 5) throw; Thread.Sleep(50); } }
                }
                catch { }
            }
        }
    }
}
