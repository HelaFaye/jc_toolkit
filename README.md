# Joy-Con Toolkit

Joy-Con Toolkit changes and inspects official Nintendo Switch Joy-Cons and Pro Controllers:
their colors, SPI flash backups, serial number, stick and motion calibration, HD Rumble, the
IR camera and NFC. This is CTCaer's Joy-Con Toolkit v5.2.0, ported to Python with a
[Kivy](https://kivy.org) interface: one app for **Windows, macOS, Linux and Android**, plus a
command line version, and a **Nintendo Switch** homebrew version.

## Download

Ready-made builds are on the [Releases page](https://github.com/HelaFaye/jc_toolkit/releases)
(and attached to every run of the `Build` workflow under Actions):

| System | File | Start |
|---|---|---|
| Windows (64-bit) | `JoyConToolkit-<version>-windows-x86_64.zip` | Unzip, run `JoyConToolkit\JoyConToolkit.exe` |
| Windows (32-bit) | `JoyConToolkit-<version>-windows-x86.zip` | The same (no MIDI devices: python-rtmidi has no 32-bit build; MIDI files work) |
| macOS (Apple Silicon) | `JoyConToolkit-<version>-macos-arm64.zip` | Unzip, open `Joy-Con Toolkit.app` |
| macOS (Intel) | `JoyConToolkit-<version>-macos-x86_64.zip` | The same |
| Linux (x86_64) | `JoyConToolkit-<version>-linux-x86_64.tar.gz` | Extract, run `JoyConToolkit/install.sh` once (controller access and a menu entry, see [Linux](#linux)), then open it from your menu or run `JoyConToolkit/JoyConToolkit` |
| Linux (64-bit ARM: Raspberry Pi 4/5...) | `JoyConToolkit-<version>-linux-arm64.tar.gz` | The same |
| Android (arm64, armv7) | `jctool-<version>-arm64-v8a_armeabi-v7a-debug.apk` | Install it (allow installing from your browser / file manager) |
| Nintendo Switch (homebrew) | `JoyConToolkit-<version>-switch.nro` | Copy it to `/switch/` on the SD card, open it from the Homebrew Menu (see [Nintendo Switch](#nintendo-switch)) |

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

1. Let your user open the controllers: in the downloaded app's folder, run `./install.sh`.
   It installs the udev rule (`50-nintendo-switch-controllers.rules`, with sudo) and adds
   Joy-Con Toolkit to your applications menu (`./install.sh --menu-only` skips the rule,
   `--uninstall` removes both). From source, install the rule by hand:
   `sudo cp packaging/linux/50-nintendo-switch-controllers.rules /etc/udev/rules.d/`, then
   `sudo udevadm control --reload-rules && sudo udevadm trigger --subsystem-match=hidraw`.
2. Pair the controller with your desktop's Bluetooth settings, or `bluetoothctl`:
   `scan on`, then (holding the sync button) `pair <MAC>`, `trust <MAC>`, `connect <MAC>`.
3. If values stay empty or commands time out, the kernel's `hid_nintendo` driver is also
   talking to the controller: `sudo systemctl stop joycond` (if installed) and
   `sudo modprobe -r hid_nintendo`, then reconnect it (`sudo modprobe hid_nintendo`
   afterwards for normal gamepad use).

On a Wayland desktop the app's window opens through XWayland (Kivy's SDL2 has no Wayland
driver; the app switches `SDL_VIDEODRIVER=wayland` to `x11` itself), so XWayland must be
available, as it is by default on KDE and GNOME.

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

### Nintendo Switch

A homebrew app (`switch/`, C with [libnx](https://github.com/switchbrew/libnx)) for a Switch
running custom firmware (Atmosphere) and the Homebrew Menu, system version 6.0.0 or newer.
It works on the controllers connected to the Switch, through the Switch's own HID services,
so there's nothing to pair: choose a controller from the list.

| Screen | What it does |
|---|---|
| Device info | Type, connection, MAC, S/N, battery, colors, which calibration is in use |
| Colors | RGB editor for the body, buttons and (Pro Controller) grips, the retail and grip presets, your colors (`colors.xml`, the desktop app's format) |
| Back up / Restore | A 512KB SPI backup in the desktop app's format (`spi_<type>_<MAC>.bin`, so backups move between the two), with the same checks; restore the colors, S/N, user calibration, or a full restore |
| Calibration | The factory and user stick and motion calibration, and back to factory |
| Serial number | Change it (the original is kept inside the controller) and restore it |
| Button test | Buttons, sticks and motion sensors, as the Switch reports them |
| HD Rumble | A tester for both bands, and `.jcvib` / `.bnvib` playback |
| IR camera | A live preview (like `jctool-cli`'s) with exposure, gain, LEDs and negative, and 320x240 pictures (`.bmp`) |
| NFC | A tag's UID and type |
| HOME LED, SPI flash viewer, Disconnect | |

Its files are in `/switch/jctool/` on the SD card. If a controller stops answering or
disconnects during an SPI read or write, the operation gives up after a few seconds with
an error, rather than waiting for it; reconnect the controller and try again. The Switch applies the controller's
calibration itself, so stick and motion calibration are done in System Settings >
Controllers and Sensors, and the app offers only the readout and the reset. The Switch
doesn't let homebrew send the controller raw commands, so there's no player LED control,
NFC tag dumps or the easter-egg HD Rumble tunes; the desktop app has those.

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
`Build` workflow (`.github/workflows/build.yml`) runs the tests and builds them all on
GitHub Actions with each push, and a `v*` tag (or a manual run given a tag) publishes them
as a release.

| Target | Command | Output |
|---|---|---|
| Linux | `.venv/bin/python scripts/build.py desktop` | `dist/JoyConToolkit/` and a `.tar.gz` |
| Windows | `.venv\Scripts\python scripts\build.py desktop` | `dist\JoyConToolkit\JoyConToolkit.exe` and a `.zip` |
| macOS | `.venv/bin/python scripts/build.py desktop` | `dist/Joy-Con Toolkit.app` and a `.zip` |
| Android | `.venv/bin/python scripts/build.py android` (Linux or macOS; on Windows use WSL2) | `bin/jctool-<version>-*-debug.apk` |
| Switch | `make -C switch` with [devkitPro](https://devkitpro.org/wiki/Getting_Started)'s devkitA64 and libnx, or `docker run --rm -v "$PWD":/src -w /src devkitpro/devkita64 make -C switch` | `switch/jctool.nro` |

- Desktop builds use `packaging/jctool.spec` (PyInstaller). The packaged app saves its files
  in `Documents/Joy-Con Toolkit`.
- Android builds use `buildozer.spec` (buildozer / python-for-android), from their own
  `.venv-android`. The first build downloads the Android SDK and NDK (a few GB, in
  `~/.buildozer`) and takes a while. It needs Java 17 and, on Debian/Ubuntu: `sudo apt
  install git zip unzip autoconf automake libtool pkg-config zlib1g-dev libncurses-dev cmake
  libffi-dev libssl-dev`. The Bluetooth helper is compiled with the NDK's clang for each of
  the APK's ABIs before buildozer packs the app (`scripts/build.py helper` compiles only it).
  `scripts/build.py android release` makes an unsigned release APK/AAB to sign.
- A 32-bit Python on Windows builds the 32-bit app: `setup_venv.bat` and `build.py` pick
  Kivy 2.3.0 (the last with 32-bit wheels, `packaging/constraints-win32.txt`).
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
| `switch/` | The Switch homebrew. `switch/source/tables.h` is generated from `jctool/tables.py` by `tools/gen_switch_tables.py` |

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
