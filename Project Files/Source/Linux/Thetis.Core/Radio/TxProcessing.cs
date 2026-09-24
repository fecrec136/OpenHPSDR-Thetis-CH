/*  TxProcessing.cs

This file is part of a program that implements a Software-Defined Radio.

Transmit audio processing: leveler, compressor (CPDR), CESSB overshoot
control, 10-band EQ, CFC (continuous frequency compressor), phase rotator,
and VOX / downward expander (DEXP).  Defaults are the Windows console's
(radio.cs RadioDSPTX, setup.designer.cs, console.Designer.cs), and the
settings are applied with the same WDSP / ChannelMaster calls as setup.cs,
eqform.cs and cmaster.cs.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;

namespace Thetis.Radio
{
    public sealed class TxProcessing
    {
        // --- leveler (radio.cs tx_leveler_*) ---
        public bool LevelerOn { get; set; } = true;
        public double LevelerMaxGainDb { get; set; } = 15.0;
        public int LevelerDecayMs { get; set; } = 100;

        // --- compressor, "CPDR" (radio.cs tx_compand_*; ptbCPDR 0..20, default 1) ---
        public bool CompressorOn { get; set; }
        public double CompressorDb { get; set; } = 1.0;

        // --- CESSB overshoot control (radio.cs tx_osctrl_on) ---
        public bool CessbOn { get; set; }

        // --- 10-band graphic EQ (eqform: preamp + 32 Hz .. 16 kHz, dB) ---
        public bool EqOn { get; set; }
        public int EqPreampDb { get; set; }
        public int[] EqBandsDb { get; set; } = new int[10];
        public static readonly int[] EqFrequencies = { 32, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        // --- CFC (setup.cs setCFCProfile; defaults from setup.designer.cs) ---
        public bool CfcOn { get; set; }
        public double CfcPrecompDb { get; set; }
        public bool CfcPostEqOn { get; set; }
        public double CfcPostEqGainDb { get; set; }
        public double[] CfcFrequencies { get; set; } = { 0, 125, 250, 500, 1000, 2000, 3000, 4000, 5000, 10000 };
        public double[] CfcCompressionDb { get; set; } = { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 };
        public double[] CfcPostEqDb { get; set; } = new double[10];

        // --- phase rotator (udPhRotFreq 338 Hz, udPHROTStages 8) ---
        public bool PhaseRotatorOn { get; set; }
        public double PhaseRotatorHz { get; set; } = 338.0;
        public int PhaseRotatorStages { get; set; } = 8;

        // --- VOX and downward expander (setup.designer.cs udDEXP*, udSCF*) ---
        public bool VoxOn { get; set; }
        /// <summary>Downward expander: lowers background noise between words (independent of VOX).</summary>
        public bool ExpanderOn { get; set; }
        public double VoxThresholdDb { get; set; } = -20.0;
        public int VoxHoldMs { get; set; } = 500;
        public int ExpanderAttackMs { get; set; } = 2;
        public int ExpanderReleaseMs { get; set; } = 100;
        public double ExpanderRatioDb { get; set; } = 10.0;
        public double ExpanderHysteresisDb { get; set; } = 2.0;
        public int DetectorTauMs { get; set; } = 20;
        public bool SideChannelFilterOn { get; set; } = true;
        public double SideChannelLowHz { get; set; } = 500;
        public double SideChannelHighHz { get; set; } = 1500;
        public bool LookAheadOn { get; set; } = true;
        public int LookAheadMs { get; set; } = 60;

        /// <summary>Apply everything to TX channel 'txa' and transmitter input 'dexp' (0).</summary>
        internal unsafe void Apply(int txa, int dexp, DSPMode mode)
        {
            WDSP.SetTXALevelerSt(txa, LevelerOn);
            WDSP.SetTXALevelerTop(txa, Math.Clamp(LevelerMaxGainDb, 0.0, 20.0));
            WDSP.SetTXALevelerDecay(txa, Math.Clamp(LevelerDecayMs, 1, 5000));

            WDSP.SetTXACompressorGain(txa, Math.Clamp(CompressorDb, 0.0, 20.0));
            WDSP.SetTXACompressorRun(txa, CompressorOn);
            WDSP.SetTXAosctrlRun(txa, CessbOn);

            int[] eq = new int[11];
            eq[0] = Math.Clamp(EqPreampDb, -12, 15);
            for (int i = 0; i < 10; i++) eq[i + 1] = Math.Clamp(EqBandsDb != null && i < EqBandsDb.Length ? EqBandsDb[i] : 0, -12, 15);
            fixed (int* p = eq) WDSP.SetTXAGrphEQ10(txa, p);
            WDSP.SetTXAEQRun(txa, EqOn);

            double[] f = Fill(CfcFrequencies, new double[] { 0, 125, 250, 500, 1000, 2000, 3000, 4000, 5000, 10000 });
            double[] g = Fill(CfcCompressionDb, 5.0);
            double[] e = Fill(CfcPostEqDb, 0.0);
            fixed (double* fp = f, gp = g, ep = e) WDSP.SetTXACFCOMPprofile(txa, 10, fp, gp, ep, null, null);
            WDSP.SetTXACFCOMPPrecomp(txa, Math.Clamp(CfcPrecompDb, 0.0, 16.0));
            WDSP.SetTXACFCOMPPrePeq(txa, Math.Clamp(CfcPostEqGainDb, -16.0, 16.0));
            WDSP.SetTXACFCOMPPeqRun(txa, CfcPostEqOn ? 1 : 0);
            WDSP.SetTXACFCOMPRun(txa, CfcOn ? 1 : 0);

            WDSP.SetTXAPHROTCorner(txa, Math.Clamp(PhaseRotatorHz, 50.0, 2000.0));
            WDSP.SetTXAPHROTNstages(txa, Math.Clamp(PhaseRotatorStages, 1, 16));
            WDSP.SetTXAPHROTRun(txa, PhaseRotatorOn ? 1 : 0);

            // setup.cs udDEXP*_ValueChanged
            cmaster.SetDEXPAttackTime(dexp, ExpanderAttackMs / 1000.0);
            cmaster.SetDEXPHoldTime(dexp, VoxHoldMs / 1000.0);
            cmaster.SetDEXPReleaseTime(dexp, ExpanderReleaseMs / 1000.0);
            cmaster.SetDEXPAttackThreshold(dexp, Math.Pow(10.0, VoxThresholdDb / 20.0));
            cmaster.SetDEXPExpansionRatio(dexp, Math.Pow(10.0, ExpanderRatioDb / 20.0));
            cmaster.SetDEXPHysteresisRatio(dexp, Math.Pow(10.0, -ExpanderHysteresisDb / 20.0));
            cmaster.SetDEXPDetectorTau(dexp, DetectorTauMs / 1000.0);
            cmaster.SetDEXPLowCut(dexp, SideChannelLowHz);
            cmaster.SetDEXPHighCut(dexp, SideChannelHighHz);
            cmaster.SetDEXPRunSideChannelFilter(dexp, SideChannelFilterOn);
            cmaster.SetDEXPAudioDelay(dexp, LookAheadMs / 1000.0);
            cmaster.SetDEXPRunAudioDelay(dexp, LookAheadOn);
            cmaster.SetDEXPRun(dexp, ExpanderOn);
            cmaster.SetDEXPRunVox(dexp, VoxOn && VoxModes(mode));        // cmaster.CMSetTXAVoxRun
        }

        /// <summary>cmaster.CMSetTXAVoxRun: VOX keys voice and digital modes, not CW.</summary>
        internal static bool VoxModes(DSPMode m) =>
            m == DSPMode.LSB || m == DSPMode.USB || m == DSPMode.DSB || m == DSPMode.AM ||
            m == DSPMode.SAM || m == DSPMode.FM || m == DSPMode.DIGL || m == DSPMode.DIGU;

        private static double[] Fill(double[] src, double[] fallback)
        {
            var r = (double[])fallback.Clone();
            if (src != null) for (int i = 0; i < Math.Min(src.Length, r.Length); i++) r[i] = src[i];
            return r;
        }

        private static double[] Fill(double[] src, double value)
        {
            var r = new double[10];
            for (int i = 0; i < 10; i++) r[i] = src != null && i < src.Length ? src[i] : value;
            return r;
        }
    }
}
