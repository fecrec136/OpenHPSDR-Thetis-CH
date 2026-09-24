/*  PsCurveView.cs

This file is part of a program that implements a Software-Defined Radio.

PureSignal's correction curves (WDSP 2.10's GetPSDisp2): the amplitude
correction and the phase correction against the drive level, as the
console's AmpView shows them.

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
    public sealed class PsCurveView : Control
    {
        private double[] _ax = Array.Empty<double>(), _ay = Array.Empty<double>(), _px = Array.Empty<double>(), _py = Array.Empty<double>();

        private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#0B0F13"));
        private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.Parse("#26313B")), 1);
        private static readonly IPen Amp = new Pen(new SolidColorBrush(Color.Parse("#66BB6A")), 1.5);
        private static readonly IPen Phase = new Pen(new SolidColorBrush(Color.Parse("#FFB74D")), 1.5);
        private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#9AA4AE"));

        /// <summary>New curves (512 points each); null clears the plot.</summary>
        public void SetCurves(double[] ampX, double[] ampY, double[] phaseX, double[] phaseY)
        {
            _ax = ampX ?? Array.Empty<double>();
            _ay = ampY ?? Array.Empty<double>();
            _px = phaseX ?? Array.Empty<double>();
            _py = phaseY ?? Array.Empty<double>();
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            ctx.FillRectangle(Bg, new Rect(0, 0, w, h));
            for (int i = 1; i < 4; i++)
            {
                ctx.DrawLine(Grid, new Point(w * i / 4, 0), new Point(w * i / 4, h));
                ctx.DrawLine(Grid, new Point(0, h * i / 4), new Point(w, h * i / 4));
            }
            if (_ax.Length == 0)
            {
                Text(ctx, "No correction yet: transmit with PS-A on (the two-tone signal is ideal)", new Point(8, h / 2 - 8));
                return;
            }
            Plot(ctx, _ax, _ay, Amp, w, h, out double aMin, out double aMax);
            Plot(ctx, _px, _py, Phase, w, h, out double pMin, out double pMax);
            Text(ctx, $"amplitude correction {aMin:0.00} .. {aMax:0.00}", new Point(6, 4), Amp.Brush);
            Text(ctx, $"phase correction {pMin:0.0} .. {pMax:0.0} deg", new Point(6, 20), Phase.Brush);
            Text(ctx, "drive level 0 .. 1", new Point(w - 110, h - 18));
        }

        private static void Plot(DrawingContext ctx, double[] x, double[] y, IPen pen, double w, double h, out double min, out double max)
        {
            min = double.MaxValue; max = double.MinValue;
            int n = Math.Min(x.Length, y.Length);
            for (int i = 0; i < n; i++) { if (y[i] < min) min = y[i]; if (y[i] > max) max = y[i]; }
            if (n < 2) return;
            double span = Math.Max(max - min, 1e-6), lo = min - 0.1 * span, hi = max + 0.1 * span;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (int i = 0; i < n; i++)
                {
                    var p = new Point(Math.Clamp(x[i], 0, 1) * w, h - (y[i] - lo) / (hi - lo) * h);
                    if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
                }
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, geo);
        }

        private static void Text(DrawingContext ctx, string s, Point at, IBrush brush = null) =>
            ctx.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush ?? Label), at);
    }
}
