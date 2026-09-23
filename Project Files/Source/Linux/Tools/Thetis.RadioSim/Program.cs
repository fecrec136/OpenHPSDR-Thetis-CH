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
    without a sound card.

usage: thetis-radiosim [--bind IP] [--carrier MHz[:dBFS]]... [--noise dBFS] [--status-file PATH] [--textbook-iq]

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

        // --- received EP2 audio analysis ---
        private static readonly float[] _audio = new float[24000];     // last 0.5 s of left channel at 48 kHz
        private static int _audioPos;
        private static long _audioSamples;

        private static readonly List<Carrier> _carriers = new List<Carrier>();
        private static double _noise = Math.Pow(10.0, -110.0 / 20.0);
        private static Socket _sock;
        // OpenHPSDR hardware delivers the spectrum mirrored relative to I=cos, Q=sin;
        // the (unmodified) Thetis receive chain expects exactly that, so the
        // simulator sends Q = -sin by default.  --textbook-iq sends Q = +sin.
        private static double _qSign = -1.0;

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
                    case "--carrier":
                        string[] p = args[++i].Split(':');
                        double f = double.Parse(p[0], CultureInfo.InvariantCulture) * 1e6;
                        double db = p.Length > 1 ? double.Parse(p[1], CultureInfo.InvariantCulture) : -60.0;
                        _carriers.Add(new Carrier(f, Math.Pow(10.0, db / 20.0)));
                        break;
                    case "-h":
                    case "--help":
                        Console.WriteLine("usage: thetis-radiosim [--bind IP] [--carrier MHz[:dBFS]]... [--noise dBFS] [--status-file PATH] [--textbook-iq]");
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
                lock (_lock)
                {
                    if (addr == 0)
                    {
                        _rateBits = c1 & 3;
                        _nddc = ((c4 >> 3) & 7) + 1;
                    }
                    else if (addr >= 2 && addr <= 9)
                    {
                        _ddcFreq[addr - 2] = ((long)c1 << 24) | ((long)c2 << 16) | ((long)c3 << 8) | c4;
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
                int rate, nddc; long[] freqs = new long[8]; IPEndPoint host;
                lock (_lock)
                {
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
                    pkt[b + 3] = 0;         // C0: no PTT, register 0
                    for (int s = 0; s < spr; s++)
                    {
                        for (int d = 0; d < nddc; d++)
                        {
                            double i = _noise * Gauss(rng), q = _noise * Gauss(rng);
                            for (int c = 0; c < _carriers.Count; c++)
                            {
                                double offset = _carriers[c].FrequencyHz - freqs[d];
                                if (Math.Abs(offset) >= rate / 2.0) continue;
                                int pi = d * 8 + c;
                                phase[pi] += 2.0 * Math.PI * offset / rate;
                                if (phase[pi] > Math.PI) phase[pi] -= 2.0 * Math.PI;
                                else if (phase[pi] < -Math.PI) phase[pi] += 2.0 * Math.PI;
                                i += _carriers[c].Amplitude * Math.Cos(phase[pi]);
                                q += _qSign * _carriers[c].Amplitude * Math.Sin(phase[pi]);
                            }
                            int k = b + 8 + s * (6 * nddc + 2) + d * 6;
                            Put24(pkt, k, i);
                            Put24(pkt, k + 3, q);
                        }
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
                var status = new
                {
                    streaming = _streaming,
                    sample_rate = rate,
                    nddc,
                    ddc0_hz = rx1,
                    packets_in = Interlocked.Read(ref _packetsIn),
                    packets_out = Interlocked.Read(ref _packetsOut),
                    audio_samples = total,
                    audio_rms_dbfs = rms > 0 ? 20 * Math.Log10(rms) : -200.0,
                    audio_peak_hz = bestF,
                };
                string json = JsonSerializer.Serialize(status);
                if (_streaming) Console.WriteLine("radiosim: " + json);
                if (statusFile != null)
                {
                    try { File.WriteAllText(statusFile + ".tmp", json); File.Move(statusFile + ".tmp", statusFile, true); } catch (IOException) { }
                }
            }
        }

        private static double Goertzel(float[] x, double f, double fs)
        {
            double w = 2.0 * Math.PI * f / fs, c = 2.0 * Math.Cos(w), s1 = 0, s2 = 0;
            foreach (float v in x) { double s0 = v + c * s1 - s2; s2 = s1; s1 = s0; }
            return s1 * s1 + s2 * s2 - c * s1 * s2;
        }
    }
}
