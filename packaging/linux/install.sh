#!/bin/sh
# Joy-Con Toolkit on Linux: lets your user open the controllers (installs the udev rule,
# with sudo) and adds the app to your applications menu, for the app in this folder.
#   ./install.sh              both
#   ./install.sh --menu-only  only the menu entry (no sudo)
#   ./install.sh --uninstall  removes both
set -e
here=$(cd "$(dirname "$0")" && pwd)
rule=50-nintendo-switch-controllers.rules
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
entry="$apps/joycontoolkit.desktop"

if [ "$1" = "--uninstall" ]; then
    rm -f "$entry"
    if [ -e "/etc/udev/rules.d/$rule" ]; then
        sudo rm -f "/etc/udev/rules.d/$rule"
        sudo udevadm control --reload-rules
    fi
    echo "Removed the menu entry and the udev rule."
    exit 0
fi

if [ "$1" != "--menu-only" ]; then
    echo "Installing the udev rule (sudo asks for your password)..."
    sudo install -m 644 "$here/$rule" /etc/udev/rules.d/
    sudo udevadm control --reload-rules
    sudo udevadm trigger --subsystem-match=hidraw
fi

mkdir -p "$apps"
cat > "$entry" <<EOF
[Desktop Entry]
Type=Application
Name=Joy-Con Toolkit
Comment=Colors, backups, calibration, HD Rumble, IR camera and NFC for Joy-Cons and Pro Controllers
Exec="$here/JoyConToolkit"
Icon=$here/_internal/jctool/app/icon.png
Categories=Utility;Settings;HardwareSettings;
Terminal=false
EOF
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$apps" 2>/dev/null || true
echo "Done. Reconnect your controllers, then open Joy-Con Toolkit from your applications menu."
echo "(The menu entry points to this folder: run this again if you move it.)"
