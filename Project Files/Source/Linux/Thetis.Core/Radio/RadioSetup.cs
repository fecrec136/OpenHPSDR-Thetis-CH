/*  RadioSetup.cs

This file is part of a program that implements a Software-Defined Radio.

Calibration and set-up data that the Windows console keeps on its Setup
form, with the behaviour of the console code that uses it:

  * PaCalibration   - per-band PA gain and drive-level corrections
                      (setup.cs PAProfile: GetGainForBand, calcDriveAdjust)
  * AntennaSettings - per-band Alex receive / transmit / receive-only
                      antennas (Alex.cs UpdateAlexAntSelection, AntBandFromFreq)
  * RxFrontEnd      - the receive step attenuator per model
                      (console.cs RX1AttenuatorData, RXPreampOffset)

The classes are plain data so a front end can store them as it likes.

Copyright (C) 2000-2025 Original authors
Copyright (C) 2020-2026 Richard Samphire MW0LGE

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;

namespace Thetis.Radio
{
    /// <summary>The amateur bands that have their own PA gain and antenna settings (Alex band index order).</summary>
    public static class HamBands
    {
        public static readonly Band[] All =
        {
            Band.B160M, Band.B80M, Band.B60M, Band.B40M, Band.B30M, Band.B20M,
            Band.B17M, Band.B15M, Band.B12M, Band.B10M, Band.B6M, Band.B2M,
        };

        public static string Label(Band b) => b switch
        {
            Band.B160M => "160 m", Band.B80M => "80 m", Band.B60M => "60 m", Band.B40M => "40 m",
            Band.B30M => "30 m", Band.B20M => "20 m", Band.B17M => "17 m", Band.B15M => "15 m",
            Band.B12M => "12 m", Band.B10M => "10 m", Band.B6M => "6 m", Band.B2M => "2 m",
            _ => b.ToString(),
        };

        /// <summary>Alex.AntBandFromFreq: the ham band whose antenna and PA settings apply at 'mhz'.</summary>
        public static Band ForFrequency(double mhz)
        {
            if (mhz >= 12.075)
            {
                if (mhz >= 23.17)
                    return mhz >= 26.465 ? (mhz >= 39.85 ? Band.B6M : Band.B10M) : Band.B12M;
                return mhz >= 16.209 ? (mhz >= 19.584 ? Band.B15M : Band.B17M) : Band.B20M;
            }
            if (mhz >= 6.20175) return mhz >= 8.7 ? Band.B30M : Band.B40M;
            if (mhz >= 4.66525) return Band.B60M;
            return mhz >= 2.75 ? Band.B80M : Band.B160M;
        }
    }

    #region PA gain

    public sealed class PaBandGain
    {
        /// <summary>PA gain in dB: how much the PA amplifies the drive (higher = less drive for the same power).</summary>
        public float GainDb { get; set; } = 100f;
        /// <summary>Gain corrections in dB at drive 10, 20 ... 90 % (interpolated in between, 0 at 0 and 100 %).</summary>
        public float[] DriveAdjustDb { get; set; } = new float[9];
    }

    /// <summary>setup.cs PAProfile: per-band PA gain with drive-dependent corrections.</summary>
    public sealed class PaCalibration
    {
        public HPSDRModel Model { get; set; }
        public Dictionary<Band, PaBandGain> Bands { get; set; } = new Dictionary<Band, PaBandGain>();

        /// <summary>The model's default gains (clsHardwareSpecific.DefaultPAGainsForBands), no corrections.</summary>
        public static PaCalibration DefaultsFor(HPSDRModel model)
        {
            var p = new PaCalibration { Model = model };
            float[] gains = HardwareSpecific.DefaultPAGainsForBands(model);
            foreach (Band b in HamBands.All)
                p.Bands[b] = new PaBandGain { GainDb = (int)b < gains.Length ? gains[(int)b] : 100f };
            return p;
        }

        /// <summary>PAProfile.GetGainForBand(b, drive): gain minus the interpolated drive correction.</summary>
        public float GainFor(Band b, int drivePercent)
        {
            if (!Bands.TryGetValue(b, out PaBandGain g) || g == null) return 1000f;   // console: no profile -> 1000
            return g.GainDb - DriveAdjust(g, Math.Clamp(drivePercent, 0, 100));
        }

        private static float DriveAdjust(PaBandGain g, int drive)
        {
            float[] adj = g.DriveAdjustDb;
            if (adj == null || adj.Length < 9) return 0f;
            int n = drive / 10;
            if (drive % 10 == 0)
                return n == 0 || n == 10 ? 0f : adj[n - 1];
            float low = n == 0 ? 0f : adj[n - 1];
            float high = n == 9 ? 0f : adj[n];
            float frac = (drive - n * 10) / 10f;
            return low + frac * (high - low);
        }
    }

    #endregion

    #region antennas

    public sealed class BandAntennas
    {
        /// <summary>Antenna used for receive, 1..3.</summary>
        public int RxAnt { get; set; } = 1;
        /// <summary>Antenna used for transmit, 1..3.</summary>
        public int TxAnt { get; set; } = 1;
        /// <summary>Receive-only input: 0 none, 1 "RX1 In" (Ext2), 2 "RX2 In" (Ext1), 3 XVTR.</summary>
        public int RxOnly { get; set; }
    }

    /// <summary>Alex antenna selection (Alex.cs), one set per ham band.</summary>
    public sealed class AntennaSettings
    {
        /// <summary>Setup "Alex antenna control" (off: the relays are left at ANT1).</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Route the RX bypass (RX out) during transmit.</summary>
        public bool RxOutOnTx { get; set; }
        /// <summary>Use the Ext1 / Ext2 receive input during transmit (e.g. for a separate receive antenna).</summary>
        public bool Ext1OutOnTx { get; set; }
        public bool Ext2OutOnTx { get; set; }
        public Dictionary<Band, BandAntennas> Bands { get; set; } = new Dictionary<Band, BandAntennas>();

        public static AntennaSettings Defaults()
        {
            var a = new AntennaSettings();
            foreach (Band b in HamBands.All) a.Bands[b] = new BandAntennas();
            return a;
        }

        private BandAntennas For(Band b) =>
            Bands.TryGetValue(b, out BandAntennas x) && x != null ? x : new BandAntennas();

        /// <summary>
        /// Alex.UpdateAlexAntSelection (no transverter, no Aries ATU): the
        /// antenna bits for receive or transmit at 'mhz'.
        /// </summary>
        public (int rxOnly, int trx, int tx, int rxOut) Bits(double mhz, bool tx)
        {
            BandAntennas a = For(HamBands.ForFrequency(mhz));
            int txAnt = Math.Clamp(a.TxAnt, 1, 3);
            int rxOnly, trx, rxOut;
            if (tx)
            {
                rxOnly = Ext2OutOnTx ? 1 : Ext1OutOnTx ? 2 : 0;
                rxOut = RxOutOnTx || Ext1OutOnTx || Ext2OutOnTx ? 1 : 0;
                trx = txAnt;
            }
            else
            {
                rxOnly = Math.Clamp(a.RxOnly, 0, 3);
                if (rxOnly >= 3) rxOnly -= 3;       // the XVTR input is only used with a transverter
                rxOut = rxOnly != 0 ? 1 : 0;
                trx = Math.Clamp(a.RxAnt, 1, 3);
            }
            return (rxOnly, trx, txAnt, rxOut);
        }

        /// <summary>Send the antenna selection for 'mhz' (NetworkIO.SetAntBits).</summary>
        public void Apply(double mhz, bool tx)
        {
            if (!Enabled)
            {
                NetworkIO.SetAntBits(0, 0, 0, 0, false);
                LastBits = (0, 0, 0, 0);
                return;
            }
            var bits = Bits(mhz, tx);
            NetworkIO.SetAntBits(bits.rxOnly, bits.trx, bits.tx, bits.rxOut, tx);
            LastBits = bits;
        }

        /// <summary>Last bits sent, for tests and diagnostics.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public (int rxOnly, int trx, int tx, int rxOut) LastBits { get; private set; }
    }

    #endregion

    #region receive attenuator

    /// <summary>The receive step attenuator (console RX1AttenuatorData).</summary>
    public static class RxFrontEnd
    {
        private static bool AlexWith30dB(HPSDRModel m) =>
            BandFilters.AlexPresent &&
            m != HPSDRModel.ANAN10 && m != HPSDRModel.ANAN10E && m != HPSDRModel.ANAN7000D &&
            m != HPSDRModel.ANAN8000D && m != HPSDRModel.ORIONMKII && m != HPSDRModel.ANAN_G2E &&
            m != HPSDRModel.ANAN_G2 && m != HPSDRModel.ANAN_G2_1K && m != HPSDRModel.ANVELINAPRO3 &&
            m != HPSDRModel.REDPITAYA && m != HPSDRModel.HERMESLITE;

        /// <summary>Attenuation range in dB (negative = gain on the Hermes-Lite 2's LNA).</summary>
        public static (int min, int max) Range(HPSDRModel m) =>
            m == HPSDRModel.HERMESLITE ? (-28, 32) : AlexWith30dB(m) ? (0, 61) : (0, 31);

        /// <summary>Default attenuation: 0 dB (the Hermes-Lite 2 console default leaves its LNA at 0 dB too).</summary>
        public static int Default(HPSDRModel m) => 0;

        /// <summary>(Alex attenuator bits, step attenuator data) for 'db' on model 'm'.</summary>
        public static (int alex, int step) Data(HPSDRModel m, int db)
        {
            var (lo, hi) = Range(m);
            db = Math.Clamp(db, lo, hi);
            if (m == HPSDRModel.HERMESLITE) return (0, 31 - db);          // HL2: LNA gain, wider range
            if (AlexWith30dB(m)) return db <= 31 ? (0, db) : (3, db + 2); // 30 dB Alex pad + step
            return (0, db);
        }

        /// <summary>Send attenuation 'db' for receiver 1 (ADC 1).</summary>
        public static void Apply(HPSDRModel m, int db)
        {
            var (alex, step) = Data(m, db);
            NetworkIO.SetAlexAtten(alex);
            NetworkIO.SetADC1StepAttenData(step);
        }
    }

    #endregion
}
