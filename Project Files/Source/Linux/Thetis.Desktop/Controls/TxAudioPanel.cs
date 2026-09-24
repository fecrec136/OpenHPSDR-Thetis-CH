/*  TxAudioPanel.cs

This file is part of a program that implements a Software-Defined Radio.

The transmit audio panel: microphone gain, the 10-band graphic EQ, leveler,
compressor, CESSB, CFC, phase rotator, VOX and downward expander, with the
transmit chain's meters.  It edits the same TxProcessing settings as
Setup > Transmit audio, and the main window docks it at the left, right or
bottom, or shows it in a window of its own (MainWindow.TxPanel.cs).

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Thetis.Radio;

namespace Thetis.Desktop.Controls
{
    public enum TxPanelDock { Left, Right, Bottom, Float }

    public sealed class TxAudioPanel : UserControl
    {
        private readonly RadioController _radio;
        private readonly Settings _settings;
        private readonly DispatcherTimer _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        private readonly List<Action> _refreshers = new List<Action>();
        private readonly List<(string name, ProgressBar bar, TextBlock text, double min, double max, Func<TxAudioLevels, float?> get)> _meters =
            new List<(string, ProgressBar, TextBlock, double, double, Func<TxAudioLevels, float?>)>();
        private readonly Dictionary<TxPanelDock, Button> _dockButtons = new Dictionary<TxPanelDock, Button>();
        private bool _updating;
        private Point? _dragFrom;
        private readonly WrapPanel _body = new WrapPanel { Orientation = Orientation.Horizontal };
        private readonly ScrollViewer _scroll = new ScrollViewer();

        private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA4AE"));

        /// <summary>The user asked to dock the panel elsewhere, or to float it.</summary>
        public event Action<TxPanelDock> DockRequested;

        /// <summary>The user closed the panel.</summary>
        public event Action CloseRequested;

        /// <summary>A setting changed here (the main window's VOX / COMP / EQ buttons and mic gain follow).</summary>
        public event Action Changed;

        /// <summary>The header was dragged away while docked: float the panel at this screen point.</summary>
        public event Action<PixelPoint> TearOffRequested;

        private TxProcessing Tx => _settings.TxProcessing ??= new TxProcessing();

        public TxAudioPanel(RadioController radio, Settings settings)
        {
            _radio = radio;
            _settings = settings;
            _applyTimer.Tick += (_, _) => { _applyTimer.Stop(); _radio.ApplyTxProcessing(); };

            var root = new DockPanel();
            root.Children.Add(Header());
            var body = _body;
            body.Children.Add(MetersGroup());
            body.Children.Add(EqGroup());
            body.Children.Add(DynamicsGroup());
            body.Children.Add(CfcGroup());
            body.Children.Add(VoxGroup());
            _scroll.Content = body;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            Dock = TxPanelDock.Right;
            root.Children.Add(_scroll);
            Content = new Border { Background = new SolidColorBrush(Color.Parse("#12171C")), Child = root };
            Refresh();
        }

        /// <summary>Which dock button to show as current.</summary>
        public TxPanelDock Dock
        {
            set
            {
                foreach (var (d, b) in _dockButtons) b.IsEnabled = d != value;
                // at the bottom the groups stand in one row that scrolls sideways; elsewhere they wrap
                _scroll.HorizontalScrollBarVisibility = value == TxPanelDock.Bottom ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            }
        }

        #region header

        private Control Header()
        {
            var bar = new DockPanel { Background = new SolidColorBrush(Color.Parse("#161C22")), Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
            DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
            buttons.Children.Add(DockButton(TxPanelDock.Left, "◧", "Dock on the left"));
            buttons.Children.Add(DockButton(TxPanelDock.Bottom, "⬓", "Dock at the bottom"));
            buttons.Children.Add(DockButton(TxPanelDock.Right, "◨", "Dock on the right"));
            buttons.Children.Add(DockButton(TxPanelDock.Float, "⧉", "Show in a window of its own"));
            var close = SmallButton("✕", "Close the panel (View > Transmit audio panel opens it again)");
            close.Click += (_, _) => CloseRequested?.Invoke();
            buttons.Children.Add(close);
            bar.Children.Add(buttons);
            var title = new TextBlock
            {
                Text = "Transmit audio", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0), Cursor = new Cursor(StandardCursorType.SizeAll),
            };
            ToolTip.SetTip(title, "Drag to take the panel out of the main window");
            bar.Children.Add(title);

            // drag the title away to float the panel
            bar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed && e.Source is not Button) _dragFrom = e.GetPosition(this);
            };
            bar.PointerMoved += (_, e) =>
            {
                if (_dragFrom is not Point from) return;
                var at = e.GetPosition(this);
                if (Math.Abs(at.X - from.X) + Math.Abs(at.Y - from.Y) < 40) return;
                _dragFrom = null;
                var top = TopLevel.GetTopLevel(this);
                if (top is Window w) TearOffRequested?.Invoke(w.PointToScreen(e.GetPosition(w)));
            };
            bar.PointerReleased += (_, _) => _dragFrom = null;
            return bar;
        }

        private Button DockButton(TxPanelDock dock, string glyph, string tip)
        {
            var b = SmallButton(glyph, tip);
            b.Click += (_, _) => DockRequested?.Invoke(dock);
            _dockButtons[dock] = b;
            return b;
        }

        private static Button SmallButton(string glyph, string tip)
        {
            var b = new Button { Content = glyph, Padding = new Thickness(6, 1), FontSize = 13, MinWidth = 26, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            return b;
        }

        #endregion

        #region groups

        private static Border Group(string title, Control content, double width = 300)
        {
            var p = new StackPanel();
            p.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 2, 4) });
            p.Children.Add(content);
            return new Border { Classes = { "group" }, Child = p, Width = width };
        }

        private Control MetersGroup()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*,56") };
            void Meter(string name, double min, double max, Func<TxAudioLevels, float?> get, string tip)
            {
                int r = g.RowDefinitions.Count;
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock { Text = name, FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(label, tip);
                var bar = new ProgressBar { Minimum = min, Maximum = max, Value = min, Height = 8, MinHeight = 8, Margin = new Thickness(2, 3) };
                var text = new TextBlock { Text = "-", FontSize = 11, FontFamily = new FontFamily("DejaVu Sans Mono, monospace"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(label, r); Grid.SetRow(bar, r); Grid.SetRow(text, r);
                Grid.SetColumn(bar, 1); Grid.SetColumn(text, 2);
                g.Children.Add(label); g.Children.Add(bar); g.Children.Add(text);
                _meters.Add((name, bar, text, min, max, get));
            }
            Meter("MIC", -40, 3, l => l.Mic, "Microphone peak, after mic gain (dBFS)");
            Meter("EQ", -40, 3, l => l.Eq, "Peak after the EQ (dBFS)");
            Meter("LEV", -40, 3, l => l.Leveler, "Peak after the leveler (dBFS)");
            Meter("LEV gain", 0, 20, l => l.LevelerGain, "Gain the leveler is adding (dB)");
            Meter("CFC", -40, 3, l => l.Cfc, "Peak after CFC (dBFS)");
            Meter("CFC gain", 0, 20, l => l.CfcGain, "CFC compression (dB)");
            Meter("COMP", -40, 3, l => l.Compressor, "Peak after the compressor (dBFS)");
            Meter("ALC", -40, 3, l => l.Alc, "Peak after ALC, what is transmitted (dBFS)");
            Meter("ALC gain", -20, 0, l => l.AlcGain, "ALC gain reduction (dB)");
            var p = new StackPanel();
            p.Children.Add(SliderRow("Mic gain", -40, 70, 1, "0 dB", () => _settings.MicGainDb,
                                     v => { _radio.MicGainDb = _settings.MicGainDb = Math.Round(v); }, immediate: true));
            p.Children.Add(g);
            p.Children.Add(new TextBlock { Text = "The meters read while transmitting.", FontSize = 10, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
            return Group("Microphone and meters", p);
        }

        private Control EqGroup()
        {
            var p = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            top.Children.Add(Toggle("EQ", "The 10-band transmit equaliser", () => Tx.EqOn, v => Tx.EqOn = v));
            var flat = new Button { Content = "Flat", Padding = new Thickness(8, 2) };
            ToolTip.SetTip(flat, "All bands and the preamp to 0 dB");
            flat.Click += (_, _) =>
            {
                Tx.EqPreampDb = 0;
                Tx.EqBandsDb = new int[10];
                Apply();
                Refresh();
            };
            top.Children.Add(flat);
            p.Children.Add(top);

            var bands = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (int i = 0; i < 11; i++) bands.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            bands.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            bands.RowDefinitions.Add(new RowDefinition(new GridLength(130)));
            bands.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int i = 0; i < 11; i++)
            {
                int band = i - 1;            // -1 = preamp
                string label = band < 0 ? "Pre" : EqLabel(TxProcessing.EqFrequencies[band]);
                var value = new TextBlock { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center };
                var s = new Slider
                {
                    Orientation = Orientation.Vertical, Minimum = -12, Maximum = 15, TickFrequency = 1, IsSnapToTickEnabled = true,
                    HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 0, Padding = new Thickness(0),
                };
                ToolTip.SetTip(s, band < 0 ? "Preamp (dB)" : $"{TxProcessing.EqFrequencies[band]} Hz (dB); double-click for 0");
                var name = new TextBlock { Text = label, FontSize = 10, Foreground = band < 0 ? Brushes.White : Muted, HorizontalAlignment = HorizontalAlignment.Center };
                Func<int> get = () => band < 0 ? Tx.EqPreampDb : Band(band);
                Action<int> set = v => { if (band < 0) Tx.EqPreampDb = v; else { EnsureBands(); Tx.EqBandsDb[band] = v; } };
                s.PropertyChanged += (_, e) =>
                {
                    if (e.Property != RangeBase.ValueProperty) return;
                    value.Text = ((int)s.Value).ToString("+0;-0;0", CultureInfo.InvariantCulture);
                    if (_updating) return;
                    set((int)Math.Round(s.Value));
                    Apply();
                };
                s.DoubleTapped += (_, _) => s.Value = 0;
                _refreshers.Add(() => { s.Value = get(); value.Text = get().ToString("+0;-0;0", CultureInfo.InvariantCulture); });
                Grid.SetColumn(value, i); Grid.SetColumn(s, i); Grid.SetColumn(name, i);
                Grid.SetRow(s, 1); Grid.SetRow(name, 2);
                bands.Children.Add(value); bands.Children.Add(s); bands.Children.Add(name);
            }
            p.Children.Add(bands);
            return Group("Equaliser", p, 300);
        }

        private Control DynamicsGroup()
        {
            var p = new StackPanel();
            p.Children.Add(Toggle("LEVELER", "Leveler: slow automatic gain that evens out the speech level", () => Tx.LevelerOn, v => Tx.LevelerOn = v));
            p.Children.Add(SliderRow("Leveler max gain", 0, 20, 1, "0 dB", () => Tx.LevelerMaxGainDb, v => Tx.LevelerMaxGainDb = v));
            p.Children.Add(SliderRow("Leveler decay", 1, 2000, 10, "0 ms", () => Tx.LevelerDecayMs, v => Tx.LevelerDecayMs = (int)v));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(Toggle("COMP", "Speech compressor (CPDR): raises the average power", () => Tx.CompressorOn, v => Tx.CompressorOn = v));
            row.Children.Add(Toggle("CESSB", "Controlled-envelope SSB: overshoot control after the compressor", () => Tx.CessbOn, v => Tx.CessbOn = v));
            row.Children.Add(Toggle("PHROT", "Phase rotator: makes the speech waveform more symmetrical", () => Tx.PhaseRotatorOn, v => Tx.PhaseRotatorOn = v));
            p.Children.Add(row);
            p.Children.Add(SliderRow("Compression", 0, 20, 1, "0 dB", () => Tx.CompressorDb, v => Tx.CompressorDb = v));
            p.Children.Add(SliderRow("Phase rotator corner", 50, 2000, 10, "0 Hz", () => Tx.PhaseRotatorHz, v => Tx.PhaseRotatorHz = v));
            return Group("Leveler, compressor", p);
        }

        private Control CfcGroup()
        {
            var p = new StackPanel();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Toggle("CFC", "Continuous frequency compressor: compression per frequency band (profile in Setup > Transmit audio)", () => Tx.CfcOn, v => Tx.CfcOn = v));
            row.Children.Add(Toggle("POST-EQ", "CFC's post-compression EQ", () => Tx.CfcPostEqOn, v => Tx.CfcPostEqOn = v));
            p.Children.Add(row);
            p.Children.Add(SliderRow("CFC pre-compression", 0, 16, 1, "0 dB", () => Tx.CfcPrecompDb, v => Tx.CfcPrecompDb = v));
            p.Children.Add(SliderRow("Post-EQ gain", -16, 16, 1, "0 dB", () => Tx.CfcPostEqGainDb, v => Tx.CfcPostEqGainDb = v));
            return Group("CFC", p);
        }

        private Control VoxGroup()
        {
            var p = new StackPanel();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Toggle("VOX", "Voice-operated transmit", () => Tx.VoxOn, v => Tx.VoxOn = v));
            row.Children.Add(Toggle("DEXP", "Downward expander: lowers background noise between words", () => Tx.ExpanderOn, v => Tx.ExpanderOn = v));
            p.Children.Add(row);
            p.Children.Add(SliderRow("VOX threshold", -80, 0, 1, "0 dB", () => Tx.VoxThresholdDb, v => Tx.VoxThresholdDb = v));
            p.Children.Add(SliderRow("VOX hold", 1, 2000, 10, "0 ms", () => Tx.VoxHoldMs, v => Tx.VoxHoldMs = (int)v));
            p.Children.Add(SliderRow("Expander ratio", 0, 30, 1, "0 dB", () => Tx.ExpanderRatioDb, v => Tx.ExpanderRatioDb = v));
            return Group("VOX, expander", p);
        }

        #endregion

        #region building blocks

        private ToggleButton Toggle(string text, string tip, Func<bool> get, Action<bool> set)
        {
            var b = new ToggleButton { Content = text, Classes = { "panel" } };
            ToolTip.SetTip(b, tip);
            b.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                set(b.IsChecked == true);
                Apply(now: true);
            };
            _refreshers.Add(() => b.IsChecked = get());
            return b;
        }

        private Control SliderRow(string label, double min, double max, double step, string fmt, Func<double> get, Action<double> set, bool immediate = false)
        {
            var caption = new TextBlock { Classes = { "caption" } };
            var s = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true, Margin = new Thickness(2, -6, 2, -4) };
            void Caption() => caption.Text = label + "  " + s.Value.ToString(fmt, CultureInfo.InvariantCulture);
            s.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                Caption();
                if (_updating) return;
                set(s.Value);
                if (immediate) Changed?.Invoke(); else Apply();
            };
            _refreshers.Add(() => { s.Value = Math.Clamp(get(), min, max); Caption(); });
            var p = new StackPanel();
            p.Children.Add(caption);
            p.Children.Add(s);
            return p;
        }

        private static string EqLabel(int hz) => hz >= 1000 ? (hz / 1000) + "k" : hz.ToString(CultureInfo.InvariantCulture);

        private int Band(int i) => Tx.EqBandsDb != null && i < Tx.EqBandsDb.Length ? Tx.EqBandsDb[i] : 0;

        private void EnsureBands()
        {
            if (Tx.EqBandsDb == null || Tx.EqBandsDb.Length < 10)
            {
                var b = new int[10];
                if (Tx.EqBandsDb != null) Array.Copy(Tx.EqBandsDb, b, Tx.EqBandsDb.Length);
                Tx.EqBandsDb = b;
            }
        }

        // a slider sends many values while dragged: send the settings at most every 60 ms
        private void Apply(bool now = false)
        {
            if (now) { _applyTimer.Stop(); _radio.ApplyTxProcessing(); }
            else if (!_applyTimer.IsEnabled) _applyTimer.Start();
            Changed?.Invoke();
        }

        #endregion

        /// <summary>Show the current settings (after Setup or the main window changed them).</summary>
        public void Refresh()
        {
            _updating = true;
            try { foreach (var r in _refreshers) r(); }
            finally { _updating = false; }
        }

        /// <summary>Update the meters (call a few times a second).</summary>
        public void UpdateMeters()
        {
            TxAudioLevels l = _radio.TxAudioMeters();
            foreach (var (_, bar, text, min, max, get) in _meters)
            {
                float? v = l == null ? null : get(l);
                bool ok = v is float f && f > -150 && f < 150;
                bar.Value = ok ? Math.Clamp(v.Value, min, max) : min;
                text.Text = ok ? v.Value.ToString("0.0", CultureInfo.InvariantCulture) : "-";
            }
        }
    }
}
