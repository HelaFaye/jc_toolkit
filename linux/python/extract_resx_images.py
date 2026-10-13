#!/usr/bin/env python3
# Copyright (c) 2018 CTCaer. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.
"""Extract the bitmaps and icons from jctool's .resx files into image files.

Usage: extract_resx_images.py jctool/images.resx jctool/FormJoy.resx OUTDIR

Entries stored as application/x-microsoft.net.object.bytearray.base64 are the
original image files, so they are written out unchanged.
"""

import base64
import os
import sys
import xml.etree.ElementTree as ET

EXTENSIONS = {b"\x89PNG": ".png", b"BM": ".bmp", b"GIF8": ".gif", b"\xff\xd8": ".jpg", b"\x00\x00\x01\x00": ".ico"}


def main():
    *sources, outdir = sys.argv[1:]
    os.makedirs(outdir, exist_ok=True)
    for src in sources:
        for data in ET.parse(src).getroot().iter("data"):
            if data.get("mimetype") != "application/x-microsoft.net.object.bytearray.base64":
                continue
            if not data.get("type", "").startswith(("System.Drawing.Bitmap", "System.Drawing.Icon")):
                continue
            payload = base64.b64decode(data.find("value").text)
            ext = next((e for magic, e in EXTENSIONS.items() if payload.startswith(magic)), ".bin")
            name = data.get("name").replace("$this.", "form_")
            with open(os.path.join(outdir, name + ext), "wb") as f:
                f.write(payload)
            print(name + ext)


if __name__ == "__main__":
    main()
