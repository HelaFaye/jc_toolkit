// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Port of jctool.h: packed packet structs, globals, and the hidapi layer.
//
// The hidapi wrappers reproduce what jctool's vendored Windows hidapi (jctool/hid.c)
// did on the wire, so the protocol code behaves the same on Linux:
//   - writes are zero-padded to the 49-byte output report length,
//   - a read with length 0 waits for one report and discards it,
//   - -d appends every write/read to ./traffic_log.txt in the same format.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using u8 = System.Byte;
using u16 = System.UInt16;
using u32 = System.UInt32;
using s16 = System.Int16;

namespace CppWinFormJoy
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct brcm_hdr
    {
        public u8 cmd;
        public u8 timer;
        public fixed byte rumble_l[4];
        public fixed byte rumble_r[4];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct spi_data_t
    {
        public u32 offset;
        public u8 size;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct subcmd_arg_t
    {
        public u8 arg1;
        public u8 arg2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct subcmd_21_21_t
    {
        public u8 mcu_cmd;
        public u8 mcu_subcmd;
        public u8 mcu_mode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct subcmd_21_23_04_t
    {
        public u8  mcu_cmd;
        public u8  mcu_subcmd;
        public u8  no_of_reg;
        public u16 reg1_addr;
        public u8  reg1_val;
        public u16 reg2_addr;
        public u8  reg2_val;
        public u16 reg3_addr;
        public u8  reg3_val;
        public u16 reg4_addr;
        public u8  reg4_val;
        public u16 reg5_addr;
        public u8  reg5_val;
        public u16 reg6_addr;
        public u8  reg6_val;
        public u16 reg7_addr;
        public u8  reg7_val;
        public u16 reg8_addr;
        public u8  reg8_val;
        public u16 reg9_addr;
        public u8  reg9_val;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct subcmd_21_23_01_t
    {
        public u8  mcu_cmd;
        public u8  mcu_subcmd;
        public u8  mcu_ir_mode;
        public u8  no_of_frags;
        public u16 mcu_major_v;
        public u16 mcu_minor_v;
    }

    // The union in jctool.h: every member starts right after subcmd.
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct brcm_cmd_01
    {
        [FieldOffset(0)] public u8 subcmd;
        [FieldOffset(1)] public spi_data_t spi_data;
        [FieldOffset(1)] public subcmd_arg_t subcmd_arg;
        [FieldOffset(1)] public subcmd_21_21_t subcmd_21_21;
        [FieldOffset(1)] public subcmd_21_23_04_t subcmd_21_23_04;
        [FieldOffset(1)] public subcmd_21_23_01_t subcmd_21_23_01;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ir_image_config
    {
        public u8  ir_res_reg;
        public u16 ir_exposure;
        public u8  ir_leds; // Leds to enable, Strobe/Flashlight modes
        public u16 ir_leds_intensity; // MSByte: Leds 1/2, LSB: Leds 3/4
        public u8  ir_digital_gain;
        public u8  ir_ex_light_filter;
        public u32 ir_custom_register; // MSByte: Enable/Disable, Middle Byte: Edge smoothing, LSB: Color interpolation
        public u16 ir_buffer_update_time;
        public u8  ir_hand_analysis_mode;
        public u8  ir_hand_analysis_threshold;
        public u32 ir_denoise; // MSByte: Enable/Disable, Middle Byte: Edge smoothing, LSB: Color interpolation
        public u8  ir_flip;
        public u8  ir_mode;
    }

    // "For annoying designer..": FormJoy's image resources are compiled as CppWinFormJoy.images.resources
    public class images
    {
    }

    public static unsafe partial class Jc
    {
        // handle_type_t
        public const int NOTHING  = 0;
        public const int JOYCON_L = 1;
        public const int JOYCON_R = 2;
        public const int PROCON   = 3;

        public static int handle_priority;
        public static int handle_type;

        public static bool enable_button_test;
        public static volatile bool enable_IRVideoPhoto; // volatile: read by the IR worker thread
        public static bool enable_IRAutoExposure;
        public static bool enable_NFCScanning;
        public static bool cancel_spi_dump;
        public static bool check_connection_ok;

        public static u8 timming_byte;
        public static u8 ir_max_frag_no;

        public static bool enable_traffic_dump = false;
        public static bool enable_hid_listings = false;

        public static IntPtr handle;

        ////////////////////////
        // C runtime helpers
        ////////////////////////

        public static void memset(void* dst, int value, int size)
        {
            byte* p = (byte*)dst;
            for (int i = 0; i < size; i++)
                p[i] = (byte)value;
        }

        public static void memcpy(void* dst, void* src, int size)
        {
            Buffer.MemoryCopy(src, dst, size, size);
        }

        public static void Sleep(int ms)
        {
            if (ms > 0)
                Thread.Sleep(ms);
        }

        // The Windows build is a GUI app without a console, so printf output was never visible.
        public static void printf(string format, params object[] args)
        {
        }

        public static int CLAMP(int value, int low, int high)
        {
            return value < low ? low : (value > high ? high : value);
        }

        public static u16 CLAMP(u16 value, u16 low, u16 high)
        {
            return value < low ? low : (value > high ? high : value);
        }

        public static float CLAMP(float value, float low, float high)
        {
            return value < low ? low : (value > high ? high : value);
        }

        ////////////////////////
        // hidapi (hidraw backend)
        ////////////////////////

        const string HidLib = "libhidapi-hidraw.so.0";
        const int OutputReportLength = 49;

        [StructLayout(LayoutKind.Sequential)]
        public struct hid_device_info
        {
            public IntPtr path;
            public ushort vendor_id;
            public ushort product_id;
            public IntPtr serial_number;
            public ushort release_number;
            public IntPtr manufacturer_string;
            public IntPtr product_string;
            public ushort usage_page;
            public ushort usage;
            public int interface_number;
            public hid_device_info* next;
        }

        [DllImport(HidLib)] public static extern int hid_init();
        [DllImport(HidLib)] public static extern int hid_exit();
        [DllImport(HidLib)] public static extern hid_device_info* hid_enumerate(ushort vendor_id, ushort product_id);
        [DllImport(HidLib)] public static extern void hid_free_enumeration(hid_device_info* devs);
        [DllImport(HidLib)] static extern IntPtr hid_open(ushort vendor_id, ushort product_id, IntPtr serial_number);
        [DllImport(HidLib)] static extern IntPtr hid_open_path(IntPtr path);
        [DllImport(HidLib, EntryPoint = "hid_write")] static extern int native_hid_write(IntPtr dev, byte* data, UIntPtr length);
        [DllImport(HidLib, EntryPoint = "hid_read_timeout")] static extern int native_hid_read_timeout(IntPtr dev, byte* data, UIntPtr length, int milliseconds);
        [DllImport(HidLib, EntryPoint = "hid_close")] static extern void native_hid_close(IntPtr dev);

        public static void hid_close(IntPtr dev)
        {
            if (fake == null)
                native_hid_close(dev);
            else
                fake.closes++;
        }

        // Software controller used by --selftest and --demo instead of a real device.
        public static FakeJoyCon fake;

        public static IntPtr hid_open(int vendor_id, int product_id)
        {
            if (fake != null)
                return (vendor_id == 0x57e && product_id == fake.product_id) ? FakeJoyCon.Handle : IntPtr.Zero;
            return hid_open((ushort)vendor_id, (ushort)product_id, IntPtr.Zero);
        }

        public static IntPtr hid_open_path(string path)
        {
            IntPtr p = Marshal.StringToHGlobalAnsi(path);
            try {
                return hid_open_path(p);
            }
            finally {
                Marshal.FreeHGlobal(p);
            }
        }

        public static int hid_write(IntPtr dev, u8* data, int length)
        {
            if (dev == IntPtr.Zero)
                return -1;

            // Windows pads every write to the output report length. Do the same.
            byte* buf = stackalloc byte[OutputReportLength];
            if (length < OutputReportLength) {
                memset(buf, 0, OutputReportLength);
                memcpy(buf, data, length);
                data = buf;
                length = OutputReportLength;
            }

            // Optional minimum spacing between IR/NFC MCU reports (0x11), in case a controller
            // drops output reports that arrive back to back (hidraw queues writes, Windows blocks).
            // Off by default: a real Joy-Con (R) log showed it answering every ACK within ~15ms.
            if (data[0] == 0x11 && fake == null && mcu_write_gap_ms > 0) {
                long wait = mcu_write_gap_ms - (write_clock.ElapsedMilliseconds - last_mcu_write_ms);
                if (wait > 0)
                    Thread.Sleep((int)wait);
            }

            if (enable_traffic_dump)
                traffic_log("W: ", data, length, false);

            if (fake != null)
                return fake.Write(data, length);
            long write_start = trace_start();
            int res = native_hid_write(dev, data, (UIntPtr)length);
            trace_slow("hid_write", write_start);
            if (data[0] == 0x11)
                last_mcu_write_ms = write_clock.ElapsedMilliseconds;
            return res;
        }

        static readonly System.Diagnostics.Stopwatch write_clock = System.Diagnostics.Stopwatch.StartNew();
        static long last_mcu_write_ms = -1000;
        // JCTOOL_MCU_WRITE_GAP_MS=15 (for example) turns the spacing on.
        static readonly int mcu_write_gap_ms = ParseGap(Environment.GetEnvironmentVariable("JCTOOL_MCU_WRITE_GAP_MS"), 0);

        static int ParseGap(string value, int fallback)
        {
            int ms;
            return value != null && int.TryParse(value, out ms) && ms >= 0 ? ms : fallback;
        }

        public static int hid_read_timeout(IntPtr dev, u8* data, int length, int milliseconds)
        {
            if (dev == IntPtr.Zero)
                return -1;

            if (length == 0) {
                // On Windows a 0-length read waits for a report and drops it. Keep that.
                byte* scratch = stackalloc byte[0x170];
                int got = fake != null ? fake.Read(scratch, 0x170, milliseconds)
                                       : native_hid_read_timeout(dev, scratch, (UIntPtr)0x170, milliseconds);
                if (got < 0)
                    return -1;
                if (got > 0 && enable_traffic_dump)
                    traffic_log("R: ", scratch, 0, true);
                return 0;
            }

            int res = fake != null ? fake.Read(data, length, milliseconds)
                                   : native_hid_read_timeout(dev, data, (UIntPtr)length, milliseconds);
            if (res > 0 && enable_traffic_dump)
                traffic_log("R: ", data, res, false);
            else if (res == 0 && enable_traffic_dump && traffic_timestamps)
                traffic_log("R: timeout after " + milliseconds + "ms ", data, 0, false);
            return res;
        }

        public static int hid_read(IntPtr dev, u8* data, int length)
        {
            return hid_read_timeout(dev, data, length, -1);
        }

        // JCTOOL_TIMESTAMPS=1 adds a millisecond timestamp to each -d log line and also logs
        // read timeouts, to find where time goes (e.g. slow IR transfers). Off by default so
        // the log matches the Windows build's format.
        static readonly bool traffic_timestamps = Environment.GetEnvironmentVariable("JCTOOL_TIMESTAMPS") == "1";
        static readonly System.Diagnostics.Stopwatch traffic_clock = System.Diagnostics.Stopwatch.StartNew();

        // traffic_log.txt stays open instead of being reopened for every packet (twice per IR
        // fragment). AutoFlush keeps it complete if the app crashes. Both the UI and the IR
        // thread can log, so writes are locked.
        static readonly object traffic_lock = new object();
        static StreamWriter traffic_writer;

        static void traffic_append(string text)
        {
            lock (traffic_lock) {
                if (traffic_writer == null)
                    traffic_writer = new StreamWriter("./traffic_log.txt", true) { AutoFlush = true };
                traffic_writer.Write(text);
            }
        }

        // With JCTOOL_TIMESTAMPS=1 and -d, log any traced step that takes 30ms or more.
        public static long trace_start()
        {
            return traffic_timestamps ? traffic_clock.ElapsedMilliseconds : 0;
        }

        public static void trace_slow(string what, long start)
        {
            if (!traffic_timestamps || !enable_traffic_dump)
                return;
            long took = traffic_clock.ElapsedMilliseconds - start;
            if (took < 30)
                return;
            try {
                traffic_append(String.Format("[{0,10:F1}] SLOW {1}: {2}ms\n\n",
                    traffic_clock.Elapsed.TotalMilliseconds, what, took));
            }
            catch (Exception) {
            }
        }

        static void traffic_log(string prefix, u8* data, int length, bool zero_length_read)
        {
            try {
                var sb = new StringBuilder();
                if (traffic_timestamps)
                    sb.AppendFormat("[{0,10:F1}] ", traffic_clock.Elapsed.TotalMilliseconds);
                sb.Append(prefix);
                for (int i = 0; i < length; i++)
                    sb.AppendFormat("{0:x2} ", data[i]);
                if (zero_length_read)
                    sb.Append("Requested hid read length was 0 bytes.");
                sb.Append("\n\n");
                traffic_append(sb.ToString());
            }
            catch (Exception) {
                enable_traffic_dump = false;
            }
        }

        // wchar_t is 4 bytes (UTF-32) on Linux.
        public static string wchar_to_string(IntPtr p)
        {
            if (p == IntPtr.Zero)
                return null;
            if (Environment.OSVersion.Platform != PlatformID.Unix)
                return Marshal.PtrToStringUni(p);
            var bytes = new System.Collections.Generic.List<byte>();
            for (int off = 0; ; off += 4) {
                int ch = Marshal.ReadInt32(p, off);
                if (ch == 0)
                    break;
                bytes.AddRange(BitConverter.GetBytes(ch));
            }
            return Encoding.UTF32.GetString(bytes.ToArray());
        }
    }
}
