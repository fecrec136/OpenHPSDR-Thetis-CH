/*  SerialPorts.cs

This file is part of a program that implements a Software-Defined Radio.

The serial ports of this computer, for CAT and PTT: USB to RS232 adapters
(ttyUSB: FTDI, Prolific, CH340, CP210x; ttyACM: CDC devices), the
motherboard's or a PCI card's serial ports (ttyS), and others (ttyAMA,
rfcomm).  USB adapters are offered under their /dev/serial/by-id name,
which stays the same when the adapter is plugged into another socket or
the ttyUSB numbers change.  Only ttyS ports that are really there are
listed (the kernel always creates ttyS0..3).

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Thetis.Cat
{
    /// <param name="Path">What to open (the by-id name for USB adapters).</param>
    /// <param name="Device">The tty it is now (/dev/ttyUSB0).</param>
    /// <param name="Description">For people: "ttyUSB0: FTDI FT232R USB UART (A10K1234)".</param>
    /// <param name="Accessible">False if this user may not open it (not in the dialout group).</param>
    public sealed record SerialPortInfo(string Path, string Device, string Description, bool Usb, bool Accessible);

    public static class SerialPorts
    {
        public const string DialoutHint =
            "This user may not open serial ports. Run: sudo usermod -aG dialout $USER  and log out and in again.";

        public static IReadOnlyList<SerialPortInfo> List()
        {
            var ports = new List<SerialPortInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // USB adapters, under their stable names
            const string byId = "/dev/serial/by-id";
            if (Directory.Exists(byId))
            {
                foreach (string link in Directory.GetFileSystemEntries(byId).OrderBy(p => p, StringComparer.Ordinal))
                {
                    string dev = Resolve(link);
                    if (dev == null || !seen.Add(dev)) continue;
                    ports.Add(new SerialPortInfo(link, dev, Path.GetFileName(dev) + ": " + DescribeById(Path.GetFileName(link)),
                                                 true, CanOpen(dev)));
                }
            }

            foreach (string dev in Devices())
            {
                if (!seen.Add(dev)) continue;
                string name = Path.GetFileName(dev);
                bool usb = name.StartsWith("ttyUSB", StringComparison.Ordinal) || name.StartsWith("ttyACM", StringComparison.Ordinal);
                string what = name.StartsWith("ttyS", StringComparison.Ordinal) ? "serial port"
                            : usb ? UsbProduct(name) ?? "USB serial"
                            : name.StartsWith("rfcomm", StringComparison.Ordinal) ? "Bluetooth serial"
                            : "serial port";
                ports.Add(new SerialPortInfo(dev, dev, name + ": " + what, usb, CanOpen(dev)));
            }
            return ports;
        }

        /// <summary>The tty a path is now (resolving by-id and other links).</summary>
        public static string Resolve(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                var target = fi.ResolveLinkTarget(true);
                return target?.FullName ?? (fi.Exists ? fi.FullName : null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IEnumerable<string> Devices()
        {
            IEnumerable<string> Glob(string pattern)
            {
                try { return Directory.GetFiles("/dev", pattern); }
                catch (Exception) { return Array.Empty<string>(); }
            }
            foreach (string p in new[] { "ttyUSB*", "ttyACM*", "ttyAMA*", "ttyTHS*", "rfcomm*" })
                foreach (string d in Glob(p).OrderBy(NaturalKey)) yield return d;
            foreach (string d in Glob("ttyS*").OrderBy(NaturalKey))
                if (IsRealUart(Path.GetFileName(d))) yield return d;
        }

        private static string NaturalKey(string s)
        {
            var m = Regex.Match(s, @"^(.*?)(\d+)$");
            return m.Success ? m.Groups[1].Value + m.Groups[2].Value.PadLeft(4, '0') : s;
        }

        // the kernel's placeholder 8250 ports have the platform driver "serial8250" and no hardware;
        // real ones belong to a PNP / ACPI / PCI device ("serial", "8250_pci", "exar_serial", ...)
        private static bool IsRealUart(string name)
        {
            string dev = "/sys/class/tty/" + name + "/device";
            if (!Directory.Exists(dev)) return false;
            string driver = Path.GetFileName(Resolve(Path.Combine(dev, "driver")) ?? "");
            if (driver != "serial8250") return driver.Length > 0;
            // a platform port can still be real if it has an I/O port (irq and port in sysfs)
            try
            {
                string port = File.ReadAllText("/sys/class/tty/" + name + "/port").Trim();
                string irq = File.ReadAllText("/sys/class/tty/" + name + "/irq").Trim();
                return port != "0x0" && irq != "0" && File.Exists(Path.Combine(dev, "resources"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // usb-FTDI_FT232R_USB_UART_A10K1234-if00-port0 -> "FTDI FT232R USB UART (A10K1234)"
        private static string DescribeById(string id)
        {
            string s = Regex.Replace(id, @"^usb-", "");
            s = Regex.Replace(s, @"-if\d+(-port\d+)?$", "");
            int us = s.LastIndexOf('_');
            string serial = null;
            if (us > 0 && Regex.IsMatch(s.Substring(us + 1), @"^[0-9A-Za-z]{4,}$") && Regex.IsMatch(s.Substring(us + 1), @"\d"))
            {
                serial = s.Substring(us + 1);
                s = s.Substring(0, us);
            }
            s = s.Replace('_', ' ').Trim();
            return serial != null ? $"{s} ({serial})" : s;
        }

        private static string UsbProduct(string tty)
        {
            // /sys/class/tty/ttyUSB0/device -> .../1-2:1.0/ttyUSB0; product is two levels up
            try
            {
                string dev = Resolve("/sys/class/tty/" + tty + "/device");
                for (int i = 0; i < 3 && dev != null; i++)
                {
                    string product = Path.Combine(dev, "product");
                    if (File.Exists(product))
                    {
                        string maker = Path.Combine(dev, "manufacturer");
                        string m = File.Exists(maker) ? File.ReadAllText(maker).Trim() + " " : "";
                        return m + File.ReadAllText(product).Trim();
                    }
                    dev = Path.GetDirectoryName(dev);
                }
            }
            catch (Exception) { }
            return null;
        }

        private const int R_OK = 4, W_OK = 2;

        [DllImport("libc", SetLastError = true)]
        private static extern int access(string path, int mode);

        public static bool CanOpen(string path)
        {
            try { return access(path, R_OK | W_OK) == 0; }
            catch (Exception) { return true; }
        }
    }
}
