# Store assets

Screenshots and graphics for the App Store and Google Play, all from the made-up demo board (never anyone's tasks).
Each set has `raw/` (the app as captured) and `framed/` (the caption from the screenshot plan above it, on the brand
gradient, at the same size: these are the ones to upload).

| Folder | Size | Shots |
| --- | --- | --- |
| `screenshots/iphone-6.9` | 1320 × 2868 | I1 Today, I5 Board |
| `screenshots/ipad-13` | 2064 × 2752 | P1 Today, P2 details, P3 Board, P4 Plan, P5 Reports |
| `screenshots/mac` | 2880 × 1800 | M1 Today, M2 details, M3 Board, M4 Plan, M5 Reports, M6 Today in dark |
| `screenshots/android-phone` | 1080 × 2400 | A1 Today, A2 Later, A4 Board, A5 details |
| `play-feature-graphic.png` | 1024 × 500 | The Play feature graphic (no alpha) |
| `play-icon.png` | 512 × 512 | The Play store icon (32-bit PNG, full bleed: Play rounds it), from `build/icons.py` |

Not captured yet (each needs a tap or a home screen): the iPhone's Later sheet, its scrolled Workstreams and widgets,
Android's notification shade and widgets, the Mac menu bar glance and desktop widgets.

## Making them again

* **Mac:** `store/capture-mac.sh` builds a Debug app and captures each view in a 1440 × 900 window (Screen Recording
  for the terminal; a Retina screen). It runs beside an installed One Thing and leaves it as it was.
* **iPhone and iPad:** a Debug build in a simulator, started with
  `SIMCTL_CHILD_ONETHING_DEMO=1 SIMCTL_CHILD_ONETHING_VIEW=<today|board|plan|reports> [SIMCTL_CHILD_ONETHING_OPEN=1]
  xcrun simctl launch <udid> com.onething.ios`, the status bar set with
  `xcrun simctl status_bar <udid> override --time 9:41 --batteryState charged --batteryLevel 100 --cellularBars 4`, then
  `xcrun simctl io <udid> screenshot`. Use an iOS 27 simulator (iPhone 17 Pro Max, iPad Pro 13-inch): the iOS 26.3
  ones draw every emoji as a box.
* **Android:** a debug build on a Pixel emulator, `adb shell am start -n dev.onething/.app.MainActivity --ez demo true`,
  then `adb exec-out screencap -p`. Its demo banner shows in the frame.
* **The Play icon:** `python3 build/icons.py` writes it with every other icon (pip install cairosvg pillow).
* **Framing and the graphic:** `/usr/bin/python3 store/frame.py` and `/usr/bin/python3 store/graphic.py` (Pillow, and
  the SF Pro font macOS ships; `/usr/bin/python3 -m pip install --user pillow` if it's missing).
