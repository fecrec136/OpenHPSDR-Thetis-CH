/*  MainWindow.axaml.cs

This file is part of a program that implements a Software-Defined Radio.

Main receiver window of the Linux front end: radio selection and power,
VFO, band/mode/filter, AGC, volume, noise reduction, S-meter, panadapter
and waterfall, PC audio routing, and transmit (MOX, TUNE, drive, mic,
meters and the transmit settings).

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Thetis.Audio;
using Thetis.Radio;

namespace Thetis.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly Settings _settings = Settings.Load();
        private readonly RadioController _radio = new RadioController(Settings.DataDirectory);
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly Dictionary<DSPMode, ToggleButton> _modeButtons = new Dictionary<DSPMode, ToggleButton>();
        private readonly Dictionary<AGCMode, ToggleButton> _agcButtons = new Dictionary<AGCMode, ToggleButton>();
        private readonly Dictionary<string, ToggleButton> _bandButtons = new Dictionary<string, ToggleButton>();
        private readonly List<ToggleButton> _filterButtons = new List<ToggleButton>();
        private List<DiscoveredRadio> _radios = new List<DiscoveredRadio>();
        private IReadOnlyList<AudioHostApi> _hostApis = Array.Empty<AudioHostApi>();
        private IReadOnlyList<AudioDevice> _outputs = Array.Empty<AudioDevice>();
        private IReadOnlyList<AudioDevice> _inputs = Array.Empty<AudioDevice>();
        private string _lastTxMessage;
        private float[] _pan = Array.Empty<float>();
        private float[] _wf = Array.Empty<float>();
        private bool _updating;              // suppress control events while code updates controls
        private int _meterDivider;

        private static readonly int[] _steps = { 1, 10, 50, 100, 250, 500, 1000, 2500, 5000, 9000, 10000, 12500, 25000, 100000 };
        private static readonly DSPMode[] _modes =
        {
            DSPMode.LSB, DSPMode.USB, DSPMode.DSB, DSPMode.CWL, DSPMode.CWU,
            DSPMode.FM, DSPMode.AM, DSPMode.SAM, DSPMode.DIGL, DSPMode.DIGU,
        };
        private static readonly (TxRegion region, string label)[] _regions =
        {
            (TxRegion.None, "Not set (transmit disabled)"),
            (TxRegion.IaruRegion1, "IARU Region 1 (Europe, Africa, Middle East)"),
            (TxRegion.IaruRegion2, "IARU Region 2 (Americas)"),
            (TxRegion.UnitedStates, "United States"),
            (TxRegion.IaruRegion3, "IARU Region 3 (Asia-Pacific)"),
        };
        private static readonly AGCMode[] _agcModes = { AGCMode.FIXD, AGCMode.LONG, AGCMode.SLOW, AGCMode.MED, AGCMode.FAST };

        public MainWindow()
        {
            InitializeComponent();
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
            _radio.Status += s => Dispatcher.UIThread.Post(() => StatusText.Text = s);
            _radio.TxRefused += r => Dispatcher.UIThread.Post(() => { StatusText.Text = r; _lastTxMessage = r; });
            _radio.TxRefusedOutOfBand += r => Dispatcher.UIThread.Post(() => ShowOutOfBandAlert(r));
            _radio.TxStateChanged += () => Dispatcher.UIThread.Post(RefreshTx);

            BuildStaticControls();
            BuildPanels();
            ApplySettingsToRadio();
            RefreshAll();

            Opened += async (_, _) => await StartDspAsync();
            Closing += (_, _) => Shutdown();
            Opened += (_, _) => PlacePanels();
            Opened += (_, _) => StartCat();
            // killed or crashed without closing the window: Thetis.Core unkeys the
            // radio; keep the settings too
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _settings.Save(); } catch (Exception) { } };
            _timer.Tick += (_, _) => OnTick();
        }

        #region construction

        private void BuildStaticControls()
        {
            foreach (var band in BandPlan.Bands)
            {
                var b = new ToggleButton { Content = band.Name, Classes = { "panel" } };
                b.Click += (_, _) => SelectBand(band);
                BandPanel.Children.Add(b);
                _bandButtons[band.Name] = b;
            }
            foreach (var mode in _modes)
            {
                var b = new ToggleButton { Content = mode.ToString(), Classes = { "panel" } };
                b.Click += (_, _) => SetMode(mode);
                ModePanel.Children.Add(b);
                _modeButtons[mode] = b;
            }
            foreach (var agc in _agcModes)
            {
                var b = new ToggleButton { Content = AgcLabel(agc), Classes = { "panel" } };
                b.Click += (_, _) => SetAgc(agc);
                AgcPanel.Children.Add(b);
                _agcButtons[agc] = b;
            }

            ModelBox.ItemsSource = Enum.GetValues<HPSDRModel>().Where(m => m > HPSDRModel.FIRST && m < HPSDRModel.LAST).ToList();
            StepBox.ItemsSource = _steps.Select(StepLabel).ToList();
            SampleRateBox.ItemsSource = RadioController.SampleRates.Select(r => $"{r / 1000} kHz").ToList();

            PowerButton.IsCheckedChanged += (_, _) => { if (!_updating) _ = TogglePowerAsync(); };
            DiscoverButton.Click += async (_, _) => await DiscoverAsync();
            RadioBox.SelectionChanged += (_, _) =>
            {
                if (_updating || RadioBox.SelectedIndex < 0 || RadioBox.SelectedIndex >= _radios.Count) return;
                var r = _radios[RadioBox.SelectedIndex];
                _settings.LastRadioMac = r.Info.MacAddress;
                _updating = true;
                ModelBox.SelectedItem = _settings.Model ?? r.SuggestedModel;
                _updating = false;
            };
            ModelBox.SelectionChanged += (_, _) =>
            {
                if (_updating || ModelBox.SelectedItem is not HPSDRModel m) return;
                _settings.Model = m;
                RefreshAttenuator();                // the range depends on the model
            };

            Vfo.Tuned += hz => Tune(hz / 1e6);
            Vfo.EditRequested += BeginVfoEdit;
            VfoEdit.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) EndVfoEdit(true);
                else if (e.Key == Key.Escape) EndVfoEdit(false);
            };
            VfoEdit.LostFocus += (_, _) => EndVfoEdit(false);

            Panafall.TuneTo += hz => Tune(SnapToStep(hz) / 1e6);
            Panafall.TuneSteps += n => Tune(SnapToStep((long)Math.Round(_radio.FrequencyMHz * 1e6) + (long)n * _settings.TuneStepHz) / 1e6);
            Panafall.ViewChanged += () =>
            {
                _settings.SpectrumMaxDbm = Panafall.MaxDbm;
                _settings.SpectrumMinDbm = Panafall.MinDbm;
                _settings.PanFraction = Panafall.PanFraction;
            };

            VolumeSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || _updating) return;
                _settings.Volume = VolumeSlider.Value / 100.0;
                _radio.Volume = _catMute ? 0 : _settings.Volume;      // muted over CAT / TCI until unmuted
            };
            AgcTopSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || _updating) return;
                _radio.AgcTop = _settings.AgcTop = Math.Round(AgcTopSlider.Value);
                AgcTopCaption.Text = AgcTopText(_settings.AgcTop);
            };
            ZoomSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || _updating) return;
                _settings.SpectrumZoom = ZoomSlider.Value / 100.0;
                _radio.SetSpectrumZoom(_settings.SpectrumZoom);
            };
            BuildNoiseReductionButtons();
            AnfToggle.IsCheckedChanged += (_, _) => { if (!_updating) _radio.AutoNotch = _settings.AutoNotch = AnfToggle.IsChecked == true; };
            StepBox.SelectionChanged += (_, _) => { if (!_updating && StepBox.SelectedIndex >= 0) _settings.TuneStepHz = _steps[StepBox.SelectedIndex]; };
            SampleRateBox.SelectionChanged += (_, _) =>
            {
                if (_updating || SampleRateBox.SelectedIndex < 0) return;
                _radio.SampleRate = _settings.SampleRate = RadioController.SampleRates[SampleRateBox.SelectedIndex];
            };

            PcAudioCheck.IsCheckedChanged += (_, _) => { if (!_updating) { _settings.PcAudio = PcAudioCheck.IsChecked == true; ApplyAudioRouting(); } };
            HostApiBox.SelectionChanged += (_, _) =>
            {
                if (_updating || HostApiBox.SelectedIndex < 0) return;
                _settings.AudioHostApi = _hostApis[HostApiBox.SelectedIndex].Name;
                _settings.AudioOutputDevice = null;
                LoadOutputDevices();
                ApplyAudioRouting();
            };
            InputDeviceBox.SelectionChanged += (_, _) =>
            {
                if (_updating || InputDeviceBox.SelectedIndex < 0) return;
                _settings.AudioInputDevice = _inputs[InputDeviceBox.SelectedIndex].Name;
                ApplyAudioRouting();
            };
            OutputDeviceBox.SelectionChanged += (_, _) =>
            {
                if (_updating || OutputDeviceBox.SelectedIndex < 0) return;
                _settings.AudioOutputDevice = _outputs[OutputDeviceBox.SelectedIndex].Name;
                ApplyAudioRouting();
            };

            BuildTransmitControls();

            SetupButton.Click += (_, _) => OpenSetup();
            BuildMenu();
            AttSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || _updating) return;
                int db = (int)Math.Round(AttSlider.Value);
                _radio.AttenuatorDb = db;
                _settings.AttenuatorByBand[AttBandKey(_radio.FrequencyMHz)] = db;
                RefreshAttenuator();
            };

            // keyboard: arrows tune by the step, page up/down by 10 steps
            KeyDown += (_, e) =>
            {
                if (VfoEdit.IsVisible || e.Source is TextBox) return;
                int n = e.Key switch { Key.Right or Key.Up => 1, Key.Left or Key.Down => -1, Key.PageUp => 10, Key.PageDown => -10, _ => 0 };
                if (n == 0) return;
                Tune(SnapToStep((long)Math.Round(_radio.FrequencyMHz * 1e6) + (long)n * _settings.TuneStepHz) / 1e6);
                e.Handled = true;
            };
        }

        private void BuildTransmitControls()
        {
            MicSourceBox.ItemsSource = new[] { "Radio microphone", "PC (Mic in)" };
            RegionBox.ItemsSource = _regions.Select(r => r.label).ToList();

            MoxButton.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                string why;
                if (!_radio.SetMox(MoxButton.IsChecked == true, out why)) StatusText.Text = why;
                RefreshTx();
            };
            TuneButton.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                string why;
                if (!_radio.SetTune(TuneButton.IsChecked == true, out why)) StatusText.Text = why;
                RefreshTx();
            };
            DriveSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || _updating) return;
                _radio.DrivePercent = _settings.DrivePercent = (int)Math.Round(DriveSlider.Value);
                RefreshTxCaptions();
            };
            TunePowerSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || _updating) return;
                _radio.TunePercent = _settings.TunePercent = (int)Math.Round(TunePowerSlider.Value);
                RefreshTxCaptions();
            };
            MicGainSlider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty || _updating) return;
                _radio.MicGainDb = _settings.MicGainDb = Math.Round(MicGainSlider.Value);
                RefreshTxCaptions();
                _txPanel?.Refresh();
            };
            PsToggle.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                _radio.PureSignalAutoCal = _settings.PureSignalAutoCal = PsToggle.IsChecked == true;
                RefreshTxMeters();
            };
            TwoToneToggle.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                if (!_radio.SetTwoTone(TwoToneToggle.IsChecked == true, out string why)) StatusText.Text = why;
                RefreshTx();
            };
            _radio.TxAttenuationChanged += db => Dispatcher.UIThread.Post(() => _settings.TxAttenuationByBand[AttBandKey(_radio.FrequencyMHz)] = db);
            VoxToggle.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                _settings.TxProcessing.VoxOn = VoxToggle.IsChecked == true;
                _radio.ApplyTxProcessing();
                _txPanel?.Refresh();
            };
            CompToggle.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                _settings.TxProcessing.CompressorOn = CompToggle.IsChecked == true;
                _radio.ApplyTxProcessing();
                _txPanel?.Refresh();
            };
            EqToggle.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                _settings.TxProcessing.EqOn = EqToggle.IsChecked == true;
                _radio.ApplyTxProcessing();
                _txPanel?.Refresh();
            };
            TxPanelToggle.IsCheckedChanged += (_, _) => { if (!_updating) ShowPanel(TxPanelKey, TxPanelToggle.IsChecked == true); };
            MicSourceBox.SelectionChanged += (_, _) =>
            {
                if (_updating || MicSourceBox.SelectedIndex < 0) return;
                _radio.MicSource = _settings.MicSource = MicSourceBox.SelectedIndex == 1 ? MicSource.Pc : MicSource.Radio;
            };
            TxEnableCheck.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                _radio.TransmitAllowed = _settings.TransmitAllowed = TxEnableCheck.IsChecked == true;
                if (!_radio.TransmitAllowed) { _radio.SetMox(false, out _); }
                RefreshTx();
            };
            RegionBox.SelectionChanged += (_, _) =>
            {
                if (_updating || RegionBox.SelectedIndex < 0) return;
                _radio.SetMox(false, out _);
                _radio.Region = _settings.Region = _regions[RegionBox.SelectedIndex].region;
                RefreshBandLimits();
                RefreshTx();
            };
            TxLowBox.ValueChanged += (_, _) => { if (!_updating) ApplyTxFilter(); };
            TxHighBox.ValueChanged += (_, _) => { if (!_updating) ApplyTxFilter(); };
            TxTimeoutBox.ValueChanged += (_, _) =>
            {
                if (_updating) return;
                _radio.TxTimeoutSeconds = _settings.TxTimeoutSeconds = (int)(TxTimeoutBox.Value ?? 180);
            };
            RadioPttCheck.IsCheckedChanged += (_, _) => { if (!_updating) _radio.RadioPttEnabled = _settings.RadioPtt = RadioPttCheck.IsChecked == true; };
            SwrProtectCheck.IsCheckedChanged += (_, _) => { if (!_updating) _radio.SwrProtection = _settings.SwrProtection = SwrProtectCheck.IsChecked == true; };
            BandLimitBeepCheck.IsCheckedChanged += (_, _) => { if (!_updating) _settings.BandLimitBeep = BandLimitBeepCheck.IsChecked == true; };
            N2adrCheck.IsCheckedChanged += (_, _) => { if (!_updating) _radio.Hl2N2adrFilterBoard = _settings.Hl2N2adrFilterBoard = N2adrCheck.IsChecked == true; };
        }

        #region noise reduction

        private readonly Dictionary<NrType, ToggleButton> _nrButtons = new Dictionary<NrType, ToggleButton>();

        private static readonly (NrType type, string tip)[] _nrTypes =
        {
            (NrType.Off, "No noise reduction"),
            (NrType.NR2, "NR2: spectral noise reduction (AetherSDR's extended EMNR)"),
            (NrType.RN2, "RN2: RNNoise neural noise reduction (not in FM)"),
            (NrType.NR4, "NR4: libspecbleach spectral noise reduction"),
            (NrType.DFNR, "DFNR: DeepFilterNet3 neural noise reduction (not in FM)"),
            (NrType.NNR, "NNR: WDSP's neural noise reduction"),
        };

        private void BuildNoiseReductionButtons()
        {
            foreach (var (type, tip) in _nrTypes)
            {
                var b = new ToggleButton { Content = type.ToString(), Classes = { "panel" } };
                ToolTip.SetTip(b, tip);
                b.Click += (_, _) => SetNoiseReduction(type);
                NrPanel.Children.Add(b);
                _nrButtons[type] = b;
            }
        }

        private void RefreshNoiseReduction()
        {
            foreach (var (type, b) in _nrButtons)
            {
                b.IsChecked = type == _settings.NoiseReductionType;
                b.IsEnabled = type == NrType.Off || !_radio.DspReady || AetherNr.Available(type);
            }
        }

        #endregion

        #region setup and calibration

        private HPSDRModel SelectedModel =>
            _radio.PowerOn ? _radio.Model : ModelBox.SelectedItem is HPSDRModel m ? m : _settings.Model ?? HPSDRModel.HERMES;

        private static string AttBandKey(double mhz) => BandPlan.For(mhz)?.Name ?? "GEN";

        /// <summary>Per-model calibration from the settings (before power on, and when setup opens).</summary>
        private void ApplyModelCalibration(HPSDRModel model)
        {
            _radio.PaGains = _settings.PaGains.TryGetValue(model, out PaCalibration pa) ? pa : null;
            _radio.MeterCalOffsetDb = _settings.MeterCalOffset.TryGetValue(model, out float m) ? m : null;
            _radio.DisplayCalOffsetDb = _settings.DisplayCalOffset.TryGetValue(model, out float d) ? d : null;
        }

        /// <summary>The attenuation remembered for the band at the VFO.</summary>
        private void ApplyBandAttenuator()
        {
            _radio.AttenuatorDb = _settings.AttenuatorByBand.TryGetValue(AttBandKey(_radio.FrequencyMHz), out int db) ? db : 0;
            // TX attenuator for PureSignal, per band (auto-attenuate adjusts and saves it)
            _radio.TxAttenuationDb = _settings.TxAttenuationByBand != null && _settings.TxAttenuationByBand.TryGetValue(AttBandKey(_radio.FrequencyMHz), out int tx) ? tx : 31;
            RefreshAttenuator();
        }

        private void RefreshAttenuator()
        {
            var (lo, hi) = _radio.PowerOn ? _radio.AttenuatorRange : RxFrontEnd.Range(SelectedModel);
            int db = Math.Clamp(_settings.AttenuatorByBand.TryGetValue(AttBandKey(_radio.FrequencyMHz), out int v) ? v : 0, lo, hi);
            _updating = true;
            AttSlider.Minimum = lo;
            AttSlider.Maximum = hi;
            AttSlider.Value = db;
            _updating = false;
            AttCaption.Text = db < 0 ? $"Attenuator {db} dB (LNA gain +{-db} dB)" : $"Attenuator {db} dB";
        }

        #endregion

        private void ApplyTxFilter()
        {
            _settings.TxFilterLow = (int)(TxLowBox.Value ?? 100);
            _settings.TxFilterHigh = (int)(TxHighBox.Value ?? 3000);
            _radio.TxFilter = (_settings.TxFilterLow, _settings.TxFilterHigh);
        }

        /// <summary>The AGC's maximum gain, not a volume: below about 40 dB the receiver goes quiet.</summary>
        private static string AgcTopText(double db) =>
            db < 40 ? $"AGC gain {db:0} dB - low: the receiver will be quiet (usually 80-90)" : $"AGC gain {db:0} dB";

        private static string StepLabel(int hz) => hz >= 1000 ? $"{hz / 1000.0:0.###} kHz" : $"{hz} Hz";

        private void ApplySettingsToRadio()
        {
            _radio.SampleRate = _settings.SampleRate;
            _radio.Mode = _settings.Mode;
            var presets = FilterPresets.For(_settings.Mode);
            _radio.Filter = presets[Math.Clamp(_settings.FilterIndex, 0, presets.Count - 1)];
            _radio.FrequencyMHz = _settings.FrequencyMHz;
            _radio.Agc = _settings.Agc;
            _radio.AgcTop = _settings.AgcTop;
            _radio.Volume = _settings.Volume;
            if (_settings.NoiseReduction && _settings.NoiseReductionType == NrType.Off)
                _settings.NoiseReductionType = NrType.NR2;      // the old NR2 toggle
            _settings.NoiseReduction = false;
            foreach (var (p, v) in _settings.NrParams) _radio.SetNoiseReductionParam(p, v);
            _radio.NoiseReductionType = _settings.NoiseReductionType;
            _radio.AutoNotch = _settings.AutoNotch;
            _radio.TransmitAllowed = _settings.TransmitAllowed;
            _radio.Region = _settings.Region;
            RefreshBandLimits();
            _radio.TxTimeoutSeconds = _settings.TxTimeoutSeconds;
            _radio.RadioPttEnabled = _settings.RadioPtt;
            _radio.SwrProtection = _settings.SwrProtection;
            _radio.Hl2N2adrFilterBoard = _settings.Hl2N2adrFilterBoard;
            _radio.DrivePercent = _settings.DrivePercent;
            _radio.TunePercent = _settings.TunePercent;
            _radio.MicGainDb = _settings.MicGainDb;
            _radio.MicSource = _settings.MicSource;
            _radio.TxFilter = (_settings.TxFilterLow, _settings.TxFilterHigh);
            _radio.TxProcessing = _settings.TxProcessing ??= new TxProcessing();
            _radio.PureSignal = _settings.PureSignal ??= new PureSignalSettings();
            _settings.TxAttenuationByBand ??= new Dictionary<string, int>();
            _radio.PureSignalAutoCal = _settings.PureSignalAutoCal;
            if (_settings.LpfEdges != null) BandFilters.LpfEdges = _settings.LpfEdges;
            if (_settings.HpfEdges != null) BandFilters.HpfEdges = _settings.HpfEdges;
            if (_settings.Bpf1Edges != null) BandFilters.Bpf1Edges = _settings.Bpf1Edges;
            _radio.Antennas = _settings.Antennas ??= AntennaSettings.Defaults();
            if (_settings.Model is HPSDRModel sm) ApplyModelCalibration(sm);
            ApplyBandAttenuator();
            Panafall.MaxDbm = _settings.SpectrumMaxDbm;
            Panafall.MinDbm = _settings.SpectrumMinDbm;
            Panafall.PanFraction = _settings.PanFraction;
        }

        #endregion

        #region start-up and shutdown

        private async Task StartDspAsync()
        {
            try
            {
                if (!_radio.WisdomFileExists)
                    StatusText.Text = "First start: optimising FFTs for this computer (several minutes)...";
                var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                poll.Tick += (_, _) => { string s = RadioController.WisdomStatus(); if (!string.IsNullOrEmpty(s)) StatusText.Text = s; };
                if (!_radio.WisdomFileExists) poll.Start();
                await Task.Run(() => _radio.InitializeDsp());
                poll.Stop();
                RefreshNoiseReduction();            // which filters the library offers
                if (!AetherNr.Loaded)
                {
                    string why = "The noise reduction library did not load: " + AetherNr.Error;
                    ToolTip.SetTip(NoiseCaption, why);
                    ToolTip.SetTip(NrPanel, why);
                    StatusText.Text = why;
                }
                _radio.SetSpectrumZoom(_settings.SpectrumZoom);
                LoadAudioDevices();
                _timer.Start();
                await DiscoverAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = "DSP start-up failed: " + ex.Message;
                PowerButton.IsEnabled = false;
            }
        }

        private void Shutdown()
        {
            StopCat();
            _timer.Stop();
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            RememberPanelLayout();
            _closingMain = true;
            SaveSettings();
            _radio.Dispose();
        }

        private void SaveSettings()
        {
            _settings.FrequencyMHz = _radio.FrequencyMHz;
            try { _settings.Save(); }
            catch (Exception ex) { StatusText.Text = "Could not save settings: " + ex.Message; }
        }

        #endregion

        #region radio selection and power

        private async Task DiscoverAsync()
        {
            DiscoverButton.IsEnabled = false;
            StatusText.Text = "Searching for radios...";
            try
            {
                bool loopback = Environment.GetEnvironmentVariable("THETIS_DISCOVER_LOOPBACK") == "1";
                _radios = await Task.Run(() => RadioController.Discover(ScanPerformanceProfile.Safe, loopback));
            }
            catch (Exception ex)
            {
                _radios = new List<DiscoveredRadio>();
                StatusText.Text = "Discovery failed: " + ex.Message;
            }
            _updating = true;
            RadioBox.ItemsSource = _radios.Select(r => r.ToString()).ToList();
            int idx = _radios.FindIndex(r => r.Info.MacAddress == _settings.LastRadioMac);
            if (idx < 0 && _radios.Count > 0) idx = 0;
            RadioBox.SelectedIndex = idx;
            if (idx >= 0) ModelBox.SelectedItem = _settings.Model ?? _radios[idx].SuggestedModel;
            _updating = false;
            StatusText.Text = _radios.Count == 0 ? "No radio found. Check the network connection and press Discover." : $"Found {_radios.Count} radio(s)";
            DiscoverButton.IsEnabled = true;
        }

        private async Task TogglePowerAsync()
        {
            if (PowerButton.IsChecked == true)
            {
                if (!_radio.DspReady || RadioBox.SelectedIndex < 0 || RadioBox.SelectedIndex >= _radios.Count || ModelBox.SelectedItem is not HPSDRModel model)
                {
                    StatusText.Text = _radio.DspReady ? "Select a radio and model first" : "DSP is still starting";
                    SetPower(false);
                    return;
                }
                var target = _radios[RadioBox.SelectedIndex];
                ApplyModelCalibration(model);
                PowerButton.IsEnabled = false;
                string error = null;
                bool ok = await Task.Run(() => _radio.Start(target, model, out error));
                PowerButton.IsEnabled = true;
                if (!ok)
                {
                    StatusText.Text = error;
                    SetPower(false);
                    return;
                }
                _settings.LastRadioMac = target.Info.MacAddress;
                _settings.Model = model;
                ApplyAudioRouting();
                ApplyBandAttenuator();
            }
            else
            {
                _radio.Stop();
                Panafall.Clear();
            }
            SaveSettings();
            RefreshAll();
        }

        private void SetPower(bool on)
        {
            _updating = true;
            PowerButton.IsChecked = on;
            _updating = false;
            RefreshAll();
        }

        #endregion

        #region tuning, band, mode, filter

        private long SnapToStep(long hz)
        {
            long step = Math.Max(1, _settings.TuneStepHz);
            return (long)Math.Round(hz / (double)step) * step;
        }

        private void Tune(double mhz)
        {
            mhz = Math.Clamp(mhz, 0.0, 61.44);
            CheckBandLimitCrossing(_radio.FrequencyMHz, mhz);
            bool newBand = AttBandKey(mhz) != AttBandKey(_radio.FrequencyMHz);
            _radio.FrequencyMHz = mhz;
            _settings.FrequencyMHz = mhz;
            if (newBand) ApplyBandAttenuator();
            var band = BandPlan.For(mhz);
            if (band != null) _settings.Bands[band.Name] = new BandMemory { FrequencyMHz = mhz, Mode = _radio.Mode };
            RefreshVfo();
            RefreshBands();
        }

        private void SelectBand(BandInfo band)
        {
            if (_settings.Bands.TryGetValue(band.Name, out var mem))
            {
                SetMode(mem.Mode);
                Tune(mem.FrequencyMHz);
            }
            else
            {
                SetMode(band.DefaultMode);
                Tune(band.DefaultMHz);
            }
        }

        private void SetMode(DSPMode mode)
        {
            _radio.Mode = mode;
            _settings.Mode = mode;
            _settings.FilterIndex = FilterPresets.DefaultIndex;
            var band = BandPlan.For(_radio.FrequencyMHz);
            if (band != null) _settings.Bands[band.Name] = new BandMemory { FrequencyMHz = _radio.FrequencyMHz, Mode = mode };
            RefreshModes();
            RefreshFilters();
        }

        private void SetFilter(int index)
        {
            var presets = FilterPresets.For(_radio.Mode);
            _radio.Filter = presets[index];
            _settings.FilterIndex = index;
            RefreshFilters();
        }

        private void BeginVfoEdit()
        {
            VfoEdit.Text = _radio.FrequencyMHz.ToString("0.000000", CultureInfo.InvariantCulture);
            VfoEdit.IsVisible = true;
            Vfo.IsVisible = false;
            VfoEdit.Focus();
            VfoEdit.SelectAll();
        }

        private void EndVfoEdit(bool apply)
        {
            if (!VfoEdit.IsVisible) return;
            if (apply)
            {
                string t = (VfoEdit.Text ?? "").Trim().Replace(',', '.');
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    if (v > 1000) v /= 1000.0;          // typed in kHz
                    Tune(v);
                }
            }
            VfoEdit.IsVisible = false;
            Vfo.IsVisible = true;
        }

        #endregion

        #region audio routing

        private void LoadAudioDevices()
        {
            try { _hostApis = AudioDevices.HostApis(); }
            catch (Exception ex) { _hostApis = Array.Empty<AudioHostApi>(); StatusText.Text = ex.Message; }
            _updating = true;
            HostApiBox.ItemsSource = _hostApis.Select(h => h.Name).ToList();
            int idx = _hostApis.ToList().FindIndex(h => h.Name == _settings.AudioHostApi);
            if (idx < 0) idx = _hostApis.ToList().FindIndex(h => h.Name.Contains("Pulse", StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = _hostApis.ToList().FindIndex(h => h.Name.Contains("ALSA", StringComparison.OrdinalIgnoreCase));
            if (idx < 0 && _hostApis.Count > 0) idx = 0;
            HostApiBox.SelectedIndex = idx;
            if (idx >= 0) _settings.AudioHostApi = _hostApis[idx].Name;
            _updating = false;
            LoadOutputDevices();
        }

        private void LoadOutputDevices()
        {
            _updating = true;
            int h = HostApiBox.SelectedIndex;
            _outputs = h >= 0 ? AudioDevices.Devices(_hostApis[h].Index).Where(d => d.MaxOutputChannels >= 2).ToList() : new List<AudioDevice>();
            OutputDeviceBox.ItemsSource = _outputs.Select(d => d.Name).ToList();
            int idx = _outputs.ToList().FindIndex(d => d.Name == _settings.AudioOutputDevice);
            if (idx < 0 && h >= 0) idx = _outputs.ToList().FindIndex(d => d.Index == _hostApis[h].DefaultOutputDevice);
            if (idx < 0 && _outputs.Count > 0) idx = 0;
            OutputDeviceBox.SelectedIndex = idx;
            if (idx >= 0) _settings.AudioOutputDevice = _outputs[idx].Name;

            _inputs = h >= 0 ? AudioDevices.Devices(_hostApis[h].Index).Where(d => d.MaxInputChannels >= 1).ToList() : new List<AudioDevice>();
            InputDeviceBox.ItemsSource = _inputs.Select(d => d.Name).ToList();
            idx = _inputs.ToList().FindIndex(d => d.Name == _settings.AudioInputDevice);
            if (idx < 0 && h >= 0) idx = _inputs.ToList().FindIndex(d => d.Index == _hostApis[h].DefaultInputDevice);
            if (idx < 0 && _inputs.Count > 0) idx = 0;
            InputDeviceBox.SelectedIndex = idx;
            if (idx >= 0) _settings.AudioInputDevice = _inputs[idx].Name;
            _updating = false;
        }

        private void ApplyAudioRouting()
        {
            int h = HostApiBox.SelectedIndex, o = OutputDeviceBox.SelectedIndex, i = InputDeviceBox.SelectedIndex;
            bool enable = _settings.PcAudio && h >= 0 && o >= 0;
            _radio.ConfigureVac(enable, enable ? _hostApis[h].Index : -1, enable ? _outputs[o].HostApiDeviceIndex : -1,
                                enable && i >= 0 ? _inputs[i].HostApiDeviceIndex : -1);
        }

        #endregion

        #region periodic update

        private void OnTick()
        {
            bool on = _radio.PowerOn;
            Panafall.Active = on;
            if (!on) return;

            _radio.SetSpectrumWidth(Panafall.DesiredPixels);
            int n = _radio.SpectrumPixels;
            if (_pan.Length != n) { _pan = new float[n]; _wf = new float[n]; }
            var (lo, hi) = _radio.SpectrumSpan;
            if (lo != Panafall.SpanLowHz || hi != Panafall.SpanHighHz)
                Panafall.ClearWaterfall();          // old lines were drawn at a different scale
            Panafall.SpanLowHz = lo;
            Panafall.SpanHighHz = hi;
            Panafall.CenterHz = (long)Math.Round(_radio.RxTunedMHz * 1e6);     // CW: the VFO moved by the pitch
            Panafall.VfoHz = (long)Math.Round(_radio.FrequencyMHz * 1e6);
            Panafall.FilterLowHz = _radio.Filter.Low;
            Panafall.FilterHighHz = _radio.Filter.High;
            if (_radio.GetSpectrum(_pan)) Panafall.PushPanadapter(_pan);
            if (_radio.GetSpectrum(_wf, waterfall: true)) Panafall.PushWaterfall(_wf);

            if (!_radio.Mox) Meter.Update(_radio.SignalDbm());   // the receiver is off while transmitting
            if (++_meterDivider % 6 == 0)
            {
                MeterText.Text = $"{RadioController.SUnits((float)Meter.Dbm),-7} {Meter.Dbm,7:0.0} dBm";
                SyncText.Text = !_radio.HaveSync ? "no data from radio" : RateWarning();
                NoiseCaption.Text = !AetherNr.Loaded && _settings.NoiseReductionType != NrType.NNR
                    ? "Noise reduction: only NNR available (hover for why)"
                    : _radio.PowerOn && _settings.NoiseReductionType != NrType.Off && !_radio.NoiseReductionActive
                    ? $"Noise reduction ({_settings.NoiseReductionType} cannot run {(_radio.Mode == DSPMode.FM ? "in FM" : "here")})"
                    : "Noise reduction";
                RefreshTxMeters();
            }
            if (_meterDivider % 3 == 0 && IsPanelShown(TxPanelKey)) _txPanel.UpdateMeters();
        }

        #endregion

        #region refresh controls from state

        private void RefreshAll()
        {
            _updating = true;
            PowerButton.IsChecked = _radio.PowerOn;
            ModelBox.IsEnabled = RadioBox.IsEnabled = DiscoverButton.IsEnabled = !_radio.PowerOn;
            VolumeSlider.Value = _settings.Volume * 100.0;
            AgcTopSlider.Value = _settings.AgcTop;
            AgcTopCaption.Text = AgcTopText(_settings.AgcTop);
            ZoomSlider.Value = _settings.SpectrumZoom * 100.0;
            RefreshNoiseReduction();
            AnfToggle.IsChecked = _settings.AutoNotch;
            StepBox.SelectedIndex = Math.Max(0, Array.IndexOf(_steps, _settings.TuneStepHz));
            SampleRateBox.SelectedIndex = Math.Max(0, Array.IndexOf(RadioController.SampleRates, _settings.SampleRate));
            PcAudioCheck.IsChecked = _settings.PcAudio;
            _updating = false;
            RefreshVfo();
            RefreshBands();
            RefreshModes();
            RefreshFilters();
            RefreshAgc();
            RefreshTx();
        }

        private void RefreshTx()
        {
            _updating = true;
            MoxButton.IsChecked = _radio.Mox && !_radio.Tuning && !_radio.TwoToneOn;
            TuneButton.IsChecked = _radio.Tuning;
            MoxButton.IsEnabled = TuneButton.IsEnabled = _radio.PowerOn;
            DriveSlider.Value = _settings.DrivePercent;
            TunePowerSlider.Value = _settings.TunePercent;
            MicGainSlider.Value = _settings.MicGainDb;
            MicSourceBox.SelectedIndex = _settings.MicSource == MicSource.Pc ? 1 : 0;
            VoxToggle.IsChecked = _settings.TxProcessing.VoxOn;
            CompToggle.IsChecked = _settings.TxProcessing.CompressorOn;
            EqToggle.IsChecked = _settings.TxProcessing.EqOn;
            TxPanelToggle.IsChecked = IsPanelShown(TxPanelKey);
            PsToggle.IsChecked = _settings.PureSignalAutoCal;
            TwoToneToggle.IsChecked = _radio.TwoToneOn;
            TwoToneToggle.IsEnabled = _radio.PowerOn;
            TxEnableCheck.IsChecked = _settings.TransmitAllowed;
            RegionBox.SelectedIndex = Math.Max(0, Array.FindIndex(_regions, r => r.region == _settings.Region));
            TxLowBox.Value = _settings.TxFilterLow;
            TxHighBox.Value = _settings.TxFilterHigh;
            TxTimeoutBox.Value = _settings.TxTimeoutSeconds;
            RadioPttCheck.IsChecked = _settings.RadioPtt;
            SwrProtectCheck.IsChecked = _settings.SwrProtection;
            BandLimitBeepCheck.IsChecked = _settings.BandLimitBeep;
            N2adrCheck.IsChecked = _settings.Hl2N2adrFilterBoard;
            _updating = false;
            RefreshTxCaptions();
            RefreshTxMeters();
        }

        private void RefreshTxCaptions()
        {
            DriveCaption.Text = $"Drive {_settings.DrivePercent} %";
            TunePowerCaption.Text = $"Tune power {_settings.TunePercent} %";
            MicGainCaption.Text = $"Mic gain {_settings.MicGainDb:0} dB";
        }

        private int _rateDivider;
        private string _rateWarning = "";

        /// <summary>Every two seconds: does the radio send the sample rate that was set?</summary>
        private string RateWarning()
        {
            if (++_rateDivider % 12 != 0) return _rateWarning;
            double? rate = _radio.MeasuredSampleRate();
            _rateWarning = rate is double r && Math.Abs(r - _radio.SampleRate) > 0.1 * _radio.SampleRate
                ? $"Radio sends {r / 1000:0} kHz, not {_radio.SampleRate / 1000} kHz (Help > Receive diagnostics)"
                : "";
            return _rateWarning;
        }

        /// <summary>PureSignal's state under the PS-A button (PSForm's info labels, in words).</summary>
        private void RefreshPureSignalStatus()
        {
            var ps = _radio.PureSignalStatus;
            string text = null;
            IBrush colour = TxWarning.Foreground;
            if (_settings.PureSignalAutoCal && _radio.PowerOn && _radio.Model == HPSDRModel.HERMESLITE && _radio.SampleRate != 192000)
                text = "PureSignal on the Hermes-Lite 2 needs the 192 kHz sample rate";
            else if (_settings.PureSignalAutoCal || ps.CorrectionsApplied)
            {
                string what = ps.Correcting ? "correcting" : ps.CorrectionsApplied ? "correction kept" : _radio.Mox ? "calibrating" : "waits for transmit";
                text = $"PureSignal {what}" + (ps.CalibrationCount > 0 ? $" - {ps.LevelText} ({ps.FeedbackLevel}), TX att {ps.TxAttenuationDb} dB" : "");
                colour = new SolidColorBrush(Color.Parse(
                    ps.CalibrationCount == 0 ? "#9AA4AE" :
                    ps.FeedbackLevel > 181 ? "#42A5F5" : ps.FeedbackLevel > 128 ? "#66BB6A" : ps.FeedbackLevel > 90 ? "#FFEE58" : "#EF5350"));
            }
            PsStatusText.IsVisible = text != null;
            if (text != null) { PsStatusText.Text = text; PsStatusText.Foreground = colour; }
        }

        private void RefreshTxMeters()
        {
            // WDSP's transmit meters only run while the transmitter does, as in the console
            TxMeterText.Text = _radio.Mox
                ? $"{_radio.ForwardWatts,5:0.0} W   SWR {_radio.Swr:0.0}\nmic {Math.Clamp(_radio.MicPeakDb(), -99f, 99f),3:0} dB  ALC {Math.Clamp(_radio.AlcGainDb(), -99f, 99f),3:0} dB"
                : _settings.TxProcessing.VoxOn && _radio.PowerOn
                    ? $"VOX {(_radio.VoxActive ? "heard" : "listening")}  mic {Math.Clamp(_radio.VoxPeakDb(), -99.0, 99.0),3:0} / {_settings.TxProcessing.VoxThresholdDb:0} dB"
                    : "";
            TxMeterText.IsVisible = TxMeterText.Text.Length > 0;
            RefreshPureSignalStatus();

            string warn = null;
            if (!_settings.TransmitAllowed) warn = "Transmit is off. Enable it under Transmit settings.";
            else if (_settings.Region == TxRegion.None) warn = "Choose your region under Transmit settings to transmit.";
            else if (_radio.HighSwr) warn = $"High SWR: drive reduced ({_radio.Swr:0.0}:1).";
            else if (_radio.PowerOn && _radio.PcMicUnavailable) warn = "PC microphone selected but PC audio is off: the radio's microphone is used.";
            else if (!_radio.Mox && _lastTxMessage != null && _lastTxMessage.StartsWith("Transmit stopped")) warn = _lastTxMessage;
            TxWarning.Text = warn ?? "";
            TxWarning.IsVisible = warn != null;
            if (_radio.Mox) _lastTxMessage = null;
        }

        private void RefreshVfo()
        {
            Vfo.FrequencyHz = (long)Math.Round(_radio.FrequencyMHz * 1e6);
            Vfo.Active = _radio.PowerOn;
            var band = BandPlan.For(_radio.FrequencyMHz);
            VfoInfo.Text = $"{(band != null ? band.Name + (band.Name == "WWV" ? "" : " m") : "General coverage")}   {_radio.Mode}   {_radio.Filter.Name}";
            Title = _radio.PowerOn && _radio.ConnectedRadio != null
                ? $"Thetis - {_radio.ConnectedRadio.DeviceType} {_radio.ConnectedRadio.IpAddress} - {_radio.FrequencyMHz:0.000000} MHz"
                : "Thetis";
        }

        private void RefreshBands()
        {
            var band = BandPlan.For(_radio.FrequencyMHz);
            foreach (var (name, b) in _bandButtons) b.IsChecked = band != null && band.Name == name;
        }

        private void RefreshModes()
        {
            foreach (var (mode, b) in _modeButtons) b.IsChecked = mode == _radio.Mode;
            RefreshVfo();
        }

        private void RefreshAgc()
        {
            foreach (var (agc, b) in _agcButtons) b.IsChecked = agc == _radio.Agc;
        }

        private void RefreshFilters()
        {
            var presets = FilterPresets.For(_radio.Mode);
            if (_filterButtons.Count != presets.Count || !_filterButtons.Select(b => (string)b.Content).SequenceEqual(presets.Select(p => p.Name)))
            {
                FilterPanel.Children.Clear();
                _filterButtons.Clear();
                for (int i = 0; i < presets.Count; i++)
                {
                    int idx = i;
                    var b = new ToggleButton { Content = presets[i].Name, Classes = { "panel" } };
                    b.Click += (_, _) => SetFilter(idx);
                    FilterPanel.Children.Add(b);
                    _filterButtons.Add(b);
                }
            }
            for (int i = 0; i < presets.Count; i++)
                _filterButtons[i].IsChecked = presets[i] == _radio.Filter;
            RefreshVfo();
        }

        #endregion
    }
}
