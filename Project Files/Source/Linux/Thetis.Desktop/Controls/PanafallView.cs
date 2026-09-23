/*  PanafallView.cs

This file is part of a program that implements a Software-Defined Radio.

Panadapter (spectrum) above a scrolling waterfall, fed with the dBm-per-pixel
frames produced by the wdsp analyzer.  Replaces the Direct2D renderer of
the Windows console (display.cs) for the Linux front end.

Mouse: click to tune, wheel to step-tune, Ctrl+wheel to move the dB scale,
Shift+wheel to change the dB range, drag the divider to resize.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Thetis.Desktop.Controls
{
    public sealed class PanafallView : Control
    {
        // --- what is displayed (set by the window) ---
        public long CenterHz { get; set; } = 7100000;
        public int SpanLowHz { get; set; } = -24000;
        public int SpanHighHz { get; set; } = 24000;
        public int FilterLowHz { get; set; } = -2800;
        public int FilterHighHz { get; set; } = -100;
        public double MaxDbm { get; set; } = -40;
        public double MinDbm { get; set; } = -140;
        public double PanFraction { get; set; } = 0.45;
        public bool Active { get; set; }

        /// <summary>Click-to-tune: new VFO frequency in Hz.</summary>
        public event Action<long> TuneTo;
        /// <summary>Wheel: number of tuning steps (+ up, - down).</summary>
        public event Action<int> TuneSteps;
        /// <summary>The dB scale or panadapter/waterfall split was changed by the user.</summary>
        public event Action ViewChanged;

        private float[] _pan = Array.Empty<float>();
        private WriteableBitmap _wf;
        private int _wfWidth, _wfHeight;
        private uint[] _wfRow = Array.Empty<uint>();
        private static readonly uint[] _palette = BuildPalette();
        private bool _draggingSplit;

        private static readonly IBrush _bg = new SolidColorBrush(Color.Parse("#0B0F14"));
        private static readonly IPen _grid = new Pen(new SolidColorBrush(Color.Parse("#1F2A35")), 1);
        private static readonly IBrush _label = new SolidColorBrush(Color.Parse("#7F8C99"));
        private static readonly IBrush _passband = new SolidColorBrush(Color.FromArgb(48, 120, 170, 255));
        private static readonly IPen _vfo = new Pen(new SolidColorBrush(Color.Parse("#FF5252")), 1);
        private static readonly IPen _trace = new Pen(new SolidColorBrush(Color.Parse("#FFE082")), 1.2);
        private static readonly IBrush _fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(110, 255, 214, 102), 0), new GradientStop(Color.FromArgb(10, 255, 214, 102), 1) },
        };
        private static readonly Typeface _font = new Typeface("Inter");

        public PanafallView()
        {
            ClipToBounds = true;
            Focusable = true;
        }

        private double PanHeight => Math.Round(Bounds.Height * Math.Clamp(PanFraction, 0.15, 0.9));

        /// <summary>Number of spectrum pixels the analyzer should produce (the drawing width).</summary>
        public int DesiredPixels => Math.Max(64, (int)Bounds.Width);

        public void PushPanadapter(float[] dbm)
        {
            if (_pan.Length != dbm.Length) _pan = new float[dbm.Length];
            Array.Copy(dbm, _pan, dbm.Length);
            InvalidateVisual();
        }

        public void PushWaterfall(float[] dbm)
        {
            int h = Math.Max(1, (int)(Bounds.Height - PanHeight));
            int w = dbm.Length;
            if (_wf == null || _wfWidth != w || _wfHeight != h)
            {
                _wf?.Dispose();
                _wf = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _wfWidth = w;
                _wfHeight = h;
                _wfRow = new uint[w];
                using var fb0 = _wf.Lock();
                unsafe { new Span<byte>((void*)fb0.Address, fb0.RowBytes * h).Fill(0); }
            }
            double range = Math.Max(1.0, MaxDbm - MinDbm);
            for (int i = 0; i < w; i++)
            {
                double t = (dbm[i] - MinDbm) / range;
                int idx = (int)(Math.Clamp(t, 0.0, 1.0) * (_palette.Length - 1));
                _wfRow[i] = _palette[idx];
            }
            using (var fb = _wf.Lock())
            {
                unsafe
                {
                    byte* p = (byte*)fb.Address;
                    int stride = fb.RowBytes;
                    // scroll down one row, newest line on top
                    Buffer.MemoryCopy(p, p + stride, (long)stride * (h - 1), (long)stride * (h - 1));
                    fixed (uint* src = _wfRow)
                        Buffer.MemoryCopy(src, p, stride, w * 4);
                }
            }
            InvalidateVisual();
        }

        public void ClearWaterfall()
        {
            _wf?.Dispose();
            _wf = null;
            InvalidateVisual();
        }

        public void Clear()
        {
            _pan = Array.Empty<float>();
            _wf?.Dispose();
            _wf = null;
            InvalidateVisual();
        }

        private double XForOffset(double hz, double width) =>
            (hz - SpanLowHz) / Math.Max(1.0, SpanHighHz - SpanLowHz) * width;

        private double YForDbm(double dbm, double h) =>
            (MaxDbm - dbm) / Math.Max(1.0, MaxDbm - MinDbm) * h;

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height, ph = PanHeight;
            ctx.FillRectangle(_bg, new Rect(0, 0, w, h));

            // passband
            double fx0 = XForOffset(FilterLowHz, w), fx1 = XForOffset(FilterHighHz, w);
            ctx.FillRectangle(_passband, new Rect(Math.Min(fx0, fx1), 0, Math.Abs(fx1 - fx0), ph));

            // horizontal dB grid
            double step = NiceStep(MaxDbm - MinDbm, 8);
            for (double db = Math.Ceiling(MinDbm / step) * step; db <= MaxDbm; db += step)
            {
                double y = YForDbm(db, ph);
                ctx.DrawLine(_grid, new Point(0, y), new Point(w, y));
                DrawLabel(ctx, db.ToString("0", CultureInfo.InvariantCulture), new Point(3, y - 13));
            }

            // vertical frequency grid (absolute kHz)
            double span = SpanHighHz - SpanLowHz;
            double fstep = NiceStep(span, Math.Max(2, w / 110));
            double first = Math.Ceiling((CenterHz + SpanLowHz) / fstep) * fstep;
            for (double f = first; f <= CenterHz + SpanHighHz; f += fstep)
            {
                double x = XForOffset(f - CenterHz, w);
                ctx.DrawLine(_grid, new Point(x, 0), new Point(x, ph));
                string txt = fstep >= 1000
                    ? (f / 1e6).ToString(fstep >= 100000 ? "0.0" : fstep >= 10000 ? "0.00" : "0.000", CultureInfo.InvariantCulture)
                    : (f / 1e6).ToString("0.0000", CultureInfo.InvariantCulture);
                DrawLabel(ctx, txt, new Point(x + 2, ph - 15));
            }

            // spectrum trace
            if (_pan.Length > 1 && Active)
            {
                var geo = new StreamGeometry();
                var fillGeo = new StreamGeometry();
                using (var g = geo.Open())
                using (var fg = fillGeo.Open())
                {
                    double dx = w / (_pan.Length - 1);
                    fg.BeginFigure(new Point(0, ph), true);
                    for (int i = 0; i < _pan.Length; i++)
                    {
                        double y = Math.Clamp(YForDbm(_pan[i], ph), 0, ph);
                        var pt = new Point(i * dx, y);
                        if (i == 0) g.BeginFigure(pt, false); else g.LineTo(pt);
                        fg.LineTo(pt);
                    }
                    fg.LineTo(new Point(w, ph));
                    fg.EndFigure(true);
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(_fill, null, fillGeo);
                ctx.DrawGeometry(null, _trace, geo);
            }

            // VFO marker
            double vx = XForOffset(0, w);
            ctx.DrawLine(_vfo, new Point(vx, 0), new Point(vx, ph));

            // waterfall
            if (_wf != null)
                ctx.DrawImage(_wf, new Rect(0, 0, _wfWidth, _wfHeight), new Rect(0, ph, w, h - ph));
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#34414E")), 2), new Point(0, ph), new Point(w, ph));
        }

        private static void DrawLabel(DrawingContext ctx, string text, Point p)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _font, 10, _label);
            ctx.DrawText(ft, p);
        }

        private static double NiceStep(double range, double divisions)
        {
            double raw = range / Math.Max(1.0, divisions);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var p = e.GetPosition(this);
            if (Math.Abs(p.Y - PanHeight) < 5) { _draggingSplit = true; e.Pointer.Capture(this); return; }
            if (!Active) return;
            double hz = SpanLowHz + p.X / Math.Max(1.0, Bounds.Width) * (SpanHighHz - SpanLowHz);
            TuneTo?.Invoke(CenterHz + (long)Math.Round(hz));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            Cursor = Math.Abs(p.Y - PanHeight) < 5 || _draggingSplit ? new Cursor(StandardCursorType.SizeNorthSouth) : Cursor.Default;
            if (_draggingSplit)
            {
                PanFraction = Math.Clamp(p.Y / Math.Max(1.0, Bounds.Height), 0.15, 0.9);
                _wf?.Dispose(); _wf = null;
                InvalidateVisual();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_draggingSplit) { _draggingSplit = false; e.Pointer.Capture(null); ViewChanged?.Invoke(); }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            int dir = e.Delta.Y > 0 ? 1 : -1;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                MaxDbm += 5 * dir; MinDbm += 5 * dir;
                ViewChanged?.Invoke(); InvalidateVisual();
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                MinDbm = Math.Min(MaxDbm - 20, MinDbm - 5 * dir);
                ViewChanged?.Invoke(); InvalidateVisual();
            }
            else TuneSteps?.Invoke(dir);
            e.Handled = true;
        }

        /// <summary>Classic SDR waterfall colours: black, blue, cyan, green, yellow, red, white.</summary>
        private static uint[] BuildPalette()
        {
            (double t, byte r, byte g, byte b)[] stops =
            {
                (0.00, 0, 0, 0), (0.20, 0, 0, 140), (0.40, 0, 150, 220), (0.55, 0, 200, 80),
                (0.70, 240, 230, 0), (0.85, 255, 60, 0), (1.00, 255, 255, 255),
            };
            var pal = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                int s = 0;
                while (s < stops.Length - 2 && t > stops[s + 1].t) s++;
                var (t0, r0, g0, b0) = stops[s];
                var (t1, r1, g1, b1) = stops[s + 1];
                double u = (t - t0) / (t1 - t0);
                byte r = (byte)(r0 + (r1 - r0) * u), g = (byte)(g0 + (g1 - g0) * u), b = (byte)(b0 + (b1 - b0) * u);
                pal[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;     // BGRA in memory
            }
            return pal;
        }
    }
}
