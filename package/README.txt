HALLOW GEAR - battery and charging of your Logitech gear and Bluetooth devices

A card for each piece of gear - a LIGHTSPEED mouse, an ASTRO A50 (Gen 5) headset, a Logitech
keyboard, Bluetooth earbuds / headsets / controllers - with its battery, charging / full / on
battery / wired / wireless / asleep, and the last 24 hours. It only READS: it can't change any
setting on your gear.

HOW TO INSTALL
1. Right-click the zip > Properties > tick "Unblock" > OK
2. Right-click the zip > Extract All
3. Double-click INSTALL.bat (no admin needed)

WHAT IT DOES
- Mouse (through its LIGHTSPEED receiver): name, battery %, charging / full / discharging,
  asleep, and "wired (charging)" when it's on its USB cable. Logitech HID++ 2.0 (read-only).
- ASTRO A50 (Gen 5): battery % and docked / charging, from the base station's battery question
  documented by the open-source HeadsetControl project. It can't tell whether the headset itself
  is switched on; the A50 X isn't supported.
- Wired keyboard (G713): present and wired.
- Alerts: low battery at 20 % and 10 % (configurable, once per discharge), charging started,
  fully charged, switched to wired / wireless (off by default).
- During a game nothing pops up: alerts wait until you're done, and a low battery shows as a
  quiet line in Hallow Overlay. The mouse isn't asked anything during a game - Hallow Gear only
  listens to what it sends by itself.
- Runs next to Logitech G HUB. Checks every minute (every 5 min on battery power) and when gear
  is plugged in or out.
- Hallow family: Hallow Hub's tray menu, the Hallow accent, light and dark mode, status for
  Hallow Overlay, optional start at sign-in.

GOOD TO KNOW
- Every message it can send is on a fixed read-only list (battery, name, "are you there?").
  Nothing that changes DPI, lighting, profiles, pairing or sound can even be built.
- Notifications are Windows notifications: allow them for Hallow Gear (or Hallow Hub) in
  Settings > System > Notifications.
- Nothing is sent anywhere. History (24 hours) and crash reports stay on this PC.
- Firmware updates are left to G HUB on purpose: Hallow Gear never writes to a device.
- Not supported: other brands' 2.4 GHz dongles (Razer, SteelSeries, Corsair...), the A50 Gen 4
  and the A50 X.

WHAT'S NEW IN 1.1
- Bluetooth batteries: every paired Bluetooth device that tells Windows its battery (earbuds,
  headsets, controllers, keyboards, mice) gets a card with its battery, connected / not
  connected and when it was last seen. Read from what Windows already knows, at most every
  5 minutes and never during a game; nothing is paired, connected or changed. Charging isn't
  shown for Bluetooth devices (they don't report it). Low battery alerts work the same way.
- Settings > Bluetooth devices: switch it off, hide a device, or turn its alerts off.
- Other Logitech devices: a wireless keyboard or a second mouse on any LIGHTSPEED, Unifying
  or Bolt receiver slot, and Logitech devices on their own cable or on Bluetooth, get cards too.

WHAT'S NEW IN 1.0.1
- A mouse that has gone to sleep now shows as "Asleep" (with its last battery reading), not
  "Not connected".

WHAT'S NEW IN 1.0
- The first version: your mouse, headset and keyboard's battery and charging, alerts that wait
  for your game to end, a 24-hour history, and the family link.

CREDITS
Logitech HID++ 2.0: Logitech's documents, Solaar and Linux's hid-logitech-hidpp.
ASTRO A50 Gen 5 battery query: HeadsetControl (https://github.com/Sapd/HeadsetControl).
Icons: Font Awesome Free 7.3.1 by Fonticons, Inc. (https://fontawesome.com), licensed CC BY 4.0 -
see FontAwesome-LICENSE.txt. Not affiliated with Logitech.
