/*  SMeter.cs

This file is part of a program that implements a Software-Defined Radio.

Bar S-meter: S1..S9 (6 dB per unit, S9 = -73 dBm) then +10..+60 dB,
with a slowly decaying peak marker.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Thetis.Desktop.Controls
{
    public sealed class SMeter : Control
    {
        private const double MinDbm = -127;      // S0
        private const double S9Dbm = -73;
        private const double MaxDbm = -13;       // S9+60
        private double _value = MinDbm;
        private double _peak = MinDbm;
        private DateTime _peakTime;

        private static readonly Typeface _font = new Typeface("Inter");
        private static readonly IBrush _bg = new SolidColorBrush(Color.Parse("#0B0F14"));
        private static readonly IBrush _low = new SolidColorBrush(Color.Parse("#43A047"));
        private static readonly IBrush _high = new SolidColorBrush(Color.Parse("#E53935"));
        private static readonly IBrush _scale = new SolidColorBrush(Color.Parse("#9AA4AE"));
        private static readonly IPen _tick = new Pen(new SolidColorBrush(Color.Parse("#56616C")), 1);
        private static readonly IPen _peakPen = new Pen(Brushes.White, 2);

        public double Dbm => _value;

        public void Update(double dbm)
        {
            // fast attack, slower release, like an analogue meter
            _value = dbm > _value ? dbm : _value + (dbm - _value) * 0.3;
            if (dbm >= _peak || (DateTime.UtcNow - _peakTime).TotalSeconds > 1.5)
            {
                _peak = Math.Max(dbm, _peak - 1.0);
                if (dbm >= _peak) _peakTime = DateTime.UtcNow;
            }
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize) => new Size(260, 46);

        private double X(double dbm, double w) =>
            Math.Clamp((dbm - MinDbm) / (MaxDbm - MinDbm), 0, 1) * w;

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            ctx.FillRectangle(_bg, new Rect(0, 0, w, h), 3);
            double barTop = 18, barH = h - barTop - 6;
            double xs9 = X(S9Dbm, w);
            double xv = X(_value, w);
            ctx.FillRectangle(_low, new Rect(0, barTop, Math.Min(xv, xs9), barH));
            if (xv > xs9) ctx.FillRectangle(_high, new Rect(xs9, barTop, xv - xs9, barH));
            double xp = X(_peak, w);
            ctx.DrawLine(_peakPen, new Point(xp, barTop), new Point(xp, barTop + barH));

            for (int s = 1; s <= 9; s += 2) Tick(ctx, w, MinDbm + 6 * s, s.ToString(CultureInfo.InvariantCulture));
            for (int p = 20; p <= 60; p += 20) Tick(ctx, w, S9Dbm + p, "+" + p);
        }

        private void Tick(DrawingContext ctx, double w, double dbm, string label)
        {
            double x = X(dbm, w);
            ctx.DrawLine(_tick, new Point(x, 14), new Point(x, 18));
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _font, 10,
                                       dbm > S9Dbm ? _high : _scale);
            ctx.DrawText(ft, new Point(x - ft.Width / 2, 1));
        }
    }
}
