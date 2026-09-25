/*  BandPlanRegions.cs

This file is part of a program that implements a Software-Defined Radio.

Frequency -> Band (for PA gain tables and OC outputs), and the amateur
transmit allocations used to refuse transmitting outside the bands, as
the Windows console does in CheckValidTXFreq().

The allocations are the common ITU/IARU ones for each region.  They are a
safety net, not a statement of what any particular licence allows:
national rules differ, and the operator remains responsible.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;

namespace Thetis.Radio
{
    public enum TxRegion
    {
        /// <summary>Not chosen yet: transmitting is refused until the operator picks one.</summary>
        None = 0,
        IaruRegion1,        // Europe, Africa, Middle East, northern Asia
        IaruRegion2,        // the Americas (outside the US)
        UnitedStates,
        IaruRegion3,        // Asia-Pacific
    }

    public static class BandPlanRegions
    {
        private static readonly (double lo, double hi, Band band)[] _bands =
        {
            (1.800, 2.000, Band.B160M), (3.500, 4.000, Band.B80M), (5.250, 5.450, Band.B60M),
            (7.000, 7.300, Band.B40M), (10.100, 10.150, Band.B30M), (14.000, 14.350, Band.B20M),
            (18.068, 18.168, Band.B17M), (21.000, 21.450, Band.B15M), (24.890, 24.990, Band.B12M),
            (28.000, 29.700, Band.B10M), (50.000, 54.000, Band.B6M), (144.0, 148.0, Band.B2M),
            // broadcast bands (receive only), used by OC presets
            (2.300, 2.495, Band.B120M), (3.200, 3.400, Band.B90M), (4.750, 5.060, Band.B61M),
            (5.900, 6.200, Band.B49M), (7.300, 7.450, Band.B41M), (9.400, 9.900, Band.B31M),
            (11.600, 12.100, Band.B25M), (13.570, 13.870, Band.B22M), (15.100, 15.800, Band.B19M),
            (17.480, 17.900, Band.B16M), (18.900, 19.020, Band.B14M), (21.450, 21.850, Band.B13M),
            (25.600, 26.100, Band.B11M),
        };

        public static Band BandFromFrequency(double mhz)
        {
            foreach (var (lo, hi, band) in _bands)
                if (mhz >= lo && mhz <= hi) return band;
            if (mhz >= 2.498 && mhz <= 2.502 || mhz >= 4.998 && mhz <= 5.002 || mhz >= 9.998 && mhz <= 10.002 ||
                mhz >= 14.998 && mhz <= 15.002 || mhz >= 19.998 && mhz <= 20.002)
                return Band.WWV;
            return Band.GEN;
        }

        private static readonly (double lo, double hi)[] _common =
        {
            (10.100, 10.150), (14.000, 14.350), (18.068, 18.168), (21.000, 21.450),
            (24.890, 24.990), (28.000, 29.700),
        };

        /// <summary>WRC-15 60 m allocation (5351.5 - 5366.5 kHz).</summary>
        private static readonly (double lo, double hi) _wrc15_60m = (5.3515, 5.3665);

        /// <summary>US 60 m channels: 2.8 kHz wide, centred on these frequencies (MHz).</summary>
        private static readonly double[] _us60mCentres = { 5.3320, 5.3480, 5.3585, 5.3730, 5.4050 };

        public static IReadOnlyList<(double lo, double hi)> Allocations(TxRegion region)
        {
            var list = new List<(double lo, double hi)>(_common);
            switch (region)
            {
                case TxRegion.IaruRegion1:
                    list.Add((1.810, 2.000)); list.Add((3.500, 3.800)); list.Add(_wrc15_60m);
                    list.Add((7.000, 7.200)); list.Add((50.000, 52.000));
                    break;
                case TxRegion.IaruRegion2:
                    list.Add((1.800, 2.000)); list.Add((3.500, 4.000)); list.Add(_wrc15_60m);
                    list.Add((7.000, 7.300)); list.Add((50.000, 54.000));
                    break;
                case TxRegion.UnitedStates:
                    list.Add((1.800, 2.000)); list.Add((3.500, 4.000));
                    foreach (double c in _us60mCentres) list.Add((c - 0.0014, c + 0.0014));
                    list.Add((7.000, 7.300)); list.Add((50.000, 54.000));
                    break;
                case TxRegion.IaruRegion3:
                    list.Add((1.800, 2.000)); list.Add((3.500, 3.900)); list.Add(_wrc15_60m);
                    list.Add((7.000, 7.300)); list.Add((50.000, 54.000));
                    break;
                default:
                    list.Clear();
                    break;
            }
            return list;
        }

        /// <summary>For people: "IARU Region 1".</summary>
        public static string RegionName(TxRegion region) => region switch
        {
            TxRegion.IaruRegion1 => "IARU Region 1",
            TxRegion.IaruRegion2 => "IARU Region 2",
            TxRegion.IaruRegion3 => "IARU Region 3",
            TxRegion.UnitedStates => "the United States",
            _ => "no region",
        };

        /// <summary>True if 'mhz' is inside one of the region's allocations (the band limit lines).</summary>
        public static bool InBand(TxRegion region, double mhz)
        {
            foreach (var (lo, hi) in Allocations(region))
                if (mhz >= lo - 1e-9 && mhz <= hi + 1e-9) return true;
            return false;
        }

        /// <summary>The allocation containing 'mhz', or else the nearest one; null for no region.</summary>
        public static (double lo, double hi)? NearestAllocation(TxRegion region, double mhz)
        {
            (double lo, double hi)? best = null;
            double bestDist = double.MaxValue;
            foreach (var a in Allocations(region))
            {
                double d = mhz < a.lo ? a.lo - mhz : mhz > a.hi ? mhz - a.hi : 0;
                if (d < bestDist) { bestDist = d; best = a; }
            }
            return best;
        }

        /// <summary>
        /// True if everything transmitted -- 'txMHz' plus the TX passband
        /// [lowHz, highHz] -- lies inside one allocation for the region.  For
        /// CW and the tune carrier pass the carrier offset as both edges.
        /// </summary>
        public static bool IsTxAllowed(TxRegion region, double txMHz, int lowHz, int highHz)
        {
            double lo = txMHz + Math.Min(lowHz, highHz) * 1e-6;
            double hi = txMHz + Math.Max(lowHz, highHz) * 1e-6;
            foreach (var (alo, ahi) in Allocations(region))
                if (lo >= alo - 1e-9 && hi <= ahi + 1e-9) return true;
            return false;
        }
    }
}
