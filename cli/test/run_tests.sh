#!/bin/bash
# Tests jctool-cli against an emulated controller (cli/test/fake_hidapi.cpp): every menu is
# driven with scripted input, and the results are checked in the emulated SPI flash, the
# backup file and the saved images. No controller needed.
#
#   cli/test/run_tests.sh          (from the repository root)

set -u
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"

CXXFLAGS="-std=c++11 -O2 -Wall -Wno-parentheses -Wno-unused-variable -Wno-unused-but-set-variable $(pkg-config --cflags hidapi-hidraw)"
if ! g++ $CXXFLAGS -o jctool-cli-fake "$ROOT/jctool_linux_interactive.cpp" "$ROOT/cli/jc_core.cpp" "$ROOT/cli/png.cpp" "$ROOT/cli/test/fake_hidapi.cpp"; then
    echo "FAIL  build"
    exit 1
fi

failed=0
pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1"; failed=$((failed + 1)); }
check() { if eval "$2"; then pass "$1"; else fail "$1"; fi; }

# run TYPE "input lines" [extra env...]: runs the CLI, output in out.txt, flash saved to flash.bin
run() {
    local type=$1 input=$2; shift 2
    printf '%b' "$input" | env JCFAKE_TYPE=$type JCFAKE_SPI_OUT=flash.bin "$@" ./jctool-cli-fake > out.txt 2>&1
}
# Bytes of the saved flash at an offset, as hex
flash_hex() { od -An -tx1 -j $(($1)) -N $2 flash.bin | tr -d ' \n'; }
png_size() { python3 -c "import struct,sys; d=open(sys.argv[1],'rb').read(24); print('%dx%d' % struct.unpack('>II', d[16:24]))" "$1" 2>/dev/null; }

# --- Device info, battery, LEDs, rumble, calibration
run r '2\n0\n'
check "device info: type, firmware, MAC, S/N" "grep -q 'Controller:  Joy-Con (R)' out.txt && grep -q 'FW Version:  3.89' out.txt && grep -q 'MAC:         98:B6:E9:12:34:56' out.txt && grep -q 'S/N:         XAW70012345678' out.txt"
check "device info: battery, temperature, colors" "grep -q 'Battery:     3.88V - 61%' out.txt && grep -q 'Temperature: 31.0' out.txt && grep -q 'Body:        #FF3C28' out.txt && grep -q 'Buttons:     #1E0A0A' out.txt"
run r '3\ny\n0\n'
check "battery: Fahrenheit switch" "grep -q 'Temperature: 87.8' out.txt"
run r '4\n0x05\n0\n'
check "player LEDs" "grep -q 'LED command acknowledged' out.txt"
run r '5\n0\n'
check "rumble" "grep -q 'Sending the rumble' out.txt && grep -q '\[+\] Done' out.txt"
run r '6\n0\n'
check "calibration: factory right stick" "grep -q 'Right stick: center (2070, 2013)' out.txt"
check "calibration: no user calibration, 6-axis factory" "grep -q 'Right stick: No calibration' out.txt && grep -q 'User: No calibration' out.txt && grep -q 'Factory Acc:  origin    -45    -43    341' out.txt"
check "calibration: stick device parameters" "grep -q 'Stick: deadzone' out.txt"

# --- Colors
run r '7\n123456\nabcdef\ny\n0\n'
check "colors: written to SPI 0x6050" "[ \"\$(flash_hex 0x6050 6)\" = 123456abcdef ]"
check "colors: read back" "grep -q 'Body:        #123456' out.txt"
run pro '7\n\n\n010203\n040506\ny\n0\n'
check "colors: Pro Controller grips" "[ \"\$(flash_hex 0x6056 6)\" = 010203040506 ]"

# --- SPI backup and restore
run r '8\n0\n'
check "backup: 512KB file identical to the flash" "cmp -s spi_right_98B6E9123456.bin flash.bin"
cp spi_right_98B6E9123456.bin backup.bin
printf '\x11\x22\x33' | dd of=backup.bin bs=1 seek=$((0x6050)) conv=notrunc 2>/dev/null
run r '9\nbackup.bin\n1\ny\n0\n'
check "restore: colors from a backup" "[ \"\$(flash_hex 0x6050 3)\" = 112233 ]"
head -c 1000 backup.bin > short.bin
run r '9\nshort.bin\n0\n'
check "restore: partial backup refused" "grep -q 'Partial backup' out.txt"
run l '9\nbackup.bin\n0\n'
check "restore: backup of another controller type refused" "grep -q 'Wrong backup' out.txt"
cp backup.bin corrupt.bin
printf '\x00' | dd of=corrupt.bin bs=1 seek=1 conv=notrunc 2>/dev/null
run r '9\ncorrupt.bin\n0\n'
check "restore: corrupt backup refused" "grep -q 'Corrupt backup' out.txt"
cp backup.bin other_mac.bin
printf '\x00' | dd of=other_mac.bin bs=1 seek=$((0x15)) conv=notrunc 2>/dev/null
run r '9\nother_mac.bin\ny\n5\n0\n'
check "restore: full restore disabled for another controller" "grep -q 'Full restore: disabled' out.txt"
cp backup.bin cal.bin
printf '\xb2\xa1\x01\x02\x03\x04\x05\x06\x07\x08\x09' | dd of=cal.bin bs=1 seek=$((0x801B)) conv=notrunc 2>/dev/null
run r '9\ncal.bin\n3\ny\nn\ny\n0\n'
check "restore: right stick user calibration" "[ \"\$(flash_hex 0x801B 11)\" = b2a1010203040506070809 ]"
run r '9\nbackup.bin\n4\ny\nn\ny\n0\n' JCFAKE_SPI_IN=cal.bin
check "restore: factory reset of user calibration" "[ \"\$(flash_hex 0x801B 11)\" = ffffffffffffffffffffff ]"
cp backup.bin full.bin
printf 'NEWSERIAL' | dd of=full.bin bs=1 seek=$((0x6005)) conv=notrunc 2>/dev/null
run r '9\nfull.bin\n5\ny\n0\n'
check "restore: full restore writes factory config and reboots to pairing" "grep -q 'full restore was completed' out.txt && [ \"\$(flash_hex 0x6005 9)\" = 4e455753455249414c ]"

# --- Serial number
run r '10\n1\nTESTSN123\ny\ny\n0\n'
check "S/N: changed (zero padded to 16 bytes)" "grep -q 'new S/N is now \"TESTSN123\"' out.txt && [ \"\$(flash_hex 0x6000 16)\" = 0000000000000054455354534e313233 ]"
check "S/N: original backed up at 0xF000" "[ \"\$(flash_hex 0xF000 16)\" = 0000584157373030313233343536373800 ] || [ \"\$(flash_hex 0xF000 16)\" = 00005841573730303132333435363738 ]"
cp flash.bin changed_sn.bin
run r '10\n2\ny\n0\n' JCFAKE_SPI_IN=changed_sn.bin
check "S/N: restored from the backup inside the controller" "grep -q 'restored to the device! The new S/N is now \"XAW70012345678\"' out.txt"
run pro '10\n0\n'
check "S/N: not supported on the Pro Controller" "grep -q 'not supported for Pro Controllers' out.txt"

# --- Calibration editing
run r '11\n1\ny\nn\n1000\n2000\n3000\n1100\n2100\n3100\nn\ny\n0\n'
check "user calibration: right stick written" "[ \"\$(flash_hex 0x801B 2)\" = b2a1 ]"
cp flash.bin usercal.bin
run r '6\n0\n' JCFAKE_SPI_IN=usercal.bin
check "user calibration: reads back as entered" "grep -q 'Right stick: center (2000, 2100)  X \[1000 - 3000\]  Y \[1100 - 3100\]' out.txt"
check "user calibration: disabled 6-axis erased" "[ \"\$(flash_hex 0x8026 2)\" = ffff ]"
run r '2\n0\n' JCFAKE_SPI_IN=usercal.bin
check "device info: user calibration shown" "grep -q 'Calibration: user (stick)' out.txt"
run r '2\n0\n'
check "device info: factory calibration shown" "grep -q 'Calibration: factory' out.txt"
{ printf '11\n1\ny\ny\n'; sleep 1.5; printf '\n'; sleep 5; printf '\nn\ny\n0\n'; } | env JCFAKE_TYPE=r JCFAKE_STICK=1 JCFAKE_SPI_OUT=flash.bin ./jctool-cli-fake > out.txt 2>&1
check "guided stick calibration measures and writes" "grep -q 'Measured: X 320 / 7D0 / C80   Y 384 / 834 / CE4' out.txt && [ \"\$(flash_hex 0x801B 2)\" = b2a1 ]"
cp flash.bin wizard.bin
run r '6\n0\n' JCFAKE_SPI_IN=wizard.bin
check "guided stick calibration reads back" "grep -q 'Right stick: center (2000, 2100)  X \[800 - 3200\]  Y \[900 - 3300\]' out.txt"

run r '11\n2\n150\n3000\ny\n0\n'
check "stick device parameters written" "[ \"\$(flash_hex 0x6089 3)\" = 9680bb ]"

# --- Button test (stopped with Enter)
{ printf '12\n'; sleep 1.5; printf '\n'; sleep 0.3; printf '0\n'; } | env JCFAKE_TYPE=r ./jctool-cli-fake > out.txt 2>&1
check "button test: live input shown, stops on Enter" "grep -q 'Buttons:' out.txt && grep -q 'R Stick (Raw/Cal)' out.txt && grep -q 'Acc/meter' out.txt && grep -q 'Stopping' out.txt"

# --- HD Rumble files
python3 - <<'EOF'
import struct
open('test.jcvib','wb').write(b'RRAW' + struct.pack('>HI', 5, 20) + bytes([0x00,0x01,0x40,0x40]) * 20)
open('test.bnvib','wb').write(struct.pack('<IHHI', 4, 3, 200, 80) + bytes([0x40,0x60,0x40,0x80]) * 20)
open('loop.bnvib','wb').write(struct.pack('<IHHIII', 0xC, 3, 200, 5, 15, 80) + bytes([0x40,0x60,0x40,0x80]) * 20)
EOF
run r '13\n1\ntest.jcvib\n0\n'
check "rumble player: raw .jcvib" "grep -q 'Type: Raw HD Rumble' out.txt && grep -q 'Samples: 20' out.txt && grep -q '\[+\] Done' out.txt"
run r '13\n1\ntest.bnvib\n\n\n\n\n0\n'
check "rumble player: binary .bnvib" "grep -q 'Type: Binary HD Rumble' out.txt && grep -q '\[+\] Done' out.txt"
run r '13\n1\nloop.bnvib\n1\n\n\n\n\n0\n'
check "rumble player: loop .bnvib" "grep -q 'Type: Loop Binary HD Rumble' out.txt && grep -q '\[+\] Done' out.txt"

# --- IR camera
run r '14\n17\n1\n0\n0\n'
check "IR: capture 240x320 saved (rotated 240x320 PNG)" "grep -q 'Done! Saved to IRcamera.png' out.txt && [ \"\$(png_size IRcamera.png)\" = 240x320 ]"
run r '14\n17\n3\n2\n1\n0\n0\n'
check "IR: capture 60x80" "grep -q 'Done! Saved to IRcamera.png' out.txt && [ \"\$(png_size IRcamera.png)\" = 60x80 ]"
run r '14\n17\n16\n1\n0\n0\n'
check "IR: quick capture" "grep -q 'Done! Saved to IRcamera.png' out.txt"
{ printf '14\n17\n2\n'; sleep 2; printf '\n'; sleep 0.5; printf '0\n0\n'; } | env JCFAKE_TYPE=r ./jctool-cli-fake > out.txt 2>&1
check "IR: stream updates IRstream.png, stops on Enter" "grep -q 'Status: Standby' out.txt && [ \"\$(png_size IRstream.png)\" = 240x320 ]"
{ printf '14\n17\n3\n2\n2\n'; sleep 2; printf '\n'; sleep 0.5; printf '0\n0\n'; } | env JCFAKE_TYPE=r JCFAKE_IR_STUCK=1 ./jctool-cli-fake > out.txt 2>&1
check "IR: stream sets the camera up again when it kept the old resolution" "grep -q 'IR mode set 1 (stuck' out.txt && grep -q 'IR mode set 2\$' out.txt && ! grep -q 'IR mode set 3' out.txt && grep -q 'Status: Standby' out.txt"
{ printf '14\n17\n3\n3\n2\n'; sleep 2; printf '\n'; sleep 0.5; printf '0\n0\n'; } | env JCFAKE_TYPE=r JCFAKE_IR_STUCK=1 ./jctool-cli-fake > out.txt 2>&1
check "IR: 30x40 stream sets the camera up again when it kept the old resolution" "grep -q 'IR mode set 1 (stuck' out.txt && grep -q 'IR mode set 2\$' out.txt && ! grep -q 'IR mode set 3' out.txt && grep -q 'Status: Standby' out.txt"
run l '14\n0\n'
check "IR: refused on Joy-Con (L)" "grep -q 'only on the Joy-Con (R)' out.txt"
run l '15\n0\n'
check "NFC: refused on Joy-Con (L)" "grep -q 'only on the Joy-Con (R) and the Pro Controller' out.txt"

# --- Debug, HID list, disconnect, traffic log
run r '16\n01\n\n\n\n\n02\n\n0\n'
check "debug: device info subcommand and reply" "grep -q 'Cmd:  01   Subcmd: 02' out.txt && grep -q 'Subcmd Reply' out.txt && grep -q '82 02 03 89' out.txt"
run r '17\n0\n'
check "HID listing" "grep -q 'HID Device: 0x2007' out.txt"
run r '18\ny\n0\n'
check "disconnect" "grep -q 'Subcmd: 06' out.txt"
rm -f traffic_log.txt
printf '2\n0\n' | env JCFAKE_TYPE=r ./jctool-cli-fake -d > out.txt 2>&1
check "-d writes traffic_log.txt" "grep -q '^W: 01 ' traffic_log.txt && grep -q '^R: 21 ' traffic_log.txt"

echo
if [ $failed -eq 0 ]; then
    echo "All checks passed."
else
    echo "$failed check(s) FAILED."
fi
exit $failed
