/*  cmaster.cs

This file is part of a program that implements a Software-Defined Radio.

Hand-written half of the partial 'cmaster' class for the Linux build.  The
P/Invoke table and the router tables (CMLoadRouterAll) are copied verbatim
from Console/cmaster.cs into Upstream/cmaster.g.cs by tools/sync_upstream.py;
this file holds the state and start-up logic, without the TCI server, wave
recorder/player and scope that the Windows console wires in.

Copyright (C) 2000-2025 Original authors
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
    unsafe partial class cmaster
    {
        private static int cmRCVR = 5;              // number of receivers
        public static int CMrcvr { get { return cmRCVR; } }

        private static int cmSubRCVR = 2;           // number of sub-receivers per receiver
        public static int CMsubrcvr { get { return cmSubRCVR; } }

        private static int ps_rate = 192000;
        public static int PSrate { get { return ps_rate; } }

        private static bool ps_loopback = false;

        #region callbacks

        // ChannelMaster calls these through function pointers; the create_* ones
        // unconditionally during CreateRadio().  Scope, wave player and wave
        // recorder are not ported yet, so their run flags stay 0 and the
        // per-receiver callbacks are never used.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void PushVoxCallback(int id, int active);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void CreateCallback(int id);

        [DllImport("ChannelMaster.dll", EntryPoint = "SendCBPushVox", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SendCBPushVoxNative(int id, PushVoxCallback del);
        [DllImport("ChannelMaster.dll", EntryPoint = "SendCBCreateScope", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SendCBCreateScopeNative(CreateCallback del);
        [DllImport("ChannelMaster.dll", EntryPoint = "SendCBCreateWPlay", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SendCBCreateWPlayNative(CreateCallback del);
        [DllImport("ChannelMaster.dll", EntryPoint = "SendCBCreateWRecord", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SendCBCreateWRecordNative(CreateCallback del);

        // kept in static fields so the GC never collects a delegate native code still points at
        private static readonly PushVoxCallback _pushVox = (id, active) => PushVox?.Invoke(id, active != 0);
        private static readonly CreateCallback _noCreate = id => { };

        /// <summary>Raised on a ChannelMaster thread when VOX keys (true) or unkeys (false) transmitter 'id'.</summary>
        public static event Action<int, bool> PushVox;

        public static void SendCallbacks()
        {
            SendCBPushVoxNative(0, _pushVox);
            SendCBCreateScopeNative(_noCreate);
            SendCBCreateWPlayNative(_noCreate);
            SendCBCreateWRecordNative(_noCreate);
        }

        #endregion

        public static void CMSetAudioVolume(double volume)
        {
            SetAAudioMixVolume((void*)0, 0, volume);
        }

        /// <summary>Port of the Console's cmaster.CMCreateCMaster().</summary>
        public static void CMCreateCMaster()
        {
            // set radio structure
            int[] cmSPC = new int[1] { 2 };
            int[] cmInboundSize = new int[8] { 240, 240, 240, 240, 240, 720, 240, 240 };
            fixed (int* pcmSPC = cmSPC, pcmIbSize = cmInboundSize)
                SetRadioStructure(8, cmRCVR, 1, cmSubRCVR, 1, pcmSPC, pcmIbSize, 1536000, 48000, 384000);

            // send function pointers
            SendCallbacks();

            // set default rates
            int[] xcm_inrates = new int[8] { 192000, 192000, 192000, 192000, 192000, 48000, 192000, 192000 };
            int aud_outrate = 48000;
            int[] rcvr_ch_outrates = new int[5] { 48000, 48000, 48000, 48000, 48000 };
            int[] xmtr_ch_outrates = new int[1] { 192000 };
            fixed (int* p1 = xcm_inrates, p2 = rcvr_ch_outrates, p3 = xmtr_ch_outrates)
                SetCMDefaultRates(p1, aud_outrate, p2, p3);

            // create receivers, transmitters, specials, and buffers
            CreateRadio();

            // get transmitter identifiers
            int txinid = inid(1, 0);        // stream id
            int txch = chid(txinid, 0);     // wdsp channel

            // setup transmitter input sample rate here since it is fixed
            SetXcmInrate(txinid, 48000);

            // CFIR always runs with new protocol firmware
            WDSP.SetTXACFIRRun(txch, NetworkIO.CurrentRadioProtocol != RadioProtocol.USB);

            // set PureSignal basic parameters
            SetPSRxIdx(0, 0);   // txid = 0, all current models use Stream0 for RX feedback
            SetPSTxIdx(0, 1);   // txid = 0, all current models use Stream1 for TX feedback
            puresignal.SetPSFeedbackRate(txch, ps_rate);

            // setup transmitter display
            WDSP.TXASetSipMode(txch, 1);            // 1=>call the appropriate 'analyzer'
            WDSP.TXASetSipDisplay(txch, txinid);    // disp = txinid = tx stream

            NetworkIO.CreateRNet();
        }
    }
}
