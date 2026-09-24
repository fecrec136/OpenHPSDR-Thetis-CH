/*  SetupWindow.cs

This file is part of a program that implements a Software-Defined Radio.

Setup and calibration window: receive level calibration, per-band PA gain,
filter band edges and antenna selection -- the parts of the Windows Setup
form that the Linux front end supports so far, and the transmit audio
processing (the Windows DSP / Transmit, CFC and VOX / DE setup pages and
the EQ form).  Every change is applied to
the radio at once and saved in the settings.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Thetis.Desktop.Controls;
using Thetis.Radio;

namespace Thetis.Desktop
{
    public sealed class SetupWindow : Window
    {
        private readonly RadioController _radio;
        private readonly Settings _settings;
        private readonly HPSDRModel _model;
        private readonly Action _save;
        private bool _building;

        private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA4AE"));

        public SetupWindow() : this(null, new Settings(), HPSDRModel.HERMES, () => { }) { }   // designer

        public SetupWindow(RadioController radio, Settings settings, HPSDRModel model, Action save)
        {
            _radio = radio;
            _settings = settings;
            _model = model;
            _save = save;
            Title = $"Thetis setup - {model}";
            Width = 900;
            Height = 640;
            MinWidth = 640;
            MinHeight = 420;
            Background = new SolidColorBrush(Color.Parse("#12171C"));

            var tabs = _tabs = new TabControl { Margin = new Thickness(6) };
            tabs.Items.Add(new TabItem { Header = "Receive", Content = Scroll(ReceiveTab()) });
            tabs.Items.Add(new TabItem { Header = "Noise reduction", Content = Scroll(NoiseTab()) });
            _txTab = new TabItem { Header = "Transmit audio", Content = Scroll(TxTab()) };
            tabs.Items.Add(_txTab);
            tabs.Items.Add(new TabItem { Header = "PureSignal", Content = Scroll(PureSignalTab()) });
            tabs.Items.Add(new TabItem { Header = "PA gain", Content = Scroll(PaTab()) });
            tabs.Items.Add(new TabItem { Header = "Filters", Content = Scroll(FiltersTab()) });
            tabs.Items.Add(new TabItem { Header = "Antennas", Content = Scroll(AntennaTab()) });
            Content = tabs;
            Closed += (_, _) => { _psTimer?.Stop(); _save(); };
        }

        private readonly TabControl _tabs;

        /// <summary>The tab headers, in order (the main window's Setup menu lists them).</summary>
        public static readonly string[] TabNames = { "Receive", "Noise reduction", "Transmit audio", "PureSignal", "PA gain", "Filters", "Antennas" };

        /// <summary>Bring the tab with this header to the front.</summary>
        public void ShowTab(string header)
        {
            foreach (var item in _tabs.Items)
                if (item is TabItem t && (string)t.Header == header) { _tabs.SelectedItem = t; return; }
        }

        #region helpers

        private static ScrollViewer Scroll(Control c) => new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(8, 8, 16, 8), Child = c },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,   // wrap text to the window
        };

        private static TextBlock Text(string s, bool muted = false, double size = 13)
        {
            var t = new TextBlock
            {
                Text = s, TextWrapping = TextWrapping.Wrap, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2),
            };
            if (muted) t.Foreground = Muted;      // otherwise inherit the theme's text colour
            return t;
        }

        private static TextBlock Heading(string s) => new TextBlock { Text = s, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4) };

        private static NumericUpDown Number(double value, double min, double max, double step, string format, double width = 110, bool spinner = true) => new NumericUpDown
        {
            Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step,
            FormatString = format, Width = width, Margin = new Thickness(2),
            ShowButtonSpinner = spinner,        // grids: type, or use the arrow keys / mouse wheel
            HorizontalContentAlignment = HorizontalAlignment.Right,
        };

        private static void Place(Grid g, Control c, int row, int col)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            g.Children.Add(c);
        }

        #endregion

        #region receive

        private NumericUpDown _meterBox, _displayBox;

        private Control ReceiveTab()
        {
            var p = new StackPanel { Spacing = 4 };
            var (lo, hi) = RxFrontEnd.Range(_model);
            p.Children.Add(Heading("Attenuator"));
            p.Children.Add(Text($"The {_model} attenuator covers {lo} to {hi} dB" +
                                (_model == HPSDRModel.HERMESLITE ? " (negative values are LNA gain)." : ".") +
                                " Set it per band on the main window; the S-meter and panadapter compensate for it.", true));

            p.Children.Add(Heading("Level calibration"));
            p.Children.Add(Text("Offsets added to the S-meter and the panadapter, in dB. The model's defaults are used until you change them.", true));
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("200,Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            _meterBox = Number(MeterOffset, -100, 100, 0.1, "0.0");
            _displayBox = Number(DisplayOffset, -100, 100, 0.1, "0.0");
            Place(g, Text("S-meter offset"), 0, 0); Place(g, _meterBox, 0, 1);
            Place(g, Text("Panadapter offset"), 1, 0); Place(g, _displayBox, 1, 1);
            p.Children.Add(g);
            _meterBox.ValueChanged += (_, _) =>
            {
                if (_building || _radio == null) return;
                float v = (float)(_meterBox.Value ?? 0);
                _radio.MeterCalOffsetDb = v;
                _settings.MeterCalOffset[_model] = v;
            };
            _displayBox.ValueChanged += (_, _) =>
            {
                if (_building || _radio == null) return;
                float v = (float)(_displayBox.Value ?? 0);
                _radio.DisplayCalOffsetDb = v;
                _settings.DisplayCalOffset[_model] = v;
            };
            var reset = new Button { Content = "Use the model's defaults", Margin = new Thickness(0, 4) };
            reset.Click += (_, _) =>
            {
                _settings.MeterCalOffset.Remove(_model);
                _settings.DisplayCalOffset.Remove(_model);
                if (_radio != null) { _radio.MeterCalOffsetDb = null; _radio.DisplayCalOffsetDb = null; }
                RefreshOffsets();
            };
            p.Children.Add(reset);

            p.Children.Add(Heading("Calibrate to a known signal"));
            p.Children.Add(Text("Feed in a steady carrier of known level (a signal generator, through an attenuator if needed), tune so it " +
                                "is within 2.5 kHz of the VFO, enter its level and press Calibrate. It takes about four seconds.", true));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var level = Number(-73, -140, 0, 1, "0");
            var go = new Button { Content = "Calibrate" };
            var result = Text("", true);
            row.Children.Add(Text("Signal level (dBm)"));
            row.Children.Add(level);
            row.Children.Add(go);
            p.Children.Add(row);
            p.Children.Add(result);
            go.Click += async (_, _) =>
            {
                if (_radio == null || !_radio.PowerOn) { result.Text = "Turn the radio on first."; return; }
                go.IsEnabled = false;
                result.Text = "Measuring...";
                float lvl = (float)(level.Value ?? -73);
                string error = null;
                bool ok = await Task.Run(() => _radio.CalibrateLevel(lvl, out error));
                go.IsEnabled = true;
                if (!ok) { result.Text = "Calibration failed: " + error; return; }
                _settings.MeterCalOffset[_model] = _radio.MeterCalOffsetEffective;
                _settings.DisplayCalOffset[_model] = _radio.DisplayCalOffsetEffective;
                RefreshOffsets();
                result.Text = $"Calibrated: S-meter offset {_radio.MeterCalOffsetEffective:0.0} dB, panadapter offset {_radio.DisplayCalOffsetEffective:0.0} dB.";
            };
            return p;
        }

        private float MeterOffset => _settings.MeterCalOffset.TryGetValue(_model, out float v) ? v : RadioController.DefaultMeterCalOffset(_model);
        private float DisplayOffset => _settings.DisplayCalOffset.TryGetValue(_model, out float v) ? v : RadioController.DefaultDisplayCalOffset(_model);

        private void RefreshOffsets()
        {
            _building = true;
            _meterBox.Value = (decimal)MeterOffset;
            _displayBox.Value = (decimal)DisplayOffset;
            _building = false;
        }

        #endregion

        #region noise reduction

        // AetherSDR's defaults (Nr2SettingsModel, Rn2SettingsModel, SpecbleachFilter, DeepFilterFilter)
        private static readonly Dictionary<NrParam, double> NrDefaults = new Dictionary<NrParam, double>
        {
            { NrParam.Nr2GainMethod, 2 }, { NrParam.Nr2NpeMethod, 0 }, { NrParam.Nr2AeFilter, 1 },
            { NrParam.Nr2GainMax, 1.0 }, { NrParam.Nr2GainFloor, 0.0 }, { NrParam.Nr2GainSmooth, 0.85 }, { NrParam.Nr2Qspp, 0.20 },
            { NrParam.Nr2Post2Run, 0 }, { NrParam.Nr2Post2Factor, 0.15 }, { NrParam.Nr2Post2Nlevel, 0.15 },
            { NrParam.Nr2Post2TaperHz, 2871 }, { NrParam.Nr2Post2DecaySeconds, 5.0 },
            { NrParam.Rn2DryMix, 0.0 },
            { NrParam.Nr4ReductionDb, 10 }, { NrParam.Nr4SmoothingPct, 0 }, { NrParam.Nr4WhiteningPct, 0 },
            { NrParam.Nr4Adaptive, 1 }, { NrParam.Nr4NoiseMethod, 0 }, { NrParam.Nr4MaskingDepth, 0.5 }, { NrParam.Nr4Suppression, 0.5 },
            { NrParam.DfnrAttenLimitDb, 100 }, { NrParam.DfnrPostFilterBeta, 0.0 },
            // WDSP 2.10's NNR (RXA.c create_nnr, nnet.c)
            { NrParam.NnrModel, 0 }, { NrParam.NnrMaskFloorDb, -25 }, { NrParam.NnrMaxGainDb, 12 },
            { NrParam.NnrAlpha, 1.0 }, { NrParam.NnrAlphaKneeDb, 10 }, { NrParam.NnrTau, 2.0 },
            { NrParam.NnrSmoothAttackMs, 0 }, { NrParam.NnrSmoothReleaseMs, 0 }, { NrParam.NnrPosition, 1 },
        };

        private readonly List<(NrParam p, Control c)> _nrControls = new List<(NrParam, Control)>();

        private double NrValue(NrParam p) => _settings.NrParams.TryGetValue(p, out double v) ? v : NrDefaults[p];

        private void SetNr(NrParam p, double v)
        {
            if (_building) return;
            if (Math.Abs(v - NrDefaults[p]) < 1e-9) _settings.NrParams.Remove(p);
            else _settings.NrParams[p] = v;
            _radio?.SetNoiseReductionParam(p, v);
        }

        private Control NoiseTab()
        {
            var p = new StackPanel { Spacing = 4 };
            p.Children.Add(Text("The receive noise reduction filters: NR2, RN2, NR4 and DFNR from AetherSDR (github.com/aethersdr/AetherSDR), " +
                                "and NNR, WDSP 2.10's neural noise reduction. Choose one with the Off / NR2 / RN2 / NR4 / DFNR / NNR " +
                                "buttons on the main window; the settings here apply at once. " +
                                "They run on the demodulated audio. RN2 and DFNR need 48 kHz and do not run in FM.", true));
            if (!AetherNr.Loaded)
                p.Children.Add(Text("The noise reduction library (libaethernr.so) is not loaded" +
                                    (AetherNr.Error != null ? ": " + AetherNr.Error : "."), true));

            var g = Grid2();
            p.Children.Add(Heading("NR2 - spectral noise reduction"));
            AddChoice(g, "Gain method", NrParam.Nr2GainMethod, new[] { "Linear", "Log", "Gamma", "Trained" });
            AddChoice(g, "Noise estimate", NrParam.Nr2NpeMethod, new[] { "OSMS", "MMSE", "NSTAT" });
            AddCheck(g, "Artifact filter", NrParam.Nr2AeFilter);
            AddNumber(g, "Gain max", NrParam.Nr2GainMax, 0, 4, 0.05, "0.00");
            AddNumber(g, "Gain floor", NrParam.Nr2GainFloor, 0, 1, 0.01, "0.00");
            AddNumber(g, "Gain smoothing", NrParam.Nr2GainSmooth, 0, 0.99, 0.01, "0.00");
            AddNumber(g, "Speech absence prior (qspp)", NrParam.Nr2Qspp, 0.01, 0.99, 0.01, "0.00");
            AddCheck(g, "Psychoacoustic post-processing", NrParam.Nr2Post2Run);
            AddNumber(g, "  factor", NrParam.Nr2Post2Factor, 0, 1, 0.01, "0.00");
            AddNumber(g, "  noise level", NrParam.Nr2Post2Nlevel, 0, 1, 0.01, "0.00");
            AddNumber(g, "  taper (Hz)", NrParam.Nr2Post2TaperHz, 0, 12000, 50, "0");
            AddNumber(g, "  decay (s)", NrParam.Nr2Post2DecaySeconds, 0.1, 30, 0.1, "0.0");
            p.Children.Add(g);

            g = Grid2();
            p.Children.Add(Heading("RN2 - RNNoise"));
            AddNumber(g, "Dry mix (keeps a noise floor)", NrParam.Rn2DryMix, 0, 0.5, 0.01, "0.00");
            p.Children.Add(g);

            g = Grid2();
            p.Children.Add(Heading("NR4 - libspecbleach"));
            AddNumber(g, "Reduction (dB)", NrParam.Nr4ReductionDb, 0, 40, 1, "0");
            AddNumber(g, "Smoothing (%)", NrParam.Nr4SmoothingPct, 0, 100, 1, "0");
            AddNumber(g, "Whitening (%)", NrParam.Nr4WhiteningPct, 0, 100, 1, "0");
            AddCheck(g, "Adaptive noise estimate", NrParam.Nr4Adaptive);
            AddChoice(g, "Noise estimate", NrParam.Nr4NoiseMethod, new[] { "MMSE (SPP)", "Brandt", "Martin" });
            AddNumber(g, "Masking depth", NrParam.Nr4MaskingDepth, 0, 1, 0.05, "0.00");
            AddNumber(g, "Suppression strength", NrParam.Nr4Suppression, 0, 1, 0.05, "0.00");
            p.Children.Add(g);

            g = Grid2();
            p.Children.Add(Heading("DFNR - DeepFilterNet3"));
            AddNumber(g, "Attenuation limit (dB)", NrParam.DfnrAttenLimitDb, 0, 100, 1, "0");
            AddNumber(g, "Post-filter beta", NrParam.DfnrPostFilterBeta, 0, 0.3, 0.01, "0.00");
            p.Children.Add(g);

            g = Grid2();
            p.Children.Add(Heading("NNR - WDSP neural noise reduction"));
            p.Children.Add(Text("Works on the audio resampled to 16 kHz, so it passes audio up to 8 kHz.", true));
            AddChoice(g, "Model", NrParam.NnrModel, new[] { "Standard", "Large (more CPU)" });
            AddChoice(g, "Position", NrParam.NnrPosition, new[] { "Before AGC", "After AGC" });
            AddNumber(g, "Mask floor (dB, the most it removes)", NrParam.NnrMaskFloorDb, -60, 0, 1, "0");
            AddNumber(g, "Maximum gain (dB)", NrParam.NnrMaxGainDb, 0, 24, 1, "0");
            AddNumber(g, "Strength (alpha)", NrParam.NnrAlpha, 0, 4, 0.05, "0.00");
            AddNumber(g, "Strength knee (dB)", NrParam.NnrAlphaKneeDb, 0, 40, 1, "0");
            AddNumber(g, "Noise tracking time (s)", NrParam.NnrTau, 0.05, 30, 0.05, "0.00");
            AddNumber(g, "Gain smoothing attack (ms)", NrParam.NnrSmoothAttackMs, 0, 500, 5, "0");
            AddNumber(g, "Gain smoothing release (ms)", NrParam.NnrSmoothReleaseMs, 0, 500, 5, "0");
            p.Children.Add(g);

            var reset = new Button { Content = "Defaults", Margin = new Thickness(0, 8) };
            reset.Click += (_, _) =>
            {
                _settings.NrParams.Clear();
                foreach (var (param, v) in NrDefaults) _radio?.SetNoiseReductionParam(param, v);
                _building = true;
                foreach (var (param, c) in _nrControls)
                {
                    double v = NrDefaults[param];
                    if (c is NumericUpDown n) n.Value = (decimal)v;
                    else if (c is CheckBox cb) cb.IsChecked = v != 0;
                    else if (c is ComboBox co) co.SelectedIndex = (int)v;
                }
                _building = false;
            };
            p.Children.Add(reset);
            return p;
        }

        private static Grid Grid2() => new Grid { ColumnDefinitions = new ColumnDefinitions("260,Auto") };

        private static void AddRow(Grid g, string label, Control c)
        {
            int r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Place(g, Text(label), r, 0);
            Place(g, c, r, 1);
        }

        private void AddNumber(Grid g, string label, NrParam param, double min, double max, double step, string fmt)
        {
            var n = Number(NrValue(param), min, max, step, fmt, 130);
            n.ValueChanged += (_, _) => SetNr(param, (double)(n.Value ?? (decimal)NrDefaults[param]));
            AddRow(g, label, n);
            _nrControls.Add((param, n));
        }

        private void AddCheck(Grid g, string label, NrParam param)
        {
            var c = new CheckBox { IsChecked = NrValue(param) != 0 };
            c.IsCheckedChanged += (_, _) => SetNr(param, c.IsChecked == true ? 1 : 0);
            AddRow(g, label, c);
            _nrControls.Add((param, c));
        }

        private void AddChoice(Grid g, string label, NrParam param, string[] items)
        {
            var c = new ComboBox { ItemsSource = items, SelectedIndex = (int)NrValue(param), Width = 150, Margin = new Thickness(2) };
            c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) SetNr(param, c.SelectedIndex); };
            AddRow(g, label, c);
            _nrControls.Add((param, c));
        }

        #endregion

        #region transmit audio

        private TabItem _txTab;

        private TxProcessing Tx => _settings.TxProcessing ??= new TxProcessing();

        /// <summary>A transmit audio setting changed here (the main window and its transmit audio panel follow).</summary>
        public event Action TxChanged;

        private void ApplyTx()
        {
            if (_building) return;
            _radio?.ApplyTxProcessing();
            TxChanged?.Invoke();
        }

        private void TxNumber(Grid g, string label, double value, double min, double max, double step, string fmt, Action<double> set)
        {
            var n = Number(value, min, max, step, fmt, 130);
            n.ValueChanged += (_, _) => { if (n.Value is decimal v) { set((double)v); ApplyTx(); } };
            AddRow(g, label, n);
        }

        private void TxCheck(Grid g, string label, bool value, Action<bool> set)
        {
            var c = new CheckBox { IsChecked = value };
            c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); ApplyTx(); };
            AddRow(g, label, c);
        }

        private static Grid Row(int columns) =>
            new Grid { ColumnDefinitions = new ColumnDefinitions("110," + string.Join(",", System.Linq.Enumerable.Repeat("64", columns))) };

        private Control TxTab()
        {
            var t = Tx;
            var p = new StackPanel { Spacing = 4 };
            p.Children.Add(Text("Processing of the microphone audio before it is transmitted, in WDSP's order: EQ, leveler, CFC, " +
                                "compressor, CESSB. VOX, COMP and EQ can also be switched on the main window. Changes apply at once.", true));

            var g = Grid2();
            p.Children.Add(Heading("Leveler"));
            TxCheck(g, "On", t.LevelerOn, v => t.LevelerOn = v);
            TxNumber(g, "Maximum gain (dB)", t.LevelerMaxGainDb, 0, 20, 1, "0", v => t.LevelerMaxGainDb = v);
            TxNumber(g, "Decay (ms)", t.LevelerDecayMs, 1, 5000, 10, "0", v => t.LevelerDecayMs = (int)v);
            p.Children.Add(g);

            g = Grid2();
            p.Children.Add(Heading("Compressor"));
            TxCheck(g, "On (COMP)", t.CompressorOn, v => t.CompressorOn = v);
            TxNumber(g, "Compression (dB)", t.CompressorDb, 0, 20, 1, "0", v => t.CompressorDb = v);
            TxCheck(g, "CESSB overshoot control", t.CessbOn, v => t.CessbOn = v);
            p.Children.Add(g);

            p.Children.Add(Heading("Equaliser"));
            g = Grid2();
            TxCheck(g, "On (EQ)", t.EqOn, v => t.EqOn = v);
            p.Children.Add(g);
            p.Children.Add(Text("Gain per band, -12 to +15 dB.", true));
            if (t.EqBandsDb == null || t.EqBandsDb.Length != 10) t.EqBandsDb = new int[10];
            var eq = Row(11);
            eq.RowDefinitions = new RowDefinitions("Auto,Auto");
            Place(eq, Text("Hz"), 0, 0);
            Place(eq, Text("dB"), 1, 0);
            Place(eq, Text("Preamp", true), 0, 1);
            var pre = Number(t.EqPreampDb, -12, 15, 1, "0", 60, false);
            pre.ValueChanged += (_, _) => { t.EqPreampDb = (int)(pre.Value ?? 0); ApplyTx(); };
            Place(eq, pre, 1, 1);
            for (int i = 0; i < 10; i++)
            {
                int band = i;
                int hz = TxProcessing.EqFrequencies[i];
                Place(eq, Text(hz >= 1000 ? $"{hz / 1000}k" : hz.ToString(), true), 0, i + 2);
                var n = Number(t.EqBandsDb[i], -12, 15, 1, "0", 60, false);
                n.ValueChanged += (_, _) => { t.EqBandsDb[band] = (int)(n.Value ?? 0); ApplyTx(); };
                Place(eq, n, 1, i + 2);
            }
            p.Children.Add(eq);

            p.Children.Add(Heading("CFC - continuous frequency compressor"));
            g = Grid2();
            TxCheck(g, "On", t.CfcOn, v => t.CfcOn = v);
            TxNumber(g, "Pre-compression (dB)", t.CfcPrecompDb, 0, 16, 1, "0", v => t.CfcPrecompDb = v);
            TxCheck(g, "Post-compression EQ", t.CfcPostEqOn, v => t.CfcPostEqOn = v);
            TxNumber(g, "Post-EQ gain (dB)", t.CfcPostEqGainDb, -16, 16, 1, "0", v => t.CfcPostEqGainDb = v);
            p.Children.Add(g);
            t.CfcFrequencies = Resize(t.CfcFrequencies, new double[] { 0, 125, 250, 500, 1000, 2000, 3000, 4000, 5000, 10000 });
            t.CfcCompressionDb = Resize(t.CfcCompressionDb, new double[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 });
            t.CfcPostEqDb = Resize(t.CfcPostEqDb, new double[10]);
            var cfc = Row(10);
            cfc.RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
            Place(cfc, Text("Hz"), 0, 0);
            Place(cfc, Text("Compression"), 1, 0);
            Place(cfc, Text("Post-EQ dB"), 2, 0);
            for (int i = 0; i < 10; i++)
            {
                int k = i;
                var f = Number(t.CfcFrequencies[i], 0, 20000, 10, "0", 64, false);
                var c = Number(t.CfcCompressionDb[i], 0, 20, 1, "0", 64, false);
                var e = Number(t.CfcPostEqDb[i], -20, 20, 1, "0", 64, false);
                f.ValueChanged += (_, _) => { t.CfcFrequencies[k] = (double)(f.Value ?? 0); ApplyTx(); };
                c.ValueChanged += (_, _) => { t.CfcCompressionDb[k] = (double)(c.Value ?? 0); ApplyTx(); };
                e.ValueChanged += (_, _) => { t.CfcPostEqDb[k] = (double)(e.Value ?? 0); ApplyTx(); };
                Place(cfc, f, 0, i + 1);
                Place(cfc, c, 1, i + 1);
                Place(cfc, e, 2, i + 1);
            }
            p.Children.Add(cfc);

            g = Grid2();
            p.Children.Add(Heading("Phase rotator"));
            TxCheck(g, "On", t.PhaseRotatorOn, v => t.PhaseRotatorOn = v);
            TxNumber(g, "Corner frequency (Hz)", t.PhaseRotatorHz, 50, 2000, 1, "0", v => t.PhaseRotatorHz = v);
            TxNumber(g, "Stages", t.PhaseRotatorStages, 1, 16, 1, "0", v => t.PhaseRotatorStages = (int)v);
            p.Children.Add(g);

            g = Grid2();
            p.Children.Add(Heading("VOX and downward expander"));
            p.Children.Add(Text("VOX keys the transmitter when the microphone level passes the threshold, in voice and digital modes, " +
                                "and releases it after the hold time. The main window shows the level VOX hears. The expander " +
                                "lowers the background noise between words.", true));
            TxCheck(g, "VOX on", t.VoxOn, v => t.VoxOn = v);
            TxNumber(g, "Threshold (dB)", t.VoxThresholdDb, -80, 0, 1, "0", v => t.VoxThresholdDb = v);
            TxNumber(g, "Hold (ms)", t.VoxHoldMs, 1, 2000, 10, "0", v => t.VoxHoldMs = (int)v);
            TxCheck(g, "Expander on", t.ExpanderOn, v => t.ExpanderOn = v);
            TxNumber(g, "Expansion ratio (dB)", t.ExpanderRatioDb, 0, 30, 1, "0", v => t.ExpanderRatioDb = v);
            TxNumber(g, "Hysteresis (dB)", t.ExpanderHysteresisDb, 0, 10, 0.5, "0.0", v => t.ExpanderHysteresisDb = v);
            TxNumber(g, "Attack (ms)", t.ExpanderAttackMs, 1, 100, 1, "0", v => t.ExpanderAttackMs = (int)v);
            TxNumber(g, "Release (ms)", t.ExpanderReleaseMs, 1, 1000, 10, "0", v => t.ExpanderReleaseMs = (int)v);
            TxNumber(g, "Detector time constant (ms)", t.DetectorTauMs, 1, 100, 1, "0", v => t.DetectorTauMs = (int)v);
            TxCheck(g, "Side-channel filter", t.SideChannelFilterOn, v => t.SideChannelFilterOn = v);
            TxNumber(g, "  low (Hz)", t.SideChannelLowHz, 100, 10000, 10, "0", v => t.SideChannelLowHz = v);
            TxNumber(g, "  high (Hz)", t.SideChannelHighHz, 100, 10000, 10, "0", v => t.SideChannelHighHz = v);
            TxCheck(g, "Audio look-ahead", t.LookAheadOn, v => t.LookAheadOn = v);
            TxNumber(g, "  look-ahead (ms)", t.LookAheadMs, 10, 250, 5, "0", v => t.LookAheadMs = (int)v);
            p.Children.Add(g);

            g = Grid2();
            p.Children.Add(Heading("NNR - WDSP neural noise reduction"));
            p.Children.Add(Text("Works on the audio resampled to 16 kHz, so it passes audio up to 8 kHz.", true));
            AddChoice(g, "Model", NrParam.NnrModel, new[] { "Standard", "Large (more CPU)" });
            AddChoice(g, "Position", NrParam.NnrPosition, new[] { "Before AGC", "After AGC" });
            AddNumber(g, "Mask floor (dB, the most it removes)", NrParam.NnrMaskFloorDb, -60, 0, 1, "0");
            AddNumber(g, "Maximum gain (dB)", NrParam.NnrMaxGainDb, 0, 24, 1, "0");
            AddNumber(g, "Strength (alpha)", NrParam.NnrAlpha, 0, 4, 0.05, "0.00");
            AddNumber(g, "Strength knee (dB)", NrParam.NnrAlphaKneeDb, 0, 40, 1, "0");
            AddNumber(g, "Noise tracking time (s)", NrParam.NnrTau, 0.05, 30, 0.05, "0.00");
            AddNumber(g, "Gain smoothing attack (ms)", NrParam.NnrSmoothAttackMs, 0, 500, 5, "0");
            AddNumber(g, "Gain smoothing release (ms)", NrParam.NnrSmoothReleaseMs, 0, 500, 5, "0");
            p.Children.Add(g);

            var reset = new Button { Content = "Defaults", Margin = new Thickness(0, 8) };
            reset.Click += (_, _) =>
            {
                _settings.TxProcessing = new TxProcessing();
                if (_radio != null) _radio.TxProcessing = _settings.TxProcessing;
                _txTab.Content = Scroll(TxTab());
            };
            p.Children.Add(reset);
            return p;
        }

        private static double[] Resize(double[] a, double[] defaults)
        {
            if (a != null && a.Length == defaults.Length) return a;
            var r = (double[])defaults.Clone();
            if (a != null) Array.Copy(a, r, Math.Min(a.Length, r.Length));
            return r;
        }

        #endregion

        #region PureSignal

        private DispatcherTimer _psTimer;

        private PureSignalSettings Ps => _settings.PureSignal ??= new PureSignalSettings();

        private void ApplyPs()
        {
            if (!_building) _radio?.ApplyPureSignalSettings();
        }

        /// <summary>One correction file per radio model and band (corrections do not carry across bands).</summary>
        private string PsFile()
        {
            string band = _radio != null ? BandPlan.For(_radio.FrequencyMHz)?.Name ?? "GEN" : "GEN";
            return System.IO.Path.Combine(Settings.DataDirectory, "puresignal", $"{_model}-{band}.txt");
        }

        private Control PureSignalTab()
        {
            var ps = Ps;
            var p = new StackPanel { Spacing = 4 };
            p.Children.Add(Text("PureSignal corrects the distortion of the transmitter's power amplifier. While transmitting, the radio returns " +
                                "a sample of its output; WDSP compares it with the signal sent and predistorts the transmit signal so the " +
                                "output matches. Turn it on with PS-A on the main window, then transmit - the 2-TONE test signal calibrates " +
                                "best. Auto-attenuate sets the TX attenuator so the feedback level is in range (128-181).", true));
            if (_model == HPSDRModel.HERMESLITE)
                p.Children.Add(Text("Hermes-Lite 2: PureSignal needs the 192 kHz sample rate.", true));

            p.Children.Add(Heading("Status"));
            var status = Text("", false);
            var curves = new PsCurveView { Height = 180, Margin = new Thickness(0, 4) };
            p.Children.Add(status);
            p.Children.Add(curves);

            var buttons = new WrapPanel();
            Button B(string label, Action click)
            {
                var b = new Button { Content = label, Margin = new Thickness(0, 4, 6, 4) };
                b.Click += (_, _) => click();
                buttons.Children.Add(b);
                return b;
            }
            var result = Text("", true);
            B("Calibrate once", () => { _radio?.PureSignalSingleCal(); result.Text = "Calibrates on the next transmission, then keeps that correction."; });
            B("Reset", () => { _radio?.PureSignalReset(); _settings.PureSignalAutoCal = false; result.Text = "PureSignal off, correction discarded."; });
            B("Save correction", () =>
            {
                if (_radio == null) return;
                result.Text = _radio.PureSignalSave(PsFile(), out string e) ? "Saved to " + PsFile() : e;
            });
            B("Restore correction", () =>
            {
                if (_radio == null) return;
                result.Text = _radio.PureSignalRestore(PsFile(), out string e) ? "Restored from " + PsFile() : e;
            });
            p.Children.Add(buttons);
            p.Children.Add(result);

            var g = Grid2();
            p.Children.Add(Heading("Settings"));
            TxCheck2(g, "Auto-attenuate", ps.AutoAttenuate, v => ps.AutoAttenuate = v);
            int attMin = _model == HPSDRModel.HERMESLITE ? -28 : 0;
            var att = Number(_radio?.TxAttenuationDb ?? 31, attMin, 31, 1, "0", 130);
            att.ValueChanged += (_, _) =>
            {
                if (_building || _radio == null || att.Value is not decimal v) return;
                _radio.TxAttenuationDb = (int)v;
                string band = BandPlan.For(_radio.FrequencyMHz)?.Name ?? "GEN";
                (_settings.TxAttenuationByBand ??= new Dictionary<string, int>())[band] = (int)v;
            };
            AddRow(g, "TX attenuator, this band (dB)", att);
            var peak = Number(ps.HwPeak ?? _radio?.DefaultPureSignalPeak ?? 0.4072, 0.01, 2.0, 0.0001, "0.0000", 130);
            peak.ValueChanged += (_, _) => { if (peak.Value is decimal v) { ps.HwPeak = (double)v; ApplyPs(); } };
            AddRow(g, "Hardware peak", peak);
            var defPeak = new Button { Content = "Default peak", Margin = new Thickness(2) };
            defPeak.Click += (_, _) =>
            {
                ps.HwPeak = null;
                _building = true;
                peak.Value = (decimal)(_radio?.DefaultPureSignalPeak ?? 0.4072);
                _building = false;
                ApplyPs();
            };
            AddRow(g, "", defPeak);
            PsNumber(g, "MOX delay (s)", ps.MoxDelay, 0, 10, 0.1, "0.0", v => ps.MoxDelay = v);
            PsNumber(g, "Wait between calibrations (s)", ps.CalWait, 0, 100, 0.1, "0.0", v => ps.CalWait = v);
            PsNumber(g, "TX delay (ns)", ps.TxDelayNs, -25000, 25000, 10, "0", v => ps.TxDelayNs = v);
            TxCheck2(g, "Relax tolerance", ps.RelaxTolerance, v => ps.RelaxTolerance = v);
            p.Children.Add(g);

            g = Grid2();
            p.Children.Add(Heading("Two-tone test signal"));
            PsNumber(g, "Tone 1 (Hz)", ps.TwoToneFreq1, 50, 5000, 10, "0", v => ps.TwoToneFreq1 = v);
            PsNumber(g, "Tone 2 (Hz)", ps.TwoToneFreq2, 50, 5000, 10, "0", v => ps.TwoToneFreq2 = v);
            PsNumber(g, "Level (dB)", ps.TwoToneLevelDb, -60, 0, 1, "0", v => ps.TwoToneLevelDb = v);
            p.Children.Add(g);

            // live status and curves
            double[] ax = new double[512], ay = new double[512], fx = new double[512], fy = new double[512];
            _psTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _psTimer.Tick += (_, _) =>
            {
                if (_radio == null || !_radio.PowerOn) { status.Text = "The radio is off."; curves.SetCurves(null, null, null, null); return; }
                var st = _radio.PureSignalStatus;
                status.Text = $"PS-A {(st.AutoCal ? "on" : "off")}, engine {st.State}, calibrations {st.CalibrationCount}, " +
                              $"{st.LevelText} ({st.FeedbackLevel}), correction {(st.CorrectionsApplied ? (st.Correcting ? "applied" : "kept") : "none")}, " +
                              $"TX attenuator {st.TxAttenuationDb} dB, peak {st.MaxTx:0.0000}";
                if (_radio.PureSignalCurves(ax, ay, fx, fy)) curves.SetCurves(ax, ay, fx, fy);
                else curves.SetCurves(null, null, null, null);
                if (!att.IsFocused && (int)(att.Value ?? 0) != st.TxAttenuationDb)
                {
                    _building = true;
                    att.Value = st.TxAttenuationDb;
                    _building = false;
                }
            };
            _psTimer.Start();
            return p;
        }

        private void PsNumber(Grid g, string label, double value, double min, double max, double step, string fmt, Action<double> set)
        {
            var n = Number(value, min, max, step, fmt, 130);
            n.ValueChanged += (_, _) => { if (n.Value is decimal v) { set((double)v); ApplyPs(); } };
            AddRow(g, label, n);
        }

        private void TxCheck2(Grid g, string label, bool value, Action<bool> set)
        {
            var c = new CheckBox { IsChecked = value };
            c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); ApplyPs(); };
            AddRow(g, label, c);
        }

        #endregion

        #region PA gain

        private PaCalibration _pa;
        private readonly Dictionary<Band, NumericUpDown[]> _paBoxes = new Dictionary<Band, NumericUpDown[]>();

        private PaCalibration PaForEdit()
        {
            if (!_settings.PaGains.TryGetValue(_model, out PaCalibration pa) || pa == null)
                pa = PaCalibration.DefaultsFor(_model);
            foreach (Band b in HamBands.All)
                if (!pa.Bands.ContainsKey(b) || pa.Bands[b] == null) pa.Bands[b] = PaCalibration.DefaultsFor(_model).Bands[b];
            return pa;
        }

        private Control PaTab()
        {
            _pa = PaForEdit();
            var p = new StackPanel { Spacing = 4 };
            p.Children.Add(Text($"PA gain per band for the {_model}, in dB. With the right gain, the Drive and Tune power percentages are " +
                                "close to watts on a 100 W radio. If the output is too high, raise the gain; if too low, lower it. " +
                                "The corrections are subtracted from the gain at that drive level (interpolated in between), " +
                                "for PAs whose gain changes with power.", true));
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("70,76" + string.Concat(System.Linq.Enumerable.Repeat(",62", 9))) };
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Place(g, Text("Band", true), 0, 0);
            Place(g, Text("Gain", true), 0, 1);
            for (int i = 0; i < 9; i++) Place(g, Text($"{(i + 1) * 10} %", true), 0, 2 + i);
            int r = 1;
            foreach (Band b in HamBands.All)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Place(g, Text(HamBands.Label(b)), r, 0);
                var boxes = new NumericUpDown[10];
                boxes[0] = Number(_pa.Bands[b].GainDb, 0, 100, 0.1, "0.0", 70, spinner: false);
                Place(g, boxes[0], r, 1);
                for (int i = 0; i < 9; i++)
                {
                    boxes[i + 1] = Number(_pa.Bands[b].DriveAdjustDb[i], -20, 20, 0.1, "0.0", 58, spinner: false);
                    Place(g, boxes[i + 1], r, 2 + i);
                }
                Band band = b;
                foreach (var box in boxes) box.ValueChanged += (_, _) => PaChanged(band);
                _paBoxes[b] = boxes;
                r++;
            }
            p.Children.Add(g);
            var reset = new Button { Content = "Reset to the model's defaults", Margin = new Thickness(0, 6) };
            reset.Click += (_, _) =>
            {
                _settings.PaGains.Remove(_model);
                _pa = PaForEdit();
                if (_radio != null) _radio.PaGains = null;
                _building = true;
                foreach (Band b in HamBands.All)
                {
                    _paBoxes[b][0].Value = (decimal)_pa.Bands[b].GainDb;
                    for (int i = 0; i < 9; i++) _paBoxes[b][i + 1].Value = (decimal)_pa.Bands[b].DriveAdjustDb[i];
                }
                _building = false;
            };
            p.Children.Add(reset);
            return p;
        }

        private void PaChanged(Band b)
        {
            if (_building) return;
            var boxes = _paBoxes[b];
            _pa.Bands[b].GainDb = (float)(boxes[0].Value ?? 100);
            for (int i = 0; i < 9; i++) _pa.Bands[b].DriveAdjustDb[i] = (float)(boxes[i + 1].Value ?? 0);
            _settings.PaGains[_model] = _pa;
            if (_radio != null) _radio.PaGains = _pa;
        }

        #endregion

        #region filters

        private Control FiltersTab()
        {
            var p = new StackPanel { Spacing = 4 };
            p.Children.Add(Text("Frequency ranges (MHz) that select each filter. The low-pass filter follows the transmit frequency while " +
                                "transmitting, so a wrong edge here can let harmonics out: change these only to match your hardware.", true));
            p.Children.Add(FilterTable("Alex low-pass filters", () => BandFilters.LpfEdges, e => { BandFilters.LpfEdges = e; _settings.LpfEdges = BandFilters.LpfEdges; },
                                       () => BandFilters.DefaultLpfEdges, () => _settings.LpfEdges = null));
            p.Children.Add(FilterTable("Alex high-pass filters", () => BandFilters.HpfEdges, e => { BandFilters.HpfEdges = e; _settings.HpfEdges = BandFilters.HpfEdges; },
                                       () => BandFilters.DefaultHpfEdges, () => _settings.HpfEdges = null));
            p.Children.Add(FilterTable("Band-pass filters (ANAN-7000D, 8000D, G2 and other OrionMKII / Saturn boards)", () => BandFilters.Bpf1Edges,
                                       e => { BandFilters.Bpf1Edges = e; _settings.Bpf1Edges = BandFilters.Bpf1Edges; },
                                       () => BandFilters.DefaultBpf1Edges, () => _settings.Bpf1Edges = null));
            return p;
        }

        private Control FilterTable(string title, Func<FilterEdge[]> get, Action<FilterEdge[]> set, Func<FilterEdge[]> defaults, Action clearSaved)
        {
            var p = new StackPanel();
            p.Children.Add(Heading(title));
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,116,116") };
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Place(g, Text("Filter", true), 0, 0);
            Place(g, Text("From", true), 0, 1);
            Place(g, Text("To", true), 0, 2);
            FilterEdge[] edges = get();
            var boxes = new List<(NumericUpDown from, NumericUpDown to)>();
            for (int i = 0; i < edges.Length; i++)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Place(g, Text(edges[i].Name), i + 1, 0);
                var from = Number(edges[i].StartMHz, 0, 61.44, 0.1, "0.000000", 110, spinner: false);
                var to = Number(edges[i].EndMHz, 0, 61.44, 0.1, "0.000000", 110, spinner: false);
                Place(g, from, i + 1, 1);
                Place(g, to, i + 1, 2);
                boxes.Add((from, to));
            }
            void Changed()
            {
                if (_building) return;
                FilterEdge[] e = get();
                for (int i = 0; i < e.Length; i++)
                {
                    e[i].StartMHz = (double)(boxes[i].from.Value ?? 0);
                    e[i].EndMHz = (double)(boxes[i].to.Value ?? 0);
                }
                set(e);
                _radio?.RefreshRelays();
            }
            foreach (var (from, to) in boxes)
            {
                from.ValueChanged += (_, _) => Changed();
                to.ValueChanged += (_, _) => Changed();
            }
            p.Children.Add(g);
            var reset = new Button { Content = "Defaults", Margin = new Thickness(0, 4) };
            reset.Click += (_, _) =>
            {
                FilterEdge[] d = defaults();
                set(d);
                clearSaved();
                _building = true;
                for (int i = 0; i < d.Length; i++)
                {
                    boxes[i].from.Value = (decimal)d[i].StartMHz;
                    boxes[i].to.Value = (decimal)d[i].EndMHz;
                }
                _building = false;
                _radio?.RefreshRelays();
            };
            p.Children.Add(reset);
            return p;
        }

        #endregion

        #region antennas

        private static readonly string[] RxOnlyNames = { "None", "RX1 In", "RX2 In", "XVTR" };

        private Control AntennaTab()
        {
            AntennaSettings a = _settings.Antennas ??= AntennaSettings.Defaults();
            foreach (Band b in HamBands.All)
                if (!a.Bands.ContainsKey(b) || a.Bands[b] == null) a.Bands[b] = new BandAntennas();

            var p = new StackPanel { Spacing = 4 };
            var enable = new CheckBox { Content = "Alex antenna control", IsChecked = a.Enabled };
            enable.IsCheckedChanged += (_, _) => { a.Enabled = enable.IsChecked == true; Apply(); };
            p.Children.Add(enable);
            p.Children.Add(Text("Which antenna connector the radio uses on each band. Receive-only inputs are the Alex RX1 In / RX2 In " +
                                "(Ext) ports; XVTR is only used with a transverter.", true));

            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("80,110,110,130") };
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Place(g, Text("Band", true), 0, 0);
            Place(g, Text("Receive", true), 0, 1);
            Place(g, Text("Transmit", true), 0, 2);
            Place(g, Text("Receive-only input", true), 0, 3);
            int r = 1;
            foreach (Band b in HamBands.All)
            {
                BandAntennas ba = a.Bands[b];
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Place(g, Text(HamBands.Label(b)), r, 0);
                var rx = new ComboBox { ItemsSource = new[] { "ANT1", "ANT2", "ANT3" }, SelectedIndex = Math.Clamp(ba.RxAnt, 1, 3) - 1, Margin = new Thickness(2), Width = 100 };
                var tx = new ComboBox { ItemsSource = new[] { "ANT1", "ANT2", "ANT3" }, SelectedIndex = Math.Clamp(ba.TxAnt, 1, 3) - 1, Margin = new Thickness(2), Width = 100 };
                var ro = new ComboBox { ItemsSource = RxOnlyNames, SelectedIndex = Math.Clamp(ba.RxOnly, 0, 3), Margin = new Thickness(2), Width = 120 };
                rx.SelectionChanged += (_, _) => { ba.RxAnt = rx.SelectedIndex + 1; Apply(); };
                tx.SelectionChanged += (_, _) => { ba.TxAnt = tx.SelectedIndex + 1; Apply(); };
                ro.SelectionChanged += (_, _) => { ba.RxOnly = ro.SelectedIndex; Apply(); };
                Place(g, rx, r, 1);
                Place(g, tx, r, 2);
                Place(g, ro, r, 3);
                r++;
            }
            p.Children.Add(g);

            p.Children.Add(Heading("While transmitting"));
            var rxOut = new CheckBox { Content = "Switch the RX bypass out relay", IsChecked = a.RxOutOnTx };
            var ext1 = new CheckBox { Content = "Use the RX2 In (Ext1) input", IsChecked = a.Ext1OutOnTx };
            var ext2 = new CheckBox { Content = "Use the RX1 In (Ext2) input", IsChecked = a.Ext2OutOnTx };
            rxOut.IsCheckedChanged += (_, _) => { a.RxOutOnTx = rxOut.IsChecked == true; Apply(); };
            ext1.IsCheckedChanged += (_, _) => { a.Ext1OutOnTx = ext1.IsChecked == true; Apply(); };
            ext2.IsCheckedChanged += (_, _) => { a.Ext2OutOnTx = ext2.IsChecked == true; Apply(); };
            p.Children.Add(rxOut);
            p.Children.Add(ext1);
            p.Children.Add(ext2);
            if (_model == HPSDRModel.HERMESLITE)
                p.Children.Add(Text("Hermes-Lite 2: the I/O board aerial switching of the Windows version is not supported yet.", true));
            return p;

            void Apply()
            {
                if (_radio != null) _radio.Antennas = a;
            }
        }

        #endregion
    }
}
