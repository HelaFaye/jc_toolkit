# Joy-Con Toolkit for Linux

A native Linux build of CTCaer's Joy-Con Toolkit v5.2.0, including the merged
community changes: controller priority selection, pseudo-third-party controllers,
`-l` HID listing, pastel color presets, and IR Pointing/Cluster modes.

It is a C# port that runs on Mono:

| Part | Source | How it was ported |
|---|---|---|
| Protocol code (`jctool.cpp`) | `mono/src/JcTool.cs`, `Connection.cs` | Line by line, in C# `unsafe` code, so every packet layout, offset, retry count and `goto` is unchanged |
| Main window logic (`FormJoy.h`) | `mono/src/FormJoy.cs` | Line by line |
| Main window layout | `mono/src/FormJoy.Designer.cs` | Generated from `FormJoy.h` by `mono/tools/cppcli_designer_to_cs.py` |
| Color picker (`jc_colorpicker/`) | built from the original C# sources | Unchanged apart from portable file paths |
| Rumble tunes, CRC and IR palette tables | `mono/src/Tables.cs` | Generated from the `.h` files by `mono/tools/gen_tables.py` |
| hidapi | system `libhidapi-hidraw.so.0` via `mono/src/Native.cs` | Reproduces what the Windows hidapi did on the wire (49-byte write padding, 0-length reads) |

Linux-only changes, all kept small:
- Missing Windows fonts (Segoe UI, Lucida Console) are replaced with the closest
  installed fonts at the same size.
- Text boxes are re-wrapped when a panel is shown (a Mono layout bug).
- The app works in its own folder, like on Windows, so backups, `IRcamera.png`,
  `colors.xml` and `traffic_log.txt` are written next to `jctool.exe`.
- The controller handle is closed before each reconnect check instead of leaking.
- NFC reply lengths are bounds-checked.
- The IR camera transfer runs on its own thread and hands frames to the window,
  instead of pausing for window events after every fragment. On Wayland
  (XWayland) those pauses can take a second each, which stalled the camera.

Everything that doesn't need a controller has been tested in a Linux container
with an emulated controller (`--selftest`, `--demo`). It has **not yet been
tested with a real Joy-Con or Pro Controller**; the steps below walk you
through that safely, read-only first.

---

## Step 1 — Install the dependencies

Debian / Ubuntu / Mint / Pop!_OS:

```sh
sudo apt update
sudo apt install mono-complete libgdiplus libhidapi-hidraw0 bluez python3 make git fonts-liberation2 fonts-dejavu
```

Fedora:

```sh
sudo dnf install mono-complete libgdiplus hidapi bluez python3 make git liberation-sans-fonts dejavu-sans-mono-fonts
```

Arch / Manjaro:

```sh
sudo pacman -S mono libgdiplus hidapi bluez bluez-utils python make git ttf-liberation ttf-dejavu
```

Check that the tools are there:

```sh
mono --version | head -1
mcs --version
resgen 2>&1 | head -1
ls /usr/lib*/libhidapi-hidraw.so.0 /usr/lib/*/libhidapi-hidraw.so.0 2>/dev/null
```

You should see a Mono version (6.8 or newer), a compiler version,
`Mono Resource Generator version …`, and one path to `libhidapi-hidraw.so.0`.

## Step 2 — Get the code and build

```sh
git clone https://github.com/HelaFaye/jc_toolkit.git
cd jc_toolkit
git checkout Linux
cd linux/mono
make
```

Expected: two `Compilation succeeded` lines (warnings are fine), and these files:

```sh
ls build/jctool.exe build/jcColor.dll jcprobe.exe
```

## Step 3 — Self-test (no controller needed)

```sh
make selftest
```

Expected: every line starts with `PASS` and it ends with `All checks passed.`
It checks that hidapi loads, the packet structures have the exact byte layout
of the C++ ones, the CRC and stick encoding, and every read/write command
against an emulated Joy-Con (L), Joy-Con (R) and Pro Controller.

On a desktop session it also opens the real main window briefly (it may flash)
and checks what it shows, a full 512KB SPI dump, the IR camera, the HD rumble
player and the debug console. To run it without windows popping up:

```sh
sudo apt install xvfb   # or: sudo dnf install xorg-x11-server-Xvfb / sudo pacman -S xorg-server-xvfb
xvfb-run -a -s "-screen 0 1920x1200x24" mono build/jctool.exe --selftest
```

## Step 4 — Try the app without a controller

```sh
mono build/jctool.exe --demo pro    # or: --demo l / --demo r
```

This is the full app talking to an emulated controller. Click around: colors,
Backup SPI (writes a real file), Restore SPI, More… → Playground testing →
Turn on (live button test), More… → Edit Calibration → Refresh All, IR Camera
(Joy-Con R) → Capture. Writes only change the emulated controller.

## Step 5 — Let your user open the controllers

```sh
sudo cp ../udev/50-nintendo-switch-controllers.rules /etc/udev/rules.d/
sudo udevadm control --reload-rules
sudo udevadm trigger --subsystem-match=hidraw   # only re-applies permissions to HID devices
```

## Step 6 — Pair the controller

Hold the small sync button on the controller until the lights run, then:

```sh
bluetoothctl
```

Inside `bluetoothctl`:

```
power on
scan on
```

Wait for `Joy-Con (L)`, `Joy-Con (R)` or `Pro Controller` and note its
address, then (replace the address):

```
pair 98:B6:E9:XX:XX:XX
trust 98:B6:E9:XX:XX:XX
connect 98:B6:E9:XX:XX:XX
scan off
quit
```

Check that a device node appeared and that you can open it:

```sh
ls -l /dev/hidraw*
getfacl /dev/hidraw* 2>/dev/null | grep -B3 "user:$USER"
```

## Step 7 — Read-only check from the command line

```sh
mono jcprobe.exe -l
```

Your controller should be listed with vendor `0x057e` and product `0x2006`
(Joy-Con L), `0x2007` (Joy-Con R) or `0x2009` (Pro Controller). Then:

```sh
mono jcprobe.exe
```

Expected: device type, firmware, MAC, S/N (Joy-Cons) and body/button colors.
This only reads from the controller.

If it can't connect, or times out, see **Troubleshooting** below
(usually permissions or the `hid_nintendo` driver).

## Step 8 — Run Joy-Con Toolkit

```sh
make run
```

or, with the original command line options:

```sh
mono build/jctool.exe -l    # show every HID device first
mono build/jctool.exe -d    # log all controller traffic to build/traffic_log.txt
mono build/jctool.exe -f    # don't re-check the connection on every action
```

## Step 9 — Test the features, safest first

Read-only (nothing is written to the controller):

1. The main window shows S/N, firmware, MAC, type, battery, temperature and a
   colored preview of your controller.
2. More… → Playground testing → **Turn on**: buttons, sticks and 6-axis update
   live. Turn it off again.
3. More… → Edit Calibration → **Refresh All**: your calibration values load.
4. Joy-Con (R) only: IR Camera → **Capture** saves `build/IRcamera.png`; then
   try **Stream** and **Stop**.
5. Joy-Con (R) or Pro Controller: Playground testing → NFC **Scan** and touch
   an amiibo or NFC tag.

Make a backup before any write:

6. **Backup SPI** (takes a few minutes). Copy `build/spi_*.bin` somewhere safe.

Writes (each one can be undone from your backup):

7. **Body & Buttons Color** → pick a color → OK → **Write Colors**, then
   **Restore SPI** → Load Backup → pick your backup → Restore Color.
8. HD Rumble Player → Load a `.bnvib` file → Play. (Rumble only, no writes.)

Leave S/N changes, calibration writes and Full Restore until everything above
works. They behave exactly like on Windows.

## Command-line tool (jctool-cli)

A small interactive menu for quick checks without the window: pick a
controller, then read device info, battery, and calibration, set the player
LEDs, or test the rumble. It only reads from the controller's memory.

```sh
cd ~/jc_toolkit
sudo apt install g++ libhidapi-dev   # Fedora: gcc-c++ hidapi-devel; Arch: gcc hidapi
make -f Makefile.linux
./jctool-cli
```

(`cmake -B build-cli && cmake --build build-cli` works too.)

## IR camera options (Linux only)

A full-resolution (240x320) frame is 256 fragments, and the Joy-Con sends about
one every 15-30ms over Bluetooth, so a frame takes 5-9 seconds. Capture adjusts
the exposure at the start of the second frame and saves the next complete frame
(about 30 seconds at 240x320). Lower resolutions are much faster (120x160 is a
quarter of the data).

**Quick capture** (IR Camera Settings, under Auto Exposure; off by default):
Capture adjusts the exposure during the first frame and saves the second
(about 19 seconds at 240x320).

`JCTOOL_TIMESTAMPS=1` with `-d` adds millisecond timestamps to `traffic_log.txt`.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `libhidapi-hidraw.so.0 was not found` | Install `libhidapi-hidraw0` (Debian/Ubuntu) or `hidapi` (Fedora/Arch). |
| `The device is not paired or the device was disconnected!` but `ls /dev/hidraw*` shows it | Permissions: redo Step 5 and reconnect, or test once with `sudo mono build/jctool.exe` to confirm. |
| Connects, but values stay empty, commands time out, or IR/NFC fail | The kernel's `hid_nintendo` driver is also talking to the controller. Run `sudo systemctl stop joycond` (if installed) and `sudo modprobe -r hid_nintendo`, then reconnect the controller. `sudo modprobe hid_nintendo` afterwards restores normal gamepad use. |
| A third-party controller isn't found | Run `mono jcprobe.exe -l` and add a udev line for its IDs (see the rules file). It is offered automatically (with a confirmation) when no Nintendo controller is connected. |
| Text is cut off in places | Install `fonts-liberation2` (or your distro's Liberation fonts) and `fonts-dejavu`. |
| Something behaves differently from Windows | Run with `-d`, reproduce it, and keep `build/traffic_log.txt`; it uses the same format as the Windows build's log, so the two can be compared. |

## Rebuilding after changing the original C++ project

`FormJoy.Designer.cs` and `Tables.cs` are generated from `jctool/FormJoy.h`,
`tune.h`, `ir_sensor.h` and `luts.h`:

```sh
make regen && make
```

The event handlers and protocol code in `src/` are hand-maintained ports; mirror
C++ changes there by hand.
