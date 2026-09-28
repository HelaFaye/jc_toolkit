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

                    // JCTOOL_IR_QUICK=1: 2 frames, still complete.
                    Jc.ir_quick_capture = true;
                    ir_res = form.CaptureIR();
                    int quick_fragments = fake.ir_frames_sent;
                    Jc.ir_quick_capture = false;
                    Check(ir_res == 0 && Jc.ir_last_frame_missing == 0 && quick_fragments <= 2 * 256 + 16 && File.Exists("IRcamera.png"),
                        name + ": IR quick capture saves a complete frame in 2 frames (" + quick_fragments + " fragments)");
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
