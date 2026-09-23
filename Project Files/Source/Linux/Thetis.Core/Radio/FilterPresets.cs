/*  FilterPresets.cs

This file is part of a program that implements a Software-Defined Radio.

Receive filter presets F1..F10 per mode, with the same values as
Console.InitFilterPresets() (console.cs).  Default selection is F5.

Copyright (C) 2000-2025 Original authors
Copyright (C) 2020-2026 Richard Samphire MW0LGE

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System.Collections.Generic;

namespace Thetis.Radio
{
    public readonly record struct FilterPreset(string Name, int Low, int High)
    {
        public int Width => High - Low;
        public override string ToString() => Name;
    }

    public static class FilterPresets
    {
        public const int DefaultIndex = 4;          // F5
        public const int CwPitch = 600;             // console default cw_pitch
        public const int DiguClickTuneOffset = 1500;
        public const int DiglClickTuneOffset = 2210;
        public const int FmDeviation = 5000;        // RXFMDeviation default
        public const int FmHighCut = 3000;          // RXFMHighCut default

        private static readonly (string name, int width)[] _ssb =
        {
            ("5.0k", 5000), ("4.4k", 4400), ("3.8k", 3800), ("3.3k", 3300), ("2.9k", 2900),
            ("2.7k", 2700), ("2.4k", 2400), ("2.1k", 2100), ("1.8k", 1800), ("1.0k", 1000),
        };
        private static readonly (string name, int half)[] _digital =
        {
            ("3.0k", 1500), ("2.5k", 1250), ("2.0k", 1000), ("1.5k", 750), ("1.0k", 500),
            ("800", 400), ("600", 300), ("300", 150), ("150", 75), ("75", 38),
        };
        private static readonly (string name, int half)[] _cw =
        {
            ("1.0k", 500), ("800", 400), ("600", 300), ("500", 250), ("400", 200),
            ("250", 125), ("150", 75), ("100", 50), ("50", 25), ("25", 13),
        };
        private static readonly (string name, int half)[] _am =
        {
            ("20k", 10000), ("18k", 9000), ("16k", 8000), ("12k", 6000), ("10k", 5000),
            ("9.0k", 4500), ("8.0k", 4000), ("7.0k", 3500), ("6.0k", 3000), ("5.0k", 2500),
        };

        public static IReadOnlyList<FilterPreset> For(DSPMode mode)
        {
            var list = new List<FilterPreset>(10);
            switch (mode)
            {
                case DSPMode.LSB:
                    foreach (var (n, w) in _ssb) list.Add(new FilterPreset(n, -(w + 100), -100));
                    break;
                case DSPMode.USB:
                    foreach (var (n, w) in _ssb) list.Add(new FilterPreset(n, 100, w + 100));
                    break;
                case DSPMode.DIGL:
                    foreach (var (n, h) in _digital) list.Add(new FilterPreset(n, -DiglClickTuneOffset - h, -DiglClickTuneOffset + h));
                    break;
                case DSPMode.DIGU:
                    foreach (var (n, h) in _digital) list.Add(new FilterPreset(n, DiguClickTuneOffset - h, DiguClickTuneOffset + h));
                    break;
                case DSPMode.CWL:
                    foreach (var (n, h) in _cw) list.Add(new FilterPreset(n, -CwPitch - h, -CwPitch + h));
                    break;
                case DSPMode.CWU:
                    foreach (var (n, h) in _cw) list.Add(new FilterPreset(n, CwPitch - h, CwPitch + h));
                    break;
                case DSPMode.FM:
                    int half = FmDeviation + FmHighCut;
                    list.Add(new FilterPreset($"{2 * half / 1000}k", -half, half));
                    break;
                default:    // AM, SAM, DSB, DRM, SPEC
                    foreach (var (n, h) in _am) list.Add(new FilterPreset(n, -h, h));
                    break;
            }
            return list;
        }
    }
}
