/*  RadioCatHost.cs

This file is part of a program that implements a Software-Defined Radio.

ICatHost directly on a RadioController, for use without the desktop
application (tests, a headless radio).  Calls are serialised with a lock.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Reflection;
using Thetis.Radio;

namespace Thetis.Cat
{
    public class RadioCatHost : ICatHost
    {
        private readonly RadioController _radio;
        private readonly object _lock = new object();
        private double _volumeBeforeMute = 0.5;
        private bool _mute;
        private int _filterIndex = FilterPresets.DefaultIndex;

        public RadioCatHost(RadioController radio) => _radio = radio;

        /// <summary>Turns the radio on or off for ZZPS / PS (null: not possible here).</summary>
        public Func<bool, (bool ok, string why)> PowerSwitch { get; set; }

        public virtual T Invoke<T>(Func<T> f) { lock (_lock) return f(); }

        public long FrequencyHz
        {
            get => (long)Math.Round(_radio.FrequencyMHz * 1e6);
            set => _radio.FrequencyMHz = value / 1e6;
        }

        public long VfoBHz { get; set; } = 7100000;

        public DSPMode Mode
        {
            get => _radio.Mode;
            set { _radio.Mode = value; _filterIndex = FilterPresets.DefaultIndex; }
        }

        public (int low, int high) Filter => (_radio.Filter.Low, _radio.Filter.High);

        public int FilterIndex
        {
            get => _filterIndex;
            set
            {
                var presets = FilterPresets.For(_radio.Mode);
                if (value < 0 || value >= presets.Count) return;
                _radio.Filter = presets[value];
                _filterIndex = value;
            }
        }

        public void SetFilterEdges(int low, int high)
        {
            if (high <= low) return;
            _radio.Filter = new FilterPreset("Var", low, high);
            _filterIndex = -1;
        }

        public AGCMode Agc { get => _radio.Agc; set => _radio.Agc = value; }
        public double AgcTopDb { get => _radio.AgcTop; set => _radio.AgcTop = value; }

        public int VolumePercent
        {
            get => (int)Math.Round((_mute ? _volumeBeforeMute : _radio.Volume) * 100);
            set { if (_mute) _volumeBeforeMute = value / 100.0; else _radio.Volume = value / 100.0; }
        }

        public bool Mute
        {
            get => _mute;
            set
            {
                if (value == _mute) return;
                if (value) { _volumeBeforeMute = _radio.Volume; _radio.Volume = 0; }
                else _radio.Volume = _volumeBeforeMute;
                _mute = value;
            }
        }

        public NrType NoiseReduction { get => _radio.NoiseReductionType; set => _radio.NoiseReductionType = value; }
        public bool AutoNotch { get => _radio.AutoNotch; set => _radio.AutoNotch = value; }
        public int AttenuatorDb { get => _radio.AttenuatorDb; set => _radio.AttenuatorDb = value; }
        public (int min, int max) AttenuatorRange => _radio.AttenuatorRange;
        public int StepHz { get; set; } = 100;
        public float SignalDbm => _radio.PowerOn ? _radio.SignalDbm() : -140f;
        public float AdcDbfs => _radio.AdcDbfs();
        public int SampleRate => _radio.SampleRate;
        public bool PcAudio { get => _radio.VacRunning; set { } }

        public bool PowerOn => _radio.PowerOn;

        public bool SetPower(bool on, out string why)
        {
            why = null;
            if (on == _radio.PowerOn) return true;
            if (PowerSwitch == null) { why = "power switching is not available"; return false; }
            var (ok, w) = PowerSwitch(on);
            why = w;
            return ok;
        }

        public string ModelName => _radio.Model.ToString();

        public virtual string Version =>
            typeof(RadioCatHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0";

        public bool Mox => _radio.Mox;
        public bool SetMox(bool on, out string why) => _radio.SetMox(on, out why);
        public bool Tuning => _radio.Tuning;
        public bool SetTune(bool on, out string why) => _radio.SetTune(on, out why);
        public bool TwoTone => _radio.TwoToneOn;
        public bool SetTwoTone(bool on, out string why) => _radio.SetTwoTone(on, out why);
        public int DrivePercent { get => _radio.DrivePercent; set => _radio.DrivePercent = value; }
        public int TunePercent { get => _radio.TunePercent; set => _radio.TunePercent = value; }
        public double MicGainDb { get => _radio.MicGainDb; set => _radio.MicGainDb = value; }

        public bool Vox { get => _radio.TxProcessing.VoxOn; set { _radio.TxProcessing.VoxOn = value; _radio.ApplyTxProcessing(); } }
        public int VoxHoldMs { get => _radio.TxProcessing.VoxHoldMs; set { _radio.TxProcessing.VoxHoldMs = value; _radio.ApplyTxProcessing(); } }
        public bool Compressor { get => _radio.TxProcessing.CompressorOn; set { _radio.TxProcessing.CompressorOn = value; _radio.ApplyTxProcessing(); } }
        public double CompressorDb { get => _radio.TxProcessing.CompressorDb; set { _radio.TxProcessing.CompressorDb = value; _radio.ApplyTxProcessing(); } }
        public bool TxEq { get => _radio.TxProcessing.EqOn; set { _radio.TxProcessing.EqOn = value; _radio.ApplyTxProcessing(); } }
        public bool PureSignal { get => _radio.PureSignalAutoCal; set => _radio.PureSignalAutoCal = value; }
        public void PureSignalSingleCal() => _radio.PureSignalSingleCal();
        public (int low, int high) TxFilter { get => _radio.TxFilter; set => _radio.TxFilter = value; }
        public float ForwardWatts => _radio.ForwardWatts;
        public float ReflectedWatts => _radio.ReflectedWatts;
        public float Swr => _radio.Swr;
        public float AlcDb => _radio.AlcGainDb();
        public float MicDbfs => _radio.MicPeakDb();

        public string BandName => BandPlan.For(_radio.FrequencyMHz)?.Name;

        public void SelectBand(string name)
        {
            foreach (var b in BandPlan.Bands)
            {
                if (b.Name != name) continue;
                Mode = b.DefaultMode;
                _radio.FrequencyMHz = b.DefaultMHz;
                return;
            }
        }
    }
}
