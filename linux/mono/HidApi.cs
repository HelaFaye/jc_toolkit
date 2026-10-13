// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Minimal P/Invoke binding to hidapi's hidraw backend (libhidapi-hidraw.so.0).
// Covers exactly the hidapi calls jctool.cpp uses, so the protocol code can be
// ported to C# line by line.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace JcToolkit.Linux
{
    public sealed class HidDeviceInfo
    {
        public string Path;
        public ushort VendorId;
        public ushort ProductId;
        public string SerialNumber;
        public ushort ReleaseNumber;
        public string ManufacturerString;
        public string ProductString;
        public ushort UsagePage;
        public ushort Usage;
        public int InterfaceNumber;
    }

    public static class HidApi
    {
        const string Lib = "libhidapi-hidraw.so.0";

        // Mirrors struct hid_device_info up to 'next'. Fields added after 'next'
        // in newer hidapi versions (bus_type) are never read, so the layout stays
        // compatible with hidapi 0.10 through 0.14+.
        [StructLayout(LayoutKind.Sequential)]
        struct NativeDeviceInfo
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
            public IntPtr next;
        }

        [DllImport(Lib)] public static extern int hid_init();
        [DllImport(Lib)] public static extern int hid_exit();
        [DllImport(Lib)] static extern IntPtr hid_enumerate(ushort vendor_id, ushort product_id);
        [DllImport(Lib)] static extern void hid_free_enumeration(IntPtr devs);
        [DllImport(Lib)] public static extern IntPtr hid_open(ushort vendor_id, ushort product_id, IntPtr serial_number);
        [DllImport(Lib)] public static extern IntPtr hid_open_path([MarshalAs(UnmanagedType.LPStr)] string path);
        [DllImport(Lib)] public static extern int hid_write(IntPtr dev, byte[] data, UIntPtr length);
        [DllImport(Lib)] public static extern int hid_read(IntPtr dev, byte[] data, UIntPtr length);
        [DllImport(Lib)] public static extern int hid_read_timeout(IntPtr dev, byte[] data, UIntPtr length, int milliseconds);
        [DllImport(Lib)] public static extern void hid_close(IntPtr dev);

        public static int Write(IntPtr dev, byte[] data, int length) { return hid_write(dev, data, (UIntPtr)length); }
        public static int Read(IntPtr dev, byte[] data, int length) { return hid_read(dev, data, (UIntPtr)length); }
        public static int ReadTimeout(IntPtr dev, byte[] data, int length, int ms) { return hid_read_timeout(dev, data, (UIntPtr)length, ms); }

        public static List<HidDeviceInfo> Enumerate(ushort vendorId = 0, ushort productId = 0)
        {
            var list = new List<HidDeviceInfo>();
            IntPtr devs = hid_enumerate(vendorId, productId);
            for (IntPtr cur = devs; cur != IntPtr.Zero; ) {
                var n = (NativeDeviceInfo)Marshal.PtrToStructure(cur, typeof(NativeDeviceInfo));
                list.Add(new HidDeviceInfo {
                    Path               = Marshal.PtrToStringAnsi(n.path),
                    VendorId           = n.vendor_id,
                    ProductId          = n.product_id,
                    SerialNumber       = PtrToWideString(n.serial_number),
                    ReleaseNumber      = n.release_number,
                    ManufacturerString = PtrToWideString(n.manufacturer_string),
                    ProductString      = PtrToWideString(n.product_string),
                    UsagePage          = n.usage_page,
                    Usage              = n.usage,
                    InterfaceNumber    = n.interface_number,
                });
                cur = n.next;
            }
            hid_free_enumeration(devs);
            return list;
        }

        // wchar_t is 4 bytes (UTF-32) on Linux and 2 bytes (UTF-16) on Windows.
        // Marshal.PtrToStringUni assumes UTF-16, so decode by hand.
        static string PtrToWideString(IntPtr p)
        {
            if (p == IntPtr.Zero)
                return null;
            if (Environment.OSVersion.Platform != PlatformID.Unix)
                return Marshal.PtrToStringUni(p);
            var bytes = new List<byte>();
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
