/*  Settings.cs

This file is part of a program that implements a Software-Defined Radio.

User settings of the Linux front end, saved as JSON in
$XDG_CONFIG_HOME/thetis-linux/settings.json (~/.config/thetis-linux).
Large generated files (FFT wisdom, impulse cache) go to
$XDG_DATA_HOME/thetis-linux (~/.local/share/thetis-linux).

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thetis.Desktop
{
    public sealed class BandMemory
    {
        public double FrequencyMHz { get; set; }
        public DSPMode Mode { get; set; }
    }

    public sealed class Settings
    {
        public double FrequencyMHz { get; set; } = 7.100;
        public DSPMode Mode { get; set; } = DSPMode.LSB;
        public int FilterIndex { get; set; } = 4;
        public AGCMode Agc { get; set; } = AGCMode.MED;
        public double AgcTop { get; set; } = 90.0;
        public double Volume { get; set; } = 0.5;
        public int SampleRate { get; set; } = 192000;
        public int TuneStepHz { get; set; } = 100;
        public bool NoiseReduction { get; set; }
        public bool AutoNotch { get; set; }

        public string LastRadioMac { get; set; }
        public HPSDRModel? Model { get; set; }

        public bool PcAudio { get; set; } = true;
        public string AudioHostApi { get; set; }
        public string AudioOutputDevice { get; set; }

        public double SpectrumMaxDbm { get; set; } = -40.0;
        public double SpectrumMinDbm { get; set; } = -140.0;
        public double SpectrumZoom { get; set; } = 0.0;
        public double PanFraction { get; set; } = 0.45;       // share of the height used by the panadapter

        public Dictionary<string, BandMemory> Bands { get; set; } = new Dictionary<string, BandMemory>();

        public double WindowWidth { get; set; } = 1280;
        public double WindowHeight { get; set; } = 800;

        private static readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static string ConfigDirectory => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is string c && c.Length > 0
                ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "thetis-linux");

        public static string DataDirectory => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is string d && d.Length > 0
                ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "thetis-linux");

        private static string FilePath => Path.Combine(ConfigDirectory, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), _json) ?? new Settings();
            }
            catch (Exception)
            {
                // unreadable settings: start from defaults rather than refusing to run
            }
            return new Settings();
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigDirectory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, _json));
            File.Move(tmp, FilePath, true);
        }
    }
}
