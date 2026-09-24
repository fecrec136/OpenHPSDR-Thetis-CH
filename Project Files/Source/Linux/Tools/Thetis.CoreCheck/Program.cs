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

internal static class Program
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

    private static void SimControl(string statusFile, double swr, bool ptt) =>
        File.WriteAllText(statusFile + ".ctl", FormattableString.Invariant($"{{\"swr\":{swr},\"ptt\":{(ptt ? "true" : "false")}}}"));

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

    private static void Transmit(RadioController radio, string statusFile, double tuneMHz)
    {
        string refused = null;
        radio.TxRefused += r => { refused = r; Console.WriteLine("  tx: " + r); };
        radio.Mode = DSPMode.USB;
        radio.Agc = AGCMode.MED;
        radio.MicSource = MicSource.Radio;
        radio.MicGainDb = 10;

        Console.WriteLine("== transmit gates");
        Check(!radio.SetTune(true, out string why) && !radio.Mox, "TUNE refused while transmit is disabled (" + why + ")");
        radio.TransmitAllowed = true;
        Check(!radio.SetTune(true, out why) && !radio.Mox, "TUNE refused until a region is chosen (" + why + ")");
        radio.Region = TxRegion.IaruRegion1;
        radio.FrequencyMHz = 7.199;               // USB 100-3000 Hz reaches 7.202 MHz, past the Region 1 band edge
        Check(!radio.SetMox(true, out why) && !radio.Mox, "MOX refused when the passband leaves the band (" + why + ")");
        radio.FrequencyMHz = 7.350;
        Check(!radio.SetTune(true, out why) && !radio.Mox, "TUNE refused outside the amateur bands (" + why + ")");
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
        long a0 = st.GetProperty("audio_samples").GetInt64();
        Thread.Sleep(2000);
        long a1 = SimStatus(statusFile).GetProperty("audio_samples").GetInt64();
        double audioRate = (a1 - a0) / 2.0;
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

        File.Delete(statusFile + ".ctl");
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
