/*  Thetis.CoreCheck

This file is part of a program that implements a Software-Defined Radio.

Headless end-to-end check of Thetis.Core against thetis-radiosim (or a real
Protocol 1 radio): discovery, connect, tuning, demodulation, audio routing,
S-meter, panadapter, and transmit: the transmit gates, band filters,
tune carrier, microphone path, power meters, SWR protection, timeout and
the radio's PTT input.  Exits non-zero if any check fails.

usage: thetis-corecheck <data dir> <radiosim status file> [carrier MHz] [--stress-tx cycles]

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Thetis;
using Thetis.Radio;

internal static partial class Program
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) _failures++;
    }

    private static JsonElement SimStatus(string file)
    {
        for (int i = 0; i < 20; i++)
        {
            try { return JsonDocument.Parse(File.ReadAllText(file)).RootElement; }
            catch (Exception) { Thread.Sleep(100); }
        }
        throw new IOException("no radiosim status in " + file);
    }

    private static void SimControl(string statusFile, double swr, bool ptt, double micDbfs = -20) =>
        File.WriteAllText(statusFile + ".ctl", FormattableString.Invariant(
            $"{{\"swr\":{swr},\"ptt\":{(ptt ? "true" : "false")},\"mic_dbfs\":{micDbfs}}}"));

    private static double TxLevel(string statusFile)
    {
        Thread.Sleep(2200);                         // the simulator reports once a second over 0.5 s
        return SimStatus(statusFile).GetProperty("tx_iq_dbfs").GetDouble();
    }

    /// <summary>Transmit audio processing: EQ, compressor, CFC, phase rotator, VOX.</summary>
    private static void TxAudio(RadioController radio, string statusFile, double tuneMHz)
    {
        Console.WriteLine("== transmit audio processing");
        radio.Mode = DSPMode.USB;
        radio.FrequencyMHz = tuneMHz;
        radio.TransmitAllowed = true;
        radio.Region = TxRegion.IaruRegion1;
        radio.MicSource = MicSource.Radio;
        radio.MicGainDb = 0;
        radio.DrivePercent = 10;
        SimControl(statusFile, 1.2, false, -30);    // quiet mic: stays below the ALC
        var tx = new TxProcessing { LevelerOn = false };
        radio.TxProcessing = tx;
        radio.SetMox(true, out _);
        double flat = TxLevel(statusFile);
        Console.WriteLine($"  1 kHz mic tone at -30 dBFS, no processing: TX I/Q {flat:F1} dBFS");

        tx.EqOn = true;
        tx.EqBandsDb[5] = -12;                      // the 1 kHz band
        radio.ApplyTxProcessing();
        double eq = TxLevel(statusFile);
        Check(flat - eq > 8 && flat - eq < 16, $"EQ: -12 dB at 1 kHz lowers the TX tone {flat - eq:F1} dB");
        tx.EqOn = false;
        tx.EqBandsDb[5] = 0;

        tx.CompressorOn = true;
        tx.CompressorDb = 10;
        radio.ApplyTxProcessing();
        double comp = TxLevel(statusFile);
        Check(comp - flat > 5, $"compressor at 10 dB raises the TX level {comp - flat:F1} dB");
        tx.CompressorOn = false;

        tx.CfcOn = true;
        tx.CfcPrecompDb = 10;
        radio.ApplyTxProcessing();
        double cfc = TxLevel(statusFile);
        var st = SimStatus(statusFile);
        Check(cfc - flat > 3 && Math.Abs(st.GetProperty("tx_tone_hz").GetDouble() - 1000) <= 25,
              $"CFC with 10 dB pre-compression raises the level {cfc - flat:F1} dB, tone still at 1 kHz");
        tx.CfcOn = false;

        tx.PhaseRotatorOn = true;
        radio.ApplyTxProcessing();
        double phrot = TxLevel(statusFile);
        st = SimStatus(statusFile);
        Check(Math.Abs(phrot - flat) < 3 && Math.Abs(st.GetProperty("tx_tone_hz").GetDouble() - 1000) <= 25,
              $"phase rotator: tone and level kept ({Math.Round(phrot - flat, 1) + 0.0:+0.0;-0.0;0.0} dB)");
        tx.PhaseRotatorOn = false;
        radio.SetMox(false, out _);

        Console.WriteLine("== VOX");
        SimControl(statusFile, 1.2, false, -200);   // silent mic
        // the simulator reads its control file once a second: wait until the tone
        // has really stopped, or VOX (rightly) keys on its tail
        WaitSim(statusFile, s => s.GetProperty("mic_dbfs").GetDouble() <= -150, 4000, out _);
        Thread.Sleep(300);
        tx.VoxOn = true;
        tx.VoxThresholdDb = -40;
        radio.ApplyTxProcessing();
        Thread.Sleep(1500);
        // the simulator's status lags up to a second behind the previous section's unkey
        Check(!radio.Mox && WaitSim(statusFile, s => !s.GetProperty("mox").GetBoolean(), 3000, out _) && !radio.Mox,
              "VOX on, silent mic: receiving");
        SimControl(statusFile, 1.2, false, -20);    // speak
        Check(WaitSim(statusFile, s => s.GetProperty("mox").GetBoolean(), 4000, out _) && radio.Mox && radio.TxSource == TxSource.Vox,
              "the mic tone keys the radio through VOX");
        SimControl(statusFile, 1.2, false, -200);   // stop speaking: unkeys after the 500 ms hold
        Check(WaitSim(statusFile, s => !s.GetProperty("mox").GetBoolean(), 4000, out _) && !radio.Mox, "silence unkeys it after the hold time");
        radio.TransmitAllowed = false;
        SimControl(statusFile, 1.2, false, -20);
        Thread.Sleep(2500);
        Check(!radio.Mox, "VOX does not key while transmit is disabled");
        tx.VoxOn = false;
        radio.ApplyTxProcessing();
        radio.TxProcessing = new TxProcessing();
        radio.Region = TxRegion.None;
        radio.MicGainDb = 10;
        File.Delete(statusFile + ".ctl");
        Thread.Sleep(1200);
    }

    /// <summary>Poll the simulator status until 'pred' holds (true) or 'ms' elapse (false).</summary>
    private static bool WaitSim(string file, Func<JsonElement, bool> pred, int ms, out JsonElement st)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            st = SimStatus(file);
            if (pred(st)) return true;
            Thread.Sleep(200);
        } while (sw.ElapsedMilliseconds < ms);
        return false;
    }

    private static float SpectrumPeak(RadioController radio)
    {
        float[] pix = new float[radio.SpectrumPixels];
        for (int i = 0; i < 50; i++) { if (radio.GetSpectrum(pix)) break; Thread.Sleep(40); }
        return pix.Max();
    }

    private static float SettledMeter(RadioController radio)
    {
        Thread.Sleep(2500);                         // AGC and meter averaging settle
        float sum = 0;
        for (int i = 0; i < 10; i++) { sum += radio.SignalDbm(); Thread.Sleep(50); }
        return sum / 10;
    }

    /// <summary>Setup and calibration: attenuator, antennas, PA gain, filter edges, level calibration.</summary>
    private static void Setup(RadioController radio, string statusFile, double tuneMHz)
    {
        Console.WriteLine("== attenuator");
        radio.Mode = DSPMode.USB;
        radio.Agc = AGCMode.MED;
        Check(radio.AttenuatorRange == (0, 61), $"Hermes with Alex: 0..61 dB ({radio.AttenuatorRange})");
        Check(RxFrontEnd.Data(HPSDRModel.HERMESLITE, 0) == (0, 31) && RxFrontEnd.Data(HPSDRModel.HERMESLITE, -12) == (0, 43),
              "Hermes-Lite 2: LNA data is 31 - attenuation");
        Check(RxFrontEnd.Data(HPSDRModel.HERMES, 40) == (3, 42) && RxFrontEnd.Data(HPSDRModel.ANAN7000D, 40) == (0, 31),
              "above 31 dB: 30 dB Alex pad + step (none on the 7000D, limited to 31)");
        float m0 = SettledMeter(radio), p0 = SpectrumPeak(radio);
        radio.AttenuatorDb = 20;
        WaitSim(statusFile, st => st.GetProperty("step_att_db").GetInt32() == 20, 3000, out var s1);
        Check(s1.GetProperty("step_att_db").GetInt32() == 20 && s1.GetProperty("alex_att_db").GetInt32() == 0, "20 dB: step attenuator 20 dB, Alex pads off");
        float m20 = SettledMeter(radio), p20 = SpectrumPeak(radio);
        Console.WriteLine($"  S-meter {m0:F1} -> {m20:F1} dBm, panadapter peak {p0:F1} -> {p20:F1} dBm");
        Check(Math.Abs(m20 - m0) < 2.0, "S-meter compensates for the attenuator");
        Check(Math.Abs(p20 - p0) < 3.0, "panadapter compensates for the attenuator");
        radio.AttenuatorDb = 40;
        WaitSim(statusFile, st => st.GetProperty("alex_att_db").GetInt32() == 30, 3000, out s1);
        Check(s1.GetProperty("alex_att_db").GetInt32() == 30 && s1.GetProperty("step_att_db").GetInt32() == 10,
              $"40 dB: 30 dB Alex pad + 10 dB step (got {s1.GetProperty("alex_att_db")} + {s1.GetProperty("step_att_db")})");
        radio.AttenuatorDb = 0;
        Thread.Sleep(2500);

        Console.WriteLine("== level calibration");
        Check(radio.CalibrateLevel(-50f, out string err), "calibrate to -50 dBm" + (err == null ? "" : ": " + err));
        float mc = SettledMeter(radio), pc = SpectrumPeak(radio);
        Console.WriteLine($"  after calibration: S-meter {mc:F1} dBm, panadapter peak {pc:F1} dBm (offsets {radio.MeterCalOffsetEffective:F1} / {radio.DisplayCalOffsetEffective:F1} dB)");
        Check(Math.Abs(mc + 50) < 1.5, "S-meter reads the reference level");
        Check(Math.Abs(pc + 50) < 2.0, "panadapter reads the reference level");
        radio.MeterCalOffsetDb = null;
        radio.DisplayCalOffsetDb = null;

        Console.WriteLine("== antennas");
        var ant = AntennaSettings.Defaults();
        ant.Bands[Band.B40M] = new BandAntennas { RxAnt = 2, TxAnt = 3, RxOnly = 1 };
        radio.Antennas = ant;
        WaitSim(statusFile, st => st.GetProperty("ant").GetInt32() == 2, 3000, out s1);
        Check(s1.GetProperty("ant").GetInt32() == 2 && s1.GetProperty("rx_only").GetInt32() == 1 && s1.GetProperty("rx_out").GetInt32() == 1,
              $"40 m receive: ANT2 with the RX1 input (ant {s1.GetProperty("ant")}, rx-only {s1.GetProperty("rx_only")}, rx-out {s1.GetProperty("rx_out")})");
        radio.FrequencyMHz = 14.2;
        WaitSim(statusFile, st => st.GetProperty("ant").GetInt32() == 1, 3000, out s1);
        Check(s1.GetProperty("ant").GetInt32() == 1 && s1.GetProperty("rx_only").GetInt32() == 0, "20 m receive: back to ANT1");
        radio.FrequencyMHz = tuneMHz;

        radio.TransmitAllowed = true;
        radio.Region = TxRegion.IaruRegion1;
        radio.TunePercent = 10;
        radio.SetTune(true, out _);
        WaitSim(statusFile, st => st.GetProperty("mox").GetBoolean() && st.GetProperty("ant").GetInt32() == 3, 3000, out s1);
        Check(s1.GetProperty("ant").GetInt32() == 3 && s1.GetProperty("rx_only").GetInt32() == 0, "40 m transmit: ANT3, receive input off");

        Console.WriteLine("== PA gain");
        Thread.Sleep(1200);
        double w0 = SimStatus(statusFile).GetProperty("fwd_w").GetDouble();
        var pa = PaCalibration.DefaultsFor(HPSDRModel.HERMES);
        pa.Bands[Band.B40M].GainDb -= 3f;         // a PA with 3 dB less gain: drive 3 dB more for the same watts
        radio.PaGains = pa;
        Thread.Sleep(2200);
        double w1 = SimStatus(statusFile).GetProperty("fwd_w").GetDouble();
        Console.WriteLine($"  10 % tune: {w0:F2} W with the default gain, {w1:F2} W with 3 dB less");
        Check(Math.Abs(w1 / w0 - 2.0) < 0.2, "3 dB less PA gain doubles the drive power");
        pa.Bands[Band.B40M].GainDb += 3f;
        pa.Bands[Band.B40M].DriveAdjustDb[0] = 3f;   // correction at 10 % drive
        radio.PaGains = pa;
        Thread.Sleep(2200);
        double w2 = SimStatus(statusFile).GetProperty("fwd_w").GetDouble();
        Check(Math.Abs(w2 / w0 - 2.0) < 0.2, $"a +3 dB correction at 10 % drive doubles it too ({w2:F2} W)");
        radio.SetTune(false, out _);
        radio.PaGains = null;
        radio.Antennas = null;
        radio.TransmitAllowed = false;
        radio.Region = TxRegion.None;

        Console.WriteLine("== filter band edges");
        var lpf = BandFilters.LpfEdges;
        Array.Find(lpf, e => e.Bits == BandFilters.Lpf60_40).EndMHz = 7.05;
        Array.Find(lpf, e => e.Bits == BandFilters.Lpf30_20).StartMHz = 7.050001;
        BandFilters.LpfEdges = lpf;
        radio.FrequencyMHz = tuneMHz + 0.001;
        WaitSim(statusFile, st => st.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf30_20, 3000, out s1);
        Check(s1.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf30_20, "edited LPF edges: 7.101 MHz now uses the 30/20 m filter");
        BandFilters.LpfEdges = BandFilters.DefaultLpfEdges;
        radio.FrequencyMHz = tuneMHz;
        WaitSim(statusFile, st => st.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf60_40, 3000, out s1);
        Check(s1.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf60_40, "defaults restored");
    }

    /// <summary>WDSP 2.10's NNR in the receive chain, and switching between it and AetherSDR's filters.</summary>
    private static void NeuralNoiseReduction(RadioController radio, string statusFile, double tuneMHz)
    {
        Console.WriteLine("== noise reduction (WDSP NNR)");
        radio.Mode = DSPMode.USB;
        radio.Agc = AGCMode.MED;
        radio.FrequencyMHz = 7.150;                 // noise only
        radio.NoiseReductionType = NrType.Off;
        Thread.Sleep(3000);
        double baseline = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        Console.WriteLine($"  noise audio without NR: {baseline:F1} dBFS");
        if (AetherNr.Loaded)
        {
            radio.NoiseReductionType = NrType.NR2;
            Thread.Sleep(500);
        }
        radio.NoiseReductionType = NrType.NNR;
        Thread.Sleep(4000);
        double level = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        Check(radio.NoiseReductionActive && baseline - level >= 10.0,
              $"NNR: running, noise audio {level:F1} dBFS ({baseline - level:F1} dB lower)");
        if (AetherNr.Loaded)
            Check(!radio.AetherNrRunning, "NNR replaces NR2 (only one filter runs)");

        radio.SetNoiseReductionParam(NrParam.NnrModel, 1);
        Thread.Sleep(3500);
        double large = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        Check(radio.NnrModelInUse == 1 && baseline - large >= 10.0,
              $"NNR large model: in use, noise audio {large:F1} dBFS ({baseline - large:F1} dB lower)");
        radio.SetNoiseReductionParam(NrParam.NnrModel, 0);
        radio.SetNoiseReductionParam(NrParam.NnrMaskFloorDb, -3);
        Thread.Sleep(3500);
        double shallow = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        Check(radio.NnrModelInUse == 0 && shallow - level >= 3.0,
              $"NNR mask floor -3 dB: removes less ({shallow:F1} dBFS, {shallow - level:F1} dB above the -25 dB default)");
        radio.SetNoiseReductionParam(NrParam.NnrMaskFloorDb, -25);

        radio.Mode = DSPMode.FM;                    // 192 kHz: NNR decimates by 12
        Thread.Sleep(1500);
        Check(radio.NoiseReductionActive, "NNR runs in FM");
        radio.NoiseReductionType = NrType.Off;
        radio.Mode = DSPMode.USB;
        radio.FrequencyMHz = tuneMHz;
        Thread.Sleep(2500);
        double back = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        Check(Math.Abs(SimStatus(statusFile).GetProperty("audio_peak_hz").GetDouble() - (tuneMHz == 7.1 ? 1500 : 0)) <= 50 && back > -40,
              $"NNR off: carrier demodulated again ({back:F1} dBFS)");
    }

    /// <summary>AetherSDR's NR2 / RN2 / NR4 / DFNR in the WDSP receive chain.</summary>
    private static void NoiseReduction(RadioController radio, string statusFile, double tuneMHz)
    {
        Console.WriteLine("== noise reduction (AetherSDR NR2, RN2, NR4, DFNR)");
        Check(AetherNr.Loaded, "libaethernr loaded and registered with WDSP" + (AetherNr.Loaded ? "" : ": " + AetherNr.Error));
        if (!AetherNr.Loaded) return;
        radio.Mode = DSPMode.USB;
        radio.Agc = AGCMode.MED;
        radio.FrequencyMHz = 7.150;                 // no carrier here: the audio is noise lifted by AGC
        Thread.Sleep(3000);
        double baseline = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        Console.WriteLine($"  noise audio without NR: {baseline:F1} dBFS");
        foreach (NrType t in new[] { NrType.NR2, NrType.RN2, NrType.DFNR })
        {
            radio.NoiseReductionType = t;
            Thread.Sleep(3500);                     // filters settle; the simulator averages 0.5 s
            double level = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
            Check(radio.NoiseReductionActive && baseline - level >= 10.0,
                  $"{t}: running, noise audio {level:F1} dBFS ({baseline - level:F1} dB lower)");
        }
        // NR4 is gentle by default (at most 10 dB), less than the make-up gain WDSP
        // adds to its band-pass stage whenever any noise reduction runs, so compare
        // NR4 at its default against NR4 with its reduction set to 0 dB.
        radio.SetNoiseReductionParam(NrParam.Nr4ReductionDb, 0);
        radio.NoiseReductionType = NrType.NR4;
        Thread.Sleep(3500);
        double nr4Zero = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        radio.SetNoiseReductionParam(NrParam.Nr4ReductionDb, 10);
        Thread.Sleep(3000);
        double nr4 = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        Check(radio.NoiseReductionActive && nr4Zero - nr4 >= 4.0,
              $"NR4: running, 10 dB setting lowers the noise audio {nr4Zero - nr4:F1} dB against 0 dB ({nr4:F1} dBFS)");

        radio.Mode = DSPMode.FM;                    // FM runs the receiver at 192 kHz
        radio.NoiseReductionType = NrType.RN2;
        Thread.Sleep(1000);
        Check(!radio.NoiseReductionActive, "RN2 does not run in FM (192 kHz); audio passes unchanged");
        radio.NoiseReductionType = NrType.NR2;
        Thread.Sleep(1000);
        Check(radio.NoiseReductionActive, "NR2 runs in FM");
        radio.NoiseReductionType = NrType.Off;
        radio.Mode = DSPMode.USB;
        radio.FrequencyMHz = tuneMHz;
        Thread.Sleep(2500);
        double back = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        Check(Math.Abs(SimStatus(statusFile).GetProperty("audio_peak_hz").GetDouble() - (tuneMHz == 7.1 ? 1500 : 0)) <= 50 && back > -40,
              $"NR off: carrier demodulated again ({back:F1} dBFS)");
    }

    /// <summary>
    /// PureSignal against the simulator's PA model: two-tone IMD without and
    /// with correction, the feedback DDCs, auto-attenuate, save and restore.
    /// </summary>
    private static void PureSignal(RadioController radio, string statusFile, double tuneMHz)
    {
        Console.WriteLine("== PureSignal");
        radio.Mode = DSPMode.USB;
        radio.FrequencyMHz = tuneMHz;
        radio.TransmitAllowed = true;
        radio.Region = TxRegion.IaruRegion1;
        // drive into the PA's compression, not into hard saturation (no predistortion can go past that)
        radio.DrivePercent = int.TryParse(Environment.GetEnvironmentVariable("PS_DRIVE"), out int dp) ? dp : 70;
        radio.TxAttenuationDb = 31;
        string corr = Path.Combine(Path.GetTempPath(), "thetis-corecheck-ps.txt");
        File.Delete(corr);

        Check(radio.SetTwoTone(true, out string why) && radio.TwoToneOn, "two-tone test signal keyed" + (why != null ? ": " + why : ""));
        Thread.Sleep(3000);
        var st = SimStatus(statusFile);
        double imdOff = st.GetProperty("tx_imd3_dbc").GetDouble();
        Console.WriteLine($"  PureSignal off: PA output IMD3 {imdOff:0.0} dBc, tone {st.GetProperty("tx_tone_hz")} Hz, feedback {st.GetProperty("ps")}");
        Check(imdOff > -40 && imdOff < -10 && !st.GetProperty("ps").GetBoolean(), $"without correction the PA distorts (IMD3 {imdOff:0.0} dBc), no feedback requested");

        radio.PureSignalAutoCal = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PureSignalStatus ps = radio.PureSignalStatus;
        while (sw.ElapsedMilliseconds < 30000)
        {
            Thread.Sleep(500);
            ps = radio.PureSignalStatus;
            if (ps.CorrectionsApplied && ps.FeedbackLevel > 128 && ps.FeedbackLevel <= 181 && ps.CalibrationCount >= 3) break;
        }
        st = SimStatus(statusFile);
        Console.WriteLine($"  after {sw.ElapsedMilliseconds / 1000.0:0.0} s: state {ps.State}, feedback {ps.FeedbackLevel} ({ps.LevelText}), calibrations {ps.CalibrationCount}, " +
                          $"TX attenuator {ps.TxAttenuationDb} dB (radio {st.GetProperty("tx_att_db")}), DDCs {st.GetProperty("nddc")} at {st.GetProperty("sample_rate")} Hz");
        int psRate = radio.Model == HPSDRModel.HERMESLITE ? radio.SampleRate : 192000;   // MI0BOT: the HL2 keeps its receive rate
        Check(st.GetProperty("ps").GetBoolean() && st.GetProperty("sample_rate").GetInt32() == psRate,
              $"PS-A: the radio sends feedback, at {psRate / 1000} kHz while transmitting");
        // auto-attenuate may still be stepping: wait for the radio to report the console's setting
        WaitSim(statusFile, s => s.GetProperty("tx_att_db").GetInt32() == radio.PureSignalStatus.TxAttenuationDb, 10000, out st);
        ps = radio.PureSignalStatus;
        Check(ps.TxAttenuationDb < 31 && st.GetProperty("tx_att_db").GetInt32() == ps.TxAttenuationDb,
              $"auto-attenuate brought the TX attenuator down from 31 dB to {ps.TxAttenuationDb} dB");
        Check(ps.CorrectionsApplied && ps.FeedbackLevel > 128 && ps.FeedbackLevel <= 181,
              $"calibrated and correcting (feedback level {ps.FeedbackLevel}, {ps.CalibrationCount} calibrations)");
        // a new attenuator setting recalibrates: wait for the correction to take (the sim averages 0.5 s)
        WaitSim(statusFile, s => s.GetProperty("tx_imd3_dbc").GetDouble() < imdOff - 10, 20000, out st);
        double imdOn = st.GetProperty("tx_imd3_dbc").GetDouble();
        Check(imdOn < imdOff - 10, $"PureSignal lowers the IMD3 from {imdOff:0.0} to {imdOn:0.0} dBc");

        // auto-attenuate may be recalibrating (correction raises the PA's peaks, and with them
        // the feedback level): save at a moment a correction is in place
        bool saved = false;
        for (int i = 0; i < 100 && !(saved = radio.PureSignalSave(corr, out why)); i++) Thread.Sleep(100);
        for (int i = 0; i < 20 && saved && !File.Exists(corr); i++) Thread.Sleep(100);   // WDSP writes it on its own thread
        Check(saved && File.Exists(corr), "correction saved" + (why != null ? ": " + why : ""));
        radio.PureSignalReset();
        WaitSim(statusFile, s => s.GetProperty("tx_imd3_dbc").GetDouble() > imdOn + 10, 10000, out st);
        ps = radio.PureSignalStatus;
        Console.WriteLine($"  after reset: state {ps.State}, applied {ps.CorrectionsApplied}, enabled {ps.Enabled}, feedback {ps.FeedbackLevel}");
        double imdReset = st.GetProperty("tx_imd3_dbc").GetDouble();
        Check(!radio.PureSignalStatus.CorrectionsApplied && imdReset > imdOn + 10, $"reset: the correction is off again (IMD3 {imdReset:0.0} dBc)");
        Check(radio.PureSignalRestore(corr, out why), "restore requested" + (why != null ? ": " + why : ""));
        Thread.Sleep(3000);
        ps = radio.PureSignalStatus;
        Console.WriteLine($"  after restore: state {ps.State}, applied {ps.CorrectionsApplied}, enabled {ps.Enabled}");
        double imdRestored = SimStatus(statusFile).GetProperty("tx_imd3_dbc").GetDouble();
        Check(radio.PureSignalStatus.CorrectionsApplied && imdRestored < imdOff - 10, $"restored correction applied (IMD3 {imdRestored:0.0} dBc)");

        radio.SetTwoTone(false, out _);
        Thread.Sleep(1500);
        st = SimStatus(statusFile);
        Console.WriteLine($"  unkeyed: radio mox {radio.Mox}, sim mox {st.GetProperty("mox")}, rate {st.GetProperty("sample_rate")}");
        Check(!radio.Mox && !st.GetProperty("mox").GetBoolean() && st.GetProperty("sample_rate").GetInt32() == radio.SampleRate,
              $"unkeyed: receiving at {radio.SampleRate / 1000} kHz again");
        radio.PureSignalReset();
        Thread.Sleep(500);
        radio.DrivePercent = 10;
        radio.Region = TxRegion.None;
        radio.TransmitAllowed = false;
        File.Delete(corr);
    }

    private static void Transmit(RadioController radio, string statusFile, double tuneMHz)
    {
        string refused = null;
        radio.TxRefused += r => { refused = r; Console.WriteLine("  tx: " + r); };
        radio.Mode = DSPMode.USB;
        radio.Agc = AGCMode.MED;
        radio.MicSource = MicSource.Radio;
        radio.MicGainDb = 10;

        Console.WriteLine("== transmit gates");
        int outOfBand = 0;
        radio.TxRefusedOutOfBand += _ => Interlocked.Increment(ref outOfBand);
        Check(!radio.SetTune(true, out string why) && !radio.Mox, "TUNE refused while transmit is disabled (" + why + ")");
        radio.TransmitAllowed = true;
        Check(!radio.SetTune(true, out why) && !radio.Mox, "TUNE refused until a region is chosen (" + why + ")");
        Check(outOfBand == 0, "those refusals are not reported as out of band");
        radio.Region = TxRegion.IaruRegion1;
        radio.FrequencyMHz = 7.199;               // USB 100-3000 Hz reaches 7.202 MHz, past the Region 1 band edge
        Check(!radio.SetMox(true, out why) && !radio.Mox && outOfBand == 1 && why.Contains("cross the band edge"),
              "MOX refused when the passband leaves the band, reported as out of band (" + why + ")");
        radio.FrequencyMHz = 7.350;
        Check(!radio.SetTune(true, out why) && !radio.Mox && outOfBand == 2 && why.Contains("7.000 to 7.200 MHz"),
              "TUNE refused outside the amateur bands, naming the band (" + why + ")");
        Check(BandPlanRegions.InBand(TxRegion.IaruRegion1, 7.2) && !BandPlanRegions.InBand(TxRegion.IaruRegion1, 7.2001) &&
              BandPlanRegions.InBand(TxRegion.IaruRegion2, 7.25) && !BandPlanRegions.InBand(TxRegion.None, 7.1),
              "band limits per region (7.200 MHz ends 40 m in Region 1, 7.300 in Region 2)");
        radio.FrequencyMHz = tuneMHz;
        Thread.Sleep(1500);
        Check(!SimStatus(statusFile).GetProperty("mox").GetBoolean(), "radio never keyed by a refused request");

        Console.WriteLine("== receive filters");
        WaitSim(statusFile, s => s.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf60_40, 3000, out var st);
        Check(st.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf60_40 && st.GetProperty("hpf_bits").GetInt32() == BandFilters.Hpf6_5MHz,
              $"7.1 MHz: 60/40 m LPF, 6.5 MHz HPF (lpf {st.GetProperty("lpf_bits")}, hpf {st.GetProperty("hpf_bits")})");
        radio.FrequencyMHz = 14.2;
        WaitSim(statusFile, s => s.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf30_20, 3000, out st);
        Check(st.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf30_20 && st.GetProperty("hpf_bits").GetInt32() == BandFilters.Hpf13MHz,
              $"14.2 MHz: 30/20 m LPF, 13 MHz HPF (lpf {st.GetProperty("lpf_bits")}, hpf {st.GetProperty("hpf_bits")})");
        radio.FrequencyMHz = 3.7;
        WaitSim(statusFile, s => s.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf80, 3000, out st);
        Check(st.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf80 && st.GetProperty("hpf_bits").GetInt32() == BandFilters.Hpf1_5MHz,
              $"3.7 MHz: 80 m LPF, 1.5 MHz HPF (lpf {st.GetProperty("lpf_bits")}, hpf {st.GetProperty("hpf_bits")})");
        radio.FrequencyMHz = tuneMHz;

        Console.WriteLine("== TUNE at 10 % into a 1.2:1 load");
        radio.TunePercent = 10;
        Check(radio.SetTune(true, out why) && radio.Mox && radio.Tuning, "TUNE keyed" + (why == null ? "" : ": " + why));
        Check(WaitSim(statusFile, s => s.GetProperty("mox").GetBoolean() && s.GetProperty("fwd_w").GetDouble() > 1, 4000, out st), "radio sees MOX and puts out power");
        Thread.Sleep(1200);
        st = SimStatus(statusFile);
        Console.WriteLine("  sim: " + st);
        Check(st.GetProperty("tx_hz").GetInt64() == (long)Math.Round(tuneMHz * 1e6), "TX frequency sent");
        Check(st.GetProperty("lpf_bits").GetInt32() == BandFilters.Lpf60_40, "60/40 m LPF in circuit on transmit");
        Check(Math.Abs(st.GetProperty("tx_tone_hz").GetDouble() - FilterPresets.CwPitch) <= 25, $"tune carrier at +{FilterPresets.CwPitch} Hz (got {st.GetProperty("tx_tone_hz")} Hz)");
        double fwd = st.GetProperty("fwd_w").GetDouble();
        Check(fwd > 7 && fwd < 13, $"10 % tune gives about 10 W ({fwd:F1} W)");
        Console.WriteLine($"  meters: fwd {radio.ForwardWatts:F1} W, ref {radio.ReflectedWatts:F2} W, SWR {radio.Swr:F2}");
        Check(Math.Abs(radio.ForwardWatts - fwd) < 0.15 * fwd + 0.5, "forward power meter agrees with the radio");
        Check(Math.Abs(radio.Swr - 1.2) < 0.15, "SWR meter reads 1.2:1");

        Console.WriteLine("== retune while transmitting");
        radio.FrequencyMHz = tuneMHz + 0.01;
        Check(radio.Mox, "in-band retune keeps transmitting");
        Check(WaitSim(statusFile, s => s.GetProperty("tx_hz").GetInt64() == (long)Math.Round((tuneMHz + 0.01) * 1e6), 3000, out _), "TX frequency followed");
        radio.FrequencyMHz = 14.2;
        Check(!radio.Mox, "retune to another band unkeys");
        Check(WaitSim(statusFile, s => !s.GetProperty("mox").GetBoolean(), 3000, out _), "radio unkeyed");
        radio.FrequencyMHz = tuneMHz;

        Console.WriteLine("== high SWR: TUNE at 80 % into 3:1");
        SimControl(statusFile, 3.0, false);
        radio.TunePercent = 80;
        refused = null;
        radio.SetTune(true, out _);
        Thread.Sleep(3500);
        st = SimStatus(statusFile);
        Console.WriteLine($"  meters: fwd {radio.ForwardWatts:F1} W, ref {radio.ReflectedWatts:F1} W, SWR {radio.Swr:F2}, sim fwd {st.GetProperty("fwd_w")} W");
        Check(radio.HighSwr && refused != null && refused.StartsWith("High SWR"), "high SWR detected");
        Check(radio.Mox && st.GetProperty("fwd_w").GetDouble() < 40, $"drive folded back ({st.GetProperty("fwd_w")} W instead of ~80 W)");
        radio.SetTune(false, out _);
        SimControl(statusFile, 1.2, false);
        Thread.Sleep(1500);                      // the simulator reads its control file once a second

        Console.WriteLine("== MOX with the radio's microphone (USB, 1 kHz tone at -20 dBFS)");
        radio.DrivePercent = 50;
        radio.MicGainDb = 20;
        Check(radio.SetMox(true, out why) && radio.Mox && !radio.Tuning, "MOX keyed" + (why == null ? "" : ": " + why));
        Thread.Sleep(2500);
        st = SimStatus(statusFile);
        Console.WriteLine("  sim: " + st);
        Console.WriteLine($"  mic peak {radio.MicPeakDb():F1} dB, ALC {radio.AlcGainDb():F1} dB, fwd {radio.ForwardWatts:F1} W");
        Check(radio.MicPeakDb() > -40, "mic level registered");
        Check(Math.Abs(st.GetProperty("tx_tone_hz").GetDouble() - 1000) <= 25, $"USB transmits the 1 kHz mic tone at +1 kHz (got {st.GetProperty("tx_tone_hz")} Hz)");
        Check(st.GetProperty("fwd_w").GetDouble() > 5, $"output power ({st.GetProperty("fwd_w")} W)");
        radio.Mode = DSPMode.LSB;
        Check(!radio.Mox, "mode change unkeys");
        radio.SetMox(true, out _);
        Thread.Sleep(2500);
        st = SimStatus(statusFile);
        Check(Math.Abs(st.GetProperty("tx_tone_hz").GetDouble() + 1000) <= 25, $"LSB transmits it at -1 kHz (got {st.GetProperty("tx_tone_hz")} Hz)");
        radio.SetMox(false, out _);
        radio.Mode = DSPMode.USB;

        Console.WriteLine("== open antenna");
        radio.DrivePercent = 100;
        radio.SetMox(true, out _);
        Thread.Sleep(1500);
        SimControl(statusFile, 1e6, false);     // no load: everything comes back
        Check(WaitSim(statusFile, s => !s.GetProperty("mox").GetBoolean(), 5000, out _) && !radio.Mox, "open antenna unkeys the radio");
        Check(refused != null && refused.Contains("open antenna"), "operator told why");
        SimControl(statusFile, 1.2, false);
        Thread.Sleep(1500);

        Console.WriteLine("== timeout");
        radio.TxTimeoutSeconds = 2;
        radio.TunePercent = 10;
        radio.SetTune(true, out _);
        Thread.Sleep(3000);
        Check(!radio.Mox && refused != null && refused.Contains("timeout"), "transmit timeout unkeys");
        radio.TxTimeoutSeconds = 180;

        Console.WriteLine("== radio PTT input");
        radio.DrivePercent = 10;
        SimControl(statusFile, 1.2, true);
        Check(WaitSim(statusFile, s => s.GetProperty("mox").GetBoolean(), 4000, out _) && radio.Mox && radio.TxSource == TxSource.RadioPtt, "PTT keys the radio");
        SimControl(statusFile, 1.2, false);
        Check(WaitSim(statusFile, s => !s.GetProperty("mox").GetBoolean(), 4000, out _) && !radio.Mox, "releasing PTT unkeys it");

        Console.WriteLine("== power off while transmitting");
        radio.SetTune(true, out _);
        Thread.Sleep(500);
    }

    /// <summary>
    /// PC audio (VAC) on a real sound device: the rate the device's callback
    /// really runs at, and VAC's ring-buffer diagnostics, over 'seconds'.
    /// </summary>
    private static int VacCheck(RadioController radio, DiscoveredRadio target, string device, int seconds)
    {
        // VAC_HOSTAPI selects the sound system (default ALSA); VAC_INPUT a separate microphone device
        string apiName = Environment.GetEnvironmentVariable("VAC_HOSTAPI") ?? "ALSA";
        foreach (var h in Thetis.Audio.AudioDevices.HostApis()) Console.WriteLine($"  host API {h.Index}: {h.Name}");
        var alsa = Thetis.Audio.AudioDevices.HostApis().FirstOrDefault(h => h.Name.Contains(apiName));
        if (alsa == null) { Console.WriteLine("no host API " + apiName); return 1; }
        var devs = Thetis.Audio.AudioDevices.Devices(alsa.Index);
        foreach (var d in devs) Console.WriteLine($"  device {d.HostApiDeviceIndex}: {d.Name} (in {d.MaxInputChannels}, out {d.MaxOutputChannels})");
        var dev = devs.FirstOrDefault(d => d.Name == device) ?? devs.FirstOrDefault(d => d.Name.Contains(device));
        if (dev == null) { Console.WriteLine("no device " + device); return 1; }
        string inName = Environment.GetEnvironmentVariable("VAC_INPUT");
        var inDev = inName == null ? dev : devs.FirstOrDefault(d => d.Name.Contains(inName) && d.MaxInputChannels > 0);
        if (inDev == null) { Console.WriteLine("no input device " + inName); return 1; }
        Console.WriteLine($"== VAC on '{dev.Name}'");
        radio.SampleRate = 48000;
        radio.Mode = DSPMode.USB;
        radio.FrequencyMHz = 7.100;
        radio.ConfigureVac(true, alsa.Index, dev.HostApiDeviceIndex, inDev.HostApiDeviceIndex);
        if (!radio.Start(target, HPSDRModel.HERMESLITE, out string error)) { Console.WriteLine("start failed: " + error); return 1; }
        Check(radio.VacRunning, "VAC started");
        Thread.Sleep(2000);
        long f0 = VacDiag.Frames(0);
        var d0 = VacDiag.Diags(0, 0);           // the counts are cumulative: start-up fills the ring once
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Thread.Sleep(seconds * 1000);
        double rate = (VacDiag.Frames(0) - f0) / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  PortAudio stream rate {VacDiag.StreamRate(0):0}, callback rate {rate:0} frames/s");
        int[] under = new int[2], over = new int[2];
        double[] var = new double[2];
        for (int type = 0; type < 2; type++)
        {
            var (u, o, v, nr, rs) = VacDiag.Diags(0, type);
            under[type] = u; over[type] = o; var[type] = v;
            Console.WriteLine($"  {(type == 0 ? "receiver -> device" : "device -> transmitter")}: underflows {u}, overflows {o}, ratio {v:0.0000}, ring {nr}/{rs}");
        }
        Console.WriteLine(radio.ReceiveDiagnostics(5));
        if (Environment.GetEnvironmentVariable("VAC_OFFSIGNAL") == "1")
        {
            radio.FrequencyMHz = 7.150;                         // noise only
            Thread.Sleep(3000);
            Console.WriteLine(radio.ReceiveDiagnostics(5));
            radio.FrequencyMHz = 7.100;
        }
        Check(Math.Abs(rate - 48000) < 480, $"sound device runs at 48 kHz (measured {rate:0})");
        var d1 = VacDiag.Diags(0, 0);
        int dOver = d1.overflows - d0.overflows, dUnder = d1.underflows - d0.underflows;
        // a few while the rate matcher settles after start-up; a fault drops hundreds a second
        Check(dOver + dUnder < (seconds + 5) / 2, $"receiver audio flows without dropping while running (underflows {dUnder}, overflows {dOver} in {seconds + 5} s)");
        radio.Stop();
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Receive with every Protocol 1 model at each sample rate: the S-meter,
    /// the spectrum peak and the demodulated tone must all agree with the
    /// carrier, before and after a retune (display and audio on the same DDC).
    /// </summary>
    private static int RxMatrix(RadioController radio, DiscoveredRadio target, string statusFile, double carrierMHz, string[] only)
    {
        const double tuneMHz = 7.100;
        var models = Enum.GetValues<HPSDRModel>().Where(m => m > HPSDRModel.FIRST && m < HPSDRModel.LAST).ToList();
        if (only.Length > 0) models = models.Where(m => only.Contains(m.ToString())).ToList();
        int[] rates = { 48000, 192000, 384000 };
        radio.Mode = DSPMode.USB;
        radio.Agc = AGCMode.MED;
        radio.SetSpectrumWidth(1000);
        foreach (var model in models)
            foreach (int rate in rates)
            {
                radio.SampleRate = rate;
                radio.FrequencyMHz = tuneMHz;
                string tag = $"{model} {rate / 1000} kHz";
                if (!radio.Start(target, model, out string error)) { Check(false, $"{tag}: start ({error})"); continue; }
                for (int step = 0; step < 2; step++)
                {
                    double vfo = tuneMHz + step * 0.0005;
                    radio.FrequencyMHz = vfo;
                    double toneHz = (carrierMHz - vfo) * 1e6;
                    Thread.Sleep(2500);
                    var st = SimStatus(statusFile);
                    float dbm = radio.SignalDbm();
                    float[] pix = new float[radio.SpectrumPixels];
                    bool got = false;
                    for (int i = 0; i < 50 && !got; i++) { got = radio.GetSpectrum(pix); if (!got) Thread.Sleep(40); }
                    var (lo, hi) = radio.SpectrumSpan;
                    double peakHz = got ? lo + (hi - lo) * (double)Array.IndexOf(pix, pix.Max()) / (pix.Length - 1) : double.NaN;
                    double tone = st.GetProperty("audio_peak_hz").GetDouble();
                    double level = st.GetProperty("audio_rms_dbfs").GetDouble();
                    bool ok = dbm > -110f && Math.Abs(peakHz - toneHz) < 3.0 * (hi - lo) / pix.Length + 20
                              && Math.Abs(tone - toneHz) <= 50 && level > -40;
                    Check(ok, $"{tag} at {vfo:F4} MHz: S {dbm:F0} dBm, peak {peakHz:+0;-0} Hz, audio {tone:F0} Hz {level:F0} dBFS (want {toneHz:F0} Hz), " +
                              $"sim DDCs {st.GetProperty("nddc")} DDC0 {st.GetProperty("ddc0_hz")}");
                }
                radio.Stop();
                Thread.Sleep(1000);
            }
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Power on, key TUNE, power off -- repeatedly (shutdown while transmitting).</summary>
    private static int StressTx(RadioController radio, DiscoveredRadio target, int cycles)
    {
        radio.TransmitAllowed = true;
        radio.Region = TxRegion.IaruRegion1;
        radio.Mode = DSPMode.USB;
        radio.FrequencyMHz = 7.1;
        var rng = new Random(1);
        for (int i = 0; i < cycles; i++)
        {
            if (!radio.Start(target, HPSDRModel.HERMES, out string error)) { Console.WriteLine("start failed: " + error); return 1; }
            Thread.Sleep(300 + rng.Next(500));
            radio.SetTune(true, out _);
            Thread.Sleep(rng.Next(600));
            radio.Stop();
            Console.WriteLine($"cycle {i + 1} ok");
            Thread.Sleep(rng.Next(300));
        }
        return 0;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: thetis-corecheck <data dir> <radiosim status file> [carrier MHz] [--stress-tx cycles]");
            return 2;
        }
        string dataDir = args[0], statusFile = args[1];
        double carrierMHz = args.Length > 2 && !args[2].StartsWith("--") ? double.Parse(args[2], CultureInfo.InvariantCulture) : 7.1015;
        const double tuneMHz = 7.100;
        double toneHz = (carrierMHz - tuneMHz) * 1e6;

        using var radio = new RadioController(dataDir);
        radio.Status += s => Console.WriteLine("status: " + s);

        Console.WriteLine("== DSP start-up");
        radio.InitializeDsp();
        Check(radio.DspReady, "wdsp + ChannelMaster initialised");

        Console.WriteLine("== discovery");
        var found = RadioController.Discover(ScanPerformanceProfile.VeryTolerant, includeLoopback: true);
        foreach (var r in found) Console.WriteLine($"  found {r} via {r.Nic.NicName}");
        Check(found.Count > 0, "radio discovered");
        if (found.Count == 0) return 1;
        var target = found.FirstOrDefault(r => r.Nic.IsLoopbackLocal) ?? found[0];
        Check(target.Info.DeviceType == HPSDRHW.Hermes, "board reported as Hermes");

        int psArg = Array.IndexOf(args, "--ps");
        if (psArg >= 0)
        {
            var psModel = args.Length > psArg + 1 ? Enum.Parse<HPSDRModel>(args[psArg + 1]) : HPSDRModel.HERMES;
            radio.SampleRate = args.Length > psArg + 2 ? int.Parse(args[psArg + 2]) : 48000;
            radio.Mode = DSPMode.USB;
            radio.FrequencyMHz = 7.100;
            if (!radio.Start(target, psModel, out string e1)) { Console.WriteLine("start failed: " + e1); return 1; }
            Thread.Sleep(2000);
            PureSignal(radio, statusFile, 7.100);
            radio.Stop();
            return _failures == 0 ? 0 : 1;
        }

        int catArg = Array.IndexOf(args, "--cat");
        if (catArg >= 0)
        {
            radio.SampleRate = 48000;
            radio.Mode = DSPMode.USB;
            radio.FrequencyMHz = 7.100;
            if (!radio.Start(target, HPSDRModel.HERMES, out string e2)) { Console.WriteLine("start failed: " + e2); return 1; }
            Thread.Sleep(2000);
            CatCheck(radio, statusFile, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(statusFile)), "catcheck"));
            radio.Stop();
            return _failures == 0 ? 0 : 1;
        }

        int vac = Array.IndexOf(args, "--vac");
        if (vac >= 0)
            return VacCheck(radio, target, args[vac + 1], args.Length > vac + 2 ? int.Parse(args[vac + 2]) : 10);

        int matrix = Array.IndexOf(args, "--rx-matrix");
        if (matrix >= 0)
            return RxMatrix(radio, target, statusFile, carrierMHz, args.Skip(matrix + 1).Where(a => !a.StartsWith("--")).ToArray());

        int stress = Array.IndexOf(args, "--stress-tx");
        if (stress >= 0)
            return StressTx(radio, target, int.Parse(args[stress + 1]));

        Console.WriteLine("== connect (USB, 7.100 MHz, 48 kHz)");
        radio.SampleRate = 48000;
        radio.Mode = DSPMode.USB;
        radio.Agc = AGCMode.MED;
        radio.FrequencyMHz = tuneMHz;
        radio.SetSpectrumWidth(1000);
        bool started = radio.Start(target, HPSDRModel.HERMES, out string error);
        Check(started, "radio started" + (started ? "" : ": " + error));
        if (!started) return 1;

        Thread.Sleep(3000);
        var st = SimStatus(statusFile);
        Console.WriteLine("  sim: " + st);
        Check(st.GetProperty("streaming").GetBoolean(), "radio is streaming");
        Check(st.GetProperty("ddc0_hz").GetInt64() == (long)(tuneMHz * 1e6), "DDC0 tuned to 7.100000 MHz");
        Check(st.GetProperty("sample_rate").GetInt32() == 48000, "radio set to 48 kHz");
        Check(st.GetProperty("packets_in").GetInt64() > 100, "host is sending EP2 packets");
        // the simulator writes its status once a second: use its own timestamps
        long a0 = st.GetProperty("audio_samples").GetInt64(), t0 = st.GetProperty("t_ms").GetInt64();
        Thread.Sleep(3000);
        var st1 = SimStatus(statusFile);
        long a1 = st1.GetProperty("audio_samples").GetInt64(), t1 = st1.GetProperty("t_ms").GetInt64();
        double audioRate = t1 > t0 ? (a1 - a0) * 1000.0 / (t1 - t0) : 0;
        Check(Math.Abs(audioRate - 48000) < 4000, $"radio receives 48 kHz audio (measured {audioRate:F0} samples/s)");

        float dbm = radio.SignalDbm();
        Console.WriteLine($"  S-meter {dbm:F1} dBm ({RadioController.SUnits(dbm)})");
        Check(dbm > -110f && dbm < -20f, "S-meter reads the carrier");

        float[] pix = new float[radio.SpectrumPixels];
        bool got = false;
        for (int i = 0; i < 50 && !got; i++) { got = radio.GetSpectrum(pix); if (!got) Thread.Sleep(40); }
        Check(got, "panadapter produced a frame");
        if (got)
        {
            var (lo, hi) = radio.SpectrumSpan;
            int peak = Array.IndexOf(pix, pix.Max());
            double peakHz = lo + (hi - lo) * (double)peak / (pix.Length - 1);
            float floor = pix.OrderBy(v => v).ElementAt(pix.Length / 2);
            Console.WriteLine($"  span {lo}..{hi} Hz, peak {pix[peak]:F1} dBm at {peakHz:F0} Hz, median floor {floor:F1} dBm");
            Check(Math.Abs(peakHz - toneHz) < 3.0 * (hi - lo) / pix.Length + 20, $"spectrum peak at +{toneHz:F0} Hz");
            Check(pix[peak] - floor > 30f, "carrier stands >30 dB above the noise floor");
        }

        double usbLevel = st.GetProperty("audio_rms_dbfs").GetDouble();
        double usbTone = st.GetProperty("audio_peak_hz").GetDouble();
        Check(Math.Abs(usbTone - toneHz) <= 50, $"demodulated audio tone at {toneHz:F0} Hz (got {usbTone} Hz)");
        Check(usbLevel > -40, $"audio level {usbLevel:F1} dBFS");

        Console.WriteLine("== retune +500 Hz");
        radio.FrequencyMHz = tuneMHz + 0.0005;
        Thread.Sleep(2500);
        st = SimStatus(statusFile);
        Check(st.GetProperty("ddc0_hz").GetInt64() == (long)Math.Round((tuneMHz + 0.0005) * 1e6), "DDC0 followed the retune");
        Check(Math.Abs(st.GetProperty("audio_peak_hz").GetDouble() - (toneHz - 500)) <= 50, $"audio tone moved to {toneHz - 500:F0} Hz (got {st.GetProperty("audio_peak_hz").GetDouble()} Hz)");
        radio.FrequencyMHz = tuneMHz;

        Console.WriteLine("== LSB (carrier is in the opposite sideband)");
        radio.Agc = AGCMode.FIXD;
        radio.Mode = DSPMode.USB;
        Thread.Sleep(2500);
        double fixedUsb = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        radio.Mode = DSPMode.LSB;
        Thread.Sleep(2500);
        double fixedLsb = SimStatus(statusFile).GetProperty("audio_rms_dbfs").GetDouble();
        Console.WriteLine($"  fixed-gain audio: USB {fixedUsb:F1} dBFS, LSB {fixedLsb:F1} dBFS");
        Check(fixedUsb - fixedLsb > 30, "LSB rejects the upper-sideband carrier by >30 dB");

        // a carrier tuned exactly onto the VFO is heard at the CW pitch: the
        // receiver sits the pitch away (console UpdateRX1DDSFreq)
        Console.WriteLine("== CW: VFO on the carrier");
        radio.Agc = AGCMode.MED;
        foreach (var (mode, sign) in new[] { (DSPMode.CWU, -1), (DSPMode.CWL, +1) })
        {
            radio.Mode = mode;
            radio.FrequencyMHz = carrierMHz;
            Thread.Sleep(2500);
            st = SimStatus(statusFile);
            long wantDdc = (long)Math.Round(carrierMHz * 1e6) + sign * FilterPresets.CwPitch;
            double cwTone = st.GetProperty("audio_peak_hz").GetDouble(), cwLevel = st.GetProperty("audio_rms_dbfs").GetDouble();
            Check(st.GetProperty("ddc0_hz").GetInt64() == wantDdc && Math.Abs(cwTone - FilterPresets.CwPitch) <= 50 && cwLevel > -40,
                  $"{mode}: receiver at {wantDdc} Hz, tone {cwTone:F0} Hz at {cwLevel:F0} dBFS (want {FilterPresets.CwPitch} Hz)");
        }
        radio.Mode = DSPMode.USB;
        radio.FrequencyMHz = tuneMHz;

        File.Delete(statusFile + ".ctl");
        Setup(radio, statusFile, tuneMHz);
        NoiseReduction(radio, statusFile, tuneMHz);
        NeuralNoiseReduction(radio, statusFile, tuneMHz);
        TxAudio(radio, statusFile, tuneMHz);
        PureSignal(radio, statusFile, tuneMHz);
        Transmit(radio, statusFile, tuneMHz);
        bool keyed = radio.Mox;
        radio.Stop();
        Thread.Sleep(1500);
        st = SimStatus(statusFile);
        Check(keyed && !radio.Mox && !st.GetProperty("mox").GetBoolean() && !st.GetProperty("streaming").GetBoolean(), "power off unkeys and stops the radio");
        File.Delete(statusFile + ".ctl");

        Console.WriteLine("== restart");
        started = radio.Start(target, HPSDRModel.HERMES, out error);
        Check(started && !radio.Mox, "radio restarted, receiving");
        Thread.Sleep(2000);

        Console.WriteLine("== sample rate 192 kHz");
        radio.Mode = DSPMode.USB;
        radio.Agc = AGCMode.MED;
        radio.SampleRate = 192000;
        Thread.Sleep(3000);
        st = SimStatus(statusFile);
        Check(st.GetProperty("sample_rate").GetInt32() == 192000, "radio switched to 192 kHz");
        Check(Math.Abs(st.GetProperty("audio_peak_hz").GetDouble() - toneHz) <= 50, "audio still demodulated after rate change");
        var span = radio.SpectrumSpan;
        Check(span.high - span.low > 150000, $"panadapter span widened ({span.high - span.low} Hz)");

        radio.Stop();
        Thread.Sleep(1500);
        Check(!SimStatus(statusFile).GetProperty("streaming").GetBoolean(), "radio stopped on power off");

        Console.WriteLine(_failures == 0 ? "corecheck: all checks passed" : $"corecheck: {_failures} check(s) failed");
        return _failures == 0 ? 0 : 1;
    }
}
