/*  RadioController.cs

This file is part of a program that implements a Software-Defined Radio.

UI-independent control of one OpenHPSDR radio: DSP start-up, discovery,
power on/off, receiver 1 tuning/mode/filter/AGC/volume, VAC (PC audio), the
S-meter and the panadapter data.  It reproduces the order of operations of
the Windows console (console.cs chkPower_CheckedChanged, radio.cs,
audio.cs, NetworkIO.cs) so that wdsp and ChannelMaster are driven exactly
as they are on Windows.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using Thetis.Audio;

namespace Thetis.Radio
{
    public sealed class DiscoveredRadio
    {
        public RadioInfo Info { get; init; }
        public NicRadioScanResult Nic { get; init; }
        public HPSDRModel SuggestedModel => RadioController.SuggestModel(Info.DeviceType);
        public override string ToString() =>
            $"{Info.DeviceType} {Info.IpAddress} ({(Info.Protocol == RadioDiscoveryRadioProtocol.P2 ? "P2" : "P1")}, fw {Info.CodeVersion}){(Info.IsBusy ? " [busy]" : "")}";
    }

    public sealed unsafe partial class RadioController : IDisposable
    {
        public const int MaxPixels = 16384;             // dMAX_PIXELS in wdsp/comm.h
        public static readonly int[] SampleRates = { 48000, 96000, 192000, 384000 };

        private readonly string _dataDir;
        private SpecHPSDR _spec;
        private bool _dspReady;
        private bool _powerOn;
        private HPSDRModel _model = HPSDRModel.HERMES;
        private int _sampleRate = 192000;
        private double _frequencyMHz = 7.1;
        private DSPMode _mode = DSPMode.LSB;
        private FilterPreset _filter;
        private AGCMode _agc = AGCMode.MED;
        private double _agcTop = 90.0;              // console default RXAGCMaxGain
        private double _volume = 0.5;               // 0..1, ChannelMaster audio mixer
        private bool _vacEnabled;
        private int _vacHostApi = -1;
        private int _vacOutputDevice = -1;          // per-host-API index
        private int _vacInputDevice = -1;
        private bool _vacRunning;
        private bool _nr;
        private bool _anf;
        private readonly object _specLock = new object();

        public RadioController(string dataDirectory)
        {
            _dataDir = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
            Directory.CreateDirectory(_dataDir);
            _filter = FilterPresets.For(_mode)[FilterPresets.DefaultIndex];
            // Never leave the radio transmitting when the process goes away: this runs
            // on SIGTERM / SIGINT / Environment.Exit and before an unhandled exception
            // ends the process.
            AppDomain.CurrentDomain.ProcessExit += OnProcessEnding;
            AppDomain.CurrentDomain.UnhandledException += OnProcessEnding;
        }

        private void OnProcessEnding(object sender, EventArgs e)
        {
            try { Stop(); }
            catch (Exception) { }
        }

        /// <summary>Human-readable progress/status messages (may be raised on any thread).</summary>
        public event Action<string> Status;
        private void Report(string s) => Status?.Invoke(s);

        public bool DspReady => _dspReady;
        public bool PowerOn => _powerOn;
        public RadioInfo ConnectedRadio { get; private set; }
        public string DataDirectory => _dataDir;

        #region start-up

        [DllImport("wdsp.dll", EntryPoint = "wisdom_get_status", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wisdom_get_status();

        /// <summary>Current status text of an FFTW wisdom build (for a progress display).</summary>
        public static string WisdomStatus()
        {
            IntPtr p = wisdom_get_status();
            return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p);
        }

        public bool WisdomFileExists => File.Exists(Path.Combine(_dataDir, "wdspWisdom00"));

        /// <summary>
        /// Load the native libraries, build or load FFTW wisdom, and create
        /// ChannelMaster (RadioDSP.CreateDSP in radio.cs).  The first run
        /// builds the wisdom file, which can take several minutes, so call
        /// this off the UI thread.
        /// </summary>
        public void InitializeDsp()
        {
            if (_dspReady) return;
            if (!NativeLibraries.TryLoadAll(out string error))
                throw new DllNotFoundException(error);

            Report(WisdomFileExists ? "Loading FFT wisdom..." : "Building FFT wisdom (first run, this can take several minutes)...");
            string dir = _dataDir.EndsWith(Path.DirectorySeparatorChar) ? _dataDir : _dataDir + Path.DirectorySeparatorChar;
            bool rebuilt = WDSP.WDSPwisdom(dir) == 1;

            string cache = Path.Combine(_dataDir, "impulse_cache.dat");
            if (rebuilt && File.Exists(cache)) File.Delete(cache);   // cache is invalid after new wisdom
            WDSP.init_impulse_cache(1);
            if (!rebuilt && File.Exists(cache)) WDSP.read_impulse_cache(cache);

            Report("Starting audio...");
            AudioDevices.EnsureInitialized();       // PortAudio, shared with ChannelMaster's VAC

            Report("Creating DSP channels...");
            cmaster.CMCreateCMaster();

            _spec = new SpecHPSDR(0);
            _spec.FFTSize = 16384;
            _spec.WindowType = 6;                   // 7-term Blackman-Harris (console default)
            _spec.FrameRate = 30;
            _spec.Pixels = 1024;
            _spec.SampleRate = _sampleRate;
            _spec.BlockSize = cmaster.GetBuffSize(_sampleRate);
            _spec.DetTypePan = 0;                   // peak
            _spec.DetTypeWF = 0;
            _spec.AverageModeWF = 0;
            _spec.AvTau = 0.12;
            _spec.AverageMode = 1;                  // recursive (log) average
            _spec.Update = true;

            _dspReady = true;
            Report("Ready");
        }

        #endregion

        #region discovery

        public static HPSDRModel SuggestModel(HPSDRHW hw)
        {
            switch (hw)
            {
                case HPSDRHW.HermesLite: return HPSDRModel.HERMESLITE;
                case HPSDRHW.Hermes: return HPSDRModel.ANAN100;
                case HPSDRHW.HermesII: return HPSDRModel.ANAN100B;
                case HPSDRHW.Angelia: return HPSDRModel.ANAN100D;
                case HPSDRHW.Orion: return HPSDRModel.ANAN200D;
                case HPSDRHW.OrionMKII: return HPSDRModel.ANAN7000D;
                case HPSDRHW.Saturn: return HPSDRModel.ANAN_G2;
                case HPSDRHW.SaturnMKII: return HPSDRModel.ANAN_G2;
                case HPSDRHW.HermesC10: return HPSDRModel.ANAN_G2E;
                case HPSDRHW.Atlas: return HPSDRModel.HPSDR;
                default: return HPSDRModel.HERMES;
            }
        }

        /// <summary>Search every network interface for OpenHPSDR radios (P1 and P2).</summary>
        public static List<DiscoveredRadio> Discover(ScanPerformanceProfile profile = ScanPerformanceProfile.Balanced,
                                                     bool includeLoopback = false)
        {
            var options = new RadioDiscoveryOptions
            {
                IncludeEthernet = true,
                IncludeWireless = true,
                IncludeOtherInterfaceTypes = true,
                AllowLoopback = includeLoopback,
                AllowAPIPA = true,
                IncludeGeneralBroadcast = true,
                ScanPerformance = profile,
                ProtocolMode = RadioDiscoveryProtocolMode.Auto,
            };
            var result = new List<DiscoveredRadio>();
            foreach (NicRadioScanResult nic in new RadioDiscoveryService().DiscoverUsingAllNics(options))
                foreach (RadioInfo r in nic.Radios)
                    result.Add(new DiscoveredRadio { Info = r, Nic = nic });
            return result;
        }

        #endregion

        #region power

        /// <summary>
        /// Connect to a radio and start receiving.  'model' selects the radio
        /// model (several models share one board type).  Returns false with
        /// 'error' set if the radio could not be started.
        /// </summary>
        public bool Start(DiscoveredRadio radio, HPSDRModel model, out string error)
        {
            error = null;
            if (!_dspReady) { error = "DSP is not initialised."; return false; }
            if (_powerOn) Stop();
            RadioInfo ri = radio.Info;

            if (ri.DeviceType == HPSDRHW.HermesII && ri.CodeVersion < 103)
            {
                error = "Invalid firmware: this radio needs firmware 10.3 or later.";
                return false;
            }

            _model = model;
            NetworkIO.CurrentRadioProtocol = ri.Protocol == RadioDiscoveryRadioProtocol.P1 ? RadioProtocol.USB : RadioProtocol.ETH;
            HardwareSpecific.Model = model;             // pushes model-specific settings into ChannelMaster
            cmaster.CMLoadRouterAll(model);             // how incoming DDC streams are routed to receivers
            BandFilters.UseN2adrFilterBoard(model == HPSDRModel.HERMESLITE && Hl2N2adrFilterBoard);
            NetworkIO.SetADC_cntrl_P1(DdcSetup.RxAdcCtrlP1);

            Report($"Connecting to {ri.IpAddress}...");
            int protocol = NetworkIO.CurrentRadioProtocol == RadioProtocol.USB ? 0 : 1;
            int rc = NetworkIO.nativeInitMetis(ri.IpAddress.ToString(), ri.DiscoveryPortBase,
                                               radio.Nic.LocalIPv4.ToString(), 0, protocol, (int)model, 1);
            if (rc != 0)
            {
                error = $"Could not open the network connection to the radio (error {rc}).";
                return false;
            }
            NetworkIO.BoardID = ri.DeviceType;
            NetworkIO.FWCodeVersion = ri.CodeVersion;
            NetworkIO.BetaVersion = ri.BetaVersion;
            NetworkIO.Protocol2VersionSupported = ri.Protocol2Supported;

            // per-protocol transmitter settings (audio.cs Audio.Start -> console.SampleRateTX):
            // P1 carries 48 kHz TX I/Q in the same frames as the receive audio, P2 192 kHz.
            // ChannelMaster only sends a P1 frame when both are ready, so a wrong rate here
            // starves the radio's audio codec.
            cmaster.SetXmtrChannelOutrate(0, protocol == 0 ? 48000 : 192000, false);
            WDSP.SetTXACFIRRun(cmaster.chid(cmaster.inid(1, 0), 0), protocol == 1);

            ApplySampleRate(_sampleRate, restart: false);
            DdcSetup.UpdateDDCs(model, _sampleRate, _sampleRate, false);
            DdcSetup.UpdateAAudioMixerStates(model, true, false);
            SendFrequency();

            Report("Starting data stream...");
            if (NetworkIO.StartAudioNative() != 0)
            {
                error = "The radio did not start streaming.  Is it powered and not in use by another program?";
                NetworkIO.StopAudio();
                return false;
            }

            ApplyDsp();
            ApplyTxDsp();
            ApplyVacBypass();
            WDSP.SetChannelState(WDSP.id(0, 0), 1, 1);
            cmaster.SetRunPanadapter(0, true);
            lock (_specLock) _spec.initAnalyzer();
            cmaster.CMSetAudioVolume(_volume);
            _powerOn = true;
            ConnectedRadio = ri;

            if (_vacEnabled) StartVac();
            StartTxSupervisor();
            Report($"Connected to {ri.DeviceType} at {ri.IpAddress}");
            return true;
        }

        public void Stop()
        {
            if (!_powerOn) return;
            StopTxSupervisor();
            lock (_txLock)
            {
                if (_mox) KeyDown();            // never leave the radio transmitting
            }
            _powerOn = false;
            if (NetworkIO.getHaveSync() != 0)
                WDSP.SetChannelState(WDSP.id(0, 0), 0, 1);
            DdcSetup.UpdateAAudioMixerStates(_model, false, false);
            NetworkIO.StopAudio();
            StopVac();
            ConnectedRadio = null;
            Report("Stopped");
        }

        /// <summary>True while the radio is sending data (P2: sync; P1: always true once started).</summary>
        public bool HaveSync => _powerOn && NetworkIO.getHaveSync() != 0;

        #endregion

        #region receiver 1

        public double FrequencyMHz
        {
            get => _frequencyMHz;
            set
            {
                if (value < 0.0 || value > 61.44) return;
                lock (_txLock)
                {
                    double old = _frequencyMHz;
                    _frequencyMHz = value;
                    if (!_powerOn) return;
                    SendFrequency();
                    if (_mox)
                    {
                        // Retuning while transmitting is allowed inside the same band and
                        // allocation (the filters stay the same); anything else unkeys.
                        bool sameBand = BandPlanRegions.BandFromFrequency(old) == BandPlanRegions.BandFromFrequency(value);
                        if (sameBand && WhyTxRefused(_tuning) == null)
                        {
                            double txMHz = TxDdsMHz(_tuning);
                            NetworkIO.VFOfreq(0, txMHz, 1);
                            BandFilters.Apply(HardwareSpecific.Hardware, _frequencyMHz, txMHz, true, _tuning);
                        }
                        else
                        {
                            KeyDown();
                            TxRefused?.Invoke("Transmit stopped: the new frequency is outside the band being transmitted on.");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// console UpdateRX1DDSFreq + UpdateTXDDSFreq: receive DDCs, the TX
        /// frequency, and the band filters for the new frequency.
        /// </summary>
        private void SendFrequency()
        {
            foreach (int ddc in DdcSetup.Rx1FrequencyDdcs(_model))
                NetworkIO.VFOfreq(ddc, _frequencyMHz, 0);
            if (!_mox)
            {
                NetworkIO.VFOfreq(0, _frequencyMHz, 1);
                BandFilters.Apply(HardwareSpecific.Hardware, _frequencyMHz, _frequencyMHz, false, false);
            }
        }

        /// <summary>Hermes-Lite 2 with the N2ADR filter board: drive its relays from the OC outputs.</summary>
        public bool Hl2N2adrFilterBoard { get; set; }

        public DSPMode Mode
        {
            get => _mode;
            set
            {
                if (value == _mode) return;
                _mode = value;
                var presets = FilterPresets.For(value);
                _filter = presets[Math.Min(FilterPresets.DefaultIndex, presets.Count - 1)];
                Display.RX1DSPMode = value;
                lock (_txLock)
                {
                    if (_mox)
                    {
                        KeyDown();
                        TxRefused?.Invoke("Transmit stopped: mode changed while transmitting.");
                    }
                    if (_powerOn) { ApplyDspRate(); ApplyDsp(); ApplyTxDsp(); }
                }
            }
        }

        public FilterPreset Filter
        {
            get => _filter;
            set
            {
                _filter = value;
                if (_powerOn) ApplyFilter();
            }
        }

        public AGCMode Agc
        {
            get => _agc;
            set
            {
                _agc = value;
                if (_powerOn) WDSP.SetRXAAGCMode(WDSP.id(0, 0), value);
            }
        }

        /// <summary>AGC maximum gain / RF gain in dB (console RXAGCMaxGain, default 90).</summary>
        public double AgcTop
        {
            get => _agcTop;
            set
            {
                _agcTop = Math.Clamp(value, -20.0, 120.0);
                if (_powerOn) WDSP.SetRXAAGCTop(WDSP.id(0, 0), _agcTop);
            }
        }

        /// <summary>Receive audio volume 0..1.</summary>
        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0, 1.0);
                if (_powerOn) cmaster.CMSetAudioVolume(_volume);
            }
        }

        /// <summary>Spectral noise reduction (NR2 / EMNR).</summary>
        public bool NoiseReduction
        {
            get => _nr;
            set { _nr = value; if (_powerOn) WDSP.SetRXAEMNRRun(WDSP.id(0, 0), value ? 1 : 0); }
        }

        /// <summary>Automatic notch filter.</summary>
        public bool AutoNotch
        {
            get => _anf;
            set { _anf = value; if (_powerOn) WDSP.SetRXAANFRun(WDSP.id(0, 0), value); }
        }

        public int SampleRate
        {
            get => _sampleRate;
            set
            {
                if (!SampleRates.Contains(value) || value == _sampleRate) return;
                if (_powerOn) ApplySampleRate(value, restart: true);
                else _sampleRate = value;
            }
        }

        private void ApplyDspRate()
        {
            // RadioDSP.SampleRate in radio.cs: FM demodulates at 192k, everything else at 48k
            WDSP.SetDSPSamplerate(WDSP.id(0, 0), _mode == DSPMode.FM ? 192000 : 48000);
            WDSP.SetDSPSamplerate(WDSP.id(0, 1), _mode == DSPMode.FM ? 192000 : 48000);
            WDSP.SetDSPSamplerate(WDSP.id(2, 0), 48000);
            WDSP.SetDSPSamplerate(WDSP.id(2, 1), 48000);
        }

        private void ApplyFilter()
        {
            int ch = WDSP.id(0, 0);
            WDSP.RXANBPSetFreqs(ch, _filter.Low, _filter.High);
            WDSP.SetRXABandpassFreqs(ch, _filter.Low, _filter.High);
            WDSP.SetRXASNBAOutputBandwidth(ch, _filter.Low, _filter.High);
        }

        private void ApplyDsp()
        {
            int ch = WDSP.id(0, 0);
            ApplyDspRate();
            WDSP.SetRXAMode(ch, _mode);
            ApplyFilter();
            WDSP.SetRXAAGCMode(ch, _agc);
            WDSP.SetRXAAGCTop(ch, _agcTop);
            WDSP.SetRXAPanelGain1(ch, 1.0);
            WDSP.SetRXAEMNRRun(ch, _nr ? 1 : 0);
            WDSP.SetRXAANFRun(ch, _anf);
        }

        /// <summary>Port of the ETH branch of Setup.comboAudioSampleRate1_SelectedIndexChanged.</summary>
        private void ApplySampleRate(int rate, bool restart)
        {
            if (restart)
            {
                WDSP.SetChannelState(WDSP.id(0, 1), 0, 0);
                WDSP.SetChannelState(WDSP.id(0, 0), 0, 1);
                Thread.Sleep(10);
                cmaster.SetAAudioMixStates((void*)0, 0, 3, 0);
                if (_vacRunning) StopVac();
                NetworkIO.EnableRx(0, 0);
                NetworkIO.EnableRx(1, 0);
                NetworkIO.EnableRx(2, 0);
                Thread.Sleep(20);
            }

            _sampleRate = rate;
            cmaster.SetXcmInrate(0, rate);                  // Audio.SampleRate1
            // Audio.SampleRateRX2: receiver 2 runs at the same rate.  On P1 every
            // DDC streams at the one radio rate, and RX2's audio is always in the
            // mixer, which waits for every active input -- a stale RX2 rate
            // throttles the audio sent to the radio.
            cmaster.SetXcmInrate(1, rate);
            ApplyDspRate();                                 // RadioDSP.SampleRate
            lock (_specLock)
            {
                _spec.SampleRate = rate;
                _spec.BlockSize = cmaster.GetBuffSize(rate);
            }

            if (restart)
            {
                DdcSetup.UpdateDDCs(_model, rate, rate, false);
                Thread.Sleep(1);
                cmaster.SetAAudioMixStates((void*)0, 0, 3, 3);
                if (_vacEnabled) StartVac();
                WDSP.SetChannelState(WDSP.id(0, 0), 1, 0);
            }
        }

        #endregion

        #region meters

        /// <summary>Receiver 1 peak signal level in dBm, calibrated with the model's default offset.</summary>
        public float SignalDbm()
        {
            if (!_powerOn) return -140f;
            float v = WDSP.CalculateRXMeter(0, 0, WDSP.MeterType.SIGNAL_STRENGTH);
            return v + HardwareSpecific.RXMeterCalbrationOffsetDefaults(_model);
        }

        public static string SUnits(float dbm)
        {
            // S9 = -73 dBm, 6 dB per S unit (HF)
            if (dbm <= -127f) return "S0";
            if (dbm <= -73f) return "S" + (int)Math.Round((dbm + 127f) / 6f);
            return "S9+" + (int)Math.Round(dbm + 73f);
        }

        #endregion

        #region panadapter

        /// <summary>Frequency span (Hz, relative to the centre) covered by the spectrum pixels.</summary>
        public (int low, int high) SpectrumSpan => (Display.RXDisplayLow, Display.RXDisplayHigh);

        /// <summary>Set the number of spectrum pixels (normally the panadapter width).</summary>
        public void SetSpectrumWidth(int pixels)
        {
            pixels = Math.Clamp(pixels, 64, MaxPixels);
            lock (_specLock)
            {
                if (_spec == null || _spec.Pixels == pixels) return;
                _spec.Pixels = pixels;
                if (_powerOn) _spec.initAnalyzer();
            }
        }

        /// <summary>Zoom 0 (full span) .. 1 (maximum zoom).</summary>
        public void SetSpectrumZoom(double zoom)
        {
            lock (_specLock)
            {
                if (_spec == null) return;
                _spec.ZoomSlider = Math.Clamp(zoom, 0.0, 1.0);
                _spec.PanSlider = 0.5;
                if (_powerOn) _spec.initAnalyzer();
            }
        }

        public int SpectrumPixels { get { lock (_specLock) return _spec?.Pixels ?? 0; } }

        /// <summary>
        /// Copy the latest spectrum (dBm per pixel) into 'pixels'.  Returns
        /// false if no new frame is available.  'waterfall' selects the
        /// waterfall pixel output, which has its own detector/averaging.
        /// </summary>
        public bool GetSpectrum(float[] pixels, bool waterfall = false)
        {
            if (!_powerOn || pixels == null) return false;
            int flag = 0;
            fixed (float* p = pixels)
                SpecHPSDRDLL.GetPixels(0, waterfall ? 1 : 0, p, ref flag);
            if (flag == 0) return false;
            float offset = HardwareSpecific.RXDisplayCalbrationOffsetDefauls(_model);
            if (offset != 0f)
                for (int i = 0; i < pixels.Length; i++) pixels[i] += offset;
            return true;
        }

        #endregion

        #region VAC (PC audio)

        /// <summary>Route receive audio to a PC sound device through VAC 1.</summary>
        public void ConfigureVac(bool enabled, int hostApi, int outputDevice, int inputDevice)
        {
            bool wasRunning = _vacRunning;
            if (wasRunning) StopVac();
            _vacEnabled = enabled;
            _vacHostApi = hostApi;
            _vacOutputDevice = outputDevice;
            _vacInputDevice = inputDevice;
            if (_vacEnabled && _powerOn) StartVac();
        }

        public bool VacRunning => _vacRunning;

        private void StartVac()
        {
            if (_vacHostApi < 0 || _vacOutputDevice < 0) { Report("VAC: no output device selected"); return; }
            int input = _vacInputDevice >= 0 ? _vacInputDevice : _vacOutputDevice;
            var outInfo = AudioDevices.Devices(_vacHostApi).FirstOrDefault(d => d.HostApiDeviceIndex == _vacOutputDevice);
            var inInfo = AudioDevices.Devices(_vacHostApi).FirstOrDefault(d => d.HostApiDeviceIndex == input);
            double outLatency = outInfo?.DefaultLowOutputLatency ?? 0.12;
            double inLatency = inInfo?.DefaultLowInputLatency ?? 0.12;

            // Audio.EnableVAC1 in audio.cs, with its default ring-buffer settings
            ivac.SetIVAChostAPIindex(0, _vacHostApi);
            ivac.SetIVACinputDEVindex(0, input);
            ivac.SetIVACoutputDEVindex(0, _vacOutputDevice);
            ivac.SetIVACnumChannels(0, 2);
            ivac.SetIVACstereo(0, 1);
            ivac.SetIVACvacRate(0, 48000);
            ivac.SetIVACvacSize(0, 1024);
            ivac.SetIVACInLatency(0, 0.120, 0);
            ivac.SetIVACOutLatency(0, 0.120, 0);
            ivac.SetIVACPAInLatency(0, inLatency, 0);
            ivac.SetIVACPAOutLatency(0, outLatency, 1);
            ivac.SetIVACFeedbackGain(0, 0, 4.0e-06);
            ivac.SetIVACFeedbackGain(0, 1, 4.0e-06);
            ivac.SetIVACSlewTime(0, 0, 0.003);
            ivac.SetIVACSlewTime(0, 1, 0.003);
            ivac.SetIVACPropRingMin(0, 0, 4096);
            ivac.SetIVACPropRingMin(0, 1, 4096);
            ivac.SetIVACPropRingMax(0, 0, 16384);
            ivac.SetIVACPropRingMax(0, 1, 16384);
            ivac.SetIVACFFRingMin(0, 0, 4096);
            ivac.SetIVACFFRingMin(0, 1, 4096);
            ivac.SetIVACFFRingMax(0, 0, 262144);
            ivac.SetIVACFFRingMax(0, 1, 262144);
            ivac.SetIVACFFAlpha(0, 0, 0.01);
            ivac.SetIVACFFAlpha(0, 1, 0.01);
            ivac.SetIVACinitialVars(0, 1.0, 1.0);

            if (ivac.StartAudioIVAC(0) == 1)
            {
                ivac.SetIVACrun(0, 1);
                _vacRunning = true;
                ApplyMicGain();                     // the PC microphone is only live while VAC runs
                Report($"PC audio on {outInfo?.Name ?? "device " + _vacOutputDevice}");
            }
            else
            {
                Report("VAC: could not open the selected sound device");
            }
        }

        private void StopVac()
        {
            if (!_vacRunning) return;
            ivac.SetIVACrun(0, 0);
            ivac.StopAudioIVAC(0);
            _vacRunning = false;
            ApplyMicGain();
        }

        #endregion

        public void Dispose()
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessEnding;
            AppDomain.CurrentDomain.UnhandledException -= OnProcessEnding;
            Stop();
            if (_dspReady)
            {
                WDSP.save_impulse_cache(Path.Combine(_dataDir, "impulse_cache.dat"));
                WDSP.destroy_impulse_cache();
                cmaster.DestroyRadio();
                _dspReady = false;
            }
            AudioDevices.Terminate();
        }
    }
}
