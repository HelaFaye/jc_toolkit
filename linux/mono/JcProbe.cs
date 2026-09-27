// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// jcprobe: first milestone of the Linux port. Connects the same way the
// Windows app does and prints what FormJoy's full_refresh() shows
// (device type, firmware, MAC, S/N, colors). It only reads from the controller.
//
//   mono jcprobe.exe          connect (any controller) and print info
//   mono jcprobe.exe -l       list all HID devices first (like jctool -l)
//   mono jcprobe.exe -p pro   prefer a controller type: l, r, pro

using System;
using System.Linq;

namespace JcToolkit.Linux
{
    static class JcProbe
    {
        static int Main(string[] args)
        {
            if (HidApi.hid_init() != 0) {
                Console.Error.WriteLine("hid_init() failed. Is libhidapi-hidraw0 installed?");
                return 1;
            }

            var priority = HandleType.Nothing;
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "-l") {
                    foreach (var d in HidApi.Enumerate())
                        Console.WriteLine("HID Device: 0x{0:x4} \"{1}\"\n\tvendor = 0x{2:x4} \"{3}\"\n\trelease = {4}\n\tserial = {5}\n\tusage = 0x{6:x4} page: 0x{7:x4}\n\t{8}\n",
                            d.ProductId, d.ProductString ?? "Unknown Product", d.VendorId, d.ManufacturerString ?? "Unknown Manufacturer",
                            d.ReleaseNumber, d.SerialNumber ?? "Unknown Serial Number", d.Usage, d.UsagePage, d.Path);
                }
                else if (args[i] == "-p" && i + 1 < args.Length) {
                    switch (args[++i]) {
                        case "l":   priority = HandleType.JoyConL; break;
                        case "r":   priority = HandleType.JoyConR; break;
                        case "pro": priority = HandleType.ProCon;  break;
                    }
                }
            }

            using (var jc = JoyCon.Connect(priority, dev => {
                Console.Write("A potential third-party device has been detected:\n\n\t{0} : {1}\n\n" +
                    "Editing could be potentially unstable. Would you like to use this device anyways? [y/N] ",
                    dev.ProductString, dev.ManufacturerString);
                var answer = Console.ReadLine();
                return answer != null && answer.Trim().ToLowerInvariant().StartsWith("y");
            })) {
                if (jc == null) {
                    Console.Error.WriteLine("The device is not paired or the device was disconnected!\n" +
                        "Check that it is paired, and that you can open /dev/hidraw* (see linux/udev).");
                    HidApi.hid_exit();
                    return 2;
                }

                string[] names = { "None", "Joy-Con (L)", "Joy-Con (R)", "Pro Controller" };
                Console.WriteLine("Device:   {0}", names[(int)jc.Type]);

                var info = jc.GetDeviceInfo();
                if (info != null) {
                    Console.WriteLine("Firmware: {0:X}.{1:X2}", info[0], info[1]);
                    Console.WriteLine("MAC:      {0}", string.Join(":", info.Skip(4).Take(6).Select(b => b.ToString("X2"))));
                }

                if (jc.Type != HandleType.ProCon)
                    Console.WriteLine("S/N:      {0}", jc.GetSerialNumber());

                var colors = jc.GetSpiData(0x6050, jc.Type == HandleType.ProCon ? (byte)12 : (byte)6);
                if (colors != null) {
                    Console.WriteLine("Body:     #{0:X2}{1:X2}{2:X2}", colors[0], colors[1], colors[2]);
                    Console.WriteLine("Buttons:  #{0:X2}{1:X2}{2:X2}", colors[3], colors[4], colors[5]);
                    if (colors.Length == 12) {
                        Console.WriteLine("Grip L:   #{0:X2}{1:X2}{2:X2}", colors[6], colors[7], colors[8]);
                        Console.WriteLine("Grip R:   #{0:X2}{1:X2}{2:X2}", colors[9], colors[10], colors[11]);
                    }
                }
            }
            HidApi.hid_exit();
            return 0;
        }
    }
}
