/*  SetupWindow.Cat.cs

This file is part of a program that implements a Software-Defined Radio.

Setup > CAT / TCI: four CAT ports (a serial port -- the computer's own or a
USB to RS232 adapter -- or a virtual port for programs on this computer), the
TCP CAT server, the TCI server, the answers' options, and a monitor of the
commands going in and out.  Changes take effect at once.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Thetis.Cat;

namespace Thetis.Desktop
{
    public sealed partial class SetupWindow
    {
        private readonly CatService _cat;
        private readonly Action _applyCat;
        private DispatcherTimer _catTimer;
        private readonly Dictionary<string, TextBlock> _catStatus = new Dictionary<string, TextBlock>();
        private TextBox _catLog;
        private readonly StringBuilder _catLogText = new StringBuilder();
        private bool _catLogDirty;
        private IReadOnlyList<SerialPortInfo> _serialPorts = Array.Empty<SerialPortInfo>();
        private readonly List<Action> _serialRefreshers = new List<Action>();
        private TextBlock _dialoutHint;

        private static readonly int[] Bauds = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
        private static readonly (int id, string name)[] RigIds = { (19, "TS-2000 (019)"), (20, "TS-480 (020)"), (13, "TS-50S (013)"), (900, "SDR-1000 (900)") };

        private CatSettings CatCfg
        {
            get
            {
                _settings.Cat ??= new CatSettings();
                _settings.Cat.Normalize(Settings.DataDirectory);
                return _settings.Cat;
            }
        }

        private void ApplyCat()
        {
            if (!_building) _applyCat?.Invoke();
        }

        private Control CatTab()
        {
            var cfg = CatCfg;
            var p = new StackPanel { Spacing = 4 };
            p.Children.Add(Text("CAT lets logging, digital-mode and contest programs read and set the frequency, mode, filter and " +
                                "transmit state: WSJT-X, JTDX, fldigi, Hamlib (rigctld), loggers, and controllers on another computer. " +
                                "The commands are the Windows console's (Kenwood TS-2000 and Thetis ZZ). In the program, choose the radio " +
                                "\"Kenwood TS-2000\", or \"PowerSDR/Thetis\" (Hamlib 2048) where it is offered, and the port below.", true));

            _serialPorts = SerialPorts.List();
            _dialoutHint = new TextBlock { Classes = { "warn" }, Text = SerialPorts.DialoutHint, IsVisible = _serialPorts.Any(x => !x.Accessible) };
            p.Children.Add(_dialoutHint);

            for (int i = 0; i < cfg.Ports.Count; i++) p.Children.Add(CatPortBox(i, cfg.Ports[i]));

            var refresh = new Button { Content = "Look for serial ports again", Margin = new Thickness(0, 2) };
            ToolTip.SetTip(refresh, "After plugging in a USB to RS232 adapter");
            refresh.Click += (_, _) =>
            {
                _serialPorts = SerialPorts.List();
                _dialoutHint.IsVisible = _serialPorts.Any(x => !x.Accessible);
                foreach (var r in _serialRefreshers) r();
            };
            p.Children.Add(refresh);

            // --- network ---
            p.Children.Add(Heading("Network"));
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };
            AddServerRow(g, 0, "CAT server (TCP)", "TCP", cfg.TcpEnabled, v => cfg.TcpEnabled = v, cfg.TcpPort, v => cfg.TcpPort = v,
                         cfg.TcpAllInterfaces, v => cfg.TcpAllInterfaces = v,
                         "The same commands over a network connection (Hamlib: port 127.0.0.1:31001; a controller on another computer).");
            AddServerRow(g, 2, "TCI server (WebSocket)", "TCI", cfg.TciEnabled, v => cfg.TciEnabled = v, cfg.TciPort, v => cfg.TciPort = v,
                         cfg.TciAllInterfaces, v => cfg.TciAllInterfaces = v,
                         "Expert Electronics' TCI, as the Windows console offers it: frequency, mode, filter, transmit, drive, volume, " +
                         "NR, ANF, AGC and the sensors. Audio and I/Q streaming over TCI are not offered yet: use PC audio for the audio.");
            p.Children.Add(g);

            // --- options ---
            p.Children.Add(Heading("Answers"));
            var og = Grid2();
            var rig = new ComboBox { ItemsSource = RigIds.Select(r => r.name).ToList(), Width = 200, Margin = new Thickness(2),
                                     SelectedIndex = Math.Max(0, Array.FindIndex(RigIds, r => r.id == cfg.RigId)) };
            rig.SelectionChanged += (_, _) => { if (rig.SelectedIndex >= 0) { cfg.RigId = RigIds[rig.SelectedIndex].id; ApplyCat(); } };
            AddRow(og, "ID answers as", rig);
            AddRow(og, "Report DIGU / DIGL as USB / LSB (MD)", CatCheck(cfg.DigUIsUsb, v => cfg.DigUIsUsb = v,
                "Kenwood MD has no data modes: with this on, MD reads DIGU as USB and MD2 selects DIGU (for WSJT-X set to \"Data/Pkt\" off)."));
            AddRow(og, "Allow auto information (AI1)", CatCheck(cfg.AllowAutoInformation, v => cfg.AllowAutoInformation = v,
                "Programs that ask for it are sent the frequency when it changes"));
            p.Children.Add(og);

            // --- monitor ---
            p.Children.Add(Heading("Monitor"));
            var mon = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var show = new CheckBox { Content = "Show the commands going in (<) and out (>)" };
            var clear = new Button { Content = "Clear" };
            mon.Children.Add(show);
            mon.Children.Add(clear);
            p.Children.Add(mon);
            _catLog = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 180,
                FontFamily = new FontFamily("DejaVu Sans Mono, monospace"), FontSize = 11,
            };
            p.Children.Add(_catLog);
            show.IsCheckedChanged += (_, _) =>
            {
                if (_cat == null) return;
                if (show.IsChecked == true) { _cat.Traffic += OnCatTraffic; _cat.TrafficEnabled = true; }
                else { _cat.TrafficEnabled = false; _cat.Traffic -= OnCatTraffic; }
            };
            clear.Click += (_, _) => { lock (_catLogText) _catLogText.Clear(); _catLog.Text = ""; };

            _catTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _catTimer.Tick += (_, _) => RefreshCatStatus();
            _catTimer.Start();
            Dispatcher.UIThread.Post(RefreshCatStatus);
            return p;
        }

        private Control CatPortBox(int index, CatPortSettings c)
        {
            string name = "CAT" + (index + 1);
            var box = new StackPanel { Spacing = 2 };

            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var on = new CheckBox { Content = name, IsChecked = c.Enabled, FontWeight = FontWeight.SemiBold, MinWidth = 80 };
            var kind = new ComboBox
            {
                ItemsSource = new[] { "Serial port (built-in or USB to RS232)", "Virtual port (programs on this computer)" },
                SelectedIndex = c.Kind == CatPortKind.Virtual ? 1 : 0, Width = 380,
            };
            top.Children.Add(on);
            top.Children.Add(kind);
            box.Children.Add(top);

            // serial settings
            var serial = new WrapPanel { Margin = new Thickness(24, 0, 0, 0) };
            var dev = new ComboBox { Width = 420, Margin = new Thickness(2), PlaceholderText = "Choose the port" };
            void FillDevices()
            {
                var items = _serialPorts.Select(x => x.Description + (x.Accessible ? "" : "  (no permission)")).ToList();
                int sel = -1;
                for (int k = 0; k < _serialPorts.Count; k++)
                    if (_serialPorts[k].Path == c.Device || _serialPorts[k].Device == c.Device) sel = k;
                if (sel < 0 && !string.IsNullOrEmpty(c.Device)) { items.Add(c.Device + "  (not connected now)"); sel = items.Count - 1; }
                bool b = _building;
                _building = true;
                dev.ItemsSource = items;
                dev.SelectedIndex = sel;
                _building = b;
            }
            FillDevices();
            _serialRefreshers.Add(FillDevices);
            dev.SelectionChanged += (_, _) =>
            {
                if (_building || dev.SelectedIndex < 0 || dev.SelectedIndex >= _serialPorts.Count) return;
                c.Device = _serialPorts[dev.SelectedIndex].Path;
                ApplyCat();
            };
            serial.Children.Add(dev);
            serial.Children.Add(Pick("baud", Bauds.Select(b => b.ToString()).ToArray(), Math.Max(0, Array.IndexOf(Bauds, c.Baud)), i => c.Baud = Bauds[i], 90));
            serial.Children.Add(Pick("bits", new[] { "8", "7" }, c.DataBits == 7 ? 1 : 0, i => c.DataBits = i == 1 ? 7 : 8, 60));
            serial.Children.Add(Pick("parity", new[] { "none", "odd", "even" }, (int)c.Parity, i => c.Parity = (CatParity)i, 80));
            serial.Children.Add(Pick("stop", new[] { "1", "2" }, c.StopBits == 2 ? 1 : 0, i => c.StopBits = i + 1, 60));
            serial.Children.Add(Pick("handshake", new[] { "none", "RTS/CTS", "XON/XOFF" }, (int)c.Handshake, i => c.Handshake = (CatHandshake)i, 110));
            serial.Children.Add(CatCheck(c.Dtr, v => c.Dtr = v, "Hold DTR on (some interfaces take power from it)", "DTR on"));
            serial.Children.Add(CatCheck(c.Rts, v => c.Rts = v, "Hold RTS on", "RTS on"));
            serial.Children.Add(Pick("PTT input", new[] { "none", "CTS", "DSR", "DCD" }, (int)c.Ptt, i => c.Ptt = (PttLine)i, 90,
                "Key the transmitter while this input of the port is on: a footswitch, or a program's RTS/DTR PTT through a null-modem cable"));
            serial.Children.Add(CatCheck(c.PttInvert, v => c.PttInvert = v, "Key while the input is off instead", "invert"));
            box.Children.Add(serial);

            // virtual settings
            var virt = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(24, 0, 0, 0) };
            virt.Children.Add(Text("Programs open:", true));
            var path = new TextBox { Text = c.VirtualPath, Width = 420 };
            ToolTip.SetTip(path, "Give this name to the program as its radio's serial port (any speed)");
            path.LostFocus += (_, _) => { if (path.Text != c.VirtualPath && !string.IsNullOrWhiteSpace(path.Text)) { c.VirtualPath = path.Text.Trim(); ApplyCat(); } };
            path.KeyDown += (_, e) => { if (e.Key == Key.Enter) { c.VirtualPath = path.Text.Trim(); ApplyCat(); } };
            var copy = new Button { Content = "Copy" };
            copy.Click += async (_, _) => { if (Clipboard != null) await Clipboard.SetTextAsync(c.VirtualPath); };
            virt.Children.Add(path);
            virt.Children.Add(copy);
            box.Children.Add(virt);

            var status = new TextBlock { FontSize = 12, Margin = new Thickness(24, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            _catStatus[name] = status;
            box.Children.Add(status);

            void ShowKind()
            {
                serial.IsVisible = kind.SelectedIndex == 0;
                virt.IsVisible = kind.SelectedIndex == 1;
            }
            ShowKind();
            on.IsCheckedChanged += (_, _) => { c.Enabled = on.IsChecked == true; ApplyCat(); };
            kind.SelectionChanged += (_, _) => { c.Kind = kind.SelectedIndex == 1 ? CatPortKind.Virtual : CatPortKind.Serial; ShowKind(); ApplyCat(); };
            return new Border { Classes = { "group" }, Child = box, Margin = new Thickness(0, 3) };
        }

        private void AddServerRow(Grid g, int row, string label, string key, bool enabled, Action<bool> setEnabled,
                                  int port, Action<int> setPort, bool all, Action<bool> setAll, string tip)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var on = new CheckBox { Content = label, IsChecked = enabled, MinWidth = 220 };
            ToolTip.SetTip(on, tip);
            on.IsCheckedChanged += (_, _) => { setEnabled(on.IsChecked == true); ApplyCat(); };
            var num = Number(port, 1024, 65535, 1, "0", 120);
            num.LostFocus += (_, _) => { if (num.Value is decimal v && (int)v != port) { port = (int)v; setPort(port); ApplyCat(); } };
            var everywhere = CatCheck(all, setAll, "Accept connections from other computers (otherwise only from this one)", "from other computers");
            Place(g, on, row, 0);
            Place(g, new TextBlock { Text = "port", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) }, row, 1);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            right.Children.Add(num);
            right.Children.Add(everywhere);
            Place(g, right, row, 2);
            var status = new TextBlock { FontSize = 12, Margin = new Thickness(24, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumnSpan(status, 4);
            Place(g, status, row + 1, 0);
            _catStatus[key] = status;
        }

        private Control Pick(string label, string[] items, int selected, Action<int> set, double width, string tip = null)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(4, 2) };
            p.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted });
            var c = new ComboBox { ItemsSource = items, SelectedIndex = selected, Width = width };
            c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) { set(c.SelectedIndex); ApplyCat(); } };
            p.Children.Add(c);
            if (tip != null) ToolTip.SetTip(p, tip);
            return p;
        }

        private CheckBox CatCheck(bool value, Action<bool> set, string tip, string label = null)
        {
            var c = new CheckBox { IsChecked = value, Content = label, Margin = new Thickness(4, 2) };
            ToolTip.SetTip(c, tip);
            c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); ApplyCat(); };
            return c;
        }

        private void RefreshCatStatus()
        {
            if (_cat == null)
            {
                foreach (var t in _catStatus.Values) t.Text = "";
                return;
            }
            var running = _cat.Status().ToDictionary(s => s.name, s => (s.status, s.ok));
            foreach (var (name, block) in _catStatus)
            {
                if (running.TryGetValue(name, out var st))
                {
                    block.Text = st.status;
                    block.Foreground = st.ok ? new SolidColorBrush(Color.Parse("#81C784")) : new SolidColorBrush(Color.Parse("#FFB74D"));
                }
                else
                {
                    block.Text = "off";
                    block.Foreground = Muted;
                }
            }
            if (_catLogDirty && _catLog != null)
            {
                _catLogDirty = false;
                lock (_catLogText) _catLog.Text = _catLogText.ToString();
                _catLog.CaretIndex = _catLog.Text?.Length ?? 0;
            }
        }

        private void OnCatTraffic(string port, string dir, string text)
        {
            lock (_catLogText)
            {
                _catLogText.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(port.PadRight(9)).Append(dir).Append(' ')
                           .Append(text.Replace("\r", "\\r").Replace("\n", "\\n")).Append('\n');
                if (_catLogText.Length > 60000) _catLogText.Remove(0, _catLogText.Length - 40000);
            }
            _catLogDirty = true;
        }

        private void CloseCatTab()
        {
            _catTimer?.Stop();
            if (_cat != null)
            {
                _cat.TrafficEnabled = false;
                _cat.Traffic -= OnCatTraffic;
            }
        }
    }
}
