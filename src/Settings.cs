// Hallow Gear settings: %APPDATA%\HallowGear\settings.json. Nothing secret is kept here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        [DataMember] public bool ReadMouseInGames;          // 1.0-1.3 (kept so an old file reads the same): on = asked every 2 min in games -> GameAsk "5min"
        // 1.3.1: while a game runs, ask the mouse (and other receiver devices) for its battery: "start" = once when Gear starts (1-2 short
        // messages per device), "5min" = that gentle read every 5 min, "never" = only listen. Otherwise nothing is asked during a game.
        [DataMember] public string GameAsk;
        public static readonly string[] GameAsks = { "start", "5min", "never" };
        [DataMember] public Dictionary<string, string> KnownSlotBattery;   // 1.3.1: "receiverPid:slot" -> "feature index soc wpid" - used only to LISTEN to its battery reports
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
        [DataMember] public string IdSalt;                  // 1.2.2: random, per install - makes status.json's opaque Bluetooth device ids (never the address)
        // 1.3: while I'm gaming - per alert type: "hold" (until after the game), "overlay" (Overlay line only), "popup" (Gear's pop-up on another
        // monitor), "toast" (a Windows notification anyway). Per device (DeviceGame): "<key>" (all types) or "<key>|<type>" -> mode.
        [DataMember] public string GameLow = "hold", GameCritical = "popup", GameCharging = "hold", GameFull = "hold", GameWired = "hold";
        [DataMember] public Dictionary<string, string> DeviceGame;
        [DataMember] public string PopupMonitor = "auto";   // "auto" (the monitor without the game), "same" (the game's), or a device name (\\.\DISPLAY2)
        [DataMember] public string PopupCorner = "top-right";   // top-right, top-left, bottom-right, bottom-left
        [DataMember] public int PopupSeconds = 8;           // hides after
        [DataMember] public string PopupFallback = "hold";  // one monitor (or the chosen one missing): hold, overlay, toast, same
        [DataMember] public string SoundNormal = "windows"; // outside games: windows, hallow, silent
        [DataMember] public string SoundGame = "silent";    // during games: silent, hallow (quiet), windows
        [DataMember] public int SoundVolume = 70, GameVolume = 30;   // Gear's own sound (Hallow sound, pop-up): its samples are scaled - Windows' volume is never touched

        [OnDeserializing] void Defaults(StreamingContext c) { UseHallowIcon = true; UseHallowAccent = true; Look = "hallow"; AlertLow = true; Low1 = 20; Low2 = 10; AlertCharging = true; AlertFull = true; PollSeconds = 60; Bluetooth = true;
            GameLow = "hold"; GameCritical = "popup"; GameCharging = "hold"; GameFull = "hold"; GameWired = "hold"; PopupMonitor = "auto"; PopupCorner = "top-right"; PopupSeconds = 8;
            PopupFallback = "hold"; SoundNormal = "windows"; SoundGame = "silent"; SoundVolume = 70; GameVolume = 30; }

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
            if (KnownSlotBattery == null) KnownSlotBattery = new Dictionary<string, string>();
            if (!GameAsks.Contains(GameAsk)) GameAsk = ReadMouseInGames ? "5min" : "start";   // 1.3.1
            if (BtHidden == null) BtHidden = new List<string>(); if (BtNoAlerts == null) BtNoAlerts = new List<string>();
            if (BtKnown == null) BtKnown = new Dictionary<string, string>();
            if (Look != "light" && Look != "dark") Look = "hallow";
            Low1 = Math.Max(5, Math.Min(50, Low1)); Low2 = Math.Max(1, Math.Min(Low1 - 1, Low2));
            PollSeconds = Math.Max(60, Math.Min(900, PollSeconds));
            // 1.3
            if (!GameModes.Valid(GameLow)) GameLow = "hold"; if (!GameModes.Valid(GameCritical)) GameCritical = "popup"; if (!GameModes.Valid(GameCharging)) GameCharging = "hold";
            if (!GameModes.Valid(GameFull)) GameFull = "hold"; if (!GameModes.Valid(GameWired)) GameWired = "hold";
            if (DeviceGame == null) DeviceGame = new Dictionary<string, string>();
            foreach (var k in DeviceGame.Keys.ToList()) if (!GameModes.Valid(DeviceGame[k])) DeviceGame.Remove(k);
            if (string.IsNullOrWhiteSpace(PopupMonitor)) PopupMonitor = "auto";
            if (!new[] { "top-right", "top-left", "bottom-right", "bottom-left" }.Contains(PopupCorner)) PopupCorner = "top-right";
            PopupSeconds = Math.Max(3, Math.Min(60, PopupSeconds));
            if (!GameModes.Fallbacks.Contains(PopupFallback)) PopupFallback = "hold";
            SoundNormal = GearSound.Valid(SoundNormal, "windows"); SoundGame = GearSound.Valid(SoundGame, "silent");
            SoundVolume = Math.Max(5, Math.Min(100, SoundVolume)); GameVolume = Math.Max(5, Math.Min(100, GameVolume));
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
