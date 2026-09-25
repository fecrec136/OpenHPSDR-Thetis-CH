/*  MainWindow.BandLimits.cs

This file is part of a program that implements a Software-Defined Radio.

Band limits of the chosen region: red dashed lines on the panadapter at the
edges of each amateur allocation (the ones that limit transmitting), a beep
when the VFO is tuned out of a band, and a message box when transmitting is
refused because the signal would be outside the bands.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Thetis.Audio;
using Thetis.Radio;

namespace Thetis.Desktop
{
    public partial class MainWindow
    {
        private Window _outOfBandAlert;

        /// <summary>The panadapter's red lines follow the region (none until a region is chosen).</summary>
        private void RefreshBandLimits()
        {
            Panafall.BandLimits = BandPlanRegions.Allocations(_settings.Region)
                .Select(a => ((long)Math.Round(a.lo * 1e6), (long)Math.Round(a.hi * 1e6)))
                .ToList();
            Panafall.InvalidateVisual();
        }

        /// <summary>Beep when a retune takes the VFO from inside a band to outside it.</summary>
        private void CheckBandLimitCrossing(double oldMHz, double newMHz)
        {
            if (!_settings.BandLimitBeep || _settings.Region == TxRegion.None) return;
            if (BandPlanRegions.InBand(_settings.Region, oldMHz) && !BandPlanRegions.InBand(_settings.Region, newMHz))
                Beeper.Beep();
        }

        /// <summary>Transmitting refused outside the bands: say so in a message box (one at a time).</summary>
        private void ShowOutOfBandAlert(string message)
        {
            if (_outOfBandAlert != null) { _outOfBandAlert.Activate(); return; }
            var w = new Window
            {
                Title = "Transmit not allowed",
                Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#12171C")),
                ShowInTaskbar = false,
            };
            var text = new StackPanel { Spacing = 6 };
            text.Children.Add(new TextBlock { Text = "Outside the band limits", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#EF5350")) });
            text.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock
            {
                Text = $"The red lines on the panadapter mark the bands for {BandPlanRegions.RegionName(_settings.Region)} (Transmit settings > Region).",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#9AA4AE")),
            });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(16) };
            row.Children.Add(new TextBlock { Text = "⚠", FontSize = 30, Foreground = new SolidColorBrush(Color.Parse("#FFB74D")), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var ok = new Button { Content = "OK", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center,
                                  Margin = new Thickness(16, 0, 16, 14), IsDefault = true, IsCancel = true };
            ok.Click += (_, _) => w.Close();
            var panel = new StackPanel();
            panel.Children.Add(row);
            panel.Children.Add(ok);
            w.Content = panel;
            w.Closed += (_, _) => _outOfBandAlert = null;
            _outOfBandAlert = w;
            _ = w.ShowDialog(this);
        }
    }
}
