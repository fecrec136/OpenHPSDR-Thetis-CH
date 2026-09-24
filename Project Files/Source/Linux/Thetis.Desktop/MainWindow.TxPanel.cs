/*  MainWindow.TxPanel.cs

This file is part of a program that implements a Software-Defined Radio.

Docking the transmit audio panel (Controls/TxAudioPanel.cs): at the left or
right of the panadapter, below it, or in a window of its own.  The splitters
resize a docked panel; dragging its title bar takes it out into a window,
and the buttons in its title bar put it back.  The place and sizes are saved
with the settings.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Thetis.Desktop.Controls;

namespace Thetis.Desktop
{
    public partial class MainWindow
    {
        private TxAudioPanel _txPanel;
        private Window _txWindow;
        private bool _txWindowRedocking;

        private const double TxPanelMinWidth = 240, TxPanelMinHeight = 150;

        /// <summary>Show or hide the transmit audio panel, where it was last.</summary>
        private void ShowTxPanel(bool show)
        {
            if (show == (_txPanel?.Parent != null)) { SyncTxPanelToggle(show); return; }
            if (!show) RememberTxPanelLayout();
            _settings.TxPanelVisible = show;
            if (show) PlaceTxPanel(_settings.TxPanelDock, null);
            else DetachTxPanel();
            SyncTxPanelToggle(show);
        }

        /// <summary>Move the panel (showing it if it was hidden); 'at' places a new window at a screen point.</summary>
        private void DockTxPanel(TxPanelDock dock, PixelPoint? at = null)
        {
            RememberTxPanelLayout();
            _settings.TxPanelDock = dock;
            _settings.TxPanelVisible = true;
            PlaceTxPanel(dock, at);
            SyncTxPanelToggle(true);
        }

        private void SyncTxPanelToggle(bool on)
        {
            _updating = true;
            TxPanelToggle.IsChecked = on;
            _updating = false;
        }

        private TxAudioPanel TxPanel()
        {
            if (_txPanel != null) return _txPanel;
            _txPanel = new TxAudioPanel(_radio, _settings);
            _txPanel.DockRequested += d => DockTxPanel(d);
            _txPanel.CloseRequested += () => ShowTxPanel(false);
            _txPanel.Changed += RefreshTx;
            _txPanel.TearOffRequested += at => DockTxPanel(TxPanelDock.Float, at);
            return _txPanel;
        }

        private void PlaceTxPanel(TxPanelDock dock, PixelPoint? at)
        {
            var panel = TxPanel();
            DetachTxPanel(keepWindow: dock == TxPanelDock.Float && at == null);
            panel.Dock = dock;
            panel.Refresh();
            var cols = WorkArea.ColumnDefinitions;
            var rows = WorkArea.RowDefinitions;
            double w = Math.Max(TxPanelMinWidth, _settings.TxPanelWidth);
            double h = Math.Max(TxPanelMinHeight, _settings.TxPanelHeight);
            switch (dock)
            {
                case TxPanelDock.Left:
                    cols[0].Width = new GridLength(w);
                    cols[0].MinWidth = TxPanelMinWidth;
                    LeftDock.Content = panel;
                    LeftSplitter.IsVisible = true;
                    break;
                case TxPanelDock.Right:
                    cols[4].Width = new GridLength(w);
                    cols[4].MinWidth = TxPanelMinWidth;
                    RightDock.Content = panel;
                    RightSplitter.IsVisible = true;
                    break;
                case TxPanelDock.Bottom:
                    rows[2].Height = new GridLength(h);
                    rows[2].MinHeight = TxPanelMinHeight;
                    BottomDock.Content = panel;
                    BottomSplitter.IsVisible = true;
                    break;
                case TxPanelDock.Float:
                    if (_txWindow == null)
                    {
                        _txWindow = new Window
                        {
                            Title = "Transmit audio - Thetis",
                            Icon = Icon,
                            Width = Math.Max(TxPanelMinWidth, _settings.TxPanelFloatWidth),
                            Height = Math.Max(TxPanelMinHeight, _settings.TxPanelFloatHeight),
                            MinWidth = TxPanelMinWidth,
                            MinHeight = TxPanelMinHeight,
                            Background = new SolidColorBrush(Color.Parse("#12171C")),
                            ShowInTaskbar = false,
                        };
                        if (at is PixelPoint p)
                            _txWindow.Position = new PixelPoint(p.X - 60, p.Y - 12);
                        else if (_settings.TxPanelX is int x && _settings.TxPanelY is int y)
                            _txWindow.Position = new PixelPoint(x, y);
                        else
                            _txWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                        var win = _txWindow;
                        win.Closing += (_, _) =>
                        {
                            if (_txWindowRedocking) return;
                            RememberTxPanelLayout();
                            _settings.TxPanelVisible = false;
                            SyncTxPanelToggle(false);
                        };
                        win.Closed += (_, _) =>
                        {
                            win.Content = null;
                            if (_txWindow == win) _txWindow = null;
                        };
                        _txWindow.Content = panel;
                        _txWindow.Show(this);
                    }
                    else
                    {
                        _txWindow.Content = panel;
                        _txWindow.Activate();
                    }
                    break;
            }
        }

        /// <summary>Take the panel out of wherever it is; the docking places collapse.</summary>
        private void DetachTxPanel(bool keepWindow = false)
        {
            LeftDock.Content = RightDock.Content = BottomDock.Content = null;
            LeftSplitter.IsVisible = RightSplitter.IsVisible = BottomSplitter.IsVisible = false;
            var cols = WorkArea.ColumnDefinitions;
            cols[0].MinWidth = cols[4].MinWidth = 0;
            cols[0].Width = cols[4].Width = new GridLength(0);
            WorkArea.RowDefinitions[2].MinHeight = 0;
            WorkArea.RowDefinitions[2].Height = new GridLength(0);
            if (_txWindow != null && !keepWindow)
            {
                var win = _txWindow;
                _txWindow = null;
                win.Content = null;
                _txWindowRedocking = true;
                try { win.Close(); }
                finally { _txWindowRedocking = false; }
            }
        }

        /// <summary>Keep the panel's current size (and window position) for next time.</summary>
        private void RememberTxPanelLayout()
        {
            if (_txPanel?.Parent == null) return;
            var cols = WorkArea.ColumnDefinitions;
            if (LeftDock.Content != null && cols[0].ActualWidth >= TxPanelMinWidth) _settings.TxPanelWidth = Math.Round(cols[0].ActualWidth);
            else if (RightDock.Content != null && cols[4].ActualWidth >= TxPanelMinWidth) _settings.TxPanelWidth = Math.Round(cols[4].ActualWidth);
            else if (BottomDock.Content != null && WorkArea.RowDefinitions[2].ActualHeight >= TxPanelMinHeight)
                _settings.TxPanelHeight = Math.Round(WorkArea.RowDefinitions[2].ActualHeight);
            else if (_txWindow != null)
            {
                _settings.TxPanelX = _txWindow.Position.X;
                _settings.TxPanelY = _txWindow.Position.Y;
                _settings.TxPanelFloatWidth = Math.Round(_txWindow.Width);
                _settings.TxPanelFloatHeight = Math.Round(_txWindow.Height);
            }
        }
    }
}
