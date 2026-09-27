// Copyright (c) 2018 CTCaer. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// C# port of the connection logic and basic subcommands from jctool/jctool.cpp.
// Offsets and reply checks are kept identical to the C++ code so the rest of
// jctool.cpp can be ported the same way.

using System;
using System.Linq;
using System.Threading;

namespace JcToolkit.Linux
{
    public enum HandleType { Nothing = 0, JoyConL = 1, JoyConR = 2, ProCon = 3 }

    public sealed class JoyCon : IDisposable
    {
        public const ushort VendorId = 0x57e;
        static readonly ushort[] ProductIds = { 0, 0x2006, 0x2007, 0x2009 };

        public IntPtr Handle { get; private set; }
        public HandleType Type { get; private set; }
        byte timmingByte;

        // Port of device_connection(). thirdPartyPrompt is asked before using a
        // "Wireless Gamepad" by "Nintendo" (pseudo-third-party controller).
        public static JoyCon Connect(HandleType priority = HandleType.Nothing, Func<HidDeviceInfo, bool> thirdPartyPrompt = null)
        {
            var order = priority != HandleType.Nothing
                ? new[] { priority }
                : new[] { HandleType.JoyConL, HandleType.JoyConR, HandleType.ProCon };
            foreach (var type in order) {
                if (type == HandleType.ProCon)
                    Thread.Sleep(1);
                IntPtr h = HidApi.hid_open(VendorId, ProductIds[(int)type], IntPtr.Zero);
                if (h != IntPtr.Zero)
                    return new JoyCon { Handle = h, Type = type };
            }
            if (priority != HandleType.Nothing && priority != HandleType.ProCon)
                return null;

            // hidapi's hidraw backend leaves the manufacturer string empty for
            // Bluetooth devices, so an empty manufacturer is accepted too.
            var candidate = HidApi.Enumerate().FirstOrDefault(d =>
                d.ProductString == "Wireless Gamepad" && d.Usage == 0x0005 &&
                (d.ManufacturerString == "Nintendo" || string.IsNullOrEmpty(d.ManufacturerString)));
            if (candidate != null && thirdPartyPrompt != null && thirdPartyPrompt(candidate)) {
                IntPtr h = HidApi.hid_open_path(candidate.Path);
                if (h != IntPtr.Zero)
                    return new JoyCon { Handle = h, Type = HandleType.ProCon };
            }
            return null;
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                HidApi.hid_close(Handle);
            Handle = IntPtr.Zero;
        }

        // Builds an output report 0x01 (rumble + subcommand) like jctool.cpp's
        // brcm_hdr/brcm_cmd_01 structs: [0]=cmd, [1]=timer, [2..9]=rumble,
        // [10]=subcmd, [11..]=subcmd args.
        byte[] NewSubcmd(byte subcmd)
        {
            var buf = new byte[49];
            buf[0] = 0x01;
            buf[1] = (byte)(timmingByte & 0xF);
            timmingByte++;
            buf[10] = subcmd;
            return buf;
        }

        // Sends a subcommand and waits for a reply whose u16 at 0xD matches ack,
        // with the same retry counts as jctool.cpp.
        byte[] Exchange(Func<byte[]> build, Func<byte[], int, bool> isReply, int maxErrors = 20)
        {
            var buf = new byte[49];
            for (int errors = 0; errors <= maxErrors; errors++) {
                var cmd = build();
                HidApi.Write(Handle, cmd, cmd.Length);
                for (int retries = 0; retries <= 8; retries++) {
                    int res = HidApi.ReadTimeout(Handle, buf, buf.Length, 64);
                    if (isReply(buf, res))
                        return buf;
                    if (res == 0)
                        break;
                }
            }
            return null;
        }

        static ushort U16(byte[] b, int off) { return BitConverter.ToUInt16(b, off); }
        static uint U32(byte[] b, int off) { return BitConverter.ToUInt32(b, off); }

        // Port of get_device_info(): FW major/minor, device type, unknown, MAC.
        public byte[] GetDeviceInfo()
        {
            var reply = Exchange(() => NewSubcmd(0x02), (b, r) => U16(b, 0xD) == 0x0282);
            return reply == null ? null : reply.Skip(0xF).Take(0xA).ToArray();
        }

        // Port of get_spi_data(). read_len must be <= 0x1D for a 49 byte reply.
        public byte[] GetSpiData(uint offset, byte readLen)
        {
            Func<byte[]> build = () => {
                var b = NewSubcmd(0x10);
                BitConverter.GetBytes(offset).CopyTo(b, 11);
                b[15] = readLen;
                return b;
            };
            var reply = Exchange(build, (b, r) => U16(b, 0xD) == 0x1090 && U32(b, 0xF) == offset);
            return reply == null ? null : reply.Skip(0x14).Take(readLen).ToArray();
        }

        // Port of write_spi_data(). Writes to the SPI flash: back it up first.
        public bool WriteSpiData(uint offset, byte[] data)
        {
            Func<byte[]> build = () => {
                var b = NewSubcmd(0x11);
                BitConverter.GetBytes(offset).CopyTo(b, 11);
                b[15] = (byte)data.Length;
                data.CopyTo(b, 0x10);
                return b;
            };
            return Exchange(build, (b, r) => U16(b, 0xD) == 0x1180, 19) != null;
        }

        // Port of get_sn(0x6001, 0xF).
        public string GetSerialNumber()
        {
            var sn = GetSpiData(0x6001, 0xF);
            return sn == null ? "Error!" : new string(sn.Where(c => c != 0).Select(c => (char)c).ToArray());
        }
    }
}
