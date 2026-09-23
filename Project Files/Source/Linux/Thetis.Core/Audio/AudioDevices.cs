/*  AudioDevices.cs

This file is part of a program that implements a Software-Defined Radio.

Enumerates the PC sound devices through libPA19 (Thetis' PortAudio fork),
for choosing where VAC sends receive audio.  On Linux the host APIs are
ALSA, PulseAudio (PipeWire provides this on Linux Mint 22) and JACK.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Thetis.Audio
{
    public sealed class AudioHostApi
    {
        public int Index { get; init; }
        public string Name { get; init; }
        public int DefaultInputDevice { get; init; }
        public int DefaultOutputDevice { get; init; }
        public override string ToString() => Name;
    }

    public sealed class AudioDevice
    {
        /// <summary>Global PortAudio device index.</summary>
        public int Index { get; init; }
        /// <summary>Index of the device within its host API (what ivac's SetIVAC*DEVindex expects).</summary>
        public int HostApiDeviceIndex { get; init; }
        public int HostApi { get; init; }
        public string Name { get; init; }
        public int MaxInputChannels { get; init; }
        public int MaxOutputChannels { get; init; }
        public double DefaultLowInputLatency { get; init; }
        public double DefaultLowOutputLatency { get; init; }
        public double DefaultSampleRate { get; init; }
        public override string ToString() => Name;
    }

    public static class AudioDevices
    {
        // PaDeviceInfo / PaHostApiInfo contain no C 'long' fields, so this layout
        // is the same on Windows (LLP64) and Linux (LP64).
        [StructLayout(LayoutKind.Sequential)]
        private struct PaDeviceInfo
        {
            public int structVersion;
            public IntPtr name;
            public int hostApi;
            public int maxInputChannels;
            public int maxOutputChannels;
            public double defaultLowInputLatency;
            public double defaultLowOutputLatency;
            public double defaultHighInputLatency;
            public double defaultHighOutputLatency;
            public double defaultSampleRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PaHostApiInfo
        {
            public int structVersion;
            public int type;
            public IntPtr name;
            public int deviceCount;
            public int defaultInputDevice;
            public int defaultOutputDevice;
        }

        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int PA_Initialize();
        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int PA_Terminate();
        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int PA_GetHostApiCount();
        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr PA_GetHostApiInfo(int hostApi);
        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int PA_GetDeviceCount();
        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr PA_GetDeviceInfo(int device);
        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int PA_HostApiDeviceIndexToDeviceIndex(int hostApi, int hostApiDeviceIndex);
        [DllImport("PA19.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr PA_GetErrorText(int error);

        private static bool _initialized;
        private static readonly object _lock = new object();

        /// <summary>Initialise PortAudio once for the process (ChannelMaster's VAC shares this instance).</summary>
        public static void EnsureInitialized()
        {
            lock (_lock)
            {
                if (_initialized) return;
                int rc = PA_Initialize();
                if (rc != 0)
                    throw new InvalidOperationException("PortAudio failed to initialise: " + Marshal.PtrToStringAnsi(PA_GetErrorText(rc)));
                _initialized = true;
            }
        }

        public static void Terminate()
        {
            lock (_lock)
            {
                if (!_initialized) return;
                PA_Terminate();
                _initialized = false;
            }
        }

        public static IReadOnlyList<AudioHostApi> HostApis()
        {
            EnsureInitialized();
            var list = new List<AudioHostApi>();
            int n = PA_GetHostApiCount();
            for (int i = 0; i < n; i++)
            {
                IntPtr p = PA_GetHostApiInfo(i);
                if (p == IntPtr.Zero) continue;
                PaHostApiInfo info = Marshal.PtrToStructure<PaHostApiInfo>(p);
                list.Add(new AudioHostApi
                {
                    Index = i,
                    Name = Marshal.PtrToStringUTF8(info.name),
                    DefaultInputDevice = info.defaultInputDevice,
                    DefaultOutputDevice = info.defaultOutputDevice,
                });
            }
            return list;
        }

        /// <summary>Devices of one host API, with their per-host-API indices.</summary>
        public static IReadOnlyList<AudioDevice> Devices(int hostApi)
        {
            EnsureInitialized();
            var list = new List<AudioDevice>();
            IntPtr hp = PA_GetHostApiInfo(hostApi);
            if (hp == IntPtr.Zero) return list;
            PaHostApiInfo host = Marshal.PtrToStructure<PaHostApiInfo>(hp);
            for (int i = 0; i < host.deviceCount; i++)
            {
                int global = PA_HostApiDeviceIndexToDeviceIndex(hostApi, i);
                IntPtr dp = PA_GetDeviceInfo(global);
                if (dp == IntPtr.Zero) continue;
                PaDeviceInfo d = Marshal.PtrToStructure<PaDeviceInfo>(dp);
                list.Add(new AudioDevice
                {
                    Index = global,
                    HostApiDeviceIndex = i,
                    HostApi = hostApi,
                    Name = Marshal.PtrToStringUTF8(d.name),
                    MaxInputChannels = d.maxInputChannels,
                    MaxOutputChannels = d.maxOutputChannels,
                    DefaultLowInputLatency = d.defaultLowInputLatency,
                    DefaultLowOutputLatency = d.defaultLowOutputLatency,
                    DefaultSampleRate = d.defaultSampleRate,
                });
            }
            return list;
        }
    }
}
