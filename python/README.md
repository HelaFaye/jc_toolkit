# Joy-Con Toolkit (Python / Kivy)

A cross-platform port of CTCaer's Joy-Con Toolkit: one app for **Windows, macOS and
Linux**, written in Python with a [Kivy](https://kivy.org) interface. It talks to the
controller through [hidapi](https://pypi.org/project/hidapi/) (Bluetooth or USB).

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

## Install

Python 3.8 or newer.

```
cd python
pip install .              # the app and its dependencies (Kivy, hidapi)
pip install ".[midi]"      # also MIDI devices (mido, python-rtmidi)
jctool                     # or: python -m jctool.app
```

Without installing: `pip install kivy hidapi mido python-rtmidi`, then
`python -m jctool.app` from this folder.

Options: `--demo` (an emulated controller, no hardware: `--demo pro` or `--demo l` for the
other types), `-d` (write `traffic_log.txt`, like the original's `-d`), `--selftest`.

### Windows

Pair the controller in Settings > Bluetooth (hold its sync button), then start the app.
hidapi ships Windows wheels; nothing else is needed.

### macOS

Pair the controller in System Settings > Bluetooth. The first time, macOS may ask to allow
the app (your terminal or Python) under Privacy & Security > Input Monitoring.

### Linux

1. Let your user open the controllers: install the udev rule from this repository,
   `sudo cp ../linux/udev/50-nintendo-switch-controllers.rules /etc/udev/rules.d/`, then
   `sudo udevadm control --reload-rules && sudo udevadm trigger --subsystem-match=hidraw`.
2. Pair the controller with your desktop's Bluetooth settings (or `bluetoothctl`).
3. If values stay empty or commands time out, the kernel's `hid_nintendo` driver is also
   talking to the controller: `sudo systemctl stop joycond` (if installed) and
   `sudo modprobe -r hid_nintendo`, then reconnect it.

`linux/README.md` has more detail on pairing and permissions (the same for this app).

## How it's built

| Module | |
|---|---|
| `jctool/core.py` | The protocol: a port of `cli/jc_core.cpp` (CTCaer's `jctool.cpp` through the Linux build), with the same packets, reply checks and retries, and the Linux build's fixes (complete IR frames, auto exposure on the full sensor, a camera that ignores its resolution is detected and set up again, bounded NFC lengths) |
| `jctool/hidio.py` | hidapi with the Windows behaviour the protocol expects (49-byte writes, 0-length reads drop a report), a lock per device, link health, the traffic log |
| `jctool/ops.py` | The features: info, battery, colors, backups, S/N, HD Rumble files, IR settings and images, NTAG dumps, debug commands |
| `jctool/calibration.py` | Calibration reading/writing and the guided calibrations |
| `jctool/midi.py` | MIDI files and devices on the HD Rumble |
| `jctool/fake.py` | An emulated controller (tests, `--demo`) |
| `jctool/app/` | The Kivy app. Every controller call runs on one device thread; long operations (IR stream, button test, backup, NFC) run the commands queued meanwhile between reports, like the original did from its message loop |
| `jctool/tables.py` | Generated from `jctool/*.h` by `tools/gen_tables.py` |

## Tests

```
pip install pytest
python -m pytest            # the protocol, features, IR camera, calibrations and MIDI
                            # against the emulated controller, and the app's self-test
python -m jctool.app --selftest   # drives the app's screens (needs a display)
```

## Credits

CTCaer's Joy-Con Toolkit (MIT). The MIDI tab's idea comes from
[Musical-Joycons](https://github.com/sarossilli/MusicalJoycons) by sarossilli (a separate
implementation). NFC starters by Eric Betts; analog stick math by Ryan Juckett (Hypersect).
