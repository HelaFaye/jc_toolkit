# Joy-Con Toolkit

![](http://ctcaer.com/wii/jc6.png)
This image is for reference.

## Information

Joy-Con Toolkit is a downloadable program that allows users to modify the digital appearance of their official Nintendo Switch Joy-Con and Pro Controllers via Bluetooth connection

Additional features include: IR camera captures, NFC reading, saving custom colors, backing up SPI flash, calibrating analog sticks, and more

## v5.2.0 Changelog

Near Infrared Camera:
* Add live configuration when streaming
* Add many new IR settings (de-noise, led intensity, strobe lights, etc)
* Change custom IR sensor registers
* Auto exposure mode (This is done manually, so it's experimental. Expect bugs).

User Calibration Editing:
* Add Accelerometer/Gyroscope user calibration editing
* Add Stick device factory parameters editing. Helps when you have drifting issues, that can't be fixed by calibrating the stick. Just raise the deadzone value.

Debugging / Troubleshooting:
* Add 2 command line options:
* -d: Dumps the communication packets into a log text file
* -f: Forces the app to not check for connection again. Helps in some cases that the app reports that the controller was disconnected.

Others:
* Organize the IR/Playground/Calibration panels a little better
* Many bugfixes and optimizations


## Prerequisites:

**Microsoft Visual C++ 2015-2022 (x86) Redistributable** (All Windows versions)

**Microsoft .NET Framework 4.7.2** (for Windows lower than Windows 10)

## Linux

Joy-Con Toolkit runs natively on Linux with Mono. See [linux/README.md](linux/README.md) for install, build and step-by-step testing instructions, and [docs/LINUX_PORTING.md](docs/LINUX_PORTING.md) for how the port was done.

## Python / Kivy (Windows, macOS, Linux)

A cross-platform port in Python with a Kivy interface, with every feature above plus guided calibration and MIDI on the HD Rumble. See [python/README.md](python/README.md).

## Credits

**Joy-Con Toolkit** by [CTCaer](https://github.com/CTCaer/jc_toolkit).

Community changes merged into this version:

* [linkoid](https://github.com/linkoid/jc_toolkit): `-l` command-line option to list HID device info, and pseudo-third-party controller support.
* [fienestar](https://github.com/fienestar/jc_toolkit/tree/pr): controller priority selection (Any / Joy-Con (L) / Joy-Con (R) / Pro Controller) and the `handle_type` refactor.
* [mas1850 (M.A. Schneider)](https://github.com/mas1850/jc_toolkit): Pastel Pink, Yellow, Purple and Green color presets, VS2022 toolset update, Pro Controller connection delay and README changelog.
* [arpruss (Alexander Pruss)](https://github.com/arpruss/jc_toolkit): IR camera Pointing (DPD) and Clustering modes, and the IR image border flicker fix.

Other code and research this project builds on:

* [dekuNukem and contributors](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering): Joy-Con protocol reverse engineering.
* [shinyquagsire23](https://github.com/shinyquagsire23/HID-Joy-Con-Whispering): hidapi and USB usage on Linux, used as reference for the Linux port.
* [Eric Betts (bettse)](https://github.com/bettse): NFC communication starters.
* [Ryan Juckett (Hypersect)](http://blog.hypersect.com/interpreting-analog-sticks/): analog stick dead zone calculation.
* [shuffle2](https://github.com/shuffle2/nxpad): Windows HID reference.

## References:

**Official forum** and **Binary releases**: https://gbatemp.net/threads/tool-joy-con-toolkit-v1-0.478560/

**Protocol reverse engineering**: https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering

**Protocol and hidapi usage in Linux**: https://github.com/shinyquagsire23/HID-Joy-Con-Whispering

**In windows**: https://github.com/shuffle2/nxpad
