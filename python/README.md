# Joy-Con Toolkit (Python / Kivy)

A cross-platform port of CTCaer's Joy-Con Toolkit: one app for **Windows, macOS, Linux and
Android**, written in Python with a [Kivy](https://kivy.org) interface. It talks to the
controller through [hidapi](https://pypi.org/project/hidapi/) (Bluetooth or USB) on the
desktop, and through Android's USB host API on Android.

Everything the original and the Linux (Mono) build do:

| Screen | What it does |
|---|---|
| Device | The controller in its colors; read and write body, button (and Pro Controller grip) colors; player LEDs; rumble test |
| Backup | SPI flash backup (512KB); restore colors, S/N, user calibration, factory reset of the user calibration, or a full restore, with the original's checks (partial, wrong type, corrupt, another controller) |
| Serial | Change the S/N (the original is backed up inside the controller the first time), restore it |
| Calibration | **Sticks**: guided stick calibration. **Motion**: guided 6-axis calibration, one position or two (turned 180°, cancels the surface's tilt). **Manual**: the original editor (user calibration values, stick device parameters). *Use factory* on each guided tab. |
| Button test | Buttons, sticks (raw and calibrated) and 6-axis sensors live, with the calibration data |
| HD Rumble | **Files**: .bnvib / .jcvib with the equalizer and loops, the two easter egg tunes. **MIDI**: a MIDI file or a MIDI device played on the rumble (two notes at once: one per band) |
| IR Camera | Joy-Con (R): capture (saved as `IRcamera.png`), stream with live settings, every setting of the original, Quick capture |
| NFC | Amiibo / NTAG scan and dump |
| Debug | Custom commands and their replies, the HID device list, the traffic log |

The info section shows the S/N, firmware, MAC, controller type and which calibration is
in use (factory or user); the status bar shows the link health (input reports per second
and the longest wait for one), temperature and battery.

## Install (in a venv)

Python 3.8 or newer. The setup script makes a virtual environment in `python/.venv` with the
app, MIDI device support (mido, python-rtmidi) and the tests:

```
cd python
scripts/setup_venv.sh          # Windows: scripts\setup_venv.bat
.venv/bin/jctool               # Windows: .venv\Scripts\jctool
```

Or by hand: `python3 -m venv .venv`, then `.venv/bin/pip install -e ".[midi,test]"`.
`python main.py` (with the venv active) also starts it.

Options: `--demo` (an emulated controller, no hardware: `--demo pro` or `--demo l` for the
other types), `-d` (write `traffic_log.txt`, like the original's `-d`), `--selftest`.

### Windows

Pair the controller in Settings > Bluetooth (hold its sync button), then start the app.

### macOS

Pair the controller in System Settings > Bluetooth. The first time, macOS may ask to allow
the app under Privacy & Security > Input Monitoring.

### Linux

1. Let your user open the controllers: install the udev rule from this repository,
   `sudo cp ../linux/udev/50-nintendo-switch-controllers.rules /etc/udev/rules.d/`, then
   `sudo udevadm control --reload-rules && sudo udevadm trigger --subsystem-match=hidraw`.
2. Pair the controller with your desktop's Bluetooth settings (or `bluetoothctl`).
3. If values stay empty or commands time out, the kernel's `hid_nintendo` driver is also
   talking to the controller: `sudo systemctl stop joycond` (if installed) and
   `sudo modprobe -r hid_nintendo`, then reconnect it.

The app uses hidapi's `hidraw` backend on Linux (the `hid` one goes through libusb, which
doesn't see Bluetooth devices); `JCTOOL_HID_BACKEND=libusb` switches.
`linux/README.md` has more detail on pairing and permissions (the same for this app).

### Android

Android doesn't let apps open Bluetooth controllers, so on Android the app works with a
**Pro Controller on a USB cable** (or Joy-Cons in the Charging Grip) through a USB OTG
adapter. Plug it in, then press Connect and allow the access in Android's dialog (or open
the app from the "open with" prompt Android shows when you plug it in). Everything else is
the same, except MIDI devices (MIDI files work). Backups, IR captures and the traffic log
are saved in the app's folder (Android/data/org.jctool.jctool/files); after a backup the
app also offers to save a copy anywhere (Downloads, Drive...). Files are opened with
Android's file picker. On a phone the screens scroll; the tabs and the status bar stay.

## Building

Every build starts from the venv. PyInstaller builds only for the system it runs on, so
build each desktop version on its own system (or let GitHub Actions do all four: the
`Python app` workflow in `.github/workflows/python-app.yml` runs the tests and uploads the
Linux, Windows, macOS and Android builds with each run; a `v*` tag publishes them as a
release).

| Target | Command (in `python/`) | Output |
|---|---|---|
| Linux | `.venv/bin/python scripts/build.py desktop` | `dist/JoyConToolkit/` and a `.tar.gz` |
| Windows | `.venv\Scripts\python scripts\build.py desktop` | `dist\JoyConToolkit\JoyConToolkit.exe` and a `.zip` |
| macOS | `.venv/bin/python scripts/build.py desktop` | `dist/Joy-Con Toolkit.app` and a `.zip` |
| Android | `.venv/bin/python scripts/build.py android` (Linux or macOS; on Windows use WSL2) | `bin/jctool-5.2.0-*-debug.apk` |

- Desktop builds use `packaging/jctool.spec` (PyInstaller). The packaged app saves its files
  in `Documents/Joy-Con Toolkit`.
- Android builds use `buildozer.spec` (buildozer / python-for-android). The first build
  downloads the Android SDK and NDK (a few GB, in `~/.buildozer`) and takes a while. It
  needs Java 17 and, on Debian/Ubuntu: `sudo apt install git zip unzip autoconf automake
  libtool pkg-config zlib1g-dev libncurses-dev cmake libffi-dev libssl-dev`.
  `scripts/build.py android release` makes an unsigned release APK/AAB to sign.
- `tools/make_icons.py` redraws the icons in `packaging/`.

## How it's built

| Module | |
|---|---|
| `jctool/core.py` | The protocol: a port of `cli/jc_core.cpp` (CTCaer's `jctool.cpp` through the Linux build), with the same packets, reply checks and retries, and the Linux build's fixes (complete IR frames, auto exposure on the full sensor, a camera that ignores its resolution is detected and set up again, bounded NFC lengths) |
| `jctool/hidio.py` | hidapi with the Windows behaviour the protocol expects (49-byte writes, 0-length reads drop a report), a lock per device, link health, the traffic log, the USB handshake (a controller on USB answers only after it) |
| `jctool/android_usb.py` | Android's USB host API with hidapi's interface: Java finds and opens the device, the reports go through usbdevfs |
| `jctool/ops.py` | The features: info, battery, colors, backups, S/N, HD Rumble files, IR settings and images, NTAG dumps, debug commands |
| `jctool/calibration.py` | Calibration reading/writing and the guided calibrations |
| `jctool/midi.py` | MIDI files and devices on the HD Rumble |
| `jctool/fake.py` | An emulated controller (tests, `--demo`) |
| `jctool/app/storage.py` | Where files go, and file opening / saving (Android's file picker on Android) |
| `jctool/app/` | The Kivy app. Every controller call runs on one device thread; long operations (IR stream, button test, backup, NFC) run the commands queued meanwhile between reports, like the original did from its message loop |
| `jctool/tables.py` | Generated from `jctool/*.h` by `tools/gen_tables.py` |

## Tests

```
.venv/bin/python -m pytest        # the protocol, features, IR camera, calibrations, MIDI and
                                  # the HID backends against emulated devices, and the app's
                                  # self-test
.venv/bin/jctool --selftest       # drives the app's screens (needs a display)
```

## Credits

CTCaer's Joy-Con Toolkit (MIT). The MIDI tab's idea comes from
[Musical-Joycons](https://github.com/sarossilli/MusicalJoycons) by sarossilli (a separate
implementation). NFC starters by Eric Betts; analog stick math by Ryan Juckett (Hypersect).
