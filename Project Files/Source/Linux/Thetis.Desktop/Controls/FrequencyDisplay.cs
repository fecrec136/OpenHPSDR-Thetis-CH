/*  FrequencyDisplay.cs

This file is part of a program that implements a Software-Defined Radio.

Large VFO readout, "14.200.000".  Turning the mouse wheel over a digit
tunes by that digit's place value (as the Windows console does); a
double-click raises EditRequested so the window can offer direct entry.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Thetis.Desktop.Controls
{
    public sealed class FrequencyDisplay : Control
    {
        public static readonly StyledProperty<long> FrequencyHzProperty =
            AvaloniaProperty.Register<FrequencyDisplay, long>(nameof(FrequencyHz), 7100000);

        public static readonly StyledProperty<bool> ActiveProperty =
            AvaloniaProperty.Register<FrequencyDisplay, bool>(nameof(Active), false);

        static FrequencyDisplay()
        {
            AffectsRender<FrequencyDisplay>(FrequencyHzProperty, ActiveProperty);
        }

        public long FrequencyHz
        {
            get => GetValue(FrequencyHzProperty);
            set => SetValue(FrequencyHzProperty, value);
        }

        /// <summary>Radio running: digits are drawn bright.</summary>
        public bool Active
        {
            get => GetValue(ActiveProperty);
            set => SetValue(ActiveProperty, value);
        }

        /// <summary>Raised when the user tunes with the wheel; argument is the new frequency in Hz.</summary>
        public event Action<long> Tuned;
        public event Action EditRequested;

        private static readonly Typeface _face = new Typeface(new FontFamily("DejaVu Sans Mono, Liberation Mono, monospace"), FontStyle.Normal, FontWeight.Bold);
        private const double FontSize = 40;
        private const long MaxHz = 61_440_000;
        private int _hoverDigit = -1;          // place value exponent under the mouse (0 = 1 Hz)

        private static string FormatWithGroups(long hz)
        {
            long mhz = hz / 1_000_000;
            long khz = hz / 1000 % 1000;
            long h = hz % 1000;
            return $"{mhz,3}.{khz:000}.{h:000}";
        }

        private FormattedText Layout(string text, IBrush brush) =>
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, FontSize, brush);

        protected override Size MeasureOverride(Size availableSize)
        {
            var ft = Layout("000.000.000", Brushes.White);
            return new Size(ft.Width + 8, ft.Height + 4);
        }

        public override void Render(DrawingContext context)
        {
            string text = FormatWithGroups(FrequencyHz);
            // transparent fill: makes the whole control hit-testable, not just the glyphs
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            var bright = new SolidColorBrush(Active ? Color.Parse("#F0F4F8") : Color.Parse("#5C6670"));
            var dim = new SolidColorBrush(Color.Parse("#3A434C"));
            var hover = new SolidColorBrush(Color.Parse("#FFB300"));
            double cellW = Layout("0", bright).Width;
            double x = 4;
            // leading spaces/zeros of the MHz part are drawn dim
            bool leading = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                int place = PlaceOfChar(i, text);
                if (c != ' ' && c != '0' && c != '.') leading = false;
                if (place == 6) leading = false;              // always show the units digit of MHz
                IBrush brush = place >= 0 && place == _hoverDigit ? hover : (leading ? dim : bright);
                var ft = Layout(c == ' ' ? "0" : c.ToString(), c == ' ' ? dim : brush);
                context.DrawText(ft, new Point(x, 2));
                x += c == '.' ? cellW * 0.6 : cellW;
            }
        }

        /// <summary>Place value exponent of character i of "mmm.kkk.hhh" (-1 for separators).</summary>
        private static int PlaceOfChar(int i, string text)
        {
            if (text[i] == '.') return -1;
            int digitsAfter = 0;
            for (int j = i + 1; j < text.Length; j++) if (text[j] != '.') digitsAfter++;
            return digitsAfter;
        }

        private int PlaceAt(double px)
        {
            string text = FormatWithGroups(FrequencyHz);
            double cellW = Layout("0", Brushes.White).Width;
            double x = 4;
            for (int i = 0; i < text.Length; i++)
            {
                double w = text[i] == '.' ? cellW * 0.6 : cellW;
                if (px >= x && px < x + w) return PlaceOfChar(i, text);
                x += w;
            }
            return -1;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            int p = PlaceAt(e.GetPosition(this).X);
            if (p != _hoverDigit) { _hoverDigit = p; InvalidateVisual(); }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverDigit = -1;
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            int p = PlaceAt(e.GetPosition(this).X);
            if (p < 0) return;
            long step = (long)Math.Pow(10, p);
            long f = FrequencyHz + (e.Delta.Y > 0 ? step : -step);
            f = Math.Clamp(f, 0, MaxHz);
            FrequencyHz = f;
            Tuned?.Invoke(f);
            e.Handled = true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.ClickCount >= 2) { EditRequested?.Invoke(); e.Handled = true; }
        }
    }
}
