/*  Thetis.CoreCheck

This file is part of a program that implements a Software-Defined Radio.

Headless end-to-end check of Thetis.Core against thetis-radiosim (or a real
Protocol 1 radio): discovery, connect, tuning, demodulation, audio routing,
S-meter and panadapter.  Exits non-zero if any check fails.

usage: thetis-corecheck <data dir> <radiosim status file> [carrier MHz]

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

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: thetis-corecheck <data dir> <radiosim status file> [carrier MHz]");
            return 2;
        }
        string dataDir = args[0], statusFile = args[1];
        double carrierMHz = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 7.1015;
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
