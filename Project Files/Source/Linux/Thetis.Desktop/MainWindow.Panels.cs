/*  MainWindow.Panels.cs

This file is part of a program that implements a Software-Defined Radio.

The dockable panels: the band buttons, the mode buttons, the filter width
buttons, the S-meter, transmit (MOX, TUNE, drive, mic gain), transmit
settings, receive gain, AGC, noise reduction, tuning and display, and the
transmit audio panel.  Each can sit at the top (next to the VFO, where the
buttons are by default), on the left or right of the panadapter (where the
rest are), below it, or in a window of its own; several panels can share a
place.  The splitters resize the left, right and bottom places, and each
docked panel's grip its own height (or its width at the bottom).  Where each
panel is, its order, size and window are saved with the settings.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Thetis.Desktop.Controls;

namespace Thetis.Desktop
{
    public partial class MainWindow
    {
        // the button rows, built in BuildStaticControls / RefreshFilters, shown inside their DockFrames
        private readonly WrapPanel BandPanel = new WrapPanel();
        private readonly WrapPanel ModePanel = new WrapPanel();
        private readonly WrapPanel FilterPanel = new WrapPanel();

        private TxAudioPanel _txPanel;

        private sealed class DockedPanel
        {
            public DockFrame Frame;
            public Window Window;
            public bool Redocking;
            public PanelDock DefaultDock;
            public bool DefaultVisible;
            public int DefaultOrder;
        }

        private readonly Dictionary<string, DockedPanel> _panels = new Dictionary<string, DockedPanel>();
        private bool _closingMain;          // the main window is closing: its panel windows close with it, still shown

        public const string BandPanelKey = "bands", ModePanelKey = "modes", FilterPanelKey = "filters", TxPanelKey = "txaudio";
        public const string MeterPanelKey = "meter", TransmitPanelKey = "transmit", TxSettingsPanelKey = "txsettings",
                            ReceivePanelKey = "receive", AgcPanelKey = "agc", NoisePanelKey = "nr", DisplayPanelKey = "tuning";

        private const double DockMinWidth = 200, DockMinHeight = 60;

        /// <summary>Create the frames (the contents exist already); PlacePanels puts them where they were.</summary>
        private void BuildPanels()
        {
            _txPanel = new TxAudioPanel(_radio, _settings);
            _txPanel.Changed += RefreshTx;
            AddPanel(BandPanelKey, "Bands", BandPanel, allowTop: true, compact: true, PanelDock.Top, true, 0);
            AddPanel(ModePanelKey, "Modes", ModePanel, allowTop: true, compact: true, PanelDock.Top, true, 1);
            AddPanel(FilterPanelKey, "Filter width", FilterPanel, allowTop: true, compact: true, PanelDock.Top, true, 2);
            // what was the fixed column on the right: taken out of PanelParts (MainWindow.axaml)
            AddPart(MeterPanelKey, "S-meter", MeterPart, allowTop: true, visible: true, 10);
            AddPart(TransmitPanelKey, "Transmit", TransmitPart, allowTop: false, visible: true, 11);
            AddPart(TxSettingsPanelKey, "Transmit settings", TxSettingsPart, allowTop: false, visible: false, 12);
            AddPart(ReceivePanelKey, "Receive gain", ReceivePart, allowTop: false, visible: true, 13);
            AddPart(AgcPanelKey, "AGC", AgcPart, allowTop: false, visible: true, 14);
            AddPart(NoisePanelKey, "Noise reduction", NoisePart, allowTop: false, visible: true, 15);
            AddPart(DisplayPanelKey, "Tuning and display", DisplayPart, allowTop: false, visible: true, 16);
            AddPanel(TxPanelKey, "Transmit audio", _txPanel, allowTop: false, compact: false, PanelDock.Right, false, 17, scroll: false);
            MigrateTxPanelSettings();
        }

        private void AddPart(string key, string title, Control part, bool allowTop, bool visible, int order)
        {
            PanelParts.Children.Remove(part);
            AddPanel(key, title, part, allowTop, compact: false, PanelDock.Right, visible, order);
        }

        private void AddPanel(string key, string title, Control body, bool allowTop, bool compact, PanelDock dock, bool visible, int order,
                              bool scroll = true)
        {
            var frame = new DockFrame(key, title, body, allowTop, compact, scroll);
            frame.DockRequested += d => MovePanel(key, d);
            frame.CloseRequested += () => ShowPanel(key, false);
            frame.TearOffRequested += at => MovePanel(key, PanelDock.Float, at);
            frame.Resized += size =>
            {
                var l = Layout(key);
                if (frame.Dock == PanelDock.Bottom) l.DockWidth = size;
                else l.DockHeight = size;
            };
            _panels[key] = new DockedPanel { Frame = frame, DefaultDock = dock, DefaultVisible = visible, DefaultOrder = order };
        }

        // the transmit audio panel's settings before the panels were generalised
        private void MigrateTxPanelSettings()
        {
            _settings.Panels ??= new Dictionary<string, PanelLayout>();
            if (_settings.Panels.ContainsKey(TxPanelKey)) return;
            _settings.Panels[TxPanelKey] = new PanelLayout
            {
                Visible = _settings.TxPanelVisible,
                Dock = _settings.TxPanelDock == PanelDock.Top ? PanelDock.Right : _settings.TxPanelDock,
                Order = 17,
                X = _settings.TxPanelX,
                Y = _settings.TxPanelY,
                Width = _settings.TxPanelFloatWidth,
                Height = _settings.TxPanelFloatHeight,
            };
            _settings.DockLeftWidth = _settings.DockRightWidth = Math.Max(DockMinWidth, _settings.TxPanelWidth);
            _settings.DockBottomHeight = Math.Max(DockMinHeight, _settings.TxPanelHeight);
        }

        private PanelLayout Layout(string key)
        {
            _settings.Panels ??= new Dictionary<string, PanelLayout>();
            if (!_settings.Panels.TryGetValue(key, out var l) || l == null)
            {
                var p = _panels[key];
                l = new PanelLayout { Visible = p.DefaultVisible, Dock = p.DefaultDock, Order = p.DefaultOrder };
                _settings.Panels[key] = l;
            }
            if (l.Dock == PanelDock.Top && !_panels[key].Frame.AllowTop) l.Dock = _panels[key].DefaultDock;
            return l;
        }

        /// <summary>Put every panel where the settings say (at start-up, and after a layout reset).</summary>
        private void PlacePanels()
        {
            foreach (var key in _panels.Keys.OrderBy(k => Layout(k).Order))
            {
                var l = Layout(key);
                if (l.Visible) Place(key, l.Dock, null);
                else Detach(key, false);
            }
            UpdateDockAreas();
            SyncPanelToggles();
        }

        private bool IsPanelShown(string key) => _panels.TryGetValue(key, out var p) && p.Frame.Parent != null;

        /// <summary>Show or hide a panel, where it was last.</summary>
        private void ShowPanel(string key, bool show)
        {
            if (show == IsPanelShown(key)) { SyncPanelToggles(); return; }
            RememberPanelLayout();
            var l = Layout(key);
            l.Visible = show;
            if (show) Place(key, l.Dock, null);
            else Detach(key, false);
            UpdateDockAreas();
            SyncPanelToggles();
        }

        /// <summary>Move a panel (showing it if it was hidden); 'at' places a new window at a screen point.</summary>
        private void MovePanel(string key, PanelDock dock, PixelPoint? at = null)
        {
            if (dock == PanelDock.Top && !_panels[key].Frame.AllowTop) return;
            RememberPanelLayout();
            var l = Layout(key);
            if (l.Dock != dock)
                l.Order = _settings.Panels.Values.Where(x => x.Dock == dock).Select(x => x.Order).DefaultIfEmpty(0).Max() + 1;
            l.Dock = dock;
            l.Visible = true;
            Place(key, dock, at);
            UpdateDockAreas();
            SyncPanelToggles();
        }

        /// <summary>Everything back to the defaults: buttons at the top, the rest on the right, transmit audio hidden.</summary>
        private void ResetPanelLayout()
        {
            foreach (var key in _panels.Keys) Detach(key, false);
            _settings.Panels.Clear();
            _settings.DockLeftWidth = _settings.DockRightWidth = 330;
            _settings.DockBottomHeight = 330;
            PlacePanels();
        }

        private StackPanel Area(PanelDock dock) => dock switch
        {
            PanelDock.Top => TopDock,
            PanelDock.Left => LeftDock,
            PanelDock.Right => RightDock,
            PanelDock.Bottom => BottomDock,
            _ => null,
        };

        private void Place(string key, PanelDock dock, PixelPoint? at)
        {
            var p = _panels[key];
            var l = Layout(key);
            Detach(key, keepWindow: dock == PanelDock.Float && at == null);
            p.Frame.Dock = dock;
            p.Frame.SetDockedSize(dock == PanelDock.Bottom ? l.DockWidth : l.DockHeight);
            if (p.Frame.Body == _txPanel) _txPanel.Refresh();

            var area = Area(dock);
            if (area != null)
            {
                // keep the panels of a place in their saved order
                int index = 0;
                foreach (var child in area.Children)
                {
                    if (child is DockFrame f && Layout(f.Key).Order <= l.Order) index++;
                }
                area.Children.Insert(Math.Min(index, area.Children.Count), p.Frame);
                return;
            }

            // its own window
            if (p.Window == null)
            {
                var win = new Window
                {
                    Title = p.Frame.Title + " - Thetis",
                    Icon = Icon,
                    MinWidth = 160,
                    MinHeight = 60,
                    Background = new SolidColorBrush(Color.Parse("#12171C")),
                    ShowInTaskbar = false,
                };
                if (l.Width > 0 && l.Height > 0) { win.Width = l.Width; win.Height = l.Height; }
                else win.SizeToContent = SizeToContent.WidthAndHeight;
                if (at is PixelPoint pt) win.Position = new PixelPoint(pt.X - 60, pt.Y - 12);
                else if (l.X is int x && l.Y is int y) win.Position = new PixelPoint(x, y);
                else win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Closing += (_, e) =>
                {
                    if (p.Redocking) return;
                    var lay = Layout(key);
                    lay.X = win.Position.X;
                    lay.Y = win.Position.Y;
                    lay.Width = Math.Round(win.Bounds.Width);
                    lay.Height = Math.Round(win.Bounds.Height);
                    // closed with the main window: still shown next time
                    if (_closingMain || e.CloseReason == WindowCloseReason.OwnerWindowClosing ||
                        e.CloseReason == WindowCloseReason.ApplicationShutdown) return;
                    lay.Visible = false;
                    SyncPanelToggles();
                };
                win.Closed += (_, _) =>
                {
                    win.Content = null;
                    if (p.Window == win) p.Window = null;
                };
                win.Opened += (_, _) =>
                {
                    // fitted to its contents once; resizable from then on
                    if (win.SizeToContent != SizeToContent.Manual) win.SizeToContent = SizeToContent.Manual;
                };
                p.Window = win;
                win.Content = p.Frame;
                win.Show(this);
            }
            else
            {
                p.Window.Content = p.Frame;
                p.Window.Activate();
            }
        }

        /// <summary>Take a panel out of wherever it is.</summary>
        private void Detach(string key, bool keepWindow)
        {
            var p = _panels[key];
            foreach (var area in new[] { TopDock, LeftDock, RightDock, BottomDock }) area.Children.Remove(p.Frame);
            if (p.Window != null && !keepWindow)
            {
                var win = p.Window;
                p.Window = null;
                win.Content = null;
                p.Redocking = true;
                try { win.Close(); }
                finally { p.Redocking = false; }
            }
        }

        /// <summary>Open the docking places that have panels, close the others.</summary>
        private void UpdateDockAreas()
        {
            var cols = WorkArea.ColumnDefinitions;
            var rows = WorkArea.RowDefinitions;
            bool left = LeftDock.Children.Count > 0, right = RightDock.Children.Count > 0, bottom = BottomDock.Children.Count > 0;
            cols[0].MinWidth = left ? DockMinWidth : 0;
            cols[0].Width = new GridLength(left ? Math.Max(DockMinWidth, _settings.DockLeftWidth) : 0);
            LeftSplitter.IsVisible = left;
            cols[4].MinWidth = right ? DockMinWidth : 0;
            cols[4].Width = new GridLength(right ? Math.Max(DockMinWidth, _settings.DockRightWidth) : 0);
            RightSplitter.IsVisible = right;
            // button panels alone: the bottom place fits them; a larger panel there takes the saved height
            bool bottomSized = BottomDock.Children.OfType<DockFrame>().Any(f => !f.Compact);
            rows[2].MinHeight = bottomSized ? DockMinHeight : 0;
            rows[2].Height = !bottom ? new GridLength(0)
                           : bottomSized ? new GridLength(Math.Max(DockMinHeight, _settings.DockBottomHeight))
                           : GridLength.Auto;
            BottomSplitter.IsVisible = bottomSized;
        }

        /// <summary>Keep the docking places' sizes and the panel windows' positions for next time.</summary>
        private void RememberPanelLayout()
        {
            var cols = WorkArea.ColumnDefinitions;
            if (LeftDock.Children.Count > 0 && cols[0].ActualWidth >= DockMinWidth) _settings.DockLeftWidth = Math.Round(cols[0].ActualWidth);
            if (RightDock.Children.Count > 0 && cols[4].ActualWidth >= DockMinWidth) _settings.DockRightWidth = Math.Round(cols[4].ActualWidth);
            var row = WorkArea.RowDefinitions[2];
            if (BottomDock.Children.OfType<DockFrame>().Any(f => !f.Compact) && row.ActualHeight >= DockMinHeight)
                _settings.DockBottomHeight = Math.Round(row.ActualHeight);
            foreach (var (key, p) in _panels)
            {
                if (p.Window == null) continue;
                var l = Layout(key);
                l.X = p.Window.Position.X;
                l.Y = p.Window.Position.Y;
                l.Width = Math.Round(p.Window.Bounds.Width);
                l.Height = Math.Round(p.Window.Bounds.Height);
            }
        }

        private void SyncPanelToggles()
        {
            _updating = true;
            TxPanelToggle.IsChecked = IsPanelShown(TxPanelKey);
            _updating = false;
        }

        /// <summary>Show a panel and scroll the docking place to it.</summary>
        private void RevealPanel(string key)
        {
            ShowPanel(key, true);
            var p = _panels[key];
            if (p.Window != null) p.Window.Activate();
            else Dispatcher.UIThread.Post(() => p.Frame.BringIntoView());
        }

        /// <summary>View menu: each panel with show / hide and where it goes.</summary>
        private List<Control> PanelMenus()
        {
            var items = new List<Control>();
            foreach (var (key, p) in _panels.OrderBy(kv => kv.Value.DefaultOrder))
            {
                var l = Layout(key);
                bool shown = IsPanelShown(key);
                string gesture = key == TxPanelKey ? "  (Ctrl+Shift+A)" : "";
                var sub = new List<Control> { Check("_Show" + gesture, shown, () => ShowPanel(key, !shown)), new Separator() };
                if (p.Frame.AllowTop) sub.Add(Radio("_Top, next to the VFO", shown && l.Dock == PanelDock.Top, () => MovePanel(key, PanelDock.Top)));
                sub.Add(Radio("_Left", shown && l.Dock == PanelDock.Left, () => MovePanel(key, PanelDock.Left)));
                sub.Add(Radio("_Right", shown && l.Dock == PanelDock.Right, () => MovePanel(key, PanelDock.Right)));
                sub.Add(Radio("_Bottom", shown && l.Dock == PanelDock.Bottom, () => MovePanel(key, PanelDock.Bottom)));
                sub.Add(Radio("_Own window", shown && l.Dock == PanelDock.Float, () => MovePanel(key, PanelDock.Float)));
                sub.Add(new Separator());
                sub.Add(Item("_Fit to the contents", () => { l.DockHeight = l.DockWidth = null; p.Frame.SetDockedSize(null); },
                             enabled: shown && p.Frame.Dock != PanelDock.Float && (l.DockHeight != null || l.DockWidth != null)));
                items.Add(Sub(p.Frame.Title, sub));
            }
            items.Add(new Separator());
            items.Add(Item("_Reset the panel layout", ResetPanelLayout));
            return items;
        }
    }
}
