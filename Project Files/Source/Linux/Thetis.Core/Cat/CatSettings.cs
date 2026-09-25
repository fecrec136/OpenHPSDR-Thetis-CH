/*  CatSettings.cs

This file is part of a program that implements a Software-Defined Radio.

CAT and TCI settings (Setup > CAT / TCI): up to four CAT ports, each a
serial port (a motherboard port or a USB to RS232 adapter) or a virtual
port for programs on this computer; the CAT server on TCP; the TCI server.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System.Collections.Generic;

namespace Thetis.Cat
{
    public enum CatPortKind { Serial, Virtual }

    public enum CatParity { None, Odd, Even }

    public enum CatHandshake { None, RtsCts, XonXoff }

    /// <summary>A modem control input that keys the transmitter (a footswitch, a data interface).</summary>
    public enum PttLine { None, Cts, Dsr, Dcd }

    public sealed class CatPortSettings
    {
        public bool Enabled { get; set; }
        public CatPortKind Kind { get; set; } = CatPortKind.Serial;
        /// <summary>Serial: the device (/dev/ttyUSB0, /dev/ttyS0, or a /dev/serial/by-id name).</summary>
        public string Device { get; set; } = "";
        /// <summary>Virtual: the name programs open (a link to the pseudo-terminal).</summary>
        public string VirtualPath { get; set; } = "";
        public int Baud { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public CatParity Parity { get; set; } = CatParity.None;
        public int StopBits { get; set; } = 1;
        public CatHandshake Handshake { get; set; } = CatHandshake.None;
        /// <summary>Hold DTR / RTS on (some interfaces take power from them).</summary>
        public bool Dtr { get; set; }
        public bool Rts { get; set; }
        /// <summary>Serial: key the transmitter while this input is on.</summary>
        public PttLine Ptt { get; set; } = PttLine.None;
        public bool PttInvert { get; set; }
    }

    public sealed class CatSettings
    {
        public const int PortCount = 4;
        public const int DefaultTcpPort = 31001;        // TCPIPcatServer.DEFAULT_PORT
        public const int DefaultTciPort = 50001;        // the console's TCI server

        public List<CatPortSettings> Ports { get; set; } = new List<CatPortSettings>();

        public bool TcpEnabled { get; set; }
        public int TcpPort { get; set; } = DefaultTcpPort;
        /// <summary>Listen on all network interfaces (default: this computer only).</summary>
        public bool TcpAllInterfaces { get; set; }

        public bool TciEnabled { get; set; }
        public int TciPort { get; set; } = DefaultTciPort;
        public bool TciAllInterfaces { get; set; }

        /// <summary>ID answer: 19 TS-2000, 20 TS-480, 13 TS-50S, 900 SDR-1000.</summary>
        public int RigId { get; set; } = 19;
        public bool DigUIsUsb { get; set; }
        public bool AllowAutoInformation { get; set; } = true;

        /// <summary>Four ports, the missing ones filled in.</summary>
        public void Normalize(string virtualDirectory)
        {
            Ports ??= new List<CatPortSettings>();
            while (Ports.Count < PortCount) Ports.Add(new CatPortSettings());
            if (Ports.Count > PortCount) Ports.RemoveRange(PortCount, Ports.Count - PortCount);
            for (int i = 0; i < Ports.Count; i++)
            {
                Ports[i] ??= new CatPortSettings();
                if (string.IsNullOrEmpty(Ports[i].VirtualPath) && virtualDirectory != null)
                    Ports[i].VirtualPath = System.IO.Path.Combine(virtualDirectory, "cat" + (i + 1));
            }
        }
    }
}
