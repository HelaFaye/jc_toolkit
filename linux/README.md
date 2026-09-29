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

A text-menu version of Joy-Con Toolkit with the same features as the window, for use
in a terminal or over SSH. It is written in C++ and needs only hidapi (no Mono).

```sh
cd ~/jc_toolkit
sudo apt install g++ libhidapi-dev   # Fedora: gcc-c++ hidapi-devel; Arch: gcc hidapi
make -f Makefile.linux
./jctool-cli        # -l: list all HID devices first, -d: log traffic to traffic_log.txt
```

(`cmake -B build-cli && cmake --build build-cli` works too.)

It connects to the controller on start. Menu:

| # | Feature |
|---|---|
| 1-6 | Select device, device info (FW, MAC, S/N, battery, temperature, colors), battery, player LEDs, rumble test, read calibration |
| 7 | Colors: view and change body/buttons (and Pro Controller grips) |
| 8, 9 | Backup the SPI flash; restore colors, S/N, user calibration, factory-reset user calibration, or a full restore (with the window's checks) |
| 10 | Change the S/N (Joy-Con), or restore it from the backup inside the controller |
| 11 | Edit user stick / 6-axis calibration (with the guided stick calibration) and the stick device parameters |
| 12 | Live button, stick and 6-axis test |
| 13 | HD Rumble player (.bnvib, .jcvib, with equalizer and loops) and the two tunes |
| 14 | IR camera (Joy-Con R): every setting of the window, Capture (IRcamera.png), Stream (IRstream.png, with live exposure/gain/register changes), Quick capture, and a color preview in the terminal |
| 15 | NFC / amiibo scan with NTAG dump |
| 16-18 | Debug custom command, HID listing, disconnect the controller |

Enter stops a long operation (SPI backup, button test, stream, NFC scan). Every write asks
for confirmation first, like the window.

The protocol code (`cli/jc_core.cpp`) is ported from the Linux build's `JcTool.cs`, with
the same Linux fixes. `cli/test/run_tests.sh` tests every menu against an emulated
controller (no hardware needed).

## Calibration (Linux additions)

**Status:** the info section's third row shows `Factory`, or `User (...)` with what a user
calibration overrides (stick, motion). Hover for details; click it or **Calibrate..** to
open the Calibration screen. The CLI's device info (2) shows the same.

**Calibration screen** (More... > Edit Calibration), one tab per mode:

| Tab | What it does |
|---|---|
| Sticks | Guided stick calibration (Pro Controller: pick the stick). Click Start, let go of the stick (the circle shrinks while it holds still), then rotate it slowly along its edge, pushed all the way, until the whole ring lights up. Finish, then Save. |
| Motion | Guided motion (6-axis) calibration, for drift. Lay the controller flat and still, buttons up (the bubble shows the tilt), click Start and don't touch it for ~2 seconds (it starts over if it moves). Save writes the measured gyro and accelerometer offsets with the factory sensitivities. **Two positions** (more accurate): after the first measurement, turn the controller 180° on the same surface, keeping it flat (the arc shows the turn, measured by the gyro). Once it reads 180°, let go and click **Confirm turn** for the second measurement. The surface's tilt reverses between the two and averages out of the accelerometer offsets. |
| Manual | The original editor: user calibration values, stick device parameters, Refresh All / Write Cal. |

The wizards read the controller only while measuring, and Save (after a confirmation)
writes only what was measured: the other stick and the other calibration stay as they
are. **Use factory** (Sticks: the chosen stick; Motion) erases that user calibration, so
the controller goes back to its factory calibration. The CLI offers the guided stick calibration in menu 11 when you set a stick user
calibration.

## HD Rumble Player: MIDI (Linux addition)

The HD Rumble Player has two tabs: **Files** (the original player) and **MIDI**, which
plays music on the rumble, like [Musical-Joycons](https://github.com/sarossilli/MusicalJoycons)
by sarossilli (the idea comes from it; this is a separate implementation).

- **File:** Load MIDI.. (`.mid`), pick a part or "All parts (no drums)", Play / Stop.
- **Device:** a MIDI keyboard or other MIDI hardware. Scan lists ALSA's raw MIDI devices
  (`/dev/snd/midiC*D*`); pick one and a channel (or all), then Listen / Stop. The sustain
  pedal works. Opening the device needs access to it (usually the `audio` group or your
  desktop session). A software MIDI source (a DAW, a virtual keyboard) can reach it through
  the `snd-virmidi` kernel module (`sudo modprobe snd-virmidi`): connect the source to a
  "Virtual Raw MIDI" port (e.g. with `aconnect`) and pick that device here.

HD Rumble has two bands, so two notes sound at once: the highest held note on the high band
(octave-shifted into 400-1252 Hz) and, with "Lowest note on the low band", the lowest one on
the low band (100-626 Hz). Velocity and Volume set the strength. The bars show each band's note.

## Link health (status bar, Linux only)

Left of the temperature, updated every second from the app's own traffic (it doesn't
poll the controller): input reports received per second and the longest wait for one
while the app was reading, e.g. `66/s 17ms`. About 60-70/s and 15-30ms is normal over
Bluetooth. Orange: a wait over 100ms. Red (`Link: 3 err`): reads or writes failed, e.g.
the controller disconnected. `Link idle` means the app isn't talking to the controller.
Hover for details and totals since start.

## IR camera options (Linux only)

A full-resolution (240x320) frame is 256 fragments, and the Joy-Con sends about
one every 15-30ms over Bluetooth, so a frame takes 5-9 seconds. Capture adjusts
the exposure at the start of the second frame and saves the next complete frame
(about 30 seconds at 240x320). Lower resolutions are much faster (120x160 is a
quarter of the data).

**Quick capture** (IR Camera Settings, under Auto Exposure; off by default):
Capture skips the auto exposure adjustment, uses the Exposure value as set, and
saves the second frame (about 19 seconds at 240x320).

While the camera runs, other commands (HD rumble, battery, ...) still work: the camera
pauses while one runs, like on Windows.

If the camera doesn't apply the settings (rarely, it keeps the previous
resolution, which shows as vertical bars), a capture sets it up again and
retries once, or says so. A stream checks its first frames the same way and
restarts once.

`JCTOOL_TIMESTAMPS=1` with `-d` adds millisecond timestamps to `traffic_log.txt`.

Experiments (off by default; set when starting the app):

| Variable | Effect |
|---|---|
| `JCTOOL_IR_SKIP_LEFTOVER=1` | The first frame of a capture is a leftover from before and is thrown away. Try to make the Joy-Con skip it (saves a frame, ~10s at 240x320). The `-d` log says whether it worked. |
| `JCTOOL_IR_PATIENT_SETUP=1` | Wait ~300ms instead of ~135ms for the Joy-Con's answer to each camera setup command before sending it again. |

## Troubleshooting

| Symptom | Fix |
|---|---|
| `libhidapi-hidraw.so.0 was not found` | Install `libhidapi-hidraw0` (Debian/Ubuntu) or `hidapi` (Fedora/Arch). |
| `The device is not paired or the device was disconnected!` but `ls /dev/hidraw*` shows it | Permissions: redo Step 5 and reconnect, or test once with `sudo mono build/jctool.exe` to confirm. |
| Connects, but values stay empty, commands time out, or IR/NFC fail | The kernel's `hid_nintendo` driver is also talking to the controller. Run `sudo systemctl stop joycond` (if installed) and `sudo modprobe -r hid_nintendo`, then reconnect the controller. `sudo modprobe hid_nintendo` afterwards restores normal gamepad use. |
| A third-party controller isn't found | Run `mono jcprobe.exe -l` and add a udev line for its IDs (see the rules file). It is offered automatically (with a confirmation) when no Nintendo controller is connected. |
| Dialog buttons have no text | Mono's default dialog font falls back to your desktop's font; with Noto Sans the labels don't fit Mono's buttons and aren't drawn. The app now uses Liberation Sans (or Arimo / DejaVu Sans) for them: install `ttf-liberation` (Step 1). Dialogs keep your desktop's colors; `JCTOOL_CLASSIC_COLORS=1` uses Mono's light ones. `mono build/jctool.exe --diag-colors` shows what was chosen. |
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
