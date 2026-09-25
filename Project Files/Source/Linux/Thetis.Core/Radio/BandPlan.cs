/*  BandPlan.cs

This file is part of a program that implements a Software-Defined Radio.

Amateur bands offered on the band buttons, with default frequency and mode
used until the band has been visited (after that, the last frequency and
mode on the band are remembered in the settings).

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System.Collections.Generic;
using System.Linq;

namespace Thetis.Radio
{
    public sealed record BandInfo(string Name, double LowMHz, double HighMHz, double DefaultMHz, DSPMode DefaultMode);

    public static class BandPlan
    {
        public static readonly IReadOnlyList<BandInfo> Bands = new[]
        {
            new BandInfo("160", 1.800, 2.000, 1.840, DSPMode.LSB),
            new BandInfo("80", 3.500, 4.000, 3.650, DSPMode.LSB),
            new BandInfo("60", 5.250, 5.450, 5.357, DSPMode.USB),
            new BandInfo("40", 7.000, 7.300, 7.100, DSPMode.LSB),
            new BandInfo("30", 10.100, 10.150, 10.125, DSPMode.CWU),
            new BandInfo("20", 14.000, 14.350, 14.200, DSPMode.USB),
            new BandInfo("17", 18.068, 18.168, 18.120, DSPMode.USB),
            new BandInfo("15", 21.000, 21.450, 21.200, DSPMode.USB),
            new BandInfo("12", 24.890, 24.990, 24.940, DSPMode.USB),
            new BandInfo("10", 28.000, 29.700, 28.500, DSPMode.USB),
            new BandInfo("6", 50.000, 54.000, 50.150, DSPMode.USB),
            new BandInfo("WWV", 9.995, 10.005, 10.000, DSPMode.AM),
        };

        public static BandInfo For(double mhz) =>
            Bands.FirstOrDefault(b => mhz >= b.LowMHz && mhz <= b.HighMHz);
    }
}
