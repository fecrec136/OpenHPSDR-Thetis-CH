/*  DockFrame.cs

This file is part of a program that implements a Software-Defined Radio.

The frame around a dockable panel (bands, modes, filters, transmit audio):
a title bar with buttons that put the panel at the top (next to the VFO),
left, right or bottom of the main window, or in a window of its own, and a
close button.  Dragging the title bar takes a docked panel out into a
window.  MainWindow.Panels.cs does the placing.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Thetis.Desktop.Controls
{
    /// <summary>Where a panel is: a docking place of the main window, or its own window.</summary>
    public enum PanelDock { Top, Left, Right, Bottom, Float }

    /// <summary>Content that lays itself out differently depending on where it is docked.</summary>
    public interface IDockAware
    {
        void DockChanged(PanelDock dock);
    }

    public sealed class DockFrame : UserControl
    {
        private readonly Dictionary<PanelDock, Button> _buttons = new Dictionary<PanelDock, Button>();
        private readonly Control _body;
        private Point? _dragFrom;
        private PanelDock _dock = PanelDock.Float;

        private static readonly IBrush HeaderBg = new SolidColorBrush(Color.Parse("#161C22"));
        private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA4AE"));

        public string Key { get; }
        public string Title { get; }
        public bool AllowTop { get; }

        /// <summary>A small panel (one or two rows of buttons) that sizes itself.</summary>
        public bool Compact { get; }

        /// <summary>The panel inside the frame.</summary>
        public Control Body => _body;

        /// <summary>The user asked to dock the panel elsewhere, or to float it.</summary>
        public event Action<PanelDock> DockRequested;

        /// <summary>The user closed the panel.</summary>
        public event Action CloseRequested;

        /// <summary>The title bar was dragged away while docked: float the panel at this screen point.</summary>
        public event Action<PixelPoint> TearOffRequested;

        /// <param name="compact">A slim title bar, for small panels such as the band buttons.</param>
        public DockFrame(string key, string title, Control body, bool allowTop, bool compact)
        {
            Key = key;
            Title = title;
            AllowTop = allowTop;
            _body = body;
            Compact = compact;

            double font = compact ? 10.5 : 13;
            var bar = new DockPanel { Background = HeaderBg, Margin = new Thickness(0, 0, 0, compact ? 1 : 2) };
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
            DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
            if (allowTop) buttons.Children.Add(DockButton(PanelDock.Top, "⬒", "Dock at the top, next to the VFO", compact));
            buttons.Children.Add(DockButton(PanelDock.Left, "◧", "Dock on the left", compact));
            buttons.Children.Add(DockButton(PanelDock.Bottom, "⬓", "Dock at the bottom", compact));
            buttons.Children.Add(DockButton(PanelDock.Right, "◨", "Dock on the right", compact));
            buttons.Children.Add(DockButton(PanelDock.Float, "⧉", "Show in a window of its own", compact));
            var close = SmallButton("✕", $"Close (View > Panels > {title} opens it again)", compact);
            close.Click += (_, _) => CloseRequested?.Invoke();
            buttons.Children.Add(close);
            bar.Children.Add(buttons);
            var label = new TextBlock
            {
                Text = title, FontSize = font, FontWeight = compact ? FontWeight.Normal : FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0), Cursor = new Cursor(StandardCursorType.SizeAll),
            };
            if (compact) label.Foreground = Muted;
            ToolTip.SetTip(label, "Drag to take the panel out of the main window");
            bar.Children.Add(label);

            bar.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed && e.Source is not Button) _dragFrom = e.GetPosition(this);
            };
            bar.PointerMoved += (_, e) =>
            {
                if (_dragFrom is not Point from || _dock == PanelDock.Float) return;
                var at = e.GetPosition(this);
                if (Math.Abs(at.X - from.X) + Math.Abs(at.Y - from.Y) < 40) return;
                _dragFrom = null;
                if (TopLevel.GetTopLevel(this) is Window w) TearOffRequested?.Invoke(w.PointToScreen(e.GetPosition(w)));
            };
            bar.PointerReleased += (_, _) => _dragFrom = null;

            var root = new DockPanel();
            root.Children.Add(bar);
            root.Children.Add(body);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#12171C")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2C353E")),
                BorderThickness = new Thickness(compact ? 0 : 1),
                Margin = new Thickness(compact ? 0 : 1, compact ? 1 : 1),
                Child = root,
            };
        }

        /// <summary>Where the panel is now (its own button is greyed out).</summary>
        public PanelDock Dock
        {
            get => _dock;
            set
            {
                _dock = value;
                foreach (var (d, b) in _buttons) b.IsEnabled = d != value;
                (_body as IDockAware)?.DockChanged(value);
            }
        }

        private Button DockButton(PanelDock dock, string glyph, string tip, bool compact)
        {
            var b = SmallButton(glyph, tip, compact);
            b.Click += (_, _) => DockRequested?.Invoke(dock);
            _buttons[dock] = b;
            return b;
        }

        private static Button SmallButton(string glyph, string tip, bool compact)
        {
            var b = new Button
            {
                Content = glyph, Padding = compact ? new Thickness(4, 0) : new Thickness(6, 1), FontSize = compact ? 11 : 13,
                MinWidth = compact ? 20 : 26, MinHeight = compact ? 18 : 0, HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, tip);
            return b;
        }
    }
}
