# Hallow Gear roadmap

## Done

| Version | What |
|---|---|
| 1.0 | A card per device (battery bar and %, charging / full / on battery / wired / wireless / asleep / not connected, last read, 24-hour sparkline kept in %APPDATA%\HallowGear\history.json). LIGHTSPEED mouse over Logitech HID++ 2.0 with Windows' HID APIs (SetupDi + CreateFile + HidD_* + overlapped ReadFile / WriteFile, shared handles next to G HUB): IRoot, Device Name, Unified Battery 0x1004 / Battery Status 0x1000 / Battery Voltage 0x1001, receiver slots 1-6, asleep from error 0x09 and the 0x41 link notification, the same mouse on its USB cable merged as "wired (charging)". Passive listening to battery events (and G HUB's answers). ASTRO A50 Gen 5 battery and docked / charging through the base station's battery GET documented by HeadsetControl. Wired keyboards present / wired. Read-only by construction: SafeReport + allow-lists, tested over every HID++ feature / function and every A50 command, plus an IL scan of the WriteFile callers. Gentle polling (60 s, 5 min on battery, on device arrival / removal, Below Normal); during games the mouse is only listened to and the headset read at most every 2 min. Alerts: low / very low once per discharge (20 / 10 %, configurable), charging started, fully charged, wired / wireless (off by default); held during games, low battery to Hallow Overlay (status.json `alert`, or `flash` for an older Overlay). Family link (ping, status, open, menu, invoke, quit, --cmd, status.json with devices), Hub tray hosting and notify, accent, light / dark, crash reports, updater, --tray, start at sign-in (per user) |
| 1.0.1 | A sleeping mouse shows as "Asleep" (with its last battery), not "Not connected": on this PC's LIGHTSPEED receiver a paired mouse that has gone to sleep answers the ping with HID++ error 0x08 (unknown device), not 0x09, so any error for a known slot now means asleep / switched off. "Not connected" stays for gear whose receiver, base station or cable isn't plugged in |

## Later

| Idea | Why it isn't in 1.0 |
|---|---|
| A50 X, A50 Gen 4 | Different protocols / USB ids (Gen 4 is VID 9886; the A50 X uses a newer protocol). Not on this PC, so not testable. |
| Is the A50 switched on? | The Gen 5 base station answers the battery GET either way; HeadsetControl documents no "headset connected" read. |
| Logitech Bolt receivers, Bluetooth Logitech devices | Bolt and Bluetooth (BLE HID) need other discovery; possible later with the same read-only list. |
| Battery from G HUB's local websocket | Undocumented; HID++ already gives the mouse directly and the A50 has its own documented GET. |
| Other brands (Razer, SteelSeries, Corsair...) | Each has its own protocol; HeadsetControl documents several headsets - a later version could add their battery GETs the same way. |
| Hallow Remote / phone view | Remote could show status.json's devices later. |
