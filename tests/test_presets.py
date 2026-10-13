"""Color presets (colors.xml in the original's format) and the battery icons."""
from jctool import ops, presets


def test_user_presets_round_trip_and_limit(tmp_path):
    path = str(tmp_path / "sub" / "colors.xml")
    assert presets.load_user(path) == []
    presets.add_user(path, "Mine", (1, 2, 3))
    presets.add_user(path, "Mine", (4, 5, 6))                 # Same name: replaced
    assert presets.load_user(path) == [("Mine", (4, 5, 6))]
    for i in range(presets.MAX_USER + 3):
        presets.add_user(path, "c%d" % i, (i, i, i))
    names = [n for n, _ in presets.load_user(path)]
    assert len(names) == presets.MAX_USER and names[-1] == "c%d" % (presets.MAX_USER + 2)
    assert presets.remove_user(path, names[-1])[-1][0] == names[-2]


def test_reads_the_original_colors_xml(tmp_path):
    path = tmp_path / "colors.xml"
    path.write_text('<?xml version="1.0"?><configuration><RetailColors>'
                    '<RC key="retailpreset_01" rgb_color="0AB9E6" tooltip="Blue" />'
                    '<RC key="retailpreset_02" rgb_color="nothex" tooltip="Bad" /></RetailColors></configuration>')
    assert presets.load_user(str(path)) == [("Blue", (0x0A, 0xB9, 0xE6))]


def test_retail_presets_match_the_original():
    assert dict((n, (a, b)) for n, a, b in presets.RETAIL)["Neon Red"] == ((0xFF, 0x3C, 0x28), (0x1E, 0x0A, 0x0A))
    assert len(presets.RETAIL) == 12 and len(presets.GRIPS) == 3


def test_battery_images():
    assert [ops.battery_image(n) for n in (0, 1, 2, 7, 8, 9)] == [
        "batt_0.png", "batt_0_chr.png", "batt_25.png", "batt_75_chr.png", "batt_100.png", "batt_100_chr.png"]
