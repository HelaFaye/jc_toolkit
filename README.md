# Joy-Con Toolkit

Joy-Con Toolkit changes and inspects official Nintendo Switch Joy-Cons and Pro Controllers:
their colors, SPI flash backups, serial number, stick and motion calibration, HD Rumble, the
IR camera and NFC. This is CTCaer's Joy-Con Toolkit v5.2.0, ported to Python with a
[Kivy](https://kivy.org) interface: one app for **Windows, macOS, Linux and Android**, plus a
command line version.

## Download

Ready-made builds are on the [Releases page](https://github.com/HelaFaye/jc_toolkit/releases)
(and attached to every run of the `Build` workflow under Actions):

| System | File | Start |
|---|---|---|
| Windows (64-bit) | `JoyConToolkit-<version>-windows-x86_64.zip` | Unzip, run `JoyConToolkit\JoyConToolkit.exe` |
| macOS (Apple Silicon) | `JoyConToolkit-<version>-macos-arm64.zip` | Unzip, open `Joy-Con Toolkit.app` |
| Linux (x86_64) | `JoyConToolkit-<version>-linux-x86_64.tar.gz` | Extract, run `JoyConToolkit/JoyConToolkit` (see [Linux](#linux) for permissions) |
| Android (arm64, armv7) | `jctool-<version>-arm64-v8a_armeabi-v7a-debug.apk` | Install it (allow installing from your browser / file manager) |

The desktop builds include the command line next to the app: `jctool-cli` (`jctool-cli.exe`
on Windows; `Joy-Con Toolkit.app/Contents/MacOS/jctool-cli` on macOS).

The builds aren't signed: Windows SmartScreen may say "Windows protected your PC" (More
info, Run anyway), and macOS may say the app can't be checked (right-click it, Open, or
`xattr -dr com.apple.quarantine "Joy-Con Toolkit.app"`).

## Features

| Screen | What it does |
|---|---|
| Device | The controller in its colors; read and write body, button (and Pro Controller grip) colors, with the retail and pastel presets and your own saved colors; player LEDs; rumble test |
| Backup | SPI flash backup (512KB); restore colors, S/N, user calibration, factory reset of the user calibration, or a full restore, with the original's checks (partial, wrong type, corrupt, another controller) |
| Serial | Change the S/N (the original is backed up inside the controller the first time), restore it |
| Calibration | **Sticks**: guided stick calibration. **Motion**: guided 6-axis calibration, one position or two (turned 180°, cancels the surface's tilt). **Manual**: the original editor (user calibration values, stick device parameters). *Use factory* reverts each one |
| Button test | Buttons, sticks (raw and calibrated) and 6-axis sensors live, with the calibration data |
| HD Rumble | **Files**: .bnvib / .jcvib with the equalizer and loops, the two easter egg tunes. **MIDI**: a MIDI file or a MIDI device played on the rumble (two notes at once: one per band) |
| IR Camera | Joy-Con (R): capture (saved as `IRcamera.png`), stream with live settings, every setting of the original (resolution, Capture / Pointing / Clustering, LEDs and intensity, flashlight, strobe, external filter, selfie, exposure, auto exposure, gain, de-noise, custom registers), Quick capture |
| NFC | Amiibo / NTAG scan and dump; MIFARE tags: the UID (their contents can't be read) |
| Debug | Custom commands and their replies (SPI subcommands in expert mode), the HID device list, the traffic log, About |

The info section shows the S/N, firmware, MAC, controller type and which calibration is in
use; the status bar has Connect / Disconnect, the controller priority (which type to pick
when several are connected), the link health (input reports per second and the longest
wait), temperature (click: °C / °F) and battery.

### Command line

`jctool-cli` has the same features in an interactive menu (the C++ Linux CLI's, item for
item): device info, battery, LEDs, rumble, calibration readout and editing with the guided
stick and motion calibrations, colors, backup and restore, S/N, button test, HD Rumble files,
tunes and MIDI files, the IR camera with a 24-bit color preview in the terminal, NFC, debug
commands, the HID listing and disconnect. The menu reads standard input, so it can be
scripted: `printf '2\n0\n' | jctool-cli` prints the device info.

Options (both programs): `-d` writes `traffic_log.txt`, `--demo [l|r|pro]` uses an emulated
controller (no hardware). The CLI also has `-l` (list HID devices), `-p l|r|pro` (controller
priority) and `--expert`; the app has `--selftest`.

### Differences from the original

- The color dialog's eyedropper (picking a color from anywhere on the screen) isn't
  there: Kivy can only read its own window. Hex values, the color wheel and the presets are.
- `-f` (don't check the connection again) isn't needed: the app doesn't poll the
  connection, it shows the link health instead.
- Additions: guided stick and motion calibration, MIDI on the HD Rumble, link health,
  calibration status, the command line on every system, and Android.

## Using it

### Windows

Pair the controller in Settings > Bluetooth (hold the sync button on its rail until the
lights run), then start the app.

### macOS

Pair the controller in System Settings > Bluetooth. The first time, macOS may ask to allow
the app under Privacy & Security > Input Monitoring.

### Linux

1. Let your user open the controllers: install the udev rule,
   `sudo cp packaging/linux/50-nintendo-switch-controllers.rules /etc/udev/rules.d/`, then
   `sudo udevadm control --reload-rules && sudo udevadm trigger --subsystem-match=hidraw`.
2. Pair the controller with your desktop's Bluetooth settings, or `bluetoothctl`:
   `scan on`, then (holding the sync button) `pair <MAC>`, `trust <MAC>`, `connect <MAC>`.
3. If values stay empty or commands time out, the kernel's `hid_nintendo` driver is also
   talking to the controller: `sudo systemctl stop joycond` (if installed) and
   `sudo modprobe -r hid_nintendo`, then reconnect it (`sudo modprobe hid_nintendo`
   afterwards for normal gamepad use).

The app uses hidapi's `hidraw` backend (the `hid` one goes through libusb, which doesn't see
Bluetooth controllers); `JCTOOL_HID_BACKEND=libusb` switches.

### Android

**Bluetooth (needs root: Magisk, KernelSU...).** Pair the Joy-Con or Pro Controller in
Android's Bluetooth settings, then press Connect and allow the app root access. Android's
Bluetooth service connects the controller and the kernel makes a `/dev/hidraw` node for
it; only root can open that, so the app runs its native helper (`jctool-hidraw`, built with
the NDK from `native/jctool_hidraw.c`) through `su`. The helper opens the node and relays
the reports to the app over a pipe. If the kernel's `hid_nintendo` driver has the
controller, the helper moves it to `hid-generic` while the app uses it and gives it back
after. Without root this isn't possible: Android's Bluetooth stack doesn't let apps open a
controller's HID channels.

**USB (no root).** A Pro Controller on a USB cable (or Joy-Cons in the Charging Grip)
through a USB OTG adapter: plug it in, press Connect and allow the access in Android's
dialog (or open the app from the prompt Android shows when you plug it in).

Everything else works the same, except MIDI devices (MIDI files work). Backups, IR captures
and the traffic log are saved in the app's folder (Android/data/org.jctool.jctool/files);
after a backup the app also offers to save a copy anywhere (Downloads, Drive...). Files are
opened with Android's file picker. On a phone the screens scroll; the tabs and the status
bar stay. The Debug tab's HID listing says why Bluetooth found nothing (e.g. root denied).

## Running from source

Python 3.8 or newer. The setup script makes a virtual environment in `.venv` with the app,
MIDI device support (mido, python-rtmidi) and the tests:

```
scripts/setup_venv.sh          # Windows: scripts\setup_venv.bat   (PYTHON=python3.12 picks another Python)
.venv/bin/jctool               # Windows: .venv\Scripts\jctool
.venv/bin/jctool-cli           # the command line
```

Or by hand: `python3 -m venv .venv`, then `.venv/bin/pip install -e ".[midi,test]"`.

## Building

Every build starts from the venv. PyInstaller builds only for the system it runs on; the
`Build` workflow (`.github/workflows/build.yml`) runs the tests and builds all four on
GitHub Actions with each push, and a `v*` tag publishes them as a release.

| Target | Command | Output |
|---|---|---|
| Linux | `.venv/bin/python scripts/build.py desktop` | `dist/JoyConToolkit/` and a `.tar.gz` |
| Windows | `.venv\Scripts\python scripts\build.py desktop` | `dist\JoyConToolkit\JoyConToolkit.exe` and a `.zip` |
| macOS | `.venv/bin/python scripts/build.py desktop` | `dist/Joy-Con Toolkit.app` and a `.zip` |
| Android | `.venv/bin/python scripts/build.py android` (Linux or macOS; on Windows use WSL2) | `bin/jctool-<version>-*-debug.apk` |

- Desktop builds use `packaging/jctool.spec` (PyInstaller). The packaged app saves its files
  in `Documents/Joy-Con Toolkit`.
- Android builds use `buildozer.spec` (buildozer / python-for-android), from their own
  `.venv-android`. The first build downloads the Android SDK and NDK (a few GB, in
  `~/.buildozer`) and takes a while. It needs Java 17 and, on Debian/Ubuntu: `sudo apt
  install git zip unzip autoconf automake libtool pkg-config zlib1g-dev libncurses-dev cmake
  libffi-dev libssl-dev`. The Bluetooth helper is compiled with the NDK's clang for each of
  the APK's ABIs before buildozer packs the app (`scripts/build.py helper` compiles only it).
  `scripts/build.py android release` makes an unsigned release APK/AAB to sign.
- `tools/make_icons.py` redraws the icons; `tools/gen_tables.py` regenerates
  `jctool/tables.py` from the original's C headers in `tools/headers`.

## Tests

```
.venv/bin/python -m pytest        # protocol, features, IR camera, calibrations, MIDI, NFC, the CLI
                                  # and the HID backends against emulated devices (the Bluetooth
                                  # helper is compiled with the system's C compiler and run over a
                                  # fake /sys and /dev), and the app's self-test
.venv/bin/jctool --selftest       # drives the app's screens (needs a display)
```

## How it's built

| Module | |
|---|---|
| `jctool/core.py` | The protocol: a port of CTCaer's `jctool.cpp` (through the Linux builds), with the same packets, reply checks and retries, and the Linux builds' fixes (complete IR frames, auto exposure on the full sensor, a camera that ignores its resolution is detected and set up again, bounded NFC lengths) |
| `jctool/hidio.py` | hidapi with the Windows behaviour the protocol expects (49-byte writes, 0-length reads drop a report), a lock per device, link health, the traffic log, the USB handshake |
| `jctool/android_hid.py`, `android_usb.py`, `hidraw_bridge.py`, `native/jctool_hidraw.c` | Android: Bluetooth through the root hidraw bridge, USB through the USB host API |
| `jctool/ops.py`, `calibration.py`, `midi.py`, `presets.py` | The features |
| `jctool/fake.py` | An emulated controller (tests, `--demo`) |
| `jctool/app/` | The Kivy app. Every controller call runs on one device thread; long operations run the commands queued meanwhile between reports, like the original did from its message loop |
| `jctool/cli.py` | The command line |
| `jctool/tables.py` | Generated from `tools/headers` by `tools/gen_tables.py` |

## Credits

**Joy-Con Toolkit** by [CTCaer](https://github.com/CTCaer/jc_toolkit) (MIT).

Community changes merged into this version:

* [linkoid](https://github.com/linkoid/jc_toolkit): `-l` command-line option to list HID device info, and pseudo-third-party controller support.
* [fienestar](https://github.com/fienestar/jc_toolkit/tree/pr): controller priority selection.
* [mas1850 (M.A. Schneider)](https://github.com/mas1850/jc_toolkit): pastel color presets, Pro Controller connection delay.
* [arpruss (Alexander Pruss)](https://github.com/arpruss/jc_toolkit): IR camera Pointing (DPD) and Clustering modes, and the IR image border flicker fix.

Other code and research this project builds on:

* [dekuNukem and contributors](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering): Joy-Con protocol reverse engineering.
* [shinyquagsire23](https://github.com/shinyquagsire23/HID-Joy-Con-Whispering): hidapi usage on Linux.
* [Eric Betts (bettse)](https://github.com/bettse): NFC communication starters.
* [Ryan Juckett (Hypersect)](http://blog.hypersect.com/interpreting-analog-sticks/): analog stick dead zone calculation.
* [shuffle2](https://github.com/shuffle2/nxpad): Windows HID reference.
* [Musical-Joycons](https://github.com/sarossilli/MusicalJoycons) (sarossilli): the idea for the MIDI tab (a separate implementation).

The original Windows (C++/CLI) and Linux (Mono, C++ CLI) versions are in this repository's
history before the Python port replaced them. Original forum thread:
https://gbatemp.net/threads/tool-joy-con-toolkit-v1-0.478560/
