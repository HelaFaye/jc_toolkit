// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// mono jctool.exe --selftest
//   Checks the parts of the port that can be checked without a controller:
//   hidapi loading, packet struct layout, CRC, stick encoding, the Windows-compatible
//   write padding, and every read/write path against the emulated controller.
//   With a display (or under xvfb-run) it also builds the real main window against
//   the emulated controller and checks what it shows, and dumps/validates a full SPI backup.

using System;
using System.IO;
using System.Windows.Forms;

namespace CppWinFormJoy
{
    static unsafe class SelfTest
    {
        static int failures;

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok)
                failures++;
        }

        public static int Run()
        {
            Console.WriteLine("Joy-Con Toolkit self-test (no controller needed)\n");

            // 1. hidapi
            try {
                Check(Jc.hid_init() == 0, "libhidapi-hidraw.so.0 loads and hid_init() succeeds");
                Jc.hid_device_info* devs = Jc.hid_enumerate(0, 0);
                int count = 0;
                for (var d = devs; d != null; d = d->next)
                    count++;
                Jc.hid_free_enumeration(devs);
                Check(true, "hid_enumerate() works (" + count + " HID devices visible to this user)");
            }
            catch (DllNotFoundException) {
                Check(false, "libhidapi-hidraw.so.0 loads (install libhidapi-hidraw0)");
            }

            // 2. Packet layout must match jctool.h's #pragma pack(1) structs
            byte* buf = stackalloc byte[49];
            Jc.memset(buf, 0, 49);
            var hdr = (brcm_hdr*)buf;
            var pkt = (brcm_cmd_01*)(hdr + 1);
            Check(sizeof(brcm_hdr) == 10, "brcm_hdr is 10 bytes");
            hdr->rumble_r[3] = 0xAA;
            pkt->subcmd = 0x10;
            pkt->spi_data.offset = 0x11223344;
            pkt->spi_data.size = 0x1D;
            Check(buf[9] == 0xAA && buf[10] == 0x10 && buf[11] == 0x44 && buf[14] == 0x11 && buf[15] == 0x1D,
                "subcmd at 10, spi offset at 11-14 (LE), size at 15");
            Jc.memset(buf, 0, 49);
            pkt->subcmd_21_23_04.no_of_reg = 9;
            pkt->subcmd_21_23_04.reg1_addr = 0x3001;
            pkt->subcmd_21_23_04.reg1_val = 0x55;
            pkt->subcmd_21_23_04.reg9_val = 0x66;
            Check(buf[13] == 9 && buf[14] == 0x01 && buf[15] == 0x30 && buf[16] == 0x55 && buf[40] == 0x66,
                "IR register write layout (3 bytes per register from offset 14)");
            Jc.memset(buf, 0, 49);
            pkt->subcmd_21_23_01.mcu_ir_mode = 7;
            pkt->subcmd_21_23_01.no_of_frags = 0xFF;
            pkt->subcmd_21_23_01.mcu_major_v = 0x0500;
            pkt->subcmd_21_23_01.mcu_minor_v = 0x1800;
            Check(buf[13] == 7 && buf[14] == 0xFF && buf[16] == 0x05 && buf[18] == 0x18, "IR mode packet layout");
            Check(sizeof(ir_image_config) == 22, "ir_image_config is packed (22 bytes)");

            // 3. Helpers
            byte* crc_in = stackalloc byte[9];
            for (int i = 0; i < 9; i++)
                crc_in[i] = (byte)('1' + i);
            Check(Jc.mcu_crc8_calc(crc_in, 9) == 0xF4, "MCU CRC-8 (poly 0x07) of \"123456789\" is 0xF4");
            ushort* dec = stackalloc ushort[2];
            byte* enc = stackalloc byte[3];
            dec[0] = 0x7A3; dec[1] = 0x81C;
            Jc.encode_stick_params(enc, dec);
            ushort* dec2 = stackalloc ushort[2];
            Jc.decode_stick_params(dec2, enc);
            Check(dec2[0] == 0x7A3 && dec2[1] == 0x81C, "stick calibration encode/decode round trip");
            Check(Jc.uint16_to_int16(0xFFFF) == -1 && Jc.uint16_to_int16(0x7FFF) == 32767, "uint16_to_int16");

            // 4. Protocol against the emulated controller
            foreach (int type in new[] { Jc.JOYCON_L, Jc.JOYCON_R, Jc.PROCON }) {
                var fake = new FakeJoyCon(type);
                Jc.fake = fake;
                Jc.handle = IntPtr.Zero;
                Jc.handle_priority = Jc.NOTHING;
                Jc.check_connection_ok = true;
                string name = new[] { "", "Joy-Con (L)", "Joy-Con (R)", "Pro Controller" }[type];

                Check(Jc.device_connection() == type, name + ": device_connection() finds it");

                byte* info = stackalloc byte[10];
                Jc.get_device_info(info);
                Check(info[0] == 0x03 && info[1] == 0x89 && info[4] == fake.mac[0] && info[9] == fake.mac[5],
                    name + ": get_device_info() FW 3.89 and MAC");

                if (type != Jc.PROCON)
                    Check(Jc.get_sn(0x6001, 0xF) == "XAW70012345678", name + ": get_sn() reads the S/N");

                byte* colors = stackalloc byte[12];
                Jc.get_spi_data(0x6050, 12, colors);
                Check(colors[0] == fake.spi[0x6050] && colors[11] == fake.spi[0x605B], name + ": get_spi_data() reads colors");

                byte* newc = stackalloc byte[6];
                for (int i = 0; i < 6; i++)
                    newc[i] = (byte)(0x10 * (i + 1));
                int writes_before = fake.writes;
                Check(Jc.write_spi_data(0x6050, 6, newc) == 0 && fake.spi[0x6050] == 0x10 && fake.spi[0x6055] == 0x60,
                    name + ": write_spi_data() writes colors");

                byte* batt = stackalloc byte[3];
                Jc.get_battery(batt);
                Check(batt[0] == 0x8E && batt[1] == 0x10 && batt[2] == 0x06, name + ": get_battery()");
                byte* temp = stackalloc byte[2];
                Jc.get_temperature(temp);
                Check(temp[0] == 0x60 && temp[1] == 0x00, name + ": get_temperature()");

                Jc.hid_write(Jc.handle, newc, 10);
                Check(fake.last_write_length == 49, name + ": short writes are padded to 49 bytes like on Windows");

                Check(Jc.silence_input_report() == 0 && Jc.set_led_busy() == 0 && Jc.send_rumble() == 0,
                    name + ": input report mode, LEDs and rumble commands");
            }

            // 5. Write padding (Windows hidapi sends every output report at 49 bytes)
            Jc.fake = null;
            byte* shortbuf = stackalloc byte[10];
            Check(Jc.hid_write(IntPtr.Zero, shortbuf, 10) == -1, "hid_write() on a closed handle fails safely");

            // 6. The real main window, if there is a display
            if (Environment.GetEnvironmentVariable("DISPLAY") != null)
                RunGui();
            else
                Console.WriteLine("SKIP  main window checks (no DISPLAY; run under xvfb-run to include them)");

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All checks passed." : failures + " check(s) FAILED.");
            return failures == 0 ? 0 : 1;
        }

        static void RunGui()
        {
            foreach (int type in new[] { Jc.JOYCON_R, Jc.PROCON }) {
                var fake = new FakeJoyCon(type);
                Jc.fake = fake;
                Jc.handle = IntPtr.Zero;
                Jc.handle_priority = Jc.NOTHING;
                Jc.check_connection_ok = true;
                Jc.device_connection();
                string name = type == Jc.PROCON ? "Pro Controller" : "Joy-Con (R)";

                var form = new FormJoy();
                form.Show();
                Application.DoEvents();
                Check(form.DevText == name, name + " window: controller type shown");
                Check(form.MacText == "98:B6:E9:12:34:56", name + " window: MAC shown");
                Check(form.FwText == "3.89", name + " window: firmware shown");
                Check(type == Jc.PROCON ? form.SnText == "Not supported" : form.SnText == "XAW70012345678", name + " window: S/N shown");
                Check(form.BodyText == (type == Jc.PROCON ? "Body: #323232" : "Body: #FF3C28"), name + " window: body color read from SPI");
                Check(form.PreviewImage != null, name + " window: controller preview image drawn");
                form.RefreshPreview();
                form.RefreshPreview();
                Check(form.PreviewImage != null, name + " window: preview can be redrawn repeatedly");

                // Calibration: status in the info section, guided stick and motion calibration
                // (Calibration screen tabs), and the Manual tab reading what they wrote.
                Check(form.CalText == "Factory", name + ": calibration status shown (" + form.CalText + ")");
                bool left_stick = type != Jc.JOYCON_R;
                Func<Func<bool>, int, bool> pump_until = (done, ms) => {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!done() && clock.ElapsedMilliseconds < ms) {
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(2);
                    }
                    return done();
                };
                var sticks = form.StickCal;
                form.select_cal_tab(0);
                fake.stick_raw = new[] { 2000, 2100 };
                sticks.Start();
                bool centered = pump_until(() => sticks.Rotating, 3000);
                // Three turns along the edge: X 800-3200, Y 900-3300
                for (int a = 0; a <= 3 * 360; a += 5) {
                    double r = a * Math.PI / 180;
                    fake.stick_raw = new[] { 2000 + (int)Math.Round(1200 * Math.Cos(r)), 2100 + (int)Math.Round(1200 * Math.Sin(r)) };
                    var step_clock = System.Diagnostics.Stopwatch.StartNew();
                    pump_until(() => step_clock.ElapsedMilliseconds >= 20, 100);
                }
                bool can_finish = sticks.CanFinish;
                sticks.Finish();
                int[] cal = sticks.Result;
                fake.stick_raw = null;
                Check(centered && can_finish && cal != null && Math.Abs(cal[0] - 800) <= 8 && cal[1] == 2000 && Math.Abs(cal[2] - 3200) <= 8
                    && Math.Abs(cal[3] - 900) <= 8 && cal[4] == 2100 && Math.Abs(cal[5] - 3300) <= 8,
                    name + ": guided stick calibration measures center and range (" + (cal == null ? "none" : string.Join(",", cal)) + ")");
                if (cal != null) {
                    int magic = left_stick ? 0x8010 : 0x801B;
                    int other = left_stick ? 0x801B : 0x8010;
                    byte other_before = fake.spi[other];
                    int sres = sticks.Save(false);
                    Check(sres == 0 && fake.spi[magic] == 0xB2 && fake.spi[magic + 1] == 0xA1 && fake.spi[other] == other_before
                        && form.CalText == (type == Jc.PROCON ? "User (L stick)" : "User (stick)"),
                        name + ": stick calibration saved (other stick untouched), status: " + form.CalText);
                    form.RefreshUserCal();
                    int[] back = form.UserCalFields(left_stick);
                    Check(string.Join(",", back) == string.Join(",", cal),
                        name + ": Manual tab reads the saved stick calibration (" + string.Join(",", back) + ")");
                }

                var motion = form.MotionCal;
                form.select_cal_tab(1);
                fake.imu_raw = new short[] { 60, -150, 4096 + 341, 900, -900, 900 }; // Moving: large gyro
                motion.Start();
                var flip = new Timer { Interval = 30 };
                bool flip_state = false;
                flip.Tick += (o, e) => { flip_state = !flip_state; fake.imu_raw = new short[] { 60, -150, 4096 + 341, (short)(flip_state ? 900 : -900), 0, 0 }; };
                flip.Start();
                bool measured_moving = pump_until(() => motion.Measured, 2500);
                flip.Stop();
                fake.imu_raw = new short[] { -45, -43, 4096 + 341, 25, -35, -36 };
                bool measured = pump_until(() => motion.Measured, 5000);
                fake.imu_raw = null;
                int[] mres = motion.Result;
                Check(!measured_moving && measured && mres != null && string.Join(",", mres) == "-45,-43,341,25,-35,-36",
                    name + ": guided motion calibration waits for stillness, measures the offsets (" + (mres == null ? "none" : string.Join(",", mres)) + ")");
                if (measured) {
                    int wres = motion.Save(false);
                    bool bytes_ok = fake.spi[0x8026] == 0xB2 && fake.spi[0x8027] == 0xA1
                        && (short)(fake.spi[0x802C] | fake.spi[0x802D] << 8) == 341
                        && (short)(fake.spi[0x8034] | fake.spi[0x8035] << 8) == 25;
                    for (int i = 0; i < 6; i++)
                        bytes_ok &= fake.spi[0x802E + i] == fake.spi[0x6026 + i] && fake.spi[0x803A + i] == fake.spi[0x6032 + i];
                    Check(wres == 0 && bytes_ok && form.CalText.Contains("motion"),
                        name + ": motion calibration saved with the factory sensitivities, status: " + form.CalText);
                }
                // Two positions on a tilted surface (+120/-80 on X/Y): turned 180 degrees, the tilt
                // reverses and averages out, leaving the sensor's own offsets.
                motion.TwoPositions = true;
                fake.imu_raw = new short[] { -45 + 120, -43 - 80, 4096 + 341, 25, -35, -36 };
                motion.Start();
                bool turn_asked = pump_until(() => motion.Turning, 5000);
                fake.imu_raw = new short[] { -45, -43, 4096 + 341, 25, -35, -36 + 2000 };  // Turning: ~140 deg/s
                bool turned = pump_until(() => Math.Abs(motion.Turned) >= 180, 5000);
                fake.imu_raw = new short[] { -45 - 120, -43 + 80, 4096 + 341, 25, -35, -36 };
                // The turn needs the user's confirmation: nothing happens until then
                pump_until(() => false, 1500);
                bool waited = motion.Turning && !motion.Measured && motion.CanConfirmTurn;
                motion.ConfirmTurn();
                bool measured2 = pump_until(() => motion.Measured, 5000);
                fake.imu_raw = null;
                int[] m2 = motion.Result;
                motion.TwoPositions = false;
                Check(turn_asked && turned && waited && measured2 && m2 != null && string.Join(",", m2) == "-45,-43,341,25,-35,-36",
                    name + ": two-position motion calibration: confirmed turn, cancels the surface tilt (" + (m2 == null ? "none" : string.Join(",", m2))
                    + ", turned " + (int)Math.Abs(motion.Turned) + ")");

                // Back to factory calibration
                int f1 = sticks.UseFactory(false), f2 = motion.UseFactory(false);
                int user_at = left_stick ? 0x8010 : 0x801B;
                bool erased = true;
                for (int i = 0; i < 11; i++)
                    erased &= fake.spi[user_at + i] == 0xFF;
                for (int i = 0; i < 26; i++)
                    erased &= fake.spi[0x8026 + i] == 0xFF;
                Check(f1 == 0 && f2 == 0 && erased && form.CalText == "Factory",
                    name + ": Use factory erases the stick and motion user calibration, status: " + form.CalText);
                form.select_cal_tab(2);

                string file = "selftest_spi_dump.bin";
                Jc.cancel_spi_dump = false;
                int res = Jc.dump_spi(file);
                byte[] dumped = File.Exists(file) ? File.ReadAllBytes(file) : new byte[0];
                bool same = dumped.Length == 0x80000;
                for (int i = 0; same && i < dumped.Length; i++)
                    same = dumped[i] == fake.spi[i];
                Check(res == 0 && same, name + ": SPI dump writes the full 512KB flash byte for byte");
                File.Delete(file);

                if (type == Jc.JOYCON_R) {
                    File.Delete("IRcamera.png");
                    int ir_res = form.CaptureIR();
                    int capture_fragments = fake.ir_frames_sent;
                    Check(ir_res == 0 && File.Exists("IRcamera.png") && fake.ir_frames_sent >= 256,
                        name + ": IR camera capture reassembles a 240x320 frame and saves IRcamera.png");
                    if (File.Exists("IRcamera.png")) {
                        using (var img = System.Drawing.Image.FromFile("IRcamera.png"))
                            Check(img.Width == 240 && img.Height == 320, name + ": IRcamera.png is 240x320 (rotated like on Windows)");
                        File.Delete("IRcamera.png");
                    }

                    // Auto exposure (always on for Capture) makes the Joy-Con drop a fragment; the
                    // capture must still save a complete frame without running out its retries.
                    Check(Jc.ir_last_frame_missing == 0 && capture_fragments <= 3 * 256 + 16,
                        name + ": IR capture with auto exposure saves a complete frame in 3 frames (" + capture_fragments + " fragments)");

                    // "Quick capture" option: 2 frames, still complete, exposure left as set.
                    Check(!form.IRQuickCaptureOption.Checked && !Jc.ir_quick_capture && form.IRQuickCaptureOption.Visible,
                        name + ": IR Quick capture option shown, off by default");
                    form.IRQuickCaptureOption.Checked = true;
                    decimal exposure_before = form.IRExposure;
                    ir_res = form.CaptureIR();
                    int quick_fragments = fake.ir_frames_sent;
                    form.IRQuickCaptureOption.Checked = false;
                    Check(ir_res == 0 && Jc.ir_last_frame_missing == 0 && quick_fragments <= 2 * 256 + 16 && File.Exists("IRcamera.png")
                        && form.IRExposure == exposure_before,
                        name + ": IR quick capture saves a complete frame in 2 frames without changing the exposure (" + quick_fragments + " fragments)");
                    File.Delete("IRcamera.png");

                    // 60x80 with 8302 white pixels (a real capture): auto exposure must adjust
                    // moderately, not drop to 0 (the Windows code's result: a black image).
                    form.SelectIRResolution60p(true);
                    form.IRExposure = 160;
                    fake.ir_white_pixels = 8302;
                    ir_res = form.CaptureIR();
                    Check(ir_res == 0 && form.IRExposure >= 60,
                        name + ": IR auto exposure at 60x80 doesn't black out the image (160us -> " + form.IRExposure + "us)");
                    fake.ir_white_pixels = 0;
                    form.SelectIRResolution60p(false);
                    form.IRExposure = 300;
                    File.Delete("IRcamera.png");

                    // JCTOOL_IR_SKIP_LEFTOVER: the leftover frame is skipped when the Joy-Con moves on.
                    Jc.ir_skip_leftover = true;
                    ir_res = form.CaptureIR();
                    int skip_fragments = fake.ir_frames_sent;
                    form.IRQuickCaptureOption.Checked = true;
                    int ir_res2 = form.CaptureIR();
                    int skip_quick_fragments = fake.ir_frames_sent;
                    form.IRQuickCaptureOption.Checked = false;
                    Jc.ir_skip_leftover = false;
                    Check(ir_res == 0 && ir_res2 == 0 && Jc.ir_last_frame_missing == 0 && skip_fragments <= 2 * 256 + 16 && skip_quick_fragments <= 256 + 16,
                        name + ": IR skip leftover saves a frame (" + skip_fragments + " fragments, quick " + skip_quick_fragments + ")");
                    File.Delete("IRcamera.png");

                    // The camera keeps its old settings once: set it up again and capture once more.
                    fake.ir_stale_captures = 1;
                    int sets_before = fake.ir_mode_sets;
                    ir_res = form.CaptureIR();
                    Check(ir_res == 0 && fake.ir_mode_sets - sets_before == 2 && File.Exists("IRcamera.png"),
                        name + ": IR capture sets the camera up again when it didn't apply the settings");
                    File.Delete("IRcamera.png");

                    // The camera keeps its old resolution but sends new frames (real stats):
                    // detected from the image layout.
                    form.SelectIRResolution60p(true);
                    fake.ir_stuck_captures = 1;
                    sets_before = fake.ir_mode_sets;
                    ir_res = form.CaptureIR();
                    Check(ir_res == 0 && fake.ir_mode_sets - sets_before == 2,
                        name + ": IR capture sets the camera up again when it kept the old resolution");
                    form.SelectIRResolution60p(false);
                    File.Delete("IRcamera.png");

                    // Twice in a row: report it instead of "Done".
                    fake.ir_stale_captures = 2;
                    ir_res = form.CaptureIR();
                    Check(ir_res == 10 && form.lbl_IRStatus.Text.Contains("didn't apply"),
                        name + ": IR capture reports when the camera keeps ignoring the settings");
                    fake.ir_stale_captures = 0;
                    File.Delete("IRcamera.png");

                    // The Joy-Con skips a fragment near the end of the saved frame and doesn't
                    // resend it before the last one. The capture must not save that frame.
                    fake.ir_skip_fragment = 253;
                    fake.ir_ignore_resend = true;
                    ir_res = form.CaptureIR();
                    fake.ir_ignore_resend = false;
                    Check(ir_res == 0 && fake.ir_skip_fragment == -1 && Jc.ir_last_frame_missing == 0,
                        name + ": IR capture saves a complete frame after a fragment was skipped");
                    File.Delete("IRcamera.png");

                    // On XWayland, window event processing can block for about a second at a
                    // time. Simulate that: the capture must still finish at full speed.
                    fake.ir_fragment_delay_ms = 5; // ~2.6s per capture, like hardware
                    var stall = new Timer { Interval = 20 };
                    stall.Tick += (o, e) => System.Threading.Thread.Sleep(1000);
                    stall.Start();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    ir_res = form.CaptureIR();
                    watch.Stop();
                    stall.Stop();
                    fake.ir_fragment_delay_ms = 0;
                    Check(ir_res == 0 && File.Exists("IRcamera.png") && watch.ElapsedMilliseconds < 12000,
                        name + ": IR capture finishes while the window is stalling (" + watch.ElapsedMilliseconds + "ms)");
                    File.Delete("IRcamera.png");

                    // Stream, apply a live config change while streaming, then Stop.
                    int writes_before = 0, writes_after = 0, write_thread = 0, step = 0, closes_streaming = 0;
                    var clicks = new Timer { Interval = 300 };
                    clicks.Tick += (o, e) => {
                        clicks.Stop();
                        if (++step == 1) {
                            closes_streaming = fake.closes;
                            writes_before = fake.ir_register_writes;
                            form.ClickIRConfigLive();
                            writes_after = fake.ir_register_writes;
                            write_thread = fake.ir_register_write_thread;
                            clicks.Start();
                        }
                        else {
                            form.ClickIRStream(); // Stop (checks the connection first, like every button)
                            closes_streaming = fake.closes - closes_streaming;
                        }
                    };
                    fake.ir_fragment_delay_ms = 1;
                    clicks.Start();
                    watch.Restart();
                    form.ClickIRStream();
                    watch.Stop();
                    clicks.Stop();
                    fake.ir_fragment_delay_ms = 0;
                    Check(step == 2 && writes_after > writes_before && write_thread != System.Threading.Thread.CurrentThread.ManagedThreadId
                        && watch.ElapsedMilliseconds < 5000 && form.lbl_IRStatus.Text == "Status: Standby",
                        name + ": IR stream, live config (sent by the IR thread) and Stop (" + watch.ElapsedMilliseconds + "ms)");
                    Check(closes_streaming == 0,
                        name + ": Stop doesn't close the controller while the IR thread reads it (that crashed)");
                    File.Delete("IRcamera.png");

                    // A 60x80 stream whose camera kept sending 240x320 rows (seen on a Joy-Con (R)):
                    // the stream stops, sets the camera up again and goes on until Stop.
                    form.SelectIRResolution60p(true);
                    fake.ir_stuck_captures = 1;
                    int stream_sets = fake.ir_mode_sets;
                    var stop = new Timer { Interval = 1500 };
                    stop.Tick += (o, e) => { stop.Stop(); form.ClickIRStream(); };
                    fake.ir_fragment_delay_ms = 1;
                    stop.Start();
                    form.ClickIRStream();
                    stop.Stop();
                    fake.ir_fragment_delay_ms = 0;
                    fake.ir_stuck_captures = 0;
                    form.SelectIRResolution60p(false);
                    Check(fake.ir_mode_sets - stream_sets == 2 && form.lbl_IRStatus.Text == "Status: Standby",
                        name + ": IR stream sets the camera up again when it kept the old resolution");

                    // HD Rumble player during a stream (crashed hidapi: two threads using the
                    // controller at once). Calls must not overlap, and both must finish.
                    byte[] stream_vib = new byte[0x0A + 40 * 4];
                    stream_vib[0] = 0x52; stream_vib[1] = 0x52; stream_vib[2] = 0x41; stream_vib[3] = 0x57;
                    form.vib_loaded_file = stream_vib;
                    fake.concurrent_calls = 0;
                    int rumble_writes = 0, rumble_steps = 0;
                    string battery_idle = form.RefreshBattery(), battery_streaming = null, link_streaming = null;
                    var rumble = new Timer { Interval = 400 };
                    rumble.Tick += (o, e) => {
                        rumble.Stop();
                        try {
                        if (++rumble_steps == 1) {
                            int w = fake.writes;
                            Jc.play_hd_rumble_file(1, 1, 40, 0, 0, 0, 0);
                            rumble_writes = fake.writes - w;
                            battery_streaming = form.RefreshBattery(); // Like the player's handler does
                            form.update_link_health();
                            rumble.Start();
                        }
                        else {
                            form.ClickIRStream();
                        }
                        }
                        catch (Exception ex) {
                            Console.WriteLine("rumble step failed: " + ex);
                            rumble_steps = 99;
                            form.ClickIRStream();
                        }
                    };
                    fake.ir_fragment_delay_ms = 1;
                    rumble.Start();
                    form.ClickIRStream();
                    rumble.Stop();
                    fake.ir_fragment_delay_ms = 0;
                    Check(rumble_steps == 2 && rumble_writes >= 40 && fake.concurrent_calls == 0 && form.lbl_IRStatus.Text == "Status: Standby",
                        name + ": HD rumble during an IR stream: one controller call at a time (" + fake.concurrent_calls + " overlapped)");
                    Check(battery_streaming == battery_idle && !battery_idle.Contains("0.00V"),
                        name + ": battery read during an IR stream gets its reply (" + battery_streaming + ", idle" + battery_idle + ")");
                    form.update_link_health();
                    link_streaming = form.LinkText;
                    form.update_link_health();
                    Check(System.Text.RegularExpressions.Regex.IsMatch(link_streaming, @"^\d+/s \d+ms$") && form.LinkText == "Link idle",
                        name + ": link health shown while streaming (" + link_streaming + "), idle afterwards");

                    // 30x40 (4 fragments, 3.75 rows of 320): 320 pixel rows are still recognized,
                    // a real 40 pixel wide image isn't flagged.
                    byte* frame = stackalloc byte[1200];
                    for (int i = 0; i < 1200; i++)
                        frame[i] = (byte)((i % 320) < 160 ? 30 + i % 7 : 200 - i % 5);
                    bool stuck_30p = Jc.ir_frame_has_other_width(frame, 0x03);
                    for (int i = 0; i < 1200; i++)
                        frame[i] = (byte)((i % 40) * 5 + (i / 40) * 3 + ((i % 40) % 10 == 0 ? 60 : 0));
                    Check(stuck_30p && !Jc.ir_frame_has_other_width(frame, 0x03),
                        name + ": IR 30x40 frames made of 320 pixel rows are detected");
                    File.Delete("IRcamera.png");
                }

                // HD Rumble Player, MIDI tab: encoding, a MIDI file, and live input (a FIFO standing in
                // for a raw MIDI device).
                {
                    byte* enc = stackalloc byte[4];
                    MidiRumble.Encode(440, 0.8f, 0, 0, enc);
                    double a4 = MidiRumble.DecodeHighHz(enc);
                    MidiRumble.Encode(0, 0, 0, 0, enc);
                    Check(Math.Abs(a4 - 440) < 440 * 0.012 && enc[0] == 0x00 && enc[1] == 0x01 && enc[2] == 0x40 && enc[3] == 0x40,
                        name + ": MIDI note to HD Rumble encoding (A4 -> " + a4.ToString("F1") + " Hz; silence 00 01 40 40)");

                    // Format 1, 96 ticks per quarter. Track 0: tempo 120 BPM, then 60 BPM at beat 2.
                    // Track 1 "Lead", channel 1: C4 (beat 0-1), G4 + C5 chord (beat 1-2), E4 (beat 2-3, at 60 BPM).
                    // Track 2 channel 10 (drums): F#2 hi-hat at beat 0.
                    var mid = new System.Collections.Generic.List<byte>();
                    Action<string> ascii = t => mid.AddRange(System.Text.Encoding.ASCII.GetBytes(t));
                    Action<int, int> be = (v, n) => { for (int i = n - 1; i >= 0; i--) mid.Add((byte)(v >> (8 * i))); };
                    Action<byte[]> chunk = body => { ascii("MTrk"); be(body.Length, 4); mid.AddRange(body); };
                    ascii("MThd"); be(6, 4); be(1, 2); be(3, 2); be(96, 2);
                    chunk(new byte[] { 0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20, 0x81, 0x40, 0xFF, 0x51, 0x03, 0x0F, 0x42, 0x40, 0x00, 0xFF, 0x2F, 0x00 });
                    chunk(new byte[] { 0x00, 0xFF, 0x03, 0x04, (byte)'L', (byte)'e', (byte)'a', (byte)'d',
                                       0x00, 0x90, 60, 100,  0x60, 0x80, 60, 0,  0x00, 0x90, 67, 90,  0x00, 72, 110,
                                       0x60, 0x80, 67, 0,  0x00, 72, 0,  0x00, 0x90, 64, 127,  0x60, 64, 0,  0x00, 0xFF, 0x2F, 0x00 });
                    chunk(new byte[] { 0x00, 0x99, 42, 100, 0x30, 0x89, 42, 0, 0x00, 0xFF, 0x2F, 0x00 });
                    string mid_path = "selftest.mid";
                    File.WriteAllBytes(mid_path, mid.ToArray());

                    var midi = form.Midi;
                    bool loaded = midi.Load(mid_path);
                    var song = midi.Song;
                    bool parsed = loaded && song.parts.Count == 2 && song.notes.Count == 5 && song.parts[0].name == "Lead"
                        && song.parts[1].drums && Math.Abs(song.length - 2.0) < 0.001;   // 0.5 + 0.5 + 1.0 s
                    Check(parsed, name + ": MIDI file parsed (" + (song == null ? "none" : song.parts.Count + " parts, " + song.notes.Count
                        + " notes, " + song.length.ToString("F3") + " s") + ")");

                    lock (fake.rumbles) fake.rumbles.Clear();
                    midi.Play();
                    bool finished = pump_until(() => !midi.Playing, 5000);
                    var heard = new System.Collections.Generic.List<int>();
                    byte[] final = null;
                    lock (fake.rumbles)
                        foreach (var rb in fake.rumbles) {
                            final = rb;
                            if (rb[0] == 0x00 && rb[1] == 0x01 && rb[2] == 0x40 && rb[3] == 0x40)
                                continue;
                            fixed (byte* pb = rb) {
                                int hz = (int)Math.Round(MidiRumble.DecodeHighHz(pb));
                                if (!heard.Contains(hz))
                                    heard.Add(hz);
                            }
                        }
                    // C4 261.6 -> 523 Hz, C5 523 (chord top), E4 329.6 -> 659; not the hi-hat (F#2 -> 740)
                    Func<int, bool> has = hz => heard.Exists(x => Math.Abs(x - hz) <= hz * 0.012);
                    Check(finished && has(523) && has(659) && !has(740) && final != null && final[1] == 0x01 && final[2] == 0x40,
                        name + ": MIDI file plays on the rumble, drums skipped, silent at the end (" + string.Join(",", heard) + " Hz)");

                    // Live: a FIFO as the MIDI device. A4 on, sustain on, A4 off (still sounding), sustain off.
                    string fifo = Path.Combine(Path.GetTempPath(), "jctool-selftest-midi-" + System.Diagnostics.Process.GetCurrentProcess().Id);
                    File.Delete(fifo);
                    var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo", fifo) { UseShellExecute = false });
                    mk.WaitForExit();
                    bool listening = midi.Listen(fifo);
                    Func<double> last_hz = () => {
                        lock (fake.rumbles) {
                            if (fake.rumbles.Count == 0) return -1;
                            var rb = fake.rumbles[fake.rumbles.Count - 1];
                            if (rb[0] == 0x00 && rb[1] == 0x01 && rb[2] == 0x40 && rb[3] == 0x40) return 0;
                            fixed (byte* pb = rb) return MidiRumble.DecodeHighHz(pb);
                        }
                    };
                    double on_hz = -1, held_hz = -1, off_hz = -1;
                    using (var w = new FileStream(fifo, FileMode.Open, FileAccess.Write)) {
                        Action<byte[]> send = bytes => { w.Write(bytes, 0, bytes.Length); w.Flush(); };
                        send(new byte[] { 0x90, 69, 100 });
                        pump_until(() => Math.Abs(last_hz() - 440) < 6, 1000);
                        on_hz = last_hz();
                        send(new byte[] { 0xB0, 64, 127, 0x80, 69, 0 });
                        pump_until(() => false, 150);
                        held_hz = last_hz();
                        send(new byte[] { 0xB0, 64, 0 });
                        pump_until(() => last_hz() == 0, 1000);
                        off_hz = last_hz();
                    }
                    midi.Stop();
                    File.Delete(fifo);
                    File.Delete(mid_path);
                    Check(listening && Math.Abs(on_hz - 440) < 6 && Math.Abs(held_hz - 440) < 6 && off_hz == 0 && !midi.Listening,
                        name + ": MIDI input plays live, sustain pedal holds a note (" + on_hz.ToString("F0") + ", held " + held_hz.ToString("F0")
                        + ", released " + off_hz.ToString("F0") + " Hz)");
                }

                // Debug: custom command (subcmd 0x02 device info) and its reply dump
                byte* arg = stackalloc byte[44];
                Jc.memset(arg, 0, 44);
                arg[0] = 0x01;
                arg[5] = 0x02;
                Jc.send_custom_command(arg);
                Check(form.textBoxDbg_reply.Text.StartsWith("Subcmd Reply:") && form.textBoxDbg_reply.Text.Contains("82 02 03 89"),
                    name + ": debug custom command sends and shows the reply");

                // HD Rumble player: a 20 sample raw (.jcvib) file at 1ms
                byte[] vib = new byte[0x0A + 20 * 4];
                vib[0] = 0x52; vib[1] = 0x52; vib[2] = 0x41; vib[3] = 0x57;
                for (int i = 0x0A; i < vib.Length; i++)
                    vib[i] = (byte)i;
                form.vib_loaded_file = vib;
                form.vib_file_converted = vib;
                int writes = fake.writes;
                Jc.play_hd_rumble_file(1, 1, 20, 0, 0, 0, 0);
                Check(fake.writes - writes >= 20, name + ": HD rumble player sends every sample");

                // Close() would run Form1_FormClosing, which exits the process like the original.
                form.Hide();
                form.Dispose();
            }
            Jc.fake = null;
        }
    }
}
