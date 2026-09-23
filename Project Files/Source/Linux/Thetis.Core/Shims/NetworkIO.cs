/*  NetworkIO.cs

This file is part of a program that implements a Software-Defined Radio.

The parts of Console/HPSDR/NetworkIO.cs that do not depend on the WinForms
setup form.  NetworkIOImports.cs (the P/Invoke half of this partial class)
is compiled unmodified from upstream.

Copyright (C) 2006 Bill Tracey, KD5TFD
Copyright (C) 2010-2020 Doug Wigley
Copyright (C) 2020-2026 Richard Samphire MW0LGE

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Runtime.InteropServices;

namespace Thetis
{
    unsafe partial class NetworkIO
    {
        public static HPSDRHW BoardID { get; set; } = HPSDRHW.Hermes;
        public static RadioProtocol CurrentRadioProtocol { get; set; } = RadioProtocol.ETH;
        public static RadioProtocol SelectedRadioProtocol { get; set; } = RadioProtocol.ETH;
        public static byte BetaVersion { get; set; } = 0;
        public static byte FWCodeVersion { get; set; } = 0;
        public static byte Protocol2VersionSupported { get; set; } = 0;
        public static bool FWVersionsChecked { get; set; } = false;
        public static string GetFWVersionErrorMsg { get; set; } = "";
        public static string BoardMismatch { get; set; } = "";

        // The upstream declaration of nativeInitMetis omits the final
        // p2hw_uses_differnt_ports argument, so the native side reads an
        // undefined value.  Declare the full signature here.
        [DllImport("ChannelMaster.dll", EntryPoint = "nativeInitMetis", CallingConvention = CallingConvention.Cdecl)]
        public static extern int nativeInitMetis(string netaddr, int port, string localaddr, int localport,
                                                 int protocol, int model_id, int p2hw_uses_different_ports);

        private static float _swr_protect = 1.0f;
        public static float SWRProtect
        {
            get { return _swr_protect; }
            set { _swr_protect = value; }
        }

        public static void SetOutputPower(float f)
        {
            if (f < 0.0) f = 0.0F;
            if (f >= 1.0) f = 1.0F;
            int i = (int)(255 * f * _swr_protect);
            SetOutputPowerFactor(i);
        }

        private static double[][] _lastVFOfreq = new double[2][] { new double[] { 0.0, 0.0, 0.0, 0.0 }, new double[] { 0.0 } };
        public static void VFOfreq(int id, double f, int tx)
        {
            _lastVFOfreq[tx][id] = f;
            // rounded, not truncated as upstream does: 7.1005 MHz would otherwise become 7100499 Hz
            int f_freq = (int)Math.Round((f * 1e6) * _freq_correction_factor);
            if (f_freq >= 0)
                if (CurrentRadioProtocol == RadioProtocol.USB)
                    SetVFOfreq(id, f_freq, tx);                    // sending freq Hz to firmware
                else SetVFOfreq(id, Freq2PhaseWord(f_freq), tx);   // sending phaseword to firmware
        }

        private static double _freq_correction_factor = 1.0;
        public static double FreqCorrectionFactor
        {
            get { return _freq_correction_factor; }
            set
            {
                _freq_correction_factor = value;
                VFOfreq(0, _lastVFOfreq[0][0], 0);
                VFOfreq(1, _lastVFOfreq[0][1], 0);
                VFOfreq(2, _lastVFOfreq[0][2], 0);
                VFOfreq(3, _lastVFOfreq[0][3], 0);
                VFOfreq(0, _lastVFOfreq[1][0], 1);
            }
        }

        public static int Freq2PhaseWord(int freq)                     // freq to phaseword conversion
        {
            long pw = (long)Math.Pow(2, 32) * freq / 122880000;
            return (int)pw;
        }

        public static double LowFreqOffset { get; set; }
        public static double HighFreqOffset { get; set; }
    }
}
