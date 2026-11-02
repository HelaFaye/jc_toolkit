"""Color presets: the official colors, recovered from the controllers' SPI flash (switchbrew's
Joy-Con page, and upstream issue #28 for the Pro Controller editions), and the user's own
colors, saved in colors.xml in the original's format (an RC element per color under
RetailColors). The Switch homebrew's presets are generated from these
(tools/gen_switch_tables.py)."""
import os
import xml.etree.ElementTree as ET


def _rgb(hex_):
    return tuple(int(hex_[i:i + 2], 16) for i in (0, 2, 4))


def _pairs(rows):
    return [(name, _rgb(a), _rgb(b)) for name, a, b in rows]


# (name, body, buttons): the retail Joy-Con colors (and the original's Pro Black)
RETAIL = _pairs([
    ("Grey", "828282", "0F0F0F"),
    ("Neon Red", "FF3C28", "1E0A0A"),
    ("Neon Blue", "0AB9E6", "001E1E"),
    ("Neon Yellow", "E6FF00", "142800"),
    ("Neon Green", "1EDC00", "002800"),
    ("Neon Pink", "FF3278", "28001E"),
    ("Red", "E10F00", "280A0A"),
    ("Blue", "4655F5", "00000A"),
    ("Neon Purple", "B400E6", "140014"),
    ("Neon Orange", "FAA005", "0F0A00"),
    ("White", "E6E6E6", "323232"),
    ("Pastel Pink", "FFAFAF", "372D2D"),
    ("Pastel Yellow", "F5FF82", "32332D"),
    ("Pastel Purple", "F0CBEB", "373037"),
    ("Pastel Green", "BCFFC8", "2D322D"),
    ("Pro Black", "323232", "FFFFFF"),
    ("Dev Kit Black", "313131", "0F0F0F"),
])

# (name, body, buttons): the special editions' Joy-Cons (those not already a retail color)
EDITIONS = _pairs([
    ("Let's Go Eevee (L)", "C88C32", "281900"),
    ("Let's Go Pikachu (R)", "FFDC00", "322800"),
    ("Labo Cardboard", "D7AA73", "1E1914"),
    ("Dragon Quest XI S", "1473FA", "00000F"),
    ("Animal Crossing (L)", "82FF96", "0A1E0A"),
    ("Animal Crossing (R)", "96F5F5", "0A1E28"),
    ("Fortnite Yellow", "FFCC00", "1A1100"),
    ("Fortnite Blue", "0084FF", "000F1E"),
    ("Mario Red x Blue", "F04614", "1E1914"),
    ("Monster Hunter Rise", "818282", "0E0F0F"),
    ("Skyward Sword (L)", "2D50F0", "1E0F46"),
    ("Skyward Sword (R)", "500FC8", "00051E"),
    ("Splatoon 3 (L)", "6455F5", "28282D"),
    ("Splatoon 3 (R)", "C3FA05", "1E1E28"),
    ("Pokemon Scarlet (L)", "F07341", "322D1E"),
    ("Pokemon Violet (R)", "9650AA", "32322D"),
    ("Tears of the Kingdom", "D2BE69", "32322D"),
])

# (name, left grip, right grip)
GRIPS = _pairs([
    ("Normal Grips", "464646", "464646"),
    ("Xenoblade Grips", "DD3B64", "DD3B64"),
    ("Splatoon Grips", "1EDC00", "FF3278"),
    ("Smash Grips", "464646", "FFFFFF"),
    ("Splatoon 3 Grips", "6455F5", "C3FA05"),
])

# (name, body, buttons, left grip, right grip): the Pro Controller editions. Splatoon 2 and
# Xenoblade 2 came out before grip colors (system 5.0.0): the Switch recognizes them by
# their body color, and draws their grips itself
PRO = [(name, _rgb(a), _rgb(b), _rgb(c), _rgb(d)) for name, a, b, c, d in [
    ("Pro Controller", "323232", "FFFFFF", "464646", "464646"),
    ("Splatoon 2 Edition", "313232", "FFFFFF", "1EDC00", "FF3278"),
    ("Xenoblade 2 Edition", "323132", "FFFFFF", "DD3B64", "DD3B64"),
    ("Smash Ultimate Edition", "D2D2D2", "E6E6E6", "464646", "FFFFFF"),
    ("Splatoon 3 Edition", "2D2D2D", "E6E6E6", "6455F5", "C3FA05"),
]]

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
