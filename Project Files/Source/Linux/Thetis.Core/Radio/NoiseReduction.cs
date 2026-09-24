/*  NoiseReduction.cs

This file is part of a program that implements a Software-Defined Radio.

Receive noise reduction in the WDSP receive chain.  AetherSDR's NR2, RN2,
NR4 and DFNR live in libaethernr; WDSP's extnr module runs the selected one
on the demodulated audio.  AetherNr loads the library once and hands its
functions to WDSP (SetExtNRFunctions).  NNR is WDSP 2.10's own neural noise
reduction (nnr.c); the Nnr class holds its entry points.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Thetis.Radio
{
    /// <summary>Receive noise reduction (the AetherSDR filters).</summary>
    public enum NrType
    {
        Off = 0,
        /// <summary>AetherSDR's spectral noise reduction (extended WDSP EMNR).</summary>
        NR2 = 1,
        /// <summary>RNNoise, with a dry-mix control.</summary>
        RN2 = 2,
        /// <summary>libspecbleach spectral noise reduction.</summary>
        NR4 = 3,
        /// <summary>DeepFilterNet3 neural noise reduction.</summary>
        DFNR = 4,
        /// <summary>WDSP 2.10's neural noise reduction (built into WDSP).</summary>
        NNR = 5,
    }

    /// <summary>Filter settings (aethernr.h aethernr_param).</summary>
    public enum NrParam
    {
        Nr2GainMethod = 100, Nr2NpeMethod, Nr2AeFilter, Nr2GainMax, Nr2GainFloor, Nr2GainSmooth, Nr2Qspp,
        Nr2Post2Run, Nr2Post2Factor, Nr2Post2Nlevel, Nr2Post2TaperHz, Nr2Post2DecaySeconds,
        Rn2DryMix = 200,
        Nr4ReductionDb = 300, Nr4SmoothingPct, Nr4WhiteningPct, Nr4Adaptive, Nr4NoiseMethod, Nr4MaskingDepth, Nr4Suppression,
        DfnrAttenLimitDb = 400, DfnrPostFilterBeta,
        // NNR (WDSP; not sent to libaethernr)
        NnrModel = 500, NnrMaskFloorDb, NnrMaxGainDb, NnrAlpha, NnrAlphaKneeDb, NnrTau, NnrSmoothAttackMs, NnrSmoothReleaseMs,
        NnrPosition,
    }

    public static class AetherNr
    {
        private static bool _tried;
        private static string _error;

        /// <summary>True once libaethernr is loaded and registered with WDSP.</summary>
        public static bool Loaded { get; private set; }

        /// <summary>Why it is not loaded, if it is not.</summary>
        public static string Error => _error;

        /// <summary>Load libaethernr and register its filters with WDSP (once; call after WDSP is loaded).</summary>
        public static bool Initialize()
        {
            if (_tried) return Loaded;
            _tried = true;
            try
            {
                if (!NativeLibraries.TryLoad("aethernr", out IntPtr lib, out string path))
                {
                    _error = NativeLibraries.LoadError("aethernr") ?? "libaethernr.so could not be loaded";
                    System.Console.Error.WriteLine("thetis: noise reduction unavailable: " + _error);
                    return false;
                }
                IntPtr create = NativeLibrary.GetExport(lib, "aethernr_create");
                IntPtr destroy = NativeLibrary.GetExport(lib, "aethernr_destroy");
                IntPtr process = NativeLibrary.GetExport(lib, "aethernr_process");
                IntPtr param = NativeLibrary.GetExport(lib, "aethernr_set_param");
                // DFNR's model is installed next to the library
                if (path != null) aethernr_set_model_dir(Path.GetDirectoryName(path));
                SetExtNRFunctions(create, destroy, process, param);
                Loaded = true;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                System.Console.Error.WriteLine("thetis: noise reduction unavailable: " + _error);
            }
            return Loaded;
        }

        /// <summary>True if 'type' can run at the DSP rate 'rate' (RN2 and DFNR need 48 kHz; DFNR needs its model).</summary>
        public static bool Available(NrType type, int rate = 48000) =>
            type == NrType.Off || type == NrType.NNR || (Loaded && aethernr_available((int)type, rate) != 0);

        #region native

        [DllImport("aethernr", CallingConvention = CallingConvention.Cdecl)]
        private static extern void aethernr_set_model_dir([MarshalAs(UnmanagedType.LPUTF8Str)] string dir);

        [DllImport("aethernr", CallingConvention = CallingConvention.Cdecl)]
        private static extern int aethernr_available(int type, int rate);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetExtNRFunctions(IntPtr create, IntPtr destroy, IntPtr process, IntPtr param);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXAExtNRRun(int channel, int type);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXAExtNRPosition(int channel, int position);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXAExtNRParam(int channel, int param, double value);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetRXAExtNRActive(int channel);

        #endregion
    }

    /// <summary>WDSP 2.10's neural noise reduction (nnr.c): always available, it is part of libwdsp.</summary>
    public static class Nnr
    {
        public static bool IsNnrParam(NrParam p) => (int)p >= 500 && (int)p < 600;

        /// <summary>Send one NNR setting to channel 'ch'; the smoothing pair needs both values.</summary>
        internal static void Apply(int ch, NrParam p, double v, Func<NrParam, double, double> get)
        {
            switch (p)
            {
                case NrParam.NnrModel: SetRXANNRModel(ch, (int)v); break;
                case NrParam.NnrMaskFloorDb: SetRXANNRMaskFloor(ch, v); break;
                case NrParam.NnrMaxGainDb: SetRXANNRMaxGain(ch, v); break;
                case NrParam.NnrAlpha: SetRXANNRAlpha(ch, v); break;
                case NrParam.NnrAlphaKneeDb: SetRXANNRAlphaKnee(ch, v); break;
                case NrParam.NnrTau: SetRXANNRTau(ch, v); break;
                case NrParam.NnrSmoothAttackMs:
                case NrParam.NnrSmoothReleaseMs:
                    SetRXANNRSmooth(ch, get(NrParam.NnrSmoothAttackMs, 0), get(NrParam.NnrSmoothReleaseMs, 0)); break;
                case NrParam.NnrPosition: SetRXANNRPosition(ch, (int)v); break;
            }
        }

        #region native

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXANNRRun(int channel, int run);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXANNRPosition(int channel, int position);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXANNRMaskFloor(int channel, double floorDb);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetRXANNRModel(int channel, int slot);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetRXANNRModel(int channel);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXANNRAlpha(int channel, double alpha);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXANNRAlphaKnee(int channel, double kneeDb);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXANNRTau(int channel, double tau);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXANNRMaxGain(int channel, double maxGainDb);

        [DllImport("wdsp.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void SetRXANNRSmooth(int channel, double attackMs, double releaseMs);

        #endregion
    }
}
