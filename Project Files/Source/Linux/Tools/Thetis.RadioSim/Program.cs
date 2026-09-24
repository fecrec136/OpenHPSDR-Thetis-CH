/*  Thetis.RadioSim

This file is part of a program that implements a Software-Defined Radio.

A minimal OpenHPSDR Protocol 1 (Metis/Hermes) radio simulator for testing
the Linux build without hardware.  It

  * answers discovery as a Hermes board,
  * streams receive I/Q (EP6) at the sample rate and DDC count the host
    selects, containing test carriers at fixed RF frequencies that move
    correctly as the host retunes each DDC,
  * decodes the audio the host sends back to the radio's codec (EP2) and
    reports its level and dominant tone, so the whole receive chain --
    tuning, demodulation, filtering, audio routing -- can be checked
    without a sound card,
  * sends a microphone tone in the mic samples,
  * decodes what the host sends for transmit -- MOX, TX frequency, drive,
    Alex filter and open-collector bits -- and analyses the TX I/Q,
  * models a PA and directional coupler, reporting forward and reflected
    power into a load of chosen SWR,
  * decodes the step and Alex attenuators (and applies them to the receive
    signal) and the Alex antenna relays,
  * reads '<status file>.ctl' ({"swr": 3.0, "ptt": true}) to change the
    load SWR or press the radio's PTT input while running.

usage: thetis-radiosim [--bind IP] [--carrier MHz[:dBFS]]... [--noise dBFS] [--status-file PATH] [--max-power W] [--pa-gain dB] [--swr N] [--mic-tone Hz[:dBFS]] [--textbook-iq]

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

namespace Thetis.RadioSim
{
    internal static class Program
    {
        private sealed record Carrier(double FrequencyHz, double Amplitude);

        // --- state written by the command parser, read by the streamer ---
        private static readonly object _lock = new object();
        private static IPEndPoint _host;
        private static volatile bool _streaming;
        private static int _rateBits;                      // 0..3 -> 48k..384k
        private static int _nddc = 1;
        private static readonly long[] _ddcFreq = new long[8];
        private static long _packetsIn, _packetsOut;

        // --- transmit state decoded from the host's C&C frames ---
        private static bool _mox;                          // C0 bit 0
        private static long _txFreq;                       // address 1
        private static int _drive;                         // address 9 C1 (0..255)
        private static int _hpfBits = -1, _lpfBits = -1;   // address 9 C3 / C4 (Alex)
        private static int _paDisable;                     // address 9 C3 bit 7
        private static int _ocBits;                        // address 0 C2 bits 7..1
        private static long _moxFrames;                    // frames received with MOX set

        // --- receive front end and antennas (C0=0 C3/C4, address 10 C4) ---
        private static int _stepAtt;                       // step attenuator, dB (0..31)
        private static int _alexAtt;                       // Alex 10/20 dB pads: bits 0..3 -> 0..30 dB
        private static int _ant = 1;                       // TX/RX antenna relay 1..3
        private static int _rxOnly;                        // 0 none, 1 RX1 in, 2 RX2 in, 3 XVTR
        private static int _rxOut;                         // RX bypass out
        private static readonly float[] _txI = new float[24000], _txQ = new float[24000];   // last 0.5 s of TX I/Q
        private static int _txPos;
        private static long _txSamples;

        // --- simulated PA and coupler (Hermes / 100 W class) ---
        private static double _maxPowerW = 100.0;          // PA saturates here
        private static double _paGainDb = 41.3;            // Hermes default PA gain for 40 m (clsHardwareSpecific)
        private static double _loadSwr = 1.2;              // set via the control file
        private static bool _radioPtt;                     // set via the control file
        private static volatile float _fwdW, _revW;

        // --- received EP2 audio analysis ---
        private static readonly float[] _audio = new float[24000];     // last 0.5 s of left channel at 48 kHz
        private static int _audioPos;
        private static long _audioSamples;

        private static readonly List<Carrier> _carriers = new List<Carrier>();
        private static double _noise = Math.Pow(10.0, -110.0 / 20.0);
        private static Socket _sock;
        // OpenHPSDR hardware delivers the spectrum mirrored relative to I=cos, Q=sin;
        // the (unmodified) Thetis receive chain expects exactly that, so the
        // simulator sends Q = -sin by default.  Transmit I/Q uses the same
        // orientation, so it is mirrored back before analysis.  --textbook-iq
        // uses Q = +sin both ways.
        private static double _qSign = -1.0;
        // microphone tone sent in the EP6 mic samples (the radio's mic input)
        private static double _micHz = 1000.0, _micAmp = Math.Pow(10.0, -20.0 / 20.0);

        private static int Main(string[] args)
        {
            IPAddress bind = IPAddress.Any;
            string statusFile = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--bind": bind = IPAddress.Parse(args[++i]); break;
                    case "--noise": _noise = Math.Pow(10.0, double.Parse(args[++i], CultureInfo.InvariantCulture) / 20.0); break;
                    case "--status-file": statusFile = args[++i]; break;
                    case "--textbook-iq": _qSign = 1.0; break;
                    case "--max-power": _maxPowerW = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--pa-gain": _paGainDb = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--mic-tone":
                        string[] m = args[++i].Split(':');
                        _micHz = double.Parse(m[0], CultureInfo.InvariantCulture);
                        _micAmp = m.Length > 1 ? Math.Pow(10.0, double.Parse(m[1], CultureInfo.InvariantCulture) / 20.0) : _micAmp;
                        break;
                    case "--swr": _loadSwr = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--carrier":
                        string[] p = args[++i].Split(':');
                        double f = double.Parse(p[0], CultureInfo.InvariantCulture) * 1e6;
                        double db = p.Length > 1 ? double.Parse(p[1], CultureInfo.InvariantCulture) : -60.0;
                        _carriers.Add(new Carrier(f, Math.Pow(10.0, db / 20.0)));
                        break;
                    case "-h":
                    case "--help":
                        Console.WriteLine("usage: thetis-radiosim [--bind IP] [--carrier MHz[:dBFS]]... [--noise dBFS] [--status-file PATH] [--max-power W] [--pa-gain dB] [--swr N] [--mic-tone Hz[:dBFS]] [--textbook-iq]");
                        return 0;
                    default:
                        Console.Error.WriteLine("unknown option " + args[i]);
                        return 2;
                }
            }
            if (_carriers.Count == 0) _carriers.Add(new Carrier(7.1015e6, Math.Pow(10.0, -60.0 / 20.0)));

            _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _sock.EnableBroadcast = true;
            _sock.Bind(new IPEndPoint(bind, 1024));
            Console.WriteLine($"radiosim: Hermes (Protocol 1) on {bind}:1024, carriers at " +
                              string.Join(", ", _carriers.ConvertAll(c => $"{c.FrequencyHz / 1e6:F6} MHz {20 * Math.Log10(c.Amplitude):F0} dBFS")));

            new Thread(Streamer) { IsBackground = true, Name = "EP6 streamer" }.Start();
            new Thread(() => Reporter(statusFile)) { IsBackground = true, Name = "reporter" }.Start();

            byte[] buf = new byte[2048];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                int n;
                try { n = _sock.ReceiveFrom(buf, ref from); }
                catch (SocketException) { continue; }
                if (n < 4 || buf[0] != 0xef || buf[1] != 0xfe) continue;
                var ep = (IPEndPoint)from;
                switch (buf[2])
                {
                    case 0x02: SendDiscoveryReply(ep); break;
                    case 0x04:
                        lock (_lock) { _host = ep; }
                        _streaming = (buf[3] & 1) != 0;
                        if (_streaming) { _startGeneration++; Interlocked.Exchange(ref _lastHostPacketMs, Environment.TickCount64); }
                        Console.WriteLine($"radiosim: {(_streaming ? "start" : "stop")} from {ep}");
                        break;
                    case 0x01:
                        if (n >= 1032 && buf[3] == 0x02) ParseEp2(buf);
                        break;
                }
            }
        }

        private static void SendDiscoveryReply(IPEndPoint to)
        {
            byte[] r = new byte[60];
            r[0] = 0xef; r[1] = 0xfe; r[2] = _streaming ? (byte)0x03 : (byte)0x02;
            byte[] mac = { 0x00, 0x1c, 0xc0, 0xa2, 0x13, 0x37 };
            Array.Copy(mac, 0, r, 3, 6);
            r[9] = 31;                  // firmware code version 3.1
            r[10] = 1;                  // board id 1 = Hermes
            r[20] = 4;                  // number of receivers
            _sock.SendTo(r, to);
            Console.WriteLine($"radiosim: discovery reply to {to}");
        }

        private static void ParseEp2(byte[] buf)
        {
            Interlocked.Increment(ref _packetsIn);
            Interlocked.Exchange(ref _lastHostPacketMs, Environment.TickCount64);
            for (int frame = 0; frame < 2; frame++)
            {
                int b = 8 + 512 * frame;
                if (buf[b] != 0x7f || buf[b + 1] != 0x7f || buf[b + 2] != 0x7f) continue;
                byte c0 = buf[b + 3], c1 = buf[b + 4], c2 = buf[b + 5], c3 = buf[b + 6], c4 = buf[b + 7];
                int addr = c0 >> 1;
                bool mox = (c0 & 1) != 0;
                lock (_lock)
                {
                    _mox = mox;
                    if (mox) _moxFrames++;
                    long f32 = ((long)c1 << 24) | ((long)c2 << 16) | ((long)c3 << 8) | c4;
                    if (addr == 0)
                    {
                        _rateBits = c1 & 3;
                        _ocBits = (c2 >> 1) & 0x7f;
                        _nddc = ((c4 >> 3) & 7) + 1;
                        _alexAtt = c3 & 3;
                        _rxOnly = (c3 >> 5) & 3;
                        _rxOut = (c3 >> 7) & 1;
                        _ant = (c4 & 3) + 1;
                    }
                    else if (addr == 10)
                        _stepAtt = (c4 & 0x20) != 0 ? c4 & 0x1f : 0;
                    else if (addr == 1)
                        _txFreq = f32;
                    else if (addr >= 2 && addr <= 8)
                        _ddcFreq[addr - 2] = f32;
                    else if (addr == 9)
                    {
                        _drive = c1;
                        _hpfBits = c3 & 0x7f;
                        _paDisable = (c3 >> 7) & 1;
                        _lpfBits = c4 & 0x7f;
                    }
                }
                lock (_txI)
                {
                    for (int s = 0; s < 63; s++)
                    {
                        int k = b + 8 + 8 * s + 4;
                        _txI[_txPos] = (short)((buf[k] << 8) | buf[k + 1]) / 32768f;
                        // same (mirrored) orientation as the receive I/Q; see _qSign
                        _txQ[_txPos] = (float)(_qSign * (short)((buf[k + 2] << 8) | buf[k + 3]) / 32768.0);
                        _txPos = (_txPos + 1) % _txI.Length;
                        _txSamples++;
                    }
                }
                // 63 samples of L R I Q, 16-bit big-endian
                lock (_audio)
                {
                    for (int s = 0; s < 63; s++)
                    {
                        int k = b + 8 + 8 * s;
                        short left = (short)((buf[k] << 8) | buf[k + 1]);
                        _audio[_audioPos] = left / 32768f;
                        _audioPos = (_audioPos + 1) % _audio.Length;
                        _audioSamples++;
                    }
                }
            }
        }

        private static volatile int _startGeneration;
        private static long _lastHostPacketMs;         // watchdog, like the Hermes firmware

        private static void Streamer()
        {
            while (true)
            {
                try { StreamLoop(); }
                catch (Exception ex) { Console.Error.WriteLine("radiosim: streamer error: " + ex); Thread.Sleep(100); }
            }
        }

        private static void StreamLoop()
        {
            var rng = new Random(1);
            byte[] pkt = new byte[1032];
            uint seq = 0;
            double[] phase = new double[8 * 8];
            double micPhase = 0;
            var sw = Stopwatch.StartNew();
            long samplesSent = 0;
            bool wasStreaming = false;
            int generation = -1;

            while (true)
            {
                if (!_streaming)
                {
                    wasStreaming = false;
                    Thread.Sleep(5);
                    continue;
                }
                if (generation != _startGeneration) { generation = _startGeneration; wasStreaming = false; }
                int rate, nddc; long[] freqs = new long[8]; IPEndPoint host; double gain;
                lock (_lock)
                {
                    gain = Math.Pow(10.0, -(_stepAtt + 10 * _alexAtt) / 20.0);   // the attenuators act on everything received
                    rate = 48000 << _rateBits;
                    nddc = _nddc;
                    Array.Copy(_ddcFreq, freqs, 8);
                    host = _host;
                }
                if (!wasStreaming) { sw.Restart(); samplesSent = 0; wasStreaming = true; }

                int spr = 504 / (6 * nddc + 2);
                pkt[0] = 0xef; pkt[1] = 0xfe; pkt[2] = 0x01; pkt[3] = 0x06;
                pkt[4] = (byte)(seq >> 24); pkt[5] = (byte)(seq >> 16); pkt[6] = (byte)(seq >> 8); pkt[7] = (byte)seq;
                seq++;
                for (int frame = 0; frame < 2; frame++)
                {
                    int b = 8 + 512 * frame;
                    Array.Clear(pkt, b, 512);
                    pkt[b] = 0x7f; pkt[b + 1] = 0x7f; pkt[b + 2] = 0x7f;
                    // C&C rotation as a Hermes sends it: 0x00 status, 0x08 exciter/forward,
                    // 0x10 reverse, 0x18 user ADC/supply; C0 bit 0 = PTT input
                    int rot = (int)((seq * 2 + frame) % 4);
                    byte ptt = _radioPtt ? (byte)1 : (byte)0;
                    pkt[b + 3] = (byte)((rot << 3) | ptt);
                    if (rot == 1)
                    {
                        int fwd = CouplerAdc(_fwdW, 0.09, 6);
                        pkt[b + 4] = 0; pkt[b + 5] = 0;                           // exciter (not modelled)
                        pkt[b + 6] = (byte)(fwd >> 8); pkt[b + 7] = (byte)fwd;
                    }
                    else if (rot == 2)
                    {
                        int rev = CouplerAdc(_revW, 0.09, 3);
                        pkt[b + 4] = (byte)(rev >> 8); pkt[b + 5] = (byte)rev;
                    }
                    for (int s = 0; s < spr; s++)
                    {
                        for (int d = 0; d < nddc; d++)
                        {
                            double i = gain * _noise * Gauss(rng), q = gain * _noise * Gauss(rng);
                            for (int c = 0; c < _carriers.Count; c++)
                            {
                                double offset = _carriers[c].FrequencyHz - freqs[d];
                                if (Math.Abs(offset) >= rate / 2.0) continue;
                                int pi = d * 8 + c;
                                phase[pi] += 2.0 * Math.PI * offset / rate;
                                if (phase[pi] > Math.PI) phase[pi] -= 2.0 * Math.PI;
                                else if (phase[pi] < -Math.PI) phase[pi] += 2.0 * Math.PI;
                                i += gain * _carriers[c].Amplitude * Math.Cos(phase[pi]);
                                q += gain * _qSign * _carriers[c].Amplitude * Math.Sin(phase[pi]);
                            }
                            int k = b + 8 + s * (6 * nddc + 2) + d * 6;
                            Put24(pkt, k, i);
                            Put24(pkt, k + 3, q);
                        }
                        // mic: 16-bit at the end of each sample slot; the host keeps every
                        // (rate / 48 kHz)th one, so advancing at 'rate' gives the right tone
                        micPhase += 2.0 * Math.PI * _micHz / rate;
                        if (micPhase > Math.PI) micPhase -= 2.0 * Math.PI;
                        int mic = (int)Math.Round(_micAmp * Math.Sin(micPhase) * 32767.0);
                        int mk = b + 8 + s * (6 * nddc + 2) + nddc * 6;
                        pkt[mk] = (byte)(mic >> 8); pkt[mk + 1] = (byte)mic;
                    }
                }
                if (host != null)
                {
                    try { _sock.SendTo(pkt, host); Interlocked.Increment(ref _packetsOut); } catch (SocketException) { }
                }

                // pace to real time
                samplesSent += 2 * spr;
                double due = samplesSent * 1000.0 / rate;
                double ahead = due - sw.Elapsed.TotalMilliseconds;
                if (ahead > 2.0) Thread.Sleep((int)ahead);
            }
        }

        /// <summary>Watts -> 12-bit coupler ADC reading with the Hermes constants the host uses to convert back.</summary>
        private static int CouplerAdc(double watts, double bridgeVolt, int offset)
        {
            if (watts <= 0) return 0;
            double volts = Math.Sqrt(watts * bridgeVolt);
            return (int)Math.Clamp(Math.Round(volts / 3.3 * 4095.0) + offset, 0, 4095);
        }

        private static void Put24(byte[] p, int k, double v)
        {
            int x = (int)Math.Clamp(v * 8388607.0, -8388608.0, 8388607.0);
            p[k] = (byte)(x >> 16); p[k + 1] = (byte)(x >> 8); p[k + 2] = (byte)x;
        }

        private static double Gauss(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        /// <summary>Once a second: print (and optionally write as JSON) the state and received-audio analysis.</summary>
        private static void Reporter(string statusFile)
        {
            while (true)
            {
                Thread.Sleep(1000);
                if (_streaming && Environment.TickCount64 - Interlocked.Read(ref _lastHostPacketMs) > 3000)
                {
                    _streaming = false;
                    Console.WriteLine("radiosim: watchdog - no packets from the host for 3 s, stopped");
                }
                float[] a = new float[_audio.Length];
                long total;
                lock (_audio) { Array.Copy(_audio, a, a.Length); total = _audioSamples; }

                double rms = 0;
                foreach (float x in a) rms += x * x;
                rms = Math.Sqrt(rms / a.Length);
                // coarse tone search: Goertzel every 50 Hz from 100 Hz to 5 kHz
                double bestF = 0, bestP = 0;
                for (int f = 100; f <= 5000; f += 50)
                {
                    double p = Goertzel(a, f, 48000);
                    if (p > bestP) { bestP = p; bestF = f; }
                }
                long rx1;
                int nddc, rate;
                lock (_lock) { rx1 = _ddcFreq[0]; nddc = _nddc; rate = 48000 << _rateBits; }

                // control file: {"swr": 3.0, "ptt": true}
                if (statusFile != null && File.Exists(statusFile + ".ctl"))
                {
                    try
                    {
                        var ctl = JsonDocument.Parse(File.ReadAllText(statusFile + ".ctl")).RootElement;
                        if (ctl.TryGetProperty("swr", out var sw)) _loadSwr = sw.GetDouble();
                        if (ctl.TryGetProperty("ptt", out var pt)) _radioPtt = pt.GetBoolean();
                    }
                    catch (Exception) { }
                }

                // transmit analysis: level and dominant tone of the TX I/Q (complex, -4..+4 kHz)
                float[] ti = new float[_txI.Length], tq = new float[_txQ.Length];
                lock (_txI) { Array.Copy(_txI, ti, ti.Length); Array.Copy(_txQ, tq, tq.Length); }
                double txPow = 0;
                for (int n2 = 0; n2 < ti.Length; n2++) txPow += ti[n2] * ti[n2] + tq[n2] * tq[n2];
                txPow /= ti.Length;
                double txTone = 0, txBest = 0;
                for (int f = -4000; f <= 4000; f += 25)
                {
                    double pw = GoertzelComplex(ti, tq, f, 48000);
                    if (pw > txBest) { txBest = pw; txTone = f; }
                }
                bool mox; int drive, hpf, lpf, oc, pa, stepAtt, alexAtt, ant, rxOnly, rxOut; long txf, moxFrames;
                lock (_lock)
                {
                    mox = _mox; drive = _drive; hpf = _hpfBits; lpf = _lpfBits; oc = _ocBits; pa = _paDisable; txf = _txFreq; moxFrames = _moxFrames;
                    stepAtt = _stepAtt; alexAtt = _alexAtt; ant = _ant; rxOnly = _rxOnly; rxOut = _rxOut;
                }

                // PA model, the inverse of the host's drive calculation: drive byte ->
                // 0.8 V full-scale DAC into 50 ohm -> PA gain, times the I/Q power
                double dacV = drive / 255.0 / 1.02 * 0.8;
                double fwdW = mox ? Math.Min(dacV * dacV / 0.05 * Math.Pow(10, _paGainDb / 10) / 1000.0 * Math.Min(txPow, 1.0), _maxPowerW) : 0.0;
                double gamma = (_loadSwr - 1.0) / (_loadSwr + 1.0);
                _fwdW = (float)fwdW;
                _revW = (float)(fwdW * gamma * gamma);
                var status = new
                {
                    streaming = _streaming,
                    t_ms = Environment.TickCount64,      // when this status was taken
                    sample_rate = rate,
                    nddc,
                    ddc0_hz = rx1,
                    packets_in = Interlocked.Read(ref _packetsIn),
                    packets_out = Interlocked.Read(ref _packetsOut),
                    audio_samples = total,
                    audio_rms_dbfs = rms > 0 ? 20 * Math.Log10(rms) : -200.0,
                    audio_peak_hz = bestF,
                    mox,
                    mox_frames = moxFrames,
                    tx_hz = txf,
                    drive,
                    hpf_bits = hpf,
                    lpf_bits = lpf,
                    oc_bits = oc,
                    pa_disable = pa,
                    tx_iq_dbfs = txPow > 0 ? 10 * Math.Log10(txPow) : -200.0,
                    tx_tone_hz = txTone,
                    fwd_w = Math.Round(fwdW, 2),
                    rev_w = Math.Round(fwdW * gamma * gamma, 2),
                    load_swr = _loadSwr,
                    radio_ptt = _radioPtt,
                    step_att_db = stepAtt,
                    alex_att_db = 10 * alexAtt,
                    ant,
                    rx_only = rxOnly,
                    rx_out = rxOut,
                };
                string json = JsonSerializer.Serialize(status);
                if (_streaming) Console.WriteLine("radiosim: " + json);
                if (statusFile != null)
                {
                    try { File.WriteAllText(statusFile + ".tmp", json); File.Move(statusFile + ".tmp", statusFile, true); } catch (IOException) { }
                }
            }
        }

        /// <summary>Power at frequency f (may be negative) of the complex signal i + jq.</summary>
        private static double GoertzelComplex(float[] i, float[] q, double f, double fs)
        {
            double w = -2.0 * Math.PI * f / fs, re = 0, im = 0, c = 1, sn = 0, cw = Math.Cos(w), sw = Math.Sin(w);
            for (int n = 0; n < i.Length; n++)
            {
                // (i + jq) * e^{jwn}
                re += i[n] * c - q[n] * sn;
                im += i[n] * sn + q[n] * c;
                double c2 = c * cw - sn * sw;
                sn = c * sw + sn * cw;
                c = c2;
            }
            return re * re + im * im;
        }

        private static double Goertzel(float[] x, double f, double fs)
        {
            double w = 2.0 * Math.PI * f / fs, c = 2.0 * Math.Cos(w), s1 = 0, s2 = 0;
            foreach (float v in x) { double s0 = v + c * s1 - s2; s2 = s1; s1 = s0; }
            return s1 * s1 + s2 * s2 - c * s1 * s2;
        }
    }
}
