/*  ICatHost.cs

This file is part of a program that implements a Software-Defined Radio.

What CAT and TCI control: the radio as the operator sees it.  The desktop
application implements it so that a change made over CAT updates the
controls and the saved settings as if it had been made on the main window;
RadioCatHost implements it directly on a RadioController (tests, headless).

All members are called through Invoke, on the host's own thread.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using Thetis.Radio;

namespace Thetis.Cat
{
    public interface ICatHost
    {
        /// <summary>Run 'f' where the host's state may be touched (the UI thread in the desktop application).</summary>
        T Invoke<T>(Func<T> f);

        // --- receiver ---
        /// <summary>VFO A, Hz.</summary>
        long FrequencyHz { get; set; }
        /// <summary>VFO B, Hz: remembered and reported (there is no second receiver yet).</summary>
        long VfoBHz { get; set; }
        DSPMode Mode { get; set; }
        /// <summary>Receive filter edges, Hz, relative to the VFO.</summary>
        (int low, int high) Filter { get; }
        /// <summary>Filter preset 0..9 (F1..F10) or -1 for edges set directly.</summary>
        int FilterIndex { get; set; }
        void SetFilterEdges(int low, int high);
        AGCMode Agc { get; set; }
        /// <summary>AGC gain (the console's AGC-T / "RF" control), dB.</summary>
        double AgcTopDb { get; set; }
        /// <summary>AF volume 0..100.</summary>
        int VolumePercent { get; set; }
        bool Mute { get; set; }
        NrType NoiseReduction { get; set; }
        bool AutoNotch { get; set; }
        int AttenuatorDb { get; set; }
        (int min, int max) AttenuatorRange { get; }
        /// <summary>Tuning step, Hz.</summary>
        int StepHz { get; set; }
        /// <summary>Signal level as the S-meter shows it (calibrated), dBm.</summary>
        float SignalDbm { get; }
        /// <summary>Receiver ADC level, dBFS.</summary>
        float AdcDbfs { get; }
        /// <summary>Sample rate of the receiver, Hz.</summary>
        int SampleRate { get; }
        /// <summary>PC audio (VAC) on.</summary>
        bool PcAudio { get; set; }

        // --- radio ---
        bool PowerOn { get; }
        /// <summary>Turn the radio on (the selected radio) or off; false with the reason if it cannot.</summary>
        bool SetPower(bool on, out string why);
        string ModelName { get; }
        string Version { get; }

        // --- transmit ---
        bool Mox { get; }
        bool SetMox(bool on, out string why);
        bool Tuning { get; }
        bool SetTune(bool on, out string why);
        bool TwoTone { get; }
        bool SetTwoTone(bool on, out string why);
        int DrivePercent { get; set; }
        int TunePercent { get; set; }
        double MicGainDb { get; set; }
        bool Vox { get; set; }
        int VoxHoldMs { get; set; }
        bool Compressor { get; set; }
        double CompressorDb { get; set; }
        bool TxEq { get; set; }
        bool PureSignal { get; set; }
        void PureSignalSingleCal();
        (int low, int high) TxFilter { get; set; }
        float ForwardWatts { get; }
        float ReflectedWatts { get; }
        float Swr { get; }
        float AlcDb { get; }
        float MicDbfs { get; }

        // --- bands (BandPlan names; null outside the bands) ---
        string BandName { get; }
        void SelectBand(string name);
    }
}
