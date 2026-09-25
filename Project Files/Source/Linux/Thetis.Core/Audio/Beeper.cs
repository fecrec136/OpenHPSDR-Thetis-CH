/*  Beeper.cs

This file is part of a program that implements a Software-Defined Radio.

A short beep on the computer's default sound output (the band limit
warning), played through PortAudio -- the same instance ChannelMaster's
PC audio uses -- on a background thread.  With PulseAudio or PipeWire it is
mixed with the receiver audio.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Thetis.Audio
{
    public static class Beeper
    {
        private static int _busy;

        /// <summary>Why the last beep could not be played (null if it was).</summary>
        public static string LastError { get; private set; }

        /// <summary>Play a beep and return at once; a beep asked for while one plays is dropped.</summary>
        public static void Beep(double hz = 880, int ms = 160, double level = 0.3)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            Task.Run(() =>
            {
                try { Play(hz, ms, level); LastError = null; }
                catch (Exception ex) { LastError = ex.Message; }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }

        private static void Play(double hz, int ms, double level)
        {
            const double rate = 48000;
            AudioDevices.EnsureInitialized();
            int n = (int)(rate * ms / 1000);
            var buf = new float[n];
            int ramp = (int)(rate * 0.008);          // 8 ms fade in and out: no clicks
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)ramp);
                buf[i] = (float)(level * env * Math.Sin(2 * Math.PI * hz * i / rate));
            }
            int rc = Pa_OpenDefaultStream(out IntPtr stream, 0, 1, PaFloat32, rate, 0, IntPtr.Zero, IntPtr.Zero);
            if (rc != 0 || stream == IntPtr.Zero) throw new InvalidOperationException("no sound output (PortAudio error " + rc + ")");
            try
            {
                Pa_StartStream(stream);
                Pa_WriteStream(stream, buf, (nuint)n);
                Pa_StopStream(stream);                // returns once the beep has played
            }
            finally
            {
                Pa_CloseStream(stream);
            }
        }

        #region PortAudio (unsigned long is 64-bit on Linux: nuint)

        private const nuint PaFloat32 = 0x00000001;

        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Pa_OpenDefaultStream(out IntPtr stream, int inputChannels, int outputChannels, nuint sampleFormat,
                                                       double sampleRate, nuint framesPerBuffer, IntPtr callback, IntPtr userData);

        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Pa_StartStream(IntPtr stream);

        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Pa_WriteStream(IntPtr stream, float[] buffer, nuint frames);

        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Pa_StopStream(IntPtr stream);

        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Pa_CloseStream(IntPtr stream);

        #endregion
    }
}
