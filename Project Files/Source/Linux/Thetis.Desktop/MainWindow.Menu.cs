/*  MainWindow.Menu.cs

This file is part of a program that implements a Software-Defined Radio.

The main window's menu bar: every option of the window and the setup pages
in one place.  Each menu is rebuilt when it opens, so its checks and
enabled states match the radio.  The items drive the same controls and
methods as the panels, so both always agree.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Thetis.Desktop.Controls;
using Thetis.Radio;

namespace Thetis.Desktop
{
    public partial class MainWindow
    {
        private SetupWindow _setupWindow;

        private const string ProjectUrl = "https://github.com/fecrec136/OpenHPSDR-Thetis-CH";

        private void BuildMenu()
        {
            MainMenu.Items.Add(TopMenu("_File", FileMenu));
            MainMenu.Items.Add(TopMenu("_Radio", RadioMenu));
            MainMenu.Items.Add(TopMenu("R_eceive", ReceiveMenu));
            MainMenu.Items.Add(TopMenu("_Transmit", TransmitMenu));
            MainMenu.Items.Add(TopMenu("_Audio", AudioMenu));
            MainMenu.Items.Add(TopMenu("_View", ViewMenu));
            MainMenu.Items.Add(TopMenu("_Setup", SetupMenu));
            MainMenu.Items.Add(TopMenu("_Help", HelpMenu));

            // shortcuts (the menus show them; the window handles them, so they work with the menus closed)
            Bind(Key.S, KeyModifiers.Control, SaveSettingsWithStatus);
            Bind(Key.Q, KeyModifiers.Control, Close);
            Bind(Key.D, KeyModifiers.Control, Discover);
            Bind(Key.F, KeyModifiers.Control, BeginVfoEdit);
            Bind(Key.OemComma, KeyModifiers.Control, () => OpenSetup());
            Bind(Key.F1, KeyModifiers.None, ShowShortcuts);
            Bind(Key.A, KeyModifiers.Control | KeyModifiers.Shift, () => ShowPanel(TxPanelKey, !IsPanelShown(TxPanelKey)));
        }

        #region menus

        private List<Control> FileMenu() => new List<Control>
        {
            Item("_Save settings", SaveSettingsWithStatus, "Ctrl+S"),
            Item("Open the settings _folder", () => OpenFolder(Settings.ConfigDirectory)),
            Item("Open the _data folder (FFT wisdom)", () => OpenFolder(Settings.DataDirectory)),
            new Separator(),
            Item("E_xit", Close, "Ctrl+Q"),
        };

        private List<Control> RadioMenu()
        {
            bool on = _radio.PowerOn;
            var radios = _radios.Select((r, i) => (Control)Radio(Escape(r.ToString()), RadioBox.SelectedIndex == i,
                                                                  () => RadioBox.SelectedIndex = i, !on)).ToList();
            if (radios.Count == 0) radios.Add(Item("No radio found - use Discover", null, enabled: false));
            var models = ((IEnumerable<HPSDRModel>)ModelBox.ItemsSource)
                .Select(m => (Control)Radio(Escape(m.ToString()), Equals(ModelBox.SelectedItem, m), () => ModelBox.SelectedItem = m, !on)).ToList();
            var rates = RadioController.SampleRates
                .Select((r, i) => (Control)Radio($"{r / 1000} kHz", _settings.SampleRate == r, () => SampleRateBox.SelectedIndex = i)).ToList();
            return new List<Control>
            {
                Check("_Power", on, () => PowerButton.IsChecked = !on, PowerButton.IsEnabled),
                Item("_Discover radios", Discover, "Ctrl+D", DiscoverButton.IsEnabled),
                new Separator(),
                Sub("_Radio", radios),
                Sub("_Model", models),
                Sub("_Sample rate", rates),
                new Separator(),
                Item("Receive _calibration...", () => OpenSetup("Receive")),
                Item("_Antennas...", () => OpenSetup("Antennas")),
            };
        }

        private List<Control> ReceiveMenu()
        {
            var band = BandPlan.For(_radio.FrequencyMHz);
            var bands = BandPlan.Bands.Select(b => (Control)Radio(b.Name == "WWV" ? "WWV" : b.Name + " m", band?.Name == b.Name, () => SelectBand(b))).ToList();
            var modes = _modes.Select(m => (Control)Radio(m.ToString(), _radio.Mode == m, () => SetMode(m))).ToList();
            var presets = FilterPresets.For(_radio.Mode);
            var filters = presets.Select((f, i) => (Control)Radio(f.Name, presets[i] == _radio.Filter, () => SetFilter(i))).ToList();
            var agcs = _agcModes.Select(a => (Control)Radio(AgcLabel(a), _radio.Agc == a, () => SetAgc(a))).ToList();
            var nr = _nrTypes.Select(n => (Control)Radio(n.type.ToString(), _settings.NoiseReductionType == n.type, () => SetNoiseReduction(n.type),
                                                         n.type == NrType.Off || !_radio.DspReady || AetherNr.Available(n.type))).ToList();
            nr.Add(new Separator());
            nr.Add(Item("Noise reduction _settings...", () => OpenSetup("Noise reduction")));
            var steps = _steps.Select((s, i) => (Control)Radio(StepLabel(s), _settings.TuneStepHz == s, () => StepBox.SelectedIndex = i)).ToList();

            int lo = (int)AttSlider.Minimum, hi = (int)AttSlider.Maximum, att = (int)Math.Round(AttSlider.Value);
            var levels = new SortedSet<int> { lo, hi, 0 };
            for (int v = (lo / 5) * 5; v <= hi; v += 5) if (v >= lo) levels.Add(v);
            if (lo < 0) levels.Remove(0);       // HL2: negative is LNA gain, 0 is just another point
            var atts = levels.Where(v => v >= lo && v <= hi)
                             .Select(v => (Control)Radio($"{v} dB", att == v, () => AttSlider.Value = v)).ToList();

            return new List<Control>
            {
                Item("Enter _frequency...", BeginVfoEdit, "Ctrl+F"),
                Sub("_Band", bands),
                Sub("_Mode", modes),
                Sub("F_ilter", filters),
                Sub("_Tuning step", steps),
                new Separator(),
                Sub("_AGC", agcs),
                Sub("Atte_nuator (this band)", atts),
                Sub("_Noise reduction", nr),
                Check("Automatic _notch (ANF)", _settings.AutoNotch, () => AnfToggle.IsChecked = !_settings.AutoNotch),
            };
        }

        private List<Control> TransmitMenu()
        {
            var tx = _settings.TxProcessing;
            bool on = _radio.PowerOn;
            var regions = _regions.Select((r, i) => (Control)Radio(r.label, _settings.Region == r.region, () => RegionBox.SelectedIndex = i)).ToList();
            var mics = new List<Control>
            {
                Radio("_Radio microphone", _settings.MicSource == MicSource.Radio, () => MicSourceBox.SelectedIndex = 0),
                Radio("_PC (Mic in)", _settings.MicSource == MicSource.Pc, () => MicSourceBox.SelectedIndex = 1),
            };
            return new List<Control>
            {
                Check("_MOX", _radio.Mox && !_radio.Tuning, () => MoxButton.IsChecked = MoxButton.IsChecked != true, on),
                Check("_TUNE", _radio.Tuning, () => TuneButton.IsChecked = TuneButton.IsChecked != true, on),
                new Separator(),
                Check("_VOX", tx.VoxOn, () => VoxToggle.IsChecked = !tx.VoxOn),
                Check("_Compressor (COMP)", tx.CompressorOn, () => CompToggle.IsChecked = !tx.CompressorOn),
                Check("_Equaliser (EQ)", tx.EqOn, () => EqToggle.IsChecked = !tx.EqOn),
                Check("_Leveler", tx.LevelerOn, () => SetTx(() => tx.LevelerOn = !tx.LevelerOn)),
                Check("CESS_B overshoot control", tx.CessbOn, () => SetTx(() => tx.CessbOn = !tx.CessbOn)),
                Check("CF_C (frequency compressor)", tx.CfcOn, () => SetTx(() => tx.CfcOn = !tx.CfcOn)),
                Check("_Phase rotator", tx.PhaseRotatorOn, () => SetTx(() => tx.PhaseRotatorOn = !tx.PhaseRotatorOn)),
                Check("Downward e_xpander", tx.ExpanderOn, () => SetTx(() => tx.ExpanderOn = !tx.ExpanderOn)),
                Item("Transmit _audio settings...", () => OpenSetup("Transmit audio")),
                Sub("P_ureSignal", new List<Control>
                {
                    Check("PS-A (calibrate while _transmitting)", _settings.PureSignalAutoCal, () => PsToggle.IsChecked = !_settings.PureSignalAutoCal),
                    Check("_Two-tone test signal", _radio.TwoToneOn, () => TwoToneToggle.IsChecked = !_radio.TwoToneOn, on),
                    Item("_Calibrate once", () => _radio.PureSignalSingleCal(), enabled: on),
                    Item("_Reset (discard the correction)", () => { _radio.PureSignalReset(); _settings.PureSignalAutoCal = false; RefreshTx(); }, enabled: on),
                    new Separator(),
                    Item("PureSignal _settings...", () => OpenSetup("PureSignal")),
                }),
                new Separator(),
                Sub("M_icrophone", mics),
                new Separator(),
                Check("Allow _transmitting", _settings.TransmitAllowed, () => TxEnableCheck.IsChecked = !_settings.TransmitAllowed),
                Sub("_Region", regions),
                Check("Key from the radio's P_TT input", _settings.RadioPtt, () => RadioPttCheck.IsChecked = !_settings.RadioPtt),
                Check("_SWR protection", _settings.SwrProtection, () => SwrProtectCheck.IsChecked = !_settings.SwrProtection),
                Check("HL2: N2A_DR filter board", _settings.Hl2N2adrFilterBoard, () => N2adrCheck.IsChecked = !_settings.Hl2N2adrFilterBoard),
                Item("Transmit filter and _timeout...", ShowTxSettings),
                Item("PA _gain...", () => OpenSetup("PA gain")),
            };
        }

        private List<Control> AudioMenu()
        {
            var apis = _hostApis.Select((h, i) => (Control)Radio(Escape(h.Name), HostApiBox.SelectedIndex == i, () => HostApiBox.SelectedIndex = i)).ToList();
            var outs = _outputs.Select((d, i) => (Control)Radio(Escape(d.Name), OutputDeviceBox.SelectedIndex == i, () => OutputDeviceBox.SelectedIndex = i)).ToList();
            var ins = _inputs.Select((d, i) => (Control)Radio(Escape(d.Name), InputDeviceBox.SelectedIndex == i, () => InputDeviceBox.SelectedIndex = i)).ToList();
            if (apis.Count == 0) apis.Add(Item("No sound system found", null, enabled: false));
            if (outs.Count == 0) outs.Add(Item("No output device", null, enabled: false));
            if (ins.Count == 0) ins.Add(Item("No input device", null, enabled: false));
            return new List<Control>
            {
                Check("_PC audio (receive audio to this computer)", _settings.PcAudio, () => PcAudioCheck.IsChecked = !_settings.PcAudio),
                Sub("_Sound system", apis),
                Sub("_Output device", outs),
                Sub("_Input device (PC microphone)", ins),
            };
        }

        private List<Control> ViewMenu() => new List<Control>
        {
            Item("Zoom _in", () => ZoomSlider.Value = Math.Min(100, ZoomSlider.Value + 20), enabled: ZoomSlider.Value < 100),
            Item("Zoom _out", () => ZoomSlider.Value = Math.Max(0, ZoomSlider.Value - 20), enabled: ZoomSlider.Value > 0),
            Item("_Full span", () => ZoomSlider.Value = 0, enabled: ZoomSlider.Value > 0),
            new Separator(),
            Item("_Reset the spectrum scale", ResetSpectrumView),
            new Separator(),
            Check("_Transmit settings panel", TxSettingsExpander.IsExpanded, () => TxSettingsExpander.IsExpanded = !TxSettingsExpander.IsExpanded),
            Sub("_Panels", PanelMenus()),
        };

        private List<Control> SetupMenu()
        {
            var items = new List<Control> { Item("_Setup...", () => OpenSetup(), "Ctrl+,"), new Separator() };
            foreach (string tab in SetupWindow.TabNames)
                items.Add(Item(tab + "...", () => OpenSetup(tab)));
            return items;
        }

        private List<Control> HelpMenu() => new List<Control>
        {
            Item("_Keyboard and mouse", ShowShortcuts, "F1"),
            Item("_Receive diagnostics...", ShowReceiveDiagnostics, enabled: _radio.PowerOn),
            Item("_Project page", () => _ = Launcher.LaunchUriAsync(new Uri(ProjectUrl))),
            new Separator(),
            Item("_About Thetis for Linux", ShowAbout),
        };

        #endregion

        #region actions

        private void SetAgc(AGCMode agc)
        {
            _radio.Agc = agc;
            _settings.Agc = agc;
            RefreshAgc();
        }

        private void SetNoiseReduction(NrType type)
        {
            _radio.NoiseReductionType = _settings.NoiseReductionType = type;
            RefreshNoiseReduction();
        }

        private static string AgcLabel(AGCMode agc) =>
            agc == AGCMode.FIXD ? "Fixed" : agc.ToString()[0] + agc.ToString().Substring(1).ToLowerInvariant();

        private void SetTx(Action change)
        {
            change();
            _radio.ApplyTxProcessing();
            RefreshTx();
        }

        private async void Discover()
        {
            if (DiscoverButton.IsEnabled) await DiscoverAsync();
        }

        private void SaveSettingsWithStatus()
        {
            SaveSettings();
            StatusText.Text = "Settings saved";
        }

        private void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _ = Launcher.LaunchUriAsync(new Uri(Path.GetFullPath(dir)));      // file:// URI: the file manager
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void OpenSetup(string tab = null)
        {
            if (_setupWindow == null)
            {
                HPSDRModel model = SelectedModel;
                ApplyModelCalibration(model);
                _setupWindow = new SetupWindow(_radio, _settings, model, SaveSettings, _cat, ApplyCatSettings);
                _setupWindow.Closed += (_, _) =>
                {
                    _setupWindow = null;
                    RefreshTx();                    // VOX / COMP / EQ may have changed there
                };
                _setupWindow.TxChanged += () => { RefreshTx(); _txPanel?.Refresh(); };
                if (tab != null) _setupWindow.ShowTab(tab);
                _setupWindow.Show(this);
            }
            else
            {
                if (tab != null) _setupWindow.ShowTab(tab);
                _setupWindow.Activate();
            }
        }

        private void ShowTxSettings()
        {
            TxSettingsExpander.IsExpanded = true;
            TxSettingsExpander.BringIntoView();
        }

        private void ResetSpectrumView()
        {
            var d = new Settings();
            Panafall.MaxDbm = _settings.SpectrumMaxDbm = d.SpectrumMaxDbm;
            Panafall.MinDbm = _settings.SpectrumMinDbm = d.SpectrumMinDbm;
            Panafall.PanFraction = _settings.PanFraction = d.PanFraction;
            Panafall.InvalidateVisual();
        }

        private void ShowShortcuts() => ShowText("Keyboard and mouse", new[]
        {
            ("Tuning", ""),
            ("Left / Right, Up / Down", "tune down / up by the tuning step"),
            ("Page Down / Page Up", "tune by 10 steps"),
            ("Mouse wheel on the panadapter", "tune by the step"),
            ("Click on the panadapter", "tune to that frequency"),
            ("Mouse wheel on a VFO digit", "change that digit"),
            ("Double-click the VFO, or Ctrl+F", "type a frequency (MHz, or kHz above 1000); Enter to tune, Esc to cancel"),
            ("Panadapter", ""),
            ("Ctrl + mouse wheel", "move the dB scale up or down"),
            ("Shift + mouse wheel", "change the dB range"),
            ("Drag the line between panadapter and waterfall", "change their heights"),
            ("Menus", ""),
            ("Alt + underlined letter", "open a menu"),
            ("Ctrl+S", "save the settings"),
            ("Ctrl+D", "discover radios"),
            ("Ctrl+,", "open setup"),
            ("Ctrl+Shift+A", "show or hide the transmit audio panel"),
            ("Ctrl+Q", "exit"),
            ("F1", "this list"),
        });

        private void ShowAbout()
        {
            var asm = typeof(MainWindow).Assembly;
            string version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString();
            int plus = version?.IndexOf('+') ?? -1;
            if (plus >= 0 && version.Length > plus + 8) version = version.Substring(0, plus + 8);    // short commit id
            ShowText("About Thetis for Linux", new[]
            {
                ("Thetis for Linux", ""),
                ("Version", version),
                ("Based on", "Thetis, the OpenHPSDR console (TAPR / OpenHPSDR, originally PowerSDR by FlexRadio Systems)"),
                ("DSP", "WDSP 2.10 by Warren Pratt, NR0V"),
                ("Noise reduction", "NR2, RN2, NR4 and DFNR from AetherSDR (" + (AetherNr.Loaded ? "loaded" : "not loaded") + ")"),
                ("User interface", "Avalonia, .NET 8"),
                ("Licence", "GNU General Public License, version 3 as a whole (Thetis is GPL v2 or later; AetherSDR is GPL v3)"),
                ("Settings", Settings.ConfigDirectory),
                ("Data", Settings.DataDirectory),
            });
        }

        /// <summary>Help / Receive diagnostics: measure for five seconds, then show and save the report.</summary>
        private async void ShowReceiveDiagnostics()
        {
            StatusText.Text = "Receive diagnostics: measuring for 5 seconds...";
            string report;
            try { report = await System.Threading.Tasks.Task.Run(() => _radio.ReceiveDiagnostics(5)); }
            catch (Exception ex) { report = "Diagnostics failed: " + ex; }
            string file = Path.Combine(Settings.ConfigDirectory, "receive-diagnostics.txt");
            try { Directory.CreateDirectory(Settings.ConfigDirectory); File.WriteAllText(file, report); }
            catch (Exception) { file = null; }
            StatusText.Text = file != null ? "Receive diagnostics saved to " + file : "Receive diagnostics done";

            var box = new TextBox
            {
                Text = report, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("DejaVu Sans Mono, monospace"), FontSize = 12,
            };
            var w = new Window
            {
                Title = "Receive diagnostics", Width = 900, Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#12171C")),
            };
            var copy = new Button { Content = "Copy to clipboard", Margin = new Thickness(0, 0, 8, 0) };
            copy.Click += async (_, _) => { if (w.Clipboard != null) await w.Clipboard.SetTextAsync(report); };
            var close = new Button { Content = "Close", IsCancel = true };
            close.Click += (_, _) => w.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
            buttons.Children.Add(new TextBlock { Text = file != null ? "Saved to " + file + "   " : "", VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#9AA4AE")) });
            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            var dock = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);
            dock.Children.Add(new Border { Padding = new Thickness(12, 12, 12, 0), Child = box });
            w.Content = dock;
            w.Show(this);
        }

        /// <summary>A small modal window with a two-column list; an entry with an empty value is a heading.</summary>
        private void ShowText(string title, IEnumerable<(string key, string value)> rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(16, 12) };
            foreach (var (key, value) in rows)
            {
                int r = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                bool heading = string.IsNullOrEmpty(value);
                var k = new TextBlock
                {
                    Text = key, FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal, FontSize = heading ? 14 : 13,
                    Margin = new Thickness(0, heading && r > 0 ? 10 : 2, 16, 2),
                };
                Grid.SetRow(k, r);
                if (heading) Grid.SetColumnSpan(k, 2);
                grid.Children.Add(k);
                if (heading) continue;
                var v = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#C5CED6")), Margin = new Thickness(0, 2) };
                Grid.SetRow(v, r);
                Grid.SetColumn(v, 1);
                grid.Children.Add(v);
            }
            var w = new Window
            {
                Title = title, Width = 620, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#12171C")),
            };
            var ok = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 12), IsDefault = true, IsCancel = true };
            ok.Click += (_, _) => w.Close();
            var panel = new StackPanel();
            panel.Children.Add(grid);
            panel.Children.Add(ok);
            w.Content = panel;
            _ = w.ShowDialog(this);
        }

        #endregion

        #region menu helpers

        /// <summary>A top-level menu whose items are built each time it opens.</summary>
        private static MenuItem TopMenu(string header, Func<List<Control>> build)
        {
            var m = new MenuItem { Header = header };
            m.Items.Add(new MenuItem { Header = "..." });        // so it opens as a menu
            m.SubmenuOpened += (_, e) =>
            {
                if (e.Source != m) return;                     // a nested submenu opened
                m.Items.Clear();
                foreach (var c in build()) m.Items.Add(c);
            };
            return m;
        }

        private static MenuItem Item(string header, Action click, string gesture = null, bool enabled = true)
        {
            var m = new MenuItem { Header = header, IsEnabled = enabled && click != null };
            if (gesture != null) m.InputGesture = KeyGesture.Parse(gesture);
            if (click != null) m.Click += (_, _) => click();
            return m;
        }

        private static MenuItem Check(string header, bool isChecked, Action toggle, bool enabled = true)
        {
            var m = Item(header, toggle, enabled: enabled);
            m.ToggleType = MenuItemToggleType.CheckBox;
            m.IsChecked = isChecked;
            return m;
        }

        private static MenuItem Radio(string header, bool isChecked, Action select, bool enabled = true)
        {
            var m = Item(header, select, enabled: enabled);
            m.ToggleType = MenuItemToggleType.Radio;
            m.IsChecked = isChecked;
            return m;
        }

        private static MenuItem Sub(string header, List<Control> items)
        {
            var m = new MenuItem { Header = header, IsEnabled = items.Count > 0 };
            foreach (var c in items) m.Items.Add(c);
            return m;
        }

        /// <summary>Names from outside (devices, radios) must not turn '_' into an access key.</summary>
        private static string Escape(string s) => (s ?? "").Replace("_", "__");

        private void Bind(Key key, KeyModifiers mods, Action action) =>
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, mods), Command = new ActionCommand(action) });

        private sealed class ActionCommand : ICommand
        {
            private readonly Action _action;
            public ActionCommand(Action action) => _action = action;
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => _action();
        }

        #endregion
    }
}
