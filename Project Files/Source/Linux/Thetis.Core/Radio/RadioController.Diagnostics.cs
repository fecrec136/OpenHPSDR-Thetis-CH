/*  RadioController.Diagnostics.cs

This file is part of a program that implements a Software-Defined Radio.

A receive diagnostics report for problems that only show with real hardware:
it measures the sample rate the radio really sends, and checks that the
panadapter, the S-meter and the audio agree with the settings.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Thetis.Radio
{
    public sealed unsafe partial class RadioController
    {
        private long _rateSamples;
        private readonly Stopwatch _rateClock = new Stopwatch();

        /// <summary>
        /// Protocol 1: the receive sample rate the radio really sends, averaged
        /// since the previous call (call it every second or two); null when
        /// unknown.  It should equal SampleRate.
        /// </summary>
        public double? MeasuredSampleRate()
        {
            if (!_powerOn || NetworkIO.CurrentRadioProtocol != RadioProtocol.USB) { _rateClock.Reset(); return null; }
            long n = NetworkIO.getP1RxSamples();
            double t = _rateClock.Elapsed.TotalSeconds;
            bool first = !_rateClock.IsRunning;
            long prev = _rateSamples;
            _rateSamples = n;
            _rateClock.Restart();
            if (first || t < 0.5) return null;
            return (n - prev) / t;
        }

        /// <summary>
        /// Watch the receiver for 'seconds' and return a plain-text report.  Blocks
        /// the calling thread; call it off the UI thread.
        /// </summary>
        public string ReceiveDiagnostics(int seconds = 5)
        {
            var r = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            void Line(string k, string v) => r.AppendLine(string.Format(inv, "{0,-28}{1}", k, v));

            r.AppendLine("Thetis receive diagnostics, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv));
            Line("OS", Environment.OSVersion + ", .NET " + Environment.Version);
            if (!_powerOn || ConnectedRadio == null)
            {
                r.AppendLine("The radio is off: turn it on and run this again.");
                return r.ToString();
            }

            RadioInfo ri = ConnectedRadio;
            bool p1 = NetworkIO.CurrentRadioProtocol == RadioProtocol.USB;
            Line("Radio", $"{ri.DeviceType} at {ri.IpAddress} ({ri.MacAddress}), firmware {ri.CodeVersion}.{ri.BetaVersion}, {(p1 ? "Protocol 1" : "Protocol 2")}");
            Line("Model setting", _model.ToString());
            Line("Mode / filter", $"{_mode}, {_filter.Name} ({_filter.Low} to {_filter.High} Hz)");
            Line("VFO", _frequencyMHz.ToString("0.000000", inv) + " MHz");
            Line("Receiver tuned to", RxTunedMHz.ToString("0.000000", inv) + " MHz");
            string ddcs = string.Join(", ", Enumerable.Range(0, 4).Select(i => $"DDC{i} {NetworkIO.LastVFOfreq(i, 0).ToString("0.000000", inv)}"));
            Line("Frequencies sent", ddcs + $", TX {NetworkIO.LastVFOfreq(0, 1).ToString("0.000000", inv)} MHz");
            Line("Attenuator", $"{AttenuatorDb} dB (range {RxFrontEnd.Range(_model).min} to {RxFrontEnd.Range(_model).max})");
            Line("Noise reduction", $"{_nrType} (active: {NoiseReductionActive})");
            Line("Sample rate set", $"{_sampleRate / 1000} kHz");

            // --- what the radio really sends ---
            int ooo0 = NetworkIO.getOOO();
            NetworkIO.getAndResetADC_Overload();
            long s0 = p1 ? NetworkIO.getP1RxSamples() : 0;
            long vacFrames0 = _vacRunning ? VacDiag.Frames(0) : 0;
            int xrunOut0 = VacDiag.Xruns(0, true), xrunIn0 = VacDiag.Xruns(0, false);
            var sw = Stopwatch.StartNew();
            float[] pix = new float[SpectrumPixels];
            int frames = 0;
            float sMin = float.MaxValue, sMax = float.MinValue, adcMin = float.MaxValue, adcMax = float.MinValue;
            float agcMin = float.MaxValue, agcMax = float.MinValue;
            double peakDbm = double.MinValue, peakHz = 0, atVfo = double.MinValue;
            var medians = new System.Collections.Generic.List<float>();
            int overloads = 0;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(100);
                float s = SignalDbm();          // as the S-meter shows it (calibration and attenuator included)
                float adc = WDSP.CalculateRXMeter(0, 0, WDSP.MeterType.ADC_REAL);
                float agc = WDSP.CalculateRXMeter(0, 0, WDSP.MeterType.AGC_GAIN);
                sMin = Math.Min(sMin, s); sMax = Math.Max(sMax, s);
                adcMin = Math.Min(adcMin, adc); adcMax = Math.Max(adcMax, adc);
                agcMin = Math.Min(agcMin, agc); agcMax = Math.Max(agcMax, agc);
                overloads += NetworkIO.getAndResetADC_Overload() != 0 ? 1 : 0;
                if (GetSpectrum(pix))
                {
                    frames++;
                    var (lo, hi) = SpectrumSpan;
                    double vfoOffset = (_frequencyMHz - RxTunedMHz) * 1e6;
                    for (int k = 0; k < pix.Length; k++)
                    {
                        double hz = lo + (hi - lo) * (double)k / Math.Max(1, pix.Length - 1);
                        if (pix[k] > peakDbm) { peakDbm = pix[k]; peakHz = hz; }
                        if (hz >= _filter.Low && hz <= _filter.High && pix[k] > atVfo) atVfo = pix[k];
                    }
                    medians.Add(pix.OrderBy(v => v).ElementAt(pix.Length / 2));
                }
            }
            double elapsed = sw.Elapsed.TotalSeconds;
            long s1 = p1 ? NetworkIO.getP1RxSamples() : 0;
            long vacFrames1 = _vacRunning ? VacDiag.Frames(0) : 0;

            r.AppendLine();
            if (p1)
            {
                double measured = (s1 - s0) / elapsed;
                Line("Receive DDCs in stream", NetworkIO.getP1nddc().ToString(inv));
                Line("Samples/s from the radio", measured.ToString("0", inv) +
                     (Math.Abs(measured - _sampleRate) > 0.1 * _sampleRate
                         ? $"   <-- NOT the {_sampleRate / 1000} kHz set: the radio did not take the rate" : "   (matches the setting)"));
            }
            Line("Out-of-order packets", ooo0 == 0 ? "none" : "yes (code " + ooo0 + ")");
            Line("ADC overload", overloads == 0 ? "none" : $"in {overloads} of {(int)(elapsed * 10)} readings");

            var (spanLo, spanHi) = SpectrumSpan;
            Line("Panadapter", $"span {spanLo} to {spanHi} Hz, {pix.Length} pixels");
            if (frames > 0)
            {
                Line("Panadapter noise floor", $"{medians.Average():0.0} dBm");
                Line("Strongest signal", $"{peakDbm:0.0} dBm at {peakHz:+0;-0} Hz from the receiver centre " +
                                         $"({(RxTunedMHz + peakHz * 1e-6).ToString("0.000000", inv)} MHz)");
                Line("Strongest in the filter", $"{atVfo:0.0} dBm");
            }
            Line("S-meter (as shown)", $"{sMin:0.0} to {sMax:0.0} dBm");
            Line("Receiver input level", $"{adcMin:0.0} to {adcMax:0.0} dBFS" + (adcMax - adcMin < 0.01 ? "   <-- not changing: the receiver may not be running" : ""));
            Line("AGC gain", $"{agcMin:0.0} to {agcMax:0.0} dB");
            if (frames > 0 && atVfo > medians.Average() + 20 && sMax < atVfo - 20)
                r.AppendLine("  <-- the panadapter shows a signal in the filter but the S-meter does not: display and receiver disagree");

            // --- PC audio ---
            r.AppendLine();
            Line("PC audio (VAC)", _vacRunning ? "running" : _vacEnabled ? "enabled, not running" : "off (audio goes to the radio)");
            if (_vacRunning)
            {
                string DevName(int hostDev) =>
                    Thetis.Audio.AudioDevices.Devices(_vacHostApi).FirstOrDefault(d => d.HostApiDeviceIndex == hostDev)?.Name ?? "device " + hostDev;
                var api = Thetis.Audio.AudioDevices.HostApis().FirstOrDefault(h => h.Index == _vacHostApi);
                int inDev = _vacInputDevice >= 0 ? _vacInputDevice : _vacOutputDevice;
                Line("  sound system", api?.Name ?? "host API " + _vacHostApi);
                Line("  speakers", DevName(_vacOutputDevice));
                Line("  microphone", DevName(inDev) + (inDev != _vacOutputDevice ? "   <-- a different device from the speakers" : ""));
                Line("  stream opened at", VacDiag.StreamRate(0).ToString("0", inv) + " Hz");
                if (vacFrames1 > vacFrames0)
                {
                    double cb = (vacFrames1 - vacFrames0) / elapsed;
                    Line("  device really runs at", cb.ToString("0", inv) + " frames/s" +
                         (Math.Abs(cb - 48000) > 480 ? "   <-- not 48000: the sound device is stalling or restarting" : "   (ok)"));
                }
                Line("  PortAudio xruns", $"speakers {VacDiag.Xruns(0, true) - xrunOut0}, microphone {VacDiag.Xruns(0, false) - xrunIn0} (during the test)");
                for (int type = 0; type < 2; type++)
                {
                    var (under, over, var, nring, ringsize) = VacDiag.Diags(0, type);
                    Line(type == 0 ? "  receiver -> speakers" : "  microphone -> radio",
                         $"underflows {under}, overflows {over}, rate ratio {var.ToString("0.0000", inv)}, ring {nring}/{ringsize}");
                }
                r.AppendLine("  (underflow and overflow counts are since PC audio started; a rate ratio of 0.96 or 1.04 is the limit)");
            }
            return r.ToString();
        }
    }
}
