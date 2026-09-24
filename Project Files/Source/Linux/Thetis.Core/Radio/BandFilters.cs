/*  BandFilters.cs

This file is part of a program that implements a Software-Defined Radio.

Selection of the radio's band filter relays, ported from console.cs:

  * Alex high-pass / band-pass filters for receive (setAlexHPF, and
    setBPF1ForOrionIISaturn for OrionMKII, Saturn and HermesC10 boards)
  * Alex low-pass filters, which the transmitter goes through (setAlexLPF)
  * open-collector (OC) outputs through Penny.cs, used for example by the
    N2ADR filter board of the Hermes-Lite 2

The band edges are the defaults of the Windows Setup form (setup.designer.cs)
and the switching logic is unchanged.  Sending the right low-pass filter is
what keeps harmonics off the air, so these are applied on every frequency
change and whenever the radio switches between receive and transmit.

Copyright (C) 2000-2025 Original authors
Copyright (C) 2020-2026 Richard Samphire MW0LGE

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System.Collections.Generic;

namespace Thetis.Radio
{
    public static class BandFilters
    {
        // Alex HPF bits (NetworkIO.SetAlexHPFBits)
        public const int Hpf13MHz = 0x01, Hpf20MHz = 0x02, Hpf9_5MHz = 0x04, Hpf6_5MHz = 0x08,
                         Hpf1_5MHz = 0x10, HpfBypass = 0x20, Bpf6mLna = 0x40;
        // Alex LPF bits (NetworkIO.SetAlexLPFBits)
        public const int Lpf30_20 = 0x01, Lpf60_40 = 0x02, Lpf80 = 0x04, Lpf160 = 0x08,
                         Lpf6 = 0x10, Lpf12_10 = 0x20, Lpf17_15 = 0x40;

        // --- defaults from setup.designer.cs (udAlex*LPF*, udAlex*HPF*, ud*BPF1*) ---
        private static readonly (double start, double end, int bits)[] _lpf =
        {
            (8.000001, 16.5, Lpf30_20),     // udAlex20mLPF
            (5.000001, 8.0, Lpf60_40),      // udAlex40mLPF
            (2.500001, 5.0, Lpf80),         // udAlex80mLPF
            (0.0, 2.5, Lpf160),             // udAlex160mLPF
            (35.600001, 61.44, Lpf6),       // udAlex6mLPF
            (24.000001, 35.6, Lpf12_10),    // udAlex10mLPF
            (16.500001, 24.0, Lpf17_15),    // udAlex15mLPF
        };
        private static readonly (double start, double end, int bits)[] _alexHpf =
        {
            (1.8, 6.499999, Hpf1_5MHz),     // udAlex1_5HPF
            (6.5, 9.499999, Hpf6_5MHz),     // udAlex6_5HPF
            (9.5, 12.999999, Hpf9_5MHz),    // udAlex9_5HPF
            (13.0, 19.999999, Hpf13MHz),    // udAlex13HPF
            (20.0, 49.999999, Hpf20MHz),    // udAlex20HPF
            (50.0, 61.44, Bpf6mLna),        // udAlex6BPF
        };
        private static readonly (double start, double end, int bits)[] _bpf1 =
        {
            (1.5, 2.099999, Hpf1_5MHz),     // ud1_5BPF1
            (2.1, 5.499999, Hpf6_5MHz),     // ud6_5BPF1
            (5.5, 10.999999, Hpf9_5MHz),    // ud9_5BPF1
            (11.0, 21.999999, Hpf13MHz),    // ud13BPF1
            (22.0, 34.999999, Hpf20MHz),    // ud20BPF1
            (35.0, 61.44, Bpf6mLna),        // ud6BPF1
        };

        /// <summary>Alex / BPF board fitted (Setup "Alex present"; on for every model by default).</summary>
        public static bool AlexPresent { get; set; } = true;
        /// <summary>Setup "Disable 6m LNA on TX" (default on).</summary>
        public static bool Disable6mLnaOnTx { get; set; } = true;
        /// <summary>Setup "Disable 6m LNA on RX" (default off).</summary>
        public static bool Disable6mLnaOnRx { get; set; } = false;
        /// <summary>Setup "Disable HPF on TX" (default off).</summary>
        public static bool DisableHpfOnTx { get; set; } = false;
        /// <summary>Penny / open-collector output control (Setup "Penny Ext Ctrl", default on).</summary>
        public static bool OcControlEnabled { get; set; } = true;

        /// <summary>Last bits sent, for tests and diagnostics.</summary>
        public static int LastHpfBits { get; private set; } = -1;
        public static int LastLpfBits { get; private set; } = -1;
        public static int LastOcBits { get; private set; } = -1;

        private static bool UsesBpf1(HPSDRHW hw) =>
            hw == HPSDRHW.OrionMKII || hw == HPSDRHW.Saturn || hw == HPSDRHW.HermesC10;

        /// <summary>setAlex1HPF: receive high-pass / band-pass filter for 'freqMHz'.</summary>
        public static void SetHpf(HPSDRHW hardware, double freqMHz, bool mox)
        {
            if (!AlexPresent) return;
            int bits = HpfBypass;
            if (mox && DisableHpfOnTx)
            {
                Send(HpfBypass);
                return;
            }
            var table = UsesBpf1(hardware) ? _bpf1 : _alexHpf;
            foreach (var (start, end, b) in table)
            {
                if (freqMHz >= start && freqMHz <= end)
                {
                    bits = b;
                    if (b == Bpf6mLna && (Disable6mLnaOnRx || (mox && Disable6mLnaOnTx)))
                        bits = HpfBypass;
                    break;
                }
            }
            Send(bits);

            static void Send(int b)
            {
                NetworkIO.SetAlexHPFBits(b);
                LastHpfBits = b;
            }
        }

        /// <summary>setAlexLPF: low-pass filter for 'freqMHz' (the TX frequency when transmitting).</summary>
        public static void SetLpf(double freqMHz, bool freqIsTx, bool mox)
        {
            if (!AlexPresent) return;
            int bits = Lpf6;            // console default when no range matches
            foreach (var (start, end, b) in _lpf)
            {
                if (freqMHz >= start && freqMHz <= end) { bits = b; break; }
            }
            NetworkIO.SetAlexLPFBits(bits, freqIsTx, mox);
            LastLpfBits = bits;
        }

        /// <summary>
        /// Apply every filter for the current state: console UpdateRX1DDSFreq /
        /// UpdateTXDDSFreq / HdwMOXChanged.  On receive the LPF follows the RX
        /// frequency; on transmit it follows the TX frequency.
        /// </summary>
        public static void Apply(HPSDRHW hardware, double rxMHz, double txMHz, bool mox, bool tune)
        {
            SetHpf(hardware, rxMHz, mox);
            if (mox) SetLpf(txMHz, true, true);
            else SetLpf(rxMHz, false, false);

            Band band = BandPlanRegions.BandFromFrequency(mox ? txMHz : rxMHz);
            int oc = OcControlEnabled
                ? Penny.getPenny().ExtCtrlEnable(band, band, mox, true, tune, false, false)
                : Penny.getPenny().ExtCtrlEnable(band, band, mox, false, tune, false, false);
            LastOcBits = oc;
        }

        #region Hermes-Lite 2 N2ADR filter board

        /// <summary>
        /// OC pin preset for the N2ADR filter board on a Hermes-Lite 2, as set by
        /// the Setup form's "N2ADR Filter" option (chkHERCULES_CheckedChanged).
        /// Pins are numbered 1..7 (bit = 1 &lt;&lt; (pin - 1)).
        /// </summary>
        private static readonly Dictionary<Band, (int[] rx, int[] tx)> _n2adr = new Dictionary<Band, (int[] rx, int[] tx)>
        {
            { Band.B160M, (new[] { 1 }, new[] { 1 }) },
            { Band.B80M, (new[] { 2, 7 }, new[] { 2 }) },
            { Band.B60M, (new[] { 3, 7 }, new[] { 3 }) },
            { Band.B40M, (new[] { 3, 7 }, new[] { 3 }) },
            { Band.B30M, (new[] { 4, 7 }, new[] { 4 }) },
            { Band.B20M, (new[] { 4, 7 }, new[] { 4 }) },
            { Band.B17M, (new[] { 5, 7 }, new[] { 5 }) },
            { Band.B15M, (new[] { 5, 7 }, new[] { 5 }) },
            { Band.B12M, (new[] { 6, 7 }, new[] { 6 }) },
            { Band.B10M, (new[] { 6, 7 }, new[] { 6 }) },
            // general-coverage (SWL) bands: pin 7 on receive (chkOCrcv*7)
            { Band.B120M, (new[] { 7 }, new int[0]) }, { Band.B90M, (new[] { 7 }, new int[0]) },
            { Band.B61M, (new[] { 7 }, new int[0]) }, { Band.B49M, (new[] { 7 }, new int[0]) },
            { Band.B41M, (new[] { 7 }, new int[0]) }, { Band.B31M, (new[] { 7 }, new int[0]) },
            { Band.B25M, (new[] { 7 }, new int[0]) }, { Band.B22M, (new[] { 7 }, new int[0]) },
            { Band.B19M, (new[] { 7 }, new int[0]) }, { Band.B16M, (new[] { 7 }, new int[0]) },
            { Band.B14M, (new[] { 7 }, new int[0]) }, { Band.B13M, (new[] { 7 }, new int[0]) },
            { Band.B11M, (new[] { 7 }, new int[0]) },
        };

        private static int Mask(int[] pins)
        {
            int m = 0;
            foreach (int p in pins) m |= 1 << (p - 1);
            return m;
        }

        /// <summary>Load (or clear) the N2ADR OC preset into Penny's per-band masks.</summary>
        public static void UseN2adrFilterBoard(bool enable)
        {
            Penny penny = Penny.getPenny();
            for (Band b = Band.B160M; b < Band.LAST; b++)
            {
                int rx = 0, tx = 0;
                if (enable && _n2adr.TryGetValue(b, out var pins))
                {
                    rx = Mask(pins.rx);
                    tx = Mask(pins.tx);
                }
                penny.setBandABitMask(b, (byte)rx, false);
                penny.setBandABitMask(b, (byte)tx, true);
                penny.setBandBBitMask(b, (byte)(rx & 0x70), false);
                penny.setBandBBitMask(b, (byte)(tx & 0x70), true);
            }
        }

        #endregion
    }
}
