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
using Thetis.Radio;

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
        /// <summary>Old setting (WDSP's EMNR on/off); read once and turned into NoiseReductionType.</summary>
        public bool NoiseReduction { get; set; }
        /// <summary>Receive noise reduction: AetherSDR's NR2, RN2, NR4 or DFNR.</summary>
        public NrType NoiseReductionType { get; set; } = NrType.Off;
        /// <summary>Noise reduction settings changed from their defaults.</summary>
        public Dictionary<NrParam, double> NrParams { get; set; } = new Dictionary<NrParam, double>();
        public bool AutoNotch { get; set; }

        public string LastRadioMac { get; set; }
        public HPSDRModel? Model { get; set; }

        public bool PcAudio { get; set; } = true;
        public string AudioHostApi { get; set; }
        public string AudioOutputDevice { get; set; }

        public string AudioInputDevice { get; set; }

        // transmit: off until the operator enables it and picks a region
        public bool TransmitAllowed { get; set; }
        public TxRegion Region { get; set; } = TxRegion.None;
        public int TxTimeoutSeconds { get; set; } = 180;
        public bool RadioPtt { get; set; } = true;
        public bool SwrProtection { get; set; } = true;
        public bool Hl2N2adrFilterBoard { get; set; }
        public int DrivePercent { get; set; } = 10;
        public int TunePercent { get; set; } = 10;
        public double MicGainDb { get; set; } = 10.0;
        public MicSource MicSource { get; set; } = MicSource.Radio;
        public int TxFilterLow { get; set; } = 100;
        public int TxFilterHigh { get; set; } = 3000;
        /// <summary>Leveler, compressor, CESSB, EQ, CFC, phase rotator, VOX and expander.</summary>
        public TxProcessing TxProcessing { get; set; } = new TxProcessing();
        /// <summary>PureSignal settings; PS-A (calibrate continuously) on or off; TX attenuator per band.</summary>
        public PureSignalSettings PureSignal { get; set; } = new PureSignalSettings();
        public bool PureSignalAutoCal { get; set; }
        public Dictionary<string, int> TxAttenuationByBand { get; set; } = new Dictionary<string, int>();

        // setup and calibration
        /// <summary>Receive attenuation per band ("GEN" outside the bands), dB.</summary>
        public Dictionary<string, int> AttenuatorByBand { get; set; } = new Dictionary<string, int>();
        /// <summary>S-meter / panadapter calibration per radio model (missing = the model's default).</summary>
        public Dictionary<HPSDRModel, float> MeterCalOffset { get; set; } = new Dictionary<HPSDRModel, float>();
        public Dictionary<HPSDRModel, float> DisplayCalOffset { get; set; } = new Dictionary<HPSDRModel, float>();
        /// <summary>PA gain per radio model (missing = the model's defaults).</summary>
        public Dictionary<HPSDRModel, PaCalibration> PaGains { get; set; } = new Dictionary<HPSDRModel, PaCalibration>();
        public AntennaSettings Antennas { get; set; } = AntennaSettings.Defaults();
        /// <summary>Edited filter band edges (null = defaults).</summary>
        public FilterEdge[] LpfEdges { get; set; }
        public FilterEdge[] HpfEdges { get; set; }
        public FilterEdge[] Bpf1Edges { get; set; }

        public double SpectrumMaxDbm { get; set; } = -40.0;
        public double SpectrumMinDbm { get; set; } = -140.0;
        public double SpectrumZoom { get; set; } = 0.0;
        public double PanFraction { get; set; } = 0.45;       // share of the height used by the panadapter

        public Dictionary<string, BandMemory> Bands { get; set; } = new Dictionary<string, BandMemory>();

        public double WindowWidth { get; set; } = 1280;
        public double WindowHeight { get; set; } = 800;

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
                    return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.Settings) ?? new Settings();
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, SettingsJson.Default.Settings));
            File.Move(tmp, FilePath, true);
        }
    }

    /// <summary>
    /// Source-generated (trim-safe) JSON for the settings file: indented, enums
    /// as names -- the same format the reflection-based serializer wrote.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(Settings))]
    internal sealed partial class SettingsJson : JsonSerializerContext
    {
    }
}
