Joy-Con Toolkit: the command line for PowerPC Macs
===================================================

This is the toolkit's command line (jctool-cli) as Python source, for Macs that the app
can't run on: PowerPC Macs with Mac OS X 10.5 Leopard (the app's toolkit, Kivy, and its
SDL2 need a newer Mac). It has the same menu as on every other system: device info,
battery, colors (with the presets), SPI backup and restore, calibration, S/N, button test,
HD Rumble files, IR camera (in the terminal), NFC.

It hasn't been tested on a PowerPC Mac: it's tested on big-endian Linux (PowerPC Macs are
big-endian) and its macOS HID access on an Intel Mac. Please report what works.

What it needs
-------------
- Mac OS X 10.5 Leopard (its HID access, IOHIDManager, came with 10.5).
- Python 3.9 or newer. MacPorts builds Python 3.10 on PowerPC Leopard:
    1. Install MacPorts for Leopard (https://www.macports.org/install.php, "Older OS").
    2. sudo port install python310
  (Python 3.11's MacPorts build has open bugs on PowerPC Leopard.)
- Nothing else: it reaches the controllers through macOS's IOKit directly (ctypes), so the
  hidapi package isn't needed. MIDI devices need mido and python-rtmidi, which likely
  don't build there; HD Rumble and MIDI files work without them.

Using it
--------
    cd JoyConToolkit-ppc
    ./jctool-cli            (or: PYTHON=/opt/local/bin/python3.10 ./jctool-cli)

Pair the Joy-Con or Pro Controller in System Preferences > Bluetooth first (hold the sync
button until the lights run), or connect a Pro Controller with a USB cable. PowerPC Macs
have Bluetooth 2.0 (or 2.1); if a controller won't pair, use the Pro Controller over USB.

Files (backups, IR pictures) are saved in the folder you run it from.
