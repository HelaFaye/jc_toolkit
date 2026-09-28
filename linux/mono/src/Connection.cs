// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Port of the connection code and Main() from jctool/jctool.cpp.

using System;
using System.Text;
using System.Windows.Forms;

namespace CppWinFormJoy
{
    public static unsafe partial class Jc
    {
        static string accepted_third_party_path;

        public static void output_hid_device_info_list() {
            // Enumerate and output the HID devices on the system
            hid_device_info* devs = hid_enumerate(0x0, 0x0);
            for (hid_device_info* cur_dev = devs; cur_dev != null; cur_dev = cur_dev->next) {
                string product_string      = wchar_to_string(cur_dev->product_string);
                string manufacturer_string = wchar_to_string(cur_dev->manufacturer_string);
                string serial_number       = wchar_to_string(cur_dev->serial_number);
                if (product_string == null && manufacturer_string == null && serial_number == null)
                    continue;

                var dev_info = new StringBuilder();
                dev_info.AppendFormat("HID Device: 0x{0:x4} \"{1}\"\n", cur_dev->product_id, product_string ?? "Unknown Product");
                dev_info.AppendFormat("\tvendor = 0x{0:x4} \"{1}\"\n", cur_dev->vendor_id, manufacturer_string ?? "Unknown Manufacturer");
                dev_info.AppendFormat("\trelease = {0}\n", cur_dev->release_number);
                dev_info.AppendFormat("\tserial = {0}\n", serial_number ?? "Unknown Serial Number");
                dev_info.AppendFormat("\tusage = 0x{0:x4} page: 0x{1:x}\n", cur_dev->usage, cur_dev->usage_page);
                dev_info.Append("\n" + System.Runtime.InteropServices.Marshal.PtrToStringAnsi(cur_dev->path));

                Console.WriteLine(dev_info.ToString() + "\n");
                if (MessageBox.Show(dev_info.ToString(),
                    "CTCaer's Joy-Con Toolkit - HID Device Info Report",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.Cancel) {
                    break;
                }
            }
            hid_free_enumeration(devs);
        }

        // Pseudo-third-party support. Some third-party controllers report as a
        // "Wireless Gamepad" by "Nintendo" instead of using Nintendo's VID/PIDs.
        // hidraw reports an empty manufacturer string for Bluetooth devices, so
        // that is accepted too. The user is asked once per device.
        public static int open_third_party_controller() {
            int result = NOTHING;
            if (fake != null)
                return result;

            hid_device_info* devs = hid_enumerate(0x0, 0x0);
            for (hid_device_info* cur_dev = devs; cur_dev != null; cur_dev = cur_dev->next) {
                string product_string      = wchar_to_string(cur_dev->product_string) ?? "Unknown Product";
                string manufacturer_string = wchar_to_string(cur_dev->manufacturer_string) ?? "";
                string path                = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(cur_dev->path);

                if (product_string == "Wireless Gamepad" && (manufacturer_string == "Nintendo" || manufacturer_string == "") && cur_dev->usage == 0x0005) {
                    bool use_device = accepted_third_party_path != null && accepted_third_party_path == path;
                    if (!use_device) {
                        string third_party_warning = "A potential third-party device has been detected:\n\n\t"
                            + product_string + " : " + (manufacturer_string == "" ? "Unknown Manufacturer" : manufacturer_string) + "\n\n"
                            + "Editing could be potentially unstable. Would you like to use this device anyways?";
                        use_device = MessageBox.Show(third_party_warning,
                            "CTCaer's Joy-Con Toolkit - Third-Party Device Detected",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                    }
                    if (use_device) {
                        handle = hid_open_path(path);
                        if (handle != IntPtr.Zero) {
                            accepted_third_party_path = path;
                            handle_type = PROCON;
                            result = handle_type;
                            break;
                        }
                        else {
                            accepted_third_party_path = null;
                            MessageBox.Show("Could not obtain the device.\nIt's usage has been aborted.",
                                "CTCaer's Joy-Con Toolkit - Third-Party Device Connection Failed",
                                MessageBoxButtons.OK, MessageBoxIcon.Stop);
                        }
                    }
                }
            }
            hid_free_enumeration(devs);
            return result;
        }

        public static int device_connection() {
            if (check_connection_ok) {
                if (enable_hid_listings) {
                    output_hid_device_info_list();
                    enable_hid_listings = false;
                }

                // The IR camera thread is using the handle (e.g. Stop was clicked while
                // streaming). Closing it under that thread's read would crash, and the
                // connection is evidently alive, so keep it.
                if (ir_worker != null && handle != IntPtr.Zero)
                    return handle_type;

                // The Windows code reopened the device on every check without closing the
                // previous handle. Close it first so file descriptors don't pile up.
                if (handle != IntPtr.Zero) {
                    hid_close(handle);
                    handle = IntPtr.Zero;
                }

                handle_type = NOTHING;

                const int vendor_id = 0x57e;
                int[] product_ids = { 0, 0x2006, 0x2007, 0x2009 };
                if (handle_priority != NOTHING) {
                    handle = hid_open(vendor_id, product_ids[handle_priority]);
                    if (handle != IntPtr.Zero) {
                        handle_type = handle_priority;
                        return handle_type;
                    }
                    // Third-party controllers identify as Pro Controllers
                    else if (handle_priority == PROCON) {
                        return open_third_party_controller();
                    }
                    else {
                        return NOTHING;
                    }
                }
                else {
                    foreach (int type in new[] { JOYCON_L, JOYCON_R, PROCON }) {
                        if (type == PROCON)
                            Sleep(1); // adding delay here supposedly makes the handle call work properly
                        handle = hid_open(vendor_id, product_ids[type]);
                        if (handle != IntPtr.Zero) {
                            handle_type = type;
                            return handle_type;
                        }
                    }
                    // Nothing found. Check for third-party controllers
                    return open_third_party_controller();
                }
            }
            return handle_type;
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args) {
            // On Windows the app runs from its own folder, and the SPI backups, IRcamera.png,
            // colors.xml and traffic_log.txt are written next to it. Keep that on Linux.
            try {
                Environment.CurrentDirectory = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
            }
            catch (Exception) {
            }

            if (args.Length > 0 && args[0] == "--selftest")
                return SelfTest.Run();

            // --demo [l|r|pro]: run the full app against an emulated controller (no hardware).
            // Writes only change the emulated controller's memory.
            if (args.Length > 0 && args[0] == "--demo") {
                int type = Jc.PROCON;
                int used = 1;
                if (args.Length > 1 && (args[1] == "l" || args[1] == "r" || args[1] == "pro")) {
                    type = args[1] == "l" ? Jc.JOYCON_L : args[1] == "r" ? Jc.JOYCON_R : Jc.PROCON;
                    used = 2;
                }
                Jc.fake = new FakeJoyCon(type);
                // Keep any following option (-l, -d, -f) for the normal handling below.
                string[] rest = new string[args.Length - used];
                Array.Copy(args, used, rest, 0, rest.Length);
                args = rest;
            }

            try {
                Jc.hid_init();
            }
            catch (DllNotFoundException) {
                MessageBox.Show("libhidapi-hidraw.so.0 was not found!\n\nInstall it with your package manager (Debian/Ubuntu: libhidapi-hidraw0).",
                    "CTCaer's Joy-Con Toolkit - Missing library!", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                return 1;
            }

            Jc.check_connection_ok = true;
            if (args.Length > 0) {
                if (args[0] == "-l") {
                    Jc.enable_hid_listings = true; // List all connected HID device information for first connection check
                }
            }

            while (Jc.device_connection() == 0) {
                if (MessageBox.Show(
                    "The device is not paired or the device was disconnected!\n\n" +
                    "To pair:\n  1. Press and hold the sync button until the leds are on\n" +
                    "  2. Pair the Bluetooth controller (for example with bluetoothctl)\n\nTo connect again:\n" +
                    "  1. Press a button on the controller\n  (If this doesn't work, re-pair.)\n\n" +
                    "Also check that you can open /dev/hidraw* (see linux/udev)\n" +
                    "and that the hid_nintendo driver is not holding the controller.",
                    "CTCaer's Joy-Con Toolkit - Connection Error!",
                    MessageBoxButtons.RetryCancel, MessageBoxIcon.Stop) == DialogResult.Cancel)
                    return 1;
            }
            // Enable debugging
            if (args.Length > 0) {
                if (args[0] == "-d")
                    Jc.enable_traffic_dump = true; // Enable hid_write/read logging to text file
                else if (args[0] == "-f")
                    Jc.check_connection_ok = false;   // Don't check connection after the 1st successful one
            }

            Jc.timming_byte = 0x0;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            FormJoy myform1 = new FormJoy();

            Application.Run(myform1);

            return 0;
        }
    }
}
