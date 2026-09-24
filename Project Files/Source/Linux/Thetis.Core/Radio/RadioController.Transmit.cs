/*  RadioController.Transmit.cs

This file is part of a program that implements a Software-Defined Radio.

Transmit: MOX, TUNE, drive, microphone source, transmit meters and the
transmit safety features.  The keying sequence follows console.cs
(chkMOX_CheckedChanged2, HdwMOXChanged, AudioMOXChanged, cmaster.Mox and
chkTUN_CheckedChanged); the drive calculation follows
setPowerFromDriveSlider(); SWR protection follows the console's meter
loop.

Safety, in addition to what the Windows console does:
  * transmitting is refused until the operator has enabled it and chosen
    a region (TransmitAllowed, Region)
  * everything transmitted must lie inside an amateur allocation for that
    region (BandPlanRegions.IsTxAllowed)
  * a transmit timeout (default 3 minutes) unkeys the radio
  * the radio is unkeyed on power-off and when it stops sending data

Copyright (C) 2000-2025 Original authors
Copyright (C) 2020-2026 Richard Samphire MW0LGE

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Diagnostics;
using System.Threading;

namespace Thetis.Radio
{
    public enum MicSource
    {
        /// <summary>The radio's microphone input (VAC bypassed on transmit).</summary>
        Radio,
        /// <summary>The PC sound input selected for VAC.</summary>
        Pc,
    }

    public enum TxSource { None, Manual, Tune, RadioPtt }

    public sealed unsafe partial class RadioController
    {
        // console.cs defaults
        private const int MoxDelayMs = 10;          // mox_delay: lets in-flight samples clear
        private const int RfDelayMs = 30;           // rf_delay: relays settle before RF
        private const int PttOutDelayMs = 20;       // ptt_out_delay: hardware switches back
        private const double MaxToneMag = 0.99999;  // MAX_TONE_MAG
        private const float SwrProtectionLimit = 2.0f;   // _swrProtectionLimit
        private const float TunePowerSwrIgnore = 35.0f;  // _tunePowerSwrIgnore
        private const int TxChannel = 10;           // WDSP.id(1, 0) = CMsubrcvr * CMrcvr

        private readonly object _txLock = new object();
        private bool _mox;
        private bool _tuning;
        private TxSource _txSource = TxSource.None;
        private readonly Stopwatch _txTimer = new Stopwatch();
        private Timer _supervisor;
        private int _highSwrCount;
        private bool _lastRadioPtt;

        private int _drivePercent = 10;
        private int _tunePercent = 10;
        private double _micGainDb = 10.0;
        private MicSource _micSource = MicSource.Radio;
        private int _txFilterLow = 100;             // udTXFilterLow default
        private int _txFilterHigh = 3000;           // udTXFilterHigh default

        /// <summary>Raised (on any thread) when a request to transmit is refused or transmit is stopped by a safety feature.</summary>
        public event Action<string> TxRefused;
        /// <summary>Raised (on any thread) when the radio keys or unkeys.</summary>
        public event Action TxStateChanged;

        /// <summary>Operator has enabled transmitting (off by default).</summary>
        public bool TransmitAllowed { get; set; }
        /// <summary>Region whose amateur allocations limit transmitting.</summary>
        public TxRegion Region { get; set; } = TxRegion.None;
        /// <summary>Unkey after this many seconds of continuous transmit (0 = never).</summary>
        public int TxTimeoutSeconds { get; set; } = 180;
        /// <summary>Key the radio from its own PTT input (microphone / footswitch).</summary>
        public bool RadioPttEnabled { get; set; } = true;
        /// <summary>SWR protection as in the Windows console (fold back drive above 2:1, unkey on open antenna).</summary>
        public bool SwrProtection { get; set; } = true;

        public bool Mox => _mox;
        public bool Tuning => _tuning;
        public TxSource TxSource => _txSource;
        public bool HighSwr { get; private set; }
        public float ForwardWatts { get; private set; }
        public float ReflectedWatts { get; private set; }
        public float Swr { get; private set; } = 1.0f;

        /// <summary>Drive level 0..100 (the console's "Drive" slider).</summary>
        public int DrivePercent
        {
            get => _drivePercent;
            set { _drivePercent = Math.Clamp(value, 0, 100); if (_mox && !_tuning) ApplyDrive(); }
        }

        /// <summary>Drive level 0..100 used while tuning.</summary>
        public int TunePercent
        {
            get => _tunePercent;
            set { _tunePercent = Math.Clamp(value, 0, 100); if (_tuning) ApplyDrive(); }
        }

        /// <summary>Microphone gain in dB (-40..+70 as in the console).</summary>
        public double MicGainDb
        {
            get => _micGainDb;
            set { _micGainDb = Math.Clamp(value, -40.0, 70.0); if (_powerOn) ApplyMicGain(); }
        }

        public MicSource MicSource
        {
            get => _micSource;
            set { _micSource = value; if (_powerOn) { ApplyMicGain(); ApplyVacBypass(); } }
        }

        /// <summary>True if the PC microphone is selected but PC audio (VAC) is not running, so the radio's mic is used.</summary>
        public bool PcMicUnavailable => _micSource == MicSource.Pc && !_vacRunning;

        public (int low, int high) TxFilter
        {
            get => (_txFilterLow, _txFilterHigh);
            set
            {
                _txFilterLow = Math.Clamp(Math.Min(value.low, value.high), 0, 10000);
                _txFilterHigh = Math.Clamp(Math.Max(value.low, value.high), 0, 10000);
                if (_powerOn) ApplyTxDsp();
            }
        }

        #region TX DSP set-up

        private static bool IsCw(DSPMode m) => m == DSPMode.CWL || m == DSPMode.CWU;

        /// <summary>The mode the TX channel runs in (CW is sent as a carrier in the SSB mode of the same side).</summary>
        private DSPMode TxDspMode => _mode == DSPMode.CWL ? DSPMode.LSB : _mode == DSPMode.CWU ? DSPMode.USB : _mode;

        /// <summary>console.UpdateTXLowHighFilterForMode</summary>
        private (int low, int high) TxPassband(DSPMode mode)
        {
            int low = _txFilterLow, high = _txFilterHigh;
            switch (mode)
            {
                case DSPMode.LSB: case DSPMode.CWL: case DSPMode.DIGL: return (-high, -low);
                case DSPMode.USB: case DSPMode.CWU: case DSPMode.DIGU: return (low, high);
                case DSPMode.DSB: case DSPMode.AM: case DSPMode.SAM: return (-high, high);
                case DSPMode.FM:
                    int half = FilterPresets.FmDeviation + FilterPresets.FmHighCut;
                    return (-half, half);
                case DSPMode.DRM: return (7000, 17000);
                default: return (low, high);
            }
        }

        private void ApplyTxDsp()
        {
            DSPMode m = TxDspMode;
            var (low, high) = TxPassband(m);
            WDSP.SetTXAMode(TxChannel, m);
            WDSP.SetTXABandpassFreqs(TxChannel, low, high);
            WDSP.SetTXAFMDeviation(TxChannel, FilterPresets.FmDeviation);
            ApplyMicGain();
        }

        /// <summary>cmaster.CMSetTXAPanelGain1 and the VAC TX gain.</summary>
        private void ApplyMicGain()
        {
            double gain = Math.Pow(10.0, _micGainDb / 20.0);
            bool pcMic = _micSource == MicSource.Pc && _vacRunning;
            WDSP.SetTXAPanelGain1(TxChannel, pcMic ? 1.0 : gain);
            ivac.SetIVACpreamp(0, gain);
        }

        /// <summary>With the radio's microphone selected, VAC must not replace the mic samples.</summary>
        private void ApplyVacBypass()
        {
            ivac.SetIVACbypass(0, _micSource == MicSource.Radio ? 1 : 0);
        }

        #endregion

        #region drive

        /// <summary>
        /// console.setPowerFromDriveSlider: drive % -> target dBm through the
        /// model's per-band PA gain -> RF output voltage -> drive level.
        /// </summary>
        private void ApplyDrive()
        {
            int pwr = _tuning ? _tunePercent : _drivePercent;
            double txMHz = _frequencyMHz;
            Band band = BandPlanRegions.BandFromFrequency(txMHz);
            float[] gains = HardwareSpecific.DefaultPAGainsForBands(_model);
            double gbb = band > Band.FIRST && (int)band < gains.Length ? gains[(int)band] : 100.0;
            double volume;
            if (pwr == 0 && _model != HPSDRModel.HERMESLITE)
            {
                volume = 0.0;
                if (_tuning) WDSP.SetTXAPostGenRun(TxChannel, 0);
            }
            else
            {
                if (_tuning) WDSP.SetTXAPostGenRun(TxChannel, 1);
                if (_model != HPSDRModel.HERMESLITE)
                {
                    double target_dbm = 10 * Math.Log10(pwr * 1000.0) - gbb;
                    double target_volts = Math.Sqrt(Math.Pow(10, target_dbm * 0.1) * 0.05);  // E = Sqrt(P * R)
                    volume = Math.Min(target_volts / 0.8, 1.0);
                }
                else
                {
                    volume = Math.Min(pwr * (gbb / 100) / 93.75, 1.0);   // HL2: 4-bit drive
                }
            }
            DriveVolume = volume;
            NetworkIO.SetOutputPower((float)(volume * 1.02));    // Audio.RadioVolume
        }

        /// <summary>Last drive value sent (0..1, before SWR protection), for tests and display.</summary>
        public double DriveVolume { get; private set; }

        #endregion

        #region keying

        /// <summary>Why transmitting at the current settings would be refused, or null if it is allowed.</summary>
        public string WhyTxRefused(bool tune)
        {
            if (!_powerOn) return "The radio is not on.";
            if (!TransmitAllowed) return "Transmit is disabled. Enable it in the transmit settings first.";
            if (Region == TxRegion.None) return "Choose your region in the transmit settings first.";
            if (!HaveSync) return "No data from the radio.";
            if (IsCw(_mode) && !tune) return "CW keying is not supported yet; use TUNE for a carrier.";
            var (lo, hi) = tune ? (TuneToneHz, TuneToneHz) : TxPassband(_mode);
            if (!BandPlanRegions.IsTxAllowed(Region, TxDdsMHz(tune), lo, hi))
                return $"{_frequencyMHz:0.000000} MHz with this transmit filter is outside the amateur bands for {Region}.";
            return null;
        }

        /// <summary>Tune tone offset: in the sideband of the mode (console chkTUN_CheckedChanged).</summary>
        private int TuneToneHz => TxDspMode == DSPMode.LSB || TxDspMode == DSPMode.DIGL ? -FilterPresets.CwPitch : FilterPresets.CwPitch;

        /// <summary>TX frequency: the VFO, moved by the CW pitch in CW so the carrier lands on the VFO.</summary>
        private double TxDdsMHz(bool tune)
        {
            double f = _frequencyMHz;
            if (_mode == DSPMode.CWL) f += FilterPresets.CwPitch * 1e-6;
            else if (_mode == DSPMode.CWU) f -= FilterPresets.CwPitch * 1e-6;
            return f;
        }

        /// <summary>Key (true) or unkey (false) the transmitter.  Returns false with 'reason' if refused.</summary>
        public bool SetMox(bool on, out string reason) => SetTx(on, false, TxSource.Manual, out reason);

        /// <summary>Send an unmodulated carrier at the tune drive level.</summary>
        public bool SetTune(bool on, out string reason) => SetTx(on, true, TxSource.Tune, out reason);

        private bool SetTx(bool on, bool tune, TxSource source, out string reason)
        {
            reason = null;
            lock (_txLock)
            {
                if (!on)
                {
                    if (_mox) KeyDown();
                    return true;
                }
                if (_mox)
                {
                    if (tune == _tuning) return true;
                    KeyDown();               // switching between MOX and TUNE
                }
                reason = WhyTxRefused(tune);
                if (reason != null)
                {
                    TxRefused?.Invoke(reason);
                    return false;
                }
                KeyUp(tune, source);
                return true;
            }
        }

        private void KeyUp(bool tune, TxSource source)
        {
            _tuning = tune;
            _txSource = source;
            double txMHz = TxDdsMHz(tune);

            if (tune)
            {
                WDSP.SetTXAPostGenToneFreq(TxChannel, TuneToneHz);
                WDSP.SetTXAPostGenMode(TxChannel, 0);
                WDSP.SetTXAPostGenToneMag(TxChannel, MaxToneMag);
                WDSP.SetTXAPostGenRun(TxChannel, 1);
            }
            ApplyTxDsp();
            NetworkIO.SWRProtect = 1.0f;
            _highSwrCount = 0;
            HighSwr = false;
            _mox = true;
            ApplyDrive();

            // not full duplex: shut RX1 down while transmitting
            WDSP.SetChannelState(WDSP.id(0, 1), 0, 0);
            WDSP.SetChannelState(WDSP.id(0, 0), 0, 1);
            DdcSetup.UpdateAAudioMixerStates(_model, true, false);
            DdcSetup.UpdateDDCs(_model, _sampleRate, _sampleRate, false);

            // HdwMOXChanged(true)
            NetworkIO.VFOfreq(0, txMHz, 1);
            NetworkIO.SetBPF2Gnd(1);                        // bpf2_gnd default
            BandFilters.Apply(HardwareSpecific.Hardware, _frequencyMHz, txMHz, true, tune);
            NetworkIO.SetAntBits(0, 1, 1, 0, true);         // ANT1 (Alex default antenna)
            NetworkIO.SetTRXrelay(1);
            NetworkIO.SetPttOut(1);

            // cmaster.Mox = true: router control bit, EER off
            cmaster.LoadRouterControlBit((void*)0, 0, 2, 1);
            cmaster.SetEERRun(0, false);
            NetworkIO.EnableEClassModulation(0);

            Thread.Sleep(RfDelayMs);
            ivac.SetIVACmox(0, 1);                          // Audio.MOX
            ivac.SetIVACmox(1, 0);
            WDSP.SetChannelState(TxChannel, 1, 0);           // transmitter on

            _txTimer.Restart();
            Report(tune ? "Tuning" : "Transmitting");
            TxStateChanged?.Invoke();
        }

        private void KeyDown()
        {
            _mox = false;
            WDSP.SetChannelState(TxChannel, 0, 1);           // transmitter off, drain
            Thread.Sleep(MoxDelayMs);
            if (_tuning) WDSP.SetTXAPostGenRun(TxChannel, 0);

            DdcSetup.UpdateDDCs(_model, _sampleRate, _sampleRate, false);
            DdcSetup.UpdateAAudioMixerStates(_model, true, false);
            ivac.SetIVACmox(0, 0);                          // Audio.MOX = false
            ivac.SetIVACmox(1, 0);

            // HdwMOXChanged(false)
            NetworkIO.SetPttOut(0);
            NetworkIO.SetTRXrelay(0);
            NetworkIO.VFOfreq(0, _frequencyMHz, 1);
            BandFilters.Apply(HardwareSpecific.Hardware, _frequencyMHz, _frequencyMHz, false, false);
            NetworkIO.SetAntBits(0, 1, 1, 0, false);
            NetworkIO.SetBPF2Gnd(0);

            cmaster.LoadRouterControlBit((void*)0, 0, 2, 0);   // cmaster.Mox = false
            cmaster.SetEERRun(0, false);

            Thread.Sleep(PttOutDelayMs);
            if (_powerOn) WDSP.SetChannelState(WDSP.id(0, 0), 1, 0);   // receiver back on

            _tuning = false;
            _txSource = TxSource.None;
            _txTimer.Reset();
            NetworkIO.SWRProtect = 1.0f;
            HighSwr = false;
            ForwardWatts = ReflectedWatts = 0;
            Swr = 1.0f;
            Report("Receiving");
            TxStateChanged?.Invoke();
        }

        #endregion

        #region supervisor: timeout, loss of data, radio PTT, meters, SWR

        private void StartTxSupervisor()
        {
            _supervisor?.Dispose();
            _lastRadioPtt = false;
            _supervisor = new Timer(_ => SuperviseTx(), null, 50, 50);
        }

        private void StopTxSupervisor()
        {
            _supervisor?.Dispose();
            _supervisor = null;
        }

        private void SuperviseTx()
        {
            if (!Monitor.TryEnter(_txLock)) return;         // a key change is in progress
            try
            {
                if (!_powerOn) return;

                // radio PTT input (console PollPTT)
                if (RadioPttEnabled)
                {
                    bool ptt = (NetworkIO.nativeGetDotDashPTT() & 0x01) != 0;
                    if (ptt != _lastRadioPtt)
                    {
                        _lastRadioPtt = ptt;
                        if (ptt && !_mox)
                        {
                            string why = WhyTxRefused(false);
                            if (why == null) KeyUp(false, TxSource.RadioPtt);
                            else TxRefused?.Invoke(why);
                        }
                        else if (!ptt && _mox && _txSource == TxSource.RadioPtt)
                            KeyDown();
                    }
                }

                if (!_mox) return;

                if (!HaveSync)
                {
                    KeyDown();
                    TxRefused?.Invoke("Transmit stopped: no data from the radio.");
                    return;
                }
                if (TxTimeoutSeconds > 0 && _txTimer.Elapsed.TotalSeconds >= TxTimeoutSeconds)
                {
                    KeyDown();
                    TxRefused?.Invoke($"Transmit stopped by the {TxTimeoutSeconds} s timeout.");
                    return;
                }
                UpdatePowerMeters();
            }
            finally
            {
                Monitor.Exit(_txLock);
            }
        }

        /// <summary>computeAlexFwdPower / computeRefPower and the SWR protection of the console's meter loop.</summary>
        private void UpdatePowerMeters()
        {
            bool is6m = BandPlanRegions.BandFromFrequency(_frequencyMHz) == Band.B6M;
            ForwardWatts = BridgeWatts(NetworkIO.getFwdPower(), forward: true, is6m);
            ReflectedWatts = BridgeWatts(NetworkIO.getRevPower(), forward: false, is6m);
            float fwd = ForwardWatts, rev = ReflectedWatts;

            float swr;
            float rho = (float)Math.Sqrt(rev / fwd);
            if (float.IsNaN(rho) || float.IsInfinity(rho)) swr = 1.0f;
            else swr = (1.0f + rho) / (1.0f - rho);
            if ((fwd <= 2.0f && rev <= 2.0f) || swr < 1.0f || float.IsNaN(swr) || float.IsInfinity(swr)) swr = 1.0f;
            Swr = swr;

            if (!SwrProtection || !BandFilters.AlexPresent) return;

            // open antenna: power goes out but nothing is absorbed
            if (!_tuning && fwd > 10.0f && (fwd - rev) < 1.0f && _model != HPSDRModel.ANAN8000D)
            {
                Swr = 50.0f;
                NetworkIO.SWRProtect = 0.01f;
                KeyDown();
                TxRefused?.Invoke("Transmit stopped: open antenna (almost all power reflected). Check the antenna connection.");
                return;
            }

            bool pass = _tuning && fwd >= 1.0f && fwd <= TunePowerSwrIgnore && _tunePercent <= 70;  // disable_swr_on_tune
            float fwdLimit = _model == HPSDRModel.ANAN8000D ? 2.0f * _drivePercent : 5.0f;
            if (swr > SwrProtectionLimit && fwd > fwdLimit && !pass)
            {
                if (++_highSwrCount >= 4)
                {
                    _highSwrCount = 0;
                    // Unlike the console, the fold-back holds until the radio is
                    // unkeyed and only ever lowers the drive: releasing it as soon
                    // as the reduced power drops under the 5 W threshold made the
                    // console hunt between full and reduced power.
                    float protect = Math.Min(NetworkIO.SWRProtect, SwrProtectionLimit / (swr + 1.0f));
                    if (!HighSwr) TxRefused?.Invoke($"High SWR ({swr:0.0}:1): drive reduced.");
                    HighSwr = true;
                    if (protect != NetworkIO.SWRProtect)
                    {
                        NetworkIO.SWRProtect = protect;
                        ApplyDrive();
                    }
                }
            }
            else
                _highSwrCount = 0;
        }

        /// <summary>Directional-coupler ADC reading -> watts, with the console's per-model constants.</summary>
        private float BridgeWatts(float adc, bool forward, bool is6m)
        {
            float bridge_volt, refvoltage;
            int offset;
            switch (_model)
            {
                case HPSDRModel.ANAN100:
                case HPSDRModel.ANAN100B:
                case HPSDRModel.ANAN100D:
                    bridge_volt = forward ? 0.095f : (is6m ? 0.5f : 0.095f); refvoltage = 3.3f; offset = forward ? 6 : 3; break;
                case HPSDRModel.ANAN200D:
                    bridge_volt = forward ? 0.108f : (is6m ? 0.5f : 0.108f); refvoltage = 5.0f; offset = forward ? 4 : 2; break;
                case HPSDRModel.ANAN7000D:
                case HPSDRModel.ANVELINAPRO3:
                case HPSDRModel.ANAN_G2E:
                case HPSDRModel.ANAN_G2:
                case HPSDRModel.ANAN_G2_1K:
                case HPSDRModel.REDPITAYA:
                    bridge_volt = forward ? 0.12f : (is6m ? 0.7f : 0.15f); refvoltage = 5.0f; offset = forward ? 32 : 28; break;
                case HPSDRModel.ORIONMKII:
                case HPSDRModel.ANAN8000D:
                    bridge_volt = 0.08f; refvoltage = 5.0f; offset = forward ? 18 : 16; break;
                case HPSDRModel.HERMESLITE:
                    bridge_volt = 1.5f; refvoltage = 3.3f; offset = 6; break;
                default:
                    bridge_volt = 0.09f; refvoltage = 3.3f; offset = forward ? 6 : 3; break;
            }
            if (adc < 0) adc = 0;
            float volts = (adc - offset) / 4095.0f * refvoltage;
            if (volts < 0) volts = 0;
            return volts * volts / bridge_volt;
        }

        /// <summary>Microphone peak level in dB (0 = full scale).</summary>
        public float MicPeakDb() => _powerOn ? WDSP.CalculateTXMeter(1, WDSP.MeterType.MIC_PK) : -200f;

        /// <summary>ALC gain reduction in dB.</summary>
        public float AlcGainDb() => _mox ? WDSP.CalculateTXMeter(1, WDSP.MeterType.ALC_G) : 0f;

        #endregion
    }
}
