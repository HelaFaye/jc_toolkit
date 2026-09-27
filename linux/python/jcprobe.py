#!/usr/bin/env python3
# Copyright (c) 2018 CTCaer. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.
"""jcprobe: first milestone of a Python port of Joy-Con Toolkit.

Connects the same way jctool.cpp's device_connection() does and prints what
FormJoy's full_refresh() shows. Read-only.

    pip install hidapi          # provides the 'hidraw' module used below
    python3 jcprobe.py [-l] [-p l|r|pro]

cython-hidapi's 'hid' module uses the libusb backend, which cannot see
Bluetooth controllers, so this imports the 'hidraw' module instead.
"""

import argparse
import struct
import sys
import time

import hidraw

VENDOR_ID = 0x57E
PRODUCT_IDS = {1: 0x2006, 2: 0x2007, 3: 0x2009}
NAMES = {0: "None", 1: "Joy-Con (L)", 2: "Joy-Con (R)", 3: "Pro Controller"}
JOYCON_L, JOYCON_R, PROCON = 1, 2, 3


class JoyCon:
    """Port of the connection and SPI helpers in jctool.cpp."""

    def __init__(self, dev, handle_type):
        self.dev = dev
        self.type = handle_type
        self.timming_byte = 0

    @classmethod
    def connect(cls, priority=0, third_party_prompt=None):
        order = [priority] if priority else [JOYCON_L, JOYCON_R, PROCON]
        for handle_type in order:
            if handle_type == PROCON:
                time.sleep(0.001)
            dev = hidraw.device()
            try:
                dev.open(VENDOR_ID, PRODUCT_IDS[handle_type])
                return cls(dev, handle_type)
            except OSError:
                pass
        if priority not in (0, PROCON):
            return None
        # Pseudo-third-party controllers. hidraw leaves the manufacturer
        # string empty for Bluetooth devices, so accept that too.
        for info in hidraw.enumerate():
            if (info["product_string"] == "Wireless Gamepad" and info["usage"] == 0x0005
                    and info["manufacturer_string"] in ("Nintendo", "")):
                if third_party_prompt and third_party_prompt(info):
                    dev = hidraw.device()
                    dev.open_path(info["path"])
                    return cls(dev, PROCON)
        return None

    def close(self):
        self.dev.close()

    def _subcmd(self, subcmd):
        # brcm_hdr (10 bytes) + brcm_cmd_01: [0]=cmd, [1]=timer, [10]=subcmd
        buf = bytearray(49)
        buf[0] = 0x01
        buf[1] = self.timming_byte & 0xF
        self.timming_byte += 1
        buf[10] = subcmd
        return buf

    def _exchange(self, build, is_reply, max_errors=20):
        for _ in range(max_errors + 1):
            self.dev.write(bytes(build()))
            for _ in range(9):
                reply = bytes(self.dev.read(49, 64))
                if len(reply) >= 0x13 and is_reply(reply):
                    return reply
                if not reply:
                    break
        return None

    def get_device_info(self):
        reply = self._exchange(lambda: self._subcmd(0x02),
                               lambda b: struct.unpack_from("<H", b, 0xD)[0] == 0x0282)
        return None if reply is None else reply[0xF:0xF + 0xA]

    def get_spi_data(self, offset, read_len):
        def build():
            buf = self._subcmd(0x10)
            struct.pack_into("<IB", buf, 11, offset, read_len)
            return buf
        reply = self._exchange(build, lambda b: struct.unpack_from("<HI", b, 0xD) == (0x1090, offset))
        return None if reply is None else reply[0x14:0x14 + read_len]

    def get_serial_number(self):
        sn = self.get_spi_data(0x6001, 0xF)
        return "Error!" if sn is None else sn.replace(b"\0", b"").decode("ascii", "replace")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("-l", action="store_true", help="list all HID devices")
    parser.add_argument("-p", choices=["l", "r", "pro"], help="preferred controller type")
    args = parser.parse_args()

    if args.l:
        for d in hidraw.enumerate():
            print('HID Device: 0x{product_id:04x} "{product_string}"\n\tvendor = 0x{vendor_id:04x} "{manufacturer_string}"\n'
                  "\trelease = {release_number}\n\tserial = {serial_number}\n"
                  "\tusage = 0x{usage:04x} page: 0x{usage_page:04x}\n\t{path}\n".format(**d))

    def prompt(info):
        answer = input("A potential third-party device has been detected:\n\n\t{} : {}\n\n"
                       "Editing could be potentially unstable. Would you like to use this device anyways? [y/N] "
                       .format(info["product_string"], info["manufacturer_string"]))
        return answer.strip().lower().startswith("y")

    priority = {"l": JOYCON_L, "r": JOYCON_R, "pro": PROCON}.get(args.p, 0)
    jc = JoyCon.connect(priority, prompt)
    if jc is None:
        print("The device is not paired or the device was disconnected!\n"
              "Check that it is paired, and that you can open /dev/hidraw* (see linux/udev).", file=sys.stderr)
        return 2
    try:
        print("Device:   " + NAMES[jc.type])
        info = jc.get_device_info()
        if info is not None:
            print("Firmware: {:X}.{:02X}".format(info[0], info[1]))
            print("MAC:      " + ":".join("{:02X}".format(b) for b in info[4:10]))
        if jc.type != PROCON:
            print("S/N:      " + jc.get_serial_number())
        colors = jc.get_spi_data(0x6050, 12 if jc.type == PROCON else 6)
        if colors is not None:
            labels = ["Body", "Buttons", "Grip L", "Grip R"]
            for i in range(len(colors) // 3):
                print("{:<9} #{}".format(labels[i] + ":", colors[i * 3:i * 3 + 3].hex().upper()))
    finally:
        jc.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
