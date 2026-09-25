/*  MainWindow.Cat.cs

This file is part of a program that implements a Software-Defined Radio.

CAT and TCI in the desktop application: the CatService runs the ports and
servers from Setup > CAT / TCI, and this window is its host, so a change made
over CAT goes through the same code as a click (controls, saved settings and
radio stay in step).

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Linq;
using System.Reflection;
using Avalonia.Threading;
using Thetis.Cat;
using Thetis.Radio;

namespace Thetis.Desktop
{
    public partial class MainWindow
    {
        private CatService _cat;
        private bool _catMute;
        private long _vfoBHz;

        internal CatService Cat => _cat;

        private void StartCat()
        {
            _settings.Cat ??= new CatSettings();
            _settings.Cat.Normalize(Settings.DataDirectory);
            _cat = new CatService(new Host(this));
            ApplyCatSettings();
        }

        /// <summary>(Re)start the CAT ports and servers from the settings.</summary>
        internal void ApplyCatSettings()
        {
            if (_cat == null) return;
            _settings.Cat.Normalize(Settings.DataDirectory);
            _cat.Apply(_settings.Cat);
        }

        private void StopCat()
        {
            _cat?.Stop();
            _cat = null;
        }

        private sealed class Host : ICatHost
        {
            private readonly MainWindow _w;
            private RadioController R => _w._radio;
            private Settings S => _w._settings;

            public Host(MainWindow w) => _w = w;

            public T Invoke<T>(Func<T> f) => Dispatcher.UIThread.CheckAccess() ? f() : Dispatcher.UIThread.Invoke(f);

            public long FrequencyHz
            {
                get => (long)Math.Round(R.FrequencyMHz * 1e6);
                set => _w.Tune(value / 1e6);
            }

            public long VfoBHz
            {
                get => _w._vfoBHz > 0 ? _w._vfoBHz : FrequencyHz;
                set => _w._vfoBHz = value;
            }

            public DSPMode Mode
            {
                get => R.Mode;
                set { if (value != R.Mode) _w.SetMode(value); }
            }

            public (int low, int high) Filter => (R.Filter.Low, R.Filter.High);

            public int FilterIndex
            {
                get
                {
                    var presets = FilterPresets.For(R.Mode);
                    for (int i = 0; i < presets.Count; i++) if (presets[i] == R.Filter) return i;
                    return -1;
                }
                set { if (value >= 0 && value < FilterPresets.For(R.Mode).Count) _w.SetFilter(value); }
            }

            public void SetFilterEdges(int low, int high)
            {
                if (high <= low) return;
                R.Filter = new FilterPreset("Var", low, high);
                _w.RefreshFilters();
            }

            public AGCMode Agc { get => R.Agc; set => _w.SetAgc(value); }
            public double AgcTopDb { get => R.AgcTop; set => _w.AgcTopSlider.Value = value; }

            public int VolumePercent
            {
                get => (int)Math.Round(S.Volume * 100);
                set => _w.VolumeSlider.Value = Math.Clamp(value, 0, 100);
            }

            public bool Mute
            {
                get => _w._catMute;
                set { _w._catMute = value; R.Volume = value ? 0 : S.Volume; }
            }

            public NrType NoiseReduction { get => S.NoiseReductionType; set => _w.SetNoiseReduction(value); }
            public bool AutoNotch { get => R.AutoNotch; set => _w.AnfToggle.IsChecked = value; }
            public int AttenuatorDb { get => R.AttenuatorDb; set => _w.AttSlider.Value = value; }
            public (int min, int max) AttenuatorRange => R.AttenuatorRange;

            public int StepHz
            {
                get => S.TuneStepHz;
                set
                {
                    int best = 0;
                    for (int i = 0; i < _steps.Length; i++)
                        if (Math.Abs(_steps[i] - value) < Math.Abs(_steps[best] - value)) best = i;
                    _w.StepBox.SelectedIndex = best;
                }
            }

            public float SignalDbm => R.PowerOn ? R.SignalDbm() : -140f;
            public float AdcDbfs => R.AdcDbfs();
            public int SampleRate => R.SampleRate;
            public bool PcAudio { get => S.PcAudio; set => _w.PcAudioCheck.IsChecked = value; }

            public bool PowerOn => R.PowerOn;

            public bool SetPower(bool on, out string why)
            {
                why = null;
                if (on == R.PowerOn) return true;
                if (on && (_w.RadioBox.SelectedIndex < 0 || !R.DspReady))
                {
                    why = R.DspReady ? "no radio selected" : "DSP is still starting";
                    return false;
                }
                _w.PowerButton.IsChecked = on;      // the button's handler starts or stops the radio
                return true;
            }

            public string ModelName => R.Model.ToString();

            public string Version =>
                typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0";

            public bool Mox => R.Mox;
            public bool SetMox(bool on, out string why) => Report(R.SetMox(on, out why), why);
            public bool Tuning => R.Tuning;
            public bool SetTune(bool on, out string why) => Report(R.SetTune(on, out why), why);
            public bool TwoTone => R.TwoToneOn;
            public bool SetTwoTone(bool on, out string why) => Report(R.SetTwoTone(on, out why), why);

            private bool Report(bool ok, string why)
            {
                if (!ok && !string.IsNullOrEmpty(why)) _w.StatusText.Text = "CAT: " + why;
                _w.RefreshTx();
                return ok;
            }

            public int DrivePercent { get => S.DrivePercent; set => _w.DriveSlider.Value = Math.Clamp(value, 0, 100); }
            public int TunePercent { get => S.TunePercent; set => _w.TunePowerSlider.Value = Math.Clamp(value, 0, 100); }
            public double MicGainDb { get => S.MicGainDb; set => _w.MicGainSlider.Value = value; }
            public bool Vox { get => S.TxProcessing.VoxOn; set => _w.VoxToggle.IsChecked = value; }
            public bool Compressor { get => S.TxProcessing.CompressorOn; set => _w.CompToggle.IsChecked = value; }
            public bool TxEq { get => S.TxProcessing.EqOn; set => _w.EqToggle.IsChecked = value; }

            public int VoxHoldMs
            {
                get => S.TxProcessing.VoxHoldMs;
                set { S.TxProcessing.VoxHoldMs = value; R.ApplyTxProcessing(); _w._txPanel?.Refresh(); }
            }

            public double CompressorDb
            {
                get => S.TxProcessing.CompressorDb;
                set { S.TxProcessing.CompressorDb = value; R.ApplyTxProcessing(); _w._txPanel?.Refresh(); }
            }

            public bool PureSignal { get => S.PureSignalAutoCal; set => _w.PsToggle.IsChecked = value; }
            public void PureSignalSingleCal() => R.PureSignalSingleCal();

            public (int low, int high) TxFilter
            {
                get => (S.TxFilterLow, S.TxFilterHigh);
                set
                {
                    _w.TxLowBox.Value = value.low;
                    _w.TxHighBox.Value = value.high;
                }
            }

            public float ForwardWatts => R.ForwardWatts;
            public float ReflectedWatts => R.ReflectedWatts;
            public float Swr => R.Swr;
            public float AlcDb => R.AlcGainDb();
            public float MicDbfs => R.MicPeakDb();

            public string BandName => BandPlan.For(R.FrequencyMHz)?.Name;

            public void SelectBand(string name)
            {
                var b = BandPlan.Bands.FirstOrDefault(x => x.Name == name);
                if (b != null) _w.SelectBand(b);
            }
        }
    }
}
