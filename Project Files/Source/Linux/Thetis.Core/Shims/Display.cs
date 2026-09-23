/*  Display.cs

This file is part of a program that implements a Software-Defined Radio.

The small part of the Console's static Display class that the shared
upstream files (specHPSDR.cs) talk to.  The Avalonia UI reads these values
to label its panadapter; the Direct2D renderer itself is not ported.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

namespace Thetis
{
    public static class Display
    {
        public static DisplayMode CurrentDisplayMode { get; set; } = DisplayMode.PANAFALL;
        public static DSPMode RX1DSPMode { get; set; } = DSPMode.USB;

        // frequency span (Hz, relative to the centre) shown by each analyzer,
        // set by SpecHPSDR.initAnalyzer()
        public static int RXDisplayLow { get; set; }
        public static int RXDisplayHigh { get; set; }
        public static int RX2DisplayLow { get; set; }
        public static int RX2DisplayHigh { get; set; }
        public static int TXDisplayLow { get; set; }
        public static int TXDisplayHigh { get; set; }
        public static int RXSpectrumDisplayLow { get; set; }
        public static int RXSpectrumDisplayHigh { get; set; }
    }
}
