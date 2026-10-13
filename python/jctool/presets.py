"""Color presets, from the original's color dialog: the retail body / buttons pairs (with
the pastel ones), the retail grips, and the user's own colors, saved in colors.xml in the
original's format (an RC element per color under RetailColors)."""
import os
import xml.etree.ElementTree as ET

# (name, body, buttons)
RETAIL = [
    ("Grey", (0x82, 0x82, 0x82), (0x0F, 0x0F, 0x0F)),
    ("Neon Blue", (0x0A, 0xB9, 0xE6), (0x00, 0x1E, 0x1E)),
    ("Neon Red", (0xFF, 0x3C, 0x28), (0x1E, 0x0A, 0x0A)),
    ("Neon Yellow", (0xE6, 0xFF, 0x00), (0x14, 0x28, 0x00)),
    ("Neon Pink", (0xFF, 0x32, 0x78), (0x28, 0x00, 0x1E)),
    ("Neon Green", (0x1E, 0xDC, 0x00), (0x00, 0x28, 0x00)),
    ("Red", (0xE1, 0x0F, 0x00), (0x28, 0x0A, 0x0A)),
    ("Pro Black", (0x32, 0x32, 0x32), (0xFF, 0xFF, 0xFF)),
    ("Pastel Pink", (0xFF, 0xAF, 0xAF), (0x37, 0x2D, 0x2D)),
    ("Pastel Yellow", (0xF5, 0xFF, 0x82), (0x32, 0x33, 0x2D)),
    ("Pastel Purple", (0xF0, 0xCB, 0xEB), (0x37, 0x30, 0x37)),
    ("Pastel Green", (0xBC, 0xFF, 0xC8), (0x2D, 0x32, 0x2D)),
]

# (name, left grip, right grip)
GRIPS = [
    ("Normal Grips", (0x46, 0x46, 0x46), (0x46, 0x46, 0x46)),
    ("Xenoblade Grips", (0xDD, 0x3B, 0x64), (0xDD, 0x3B, 0x64)),
    ("Splatoon Grips", (0x1E, 0xDC, 0x00), (0xFF, 0x32, 0x78)),
]

MAX_USER = 16


def load_user(path):
    """[(name, rgb)] from colors.xml (missing or unreadable: none)."""
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError):
        return []
    out = []
    for rc in root.iter("RC"):
        try:
            v = int(rc.get("rgb_color", ""), 16)
        except ValueError:
            continue
        out.append((rc.get("tooltip") or "Custom", ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF)))
    return out[:MAX_USER]


def save_user(path, presets):
    """Writes [(name, rgb)] (the newest MAX_USER) to colors.xml."""
    root = ET.Element("configuration")
    root.append(ET.Comment("Values are in RGB"))
    node = ET.SubElement(root, "RetailColors")
    for i, (name, rgb) in enumerate(presets[-MAX_USER:], 1):
        ET.SubElement(node, "RC", key="retailpreset_%02d" % i, rgb_color="%02X%02X%02X" % tuple(rgb), tooltip=name)
    if hasattr(ET, "indent"):                  # Python 3.9+
        ET.indent(root, space="\t")
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def add_user(path, name, rgb):
    presets = [p for p in load_user(path) if p[0] != name]
    presets.append((name, tuple(rgb)))
    save_user(path, presets)
    return load_user(path)


def remove_user(path, name):
    presets = [p for p in load_user(path) if p[0] != name]
    save_user(path, presets)
    return presets
