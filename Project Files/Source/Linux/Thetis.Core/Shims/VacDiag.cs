/*  VacDiag.cs

This file is part of a program that implements a Software-Defined Radio.

Diagnostic counters for VAC (PC audio) added to ChannelMaster's ivac.c.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System.Runtime.InteropServices;

namespace Thetis
{
    public static class VacDiag
    {
        /// <summary>Frames the sound device has requested from VAC 'id' since start-up.</summary>
        [DllImport("ChannelMaster.dll", EntryPoint = "getIVACframes", CallingConvention = CallingConvention.Cdecl)]
        public static extern long Frames(int id);

        /// <summary>The sample rate PortAudio reports for VAC 'id's open stream (0 if none).</summary>
        [DllImport("ChannelMaster.dll", EntryPoint = "getIVACstreamRate", CallingConvention = CallingConvention.Cdecl)]
        public static extern double StreamRate(int id);

        /// <summary>Peak level (1.0 = full scale) of the audio handed to the sound device since the last call.</summary>
        [DllImport("ChannelMaster.dll", EntryPoint = "getIVACoutPeak", CallingConvention = CallingConvention.Cdecl)]
        public static extern double OutPeak(int id);

        /// <summary>Callbacks where PortAudio reported an under/overflow: output = false the input (microphone), true the output (speakers).</summary>
        [DllImport("ChannelMaster.dll", EntryPoint = "getIVACxruns", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Xruns(int id, bool output);

        [DllImport("ChannelMaster.dll", EntryPoint = "getIVACdiags", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe void getIVACdiags(int id, int type, int* underflows, int* overflows, double* var, int* ringsize, int* nring);

        /// <summary>Ring-buffer diagnostics; type 0: receiver audio to the device, 1: device (microphone) to the transmitter.</summary>
        public static unsafe (int underflows, int overflows, double ratio, int ring, int ringSize) Diags(int id, int type)
        {
            int u, o, rs, nr;
            double v;
            getIVACdiags(id, type, &u, &o, &v, &rs, &nr);
            return (u, o, v, nr, rs);
        }
    }
}
