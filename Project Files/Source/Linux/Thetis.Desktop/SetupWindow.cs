/*  SetupWindow.cs

This file is part of a program that implements a Software-Defined Radio.

Setup and calibration window: receive level calibration, per-band PA gain,
filter band edges and antenna selection -- the parts of the Windows Setup
form that the Linux front end supports so far.  Every change is applied to
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

            var tabs = new TabControl { Margin = new Thickness(6) };
            tabs.Items.Add(new TabItem { Header = "Receive", Content = Scroll(ReceiveTab()) });
            tabs.Items.Add(new TabItem { Header = "Noise reduction", Content = Scroll(NoiseTab()) });
            tabs.Items.Add(new TabItem { Header = "PA gain", Content = Scroll(PaTab()) });
            tabs.Items.Add(new TabItem { Header = "Filters", Content = Scroll(FiltersTab()) });
            tabs.Items.Add(new TabItem { Header = "Antennas", Content = Scroll(AntennaTab()) });
            Content = tabs;
            Closed += (_, _) => _save();
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
            p.Children.Add(Text("The receive noise reduction filters from AetherSDR (github.com/aethersdr/AetherSDR). Choose one with " +
                                "the Off / NR2 / RN2 / NR4 / DFNR buttons on the main window; the settings here apply at once. " +
                                "They run after AGC, on the demodulated audio. RN2 and DFNR need 48 kHz and do not run in FM.", true));
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
