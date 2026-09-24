/*  RadioController.PureSignal.cs

This file is part of a program that implements a Software-Defined Radio.

PureSignal: adaptive predistortion of the transmitter.  While transmitting,
the radio returns two extra streams -- the signal sent to the DAC and a
sample of the PA output from the coupler -- and WDSP's calcc compares them
and corrects the TX I/Q so the amplifier's output matches its input.

This is the console's PSForm without the form: the command state machine
(timer1code), auto-attenuate (timer2code), the PSEnabled switch that turns
the feedback DDCs on, and the TX step attenuator ("ATT on TX") that
auto-attenuate adjusts to put the feedback level in range.  Defaults are
PSForm.designer.cs's.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.IO;
using System.Threading;

namespace Thetis.Radio
{
    /// <summary>PureSignal settings (PSForm).</summary>
    public sealed class PureSignalSettings
    {
        /// <summary>Adjust the TX attenuator so the feedback level is in range.</summary>
        public bool AutoAttenuate { get; set; } = true;
        /// <summary>Hardware peak (null: the model's default, HardwareSpecific.PSDefaultPeak).</summary>
        public double? HwPeak { get; set; }
        /// <summary>Wait after keying before collecting (s).</summary>
        public double MoxDelay { get; set; } = 0.2;
        /// <summary>Wait between calibrations (s).</summary>
        public double CalWait { get; set; } = 0.0;
        /// <summary>TX delay: the reference's delay against the feedback (ns).</summary>
        public double TxDelayNs { get; set; } = 150;
        /// <summary>Relax the tolerance (0.4 instead of 0.8).</summary>
        public bool RelaxTolerance { get; set; }
        /// <summary>Two-tone test generator (Setup / Tests on Windows).</summary>
        public double TwoToneFreq1 { get; set; } = 700;
        public double TwoToneFreq2 { get; set; } = 1900;
        public double TwoToneLevelDb { get; set; } = 0;
    }

    /// <summary>calcc's engine states (info[15]).</summary>
    public enum PsEngineState { Reset = 0, Wait, MoxDelay, Setup, Collect, MoxCheck, Calc, Delay, StayOn, TurnOn }

    /// <summary>What PureSignal is doing, for display.</summary>
    public readonly record struct PureSignalStatus(
        bool Enabled, bool AutoCal, int FeedbackLevel, int CalibrationCount, bool CorrectionsApplied,
        bool Correcting, PsEngineState State, double MaxTx, int TxAttenuationDb)
    {
        /// <summary>console FeedbackColourLevel: too high, good, marginal, too low.</summary>
        public string LevelText =>
            FeedbackLevel > 181 ? "feedback too high" :
            FeedbackLevel > 128 ? "feedback good" :
            FeedbackLevel > 90 ? "feedback marginal" : "feedback too low";
    }

    public sealed unsafe partial class RadioController
    {
        private enum PsCmd { Off, TurnOnAutoCalibrate, AutoCalibrate, TurnOnSingleCalibrate, SingleCalibrate, StayOn, TurnOff, InitiateRestoredCorrection }
        private enum PsAa { Monitor, SetNewValues, RestoreOperation }

        private PureSignalSettings _ps = new PureSignalSettings();
        private Thread _psThread;
        private volatile bool _psRun;
        private readonly object _psLock = new object();
        private readonly int[] _psInfo = new int[16], _psOldInfo = new int[16];
        private volatile bool _psAutoOn, _psSingleCalOn, _psRestoreOn, _psOff = true;
        private bool _psAutoCalEnabled;
        private bool _psEnabled;                    // PSForm.PSEnabled: feedback on
        private PsCmd _psCmd = PsCmd.Off;
        private PsAa _psAa = PsAa.Monitor;
        private int _psDeltaDb, _psSaveAuto, _psSaveSingle, _psAaCalCount;
        private bool _psPerformingSingleCal;
        private int _psSingleCalRetries;
        private double _psMaxTx;
        private string _psRestoreFile;
        private int _txAttenuationDb = 31;          // ATT on TX (HL2: -28..31)

        /// <summary>PureSignal settings; call ApplyPureSignalSettings after changing them in place.</summary>
        public PureSignalSettings PureSignal
        {
            get => _ps;
            set { _ps = value ?? new PureSignalSettings(); ApplyPureSignalSettings(); }
        }

        /// <summary>PS-A: calibrate continuously while transmitting (PSForm.AutoCalEnabled).</summary>
        public bool PureSignalAutoCal
        {
            get => _psAutoCalEnabled;
            set
            {
                _psAutoCalEnabled = value;
                if (value) _psAutoOn = true;
                else _psOff = true;
                PureSignalChanged?.Invoke();
            }
        }

        /// <summary>Calibrate once on the next transmission, then keep that correction.</summary>
        public void PureSignalSingleCal()
        {
            _psSingleCalOn = !_psSingleCalOn;        // a second press cancels (btnPSCalibrate_Click)
        }

        /// <summary>Turn PureSignal off and discard the correction.</summary>
        public void PureSignalReset()
        {
            _psAutoCalEnabled = false;
            _psOff = true;
            PureSignalChanged?.Invoke();
        }

        /// <summary>Save the current correction (PSSaveCorr).</summary>
        public bool PureSignalSave(string file, out string error)
        {
            error = null;
            if (!_powerOn) { error = "The radio is off."; return false; }
            if (_psInfo[14] != 1) { error = "There is no correction to save yet."; return false; }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            puresignal.PSSaveCorr(TxChannel, file);
            return true;
        }

        /// <summary>Load a saved correction and apply it (PSRestoreCorr, then the restore state).</summary>
        public bool PureSignalRestore(string file, out string error)
        {
            error = null;
            if (!_powerOn) { error = "The radio is off."; return false; }
            if (!File.Exists(file)) { error = "No saved correction: " + file; return false; }
            _psRestoreFile = file;
            _psOff = false;
            _psRestoreOn = true;
            return true;
        }

        /// <summary>Raised when PS-A is switched (for the UI).</summary>
        public event Action PureSignalChanged;

        /// <summary>The TX step attenuator used while PureSignal runs (dB; the HL2 goes to -28).</summary>
        public int TxAttenuationDb
        {
            get => _txAttenuationDb;
            set
            {
                _txAttenuationDb = ClampTxAtt(value);
                if (_powerOn) lock (_txLock) ApplyTxAttenuation();
            }
        }

        private int ClampTxAtt(int v) => Math.Clamp(v, _model == HPSDRModel.HERMESLITE ? -28 : 0, 31);

        /// <summary>The model's default hardware peak (HardwareSpecific.PSDefaultPeak).</summary>
        public double DefaultPureSignalPeak => HardwareSpecific.PSDefaultPeak;

        public PureSignalStatus PureSignalStatus
        {
            get
            {
                lock (_psLock)
                    return new PureSignalStatus(_psEnabled || _psInfo[14] == 1, _psAutoCalEnabled, _psInfo[4], _psInfo[5],
                                                _psInfo[14] == 1, _psInfo[14] == 1 && _psInfo[4] > 90,
                                                (PsEngineState)_psInfo[15], _psMaxTx, _txAttenuationDb);
            }
        }

        /// <summary>
        /// WDSP 2.10's correction curves (GetPSDisp2): the amplitude correction
        /// (gain vs. drive, 0..1) and the phase correction (degrees vs. drive),
        /// 512 points each.  False until a correction exists.
        /// </summary>
        public bool PureSignalCurves(double[] ampX, double[] ampY, double[] phaseX, double[] phaseY)
        {
            if (!_powerOn || _psInfo[14] != 1 || ampX.Length < 512 || ampY.Length < 512 || phaseX.Length < 512 || phaseY.Length < 512)
                return false;
            int n = 8192;
            double[] x = new double[n], ym = new double[n], yc = new double[n], ys = new double[n];
            int nsamps, cpts;
            double phsRef;
            fixed (double* px = x, pym = ym, pyc = yc, pys = ys, ax = ampX, ay = ampY, fx = phaseX, fy = phaseY)
                GetPSDisp2(TxChannel, px, pym, pyc, pys, ax, ay, fx, fy, &nsamps, &cpts, &phsRef);
            return cpts > 0;
        }

        // WDSP 2.10's GetPSDisp (renamed; the console's 7-argument GetPSDisp is a compatibility wrapper)
        [System.Runtime.InteropServices.DllImport("wdsp.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern void GetPSDisp2(int channel, double* x, double* ym, double* yc, double* ys,
                                              double* xmCor, double* ymCor, double* xaCor, double* yaCor,
                                              int* nsamps, int* cpts, double* phsRefDeg);

        /// <summary>PSForm's value-changed handlers.</summary>
        public void ApplyPureSignalSettings()
        {
            if (!_dspReady) return;
            puresignal.SetPSHWPeak(TxChannel, _ps.HwPeak ?? HardwareSpecific.PSDefaultPeak);
            puresignal.SetPSMoxDelay(TxChannel, Math.Clamp(_ps.MoxDelay, 0.0, 10.0));
            puresignal.SetPSLoopDelay(TxChannel, Math.Clamp(_ps.CalWait, 0.0, 100.0));
            puresignal.SetPSTXDelay(TxChannel, Math.Clamp(_ps.TxDelayNs, -25000, 25000) * 1.0e-09);
            puresignal.SetPSPtol(TxChannel, _ps.RelaxTolerance ? 0.400 : 0.800);
            puresignal.SetPSIntsAndSpi(TxChannel, 16, 256);
            puresignal.SetPSFeedbackRate(TxChannel, cmaster.PSrate);

        }

        /// <summary>PSForm.PSEnabled: the radio sends the feedback streams while transmitting.</summary>
        private void SetPsEnabled(bool on)
        {
            lock (_txLock)
            {
                _psEnabled = on;
                if (!_powerOn) return;
                DdcSetup.UpdateDDCs(_model, _sampleRate, _sampleRate, false, _mox, on);
                NetworkIO.SetPureSignal(on ? 1 : 0);
                NetworkIO.SendHighPriority(1);
                DdcSetup.UpdateAAudioMixerStates(_model, true, false);
                cmaster.LoadRouterControlBit((void*)0, 0, 0, on ? 1 : 0);
                puresignal.SetPSRunCal(TxChannel, on);
                if (_mox) ApplyTxAttenuation();
            }
        }

        /// <summary>console.TxAttenData: the step attenuator during transmit (ATT on TX while PureSignal runs).</summary>
        private void ApplyTxAttenuation()
        {
            if (!_mox) return;
            if (_psEnabled)
                NetworkIO.SetTxAttenData(_model == HPSDRModel.HERMESLITE ? 31 - _txAttenuationDb : _txAttenuationDb);
            else
                NetworkIO.SetTxAttenData(0);
        }

        /// <summary>KeyUp / KeyDown: PSForm.Mox, the feedback DDCs and the TX attenuator.</summary>
        private void PureSignalMox(bool mox)
        {
            DdcSetup.UpdateDDCs(_model, _sampleRate, _sampleRate, false, mox, _psEnabled);
            if (mox) ApplyTxAttenuation();
            puresignal.SetPSMox(TxChannel, mox);
        }

        private void StartPureSignal()
        {
            ApplyPureSignalSettings();
            _txAttenuationDb = ClampTxAtt(_txAttenuationDb);
            _psCmd = PsCmd.Off;
            _psAa = PsAa.Monitor;
            _psOff = !_psAutoCalEnabled;
            _psAutoOn = _psAutoCalEnabled;
            _psRun = true;
            _psThread = new Thread(PsLoop) { IsBackground = true, Name = "PureSignal" };
            _psThread.Start();
        }

        private void StopPureSignal()
        {
            _psRun = false;
            _psThread?.Join(1000);
            _psThread = null;
            _psEnabled = false;
            Array.Clear(_psInfo);
        }

        // PSForm.PSLoop: the command state machine every 10 ms, auto-attenuate every 100 ms
        private void PsLoop()
        {
            int n = 0;
            while (_psRun)
            {
                try
                {
                    PsTimer1();
                    if (n == 0) PsTimer2();
                }
                catch (Exception ex)
                {
                    System.Console.Error.WriteLine("PureSignal: " + ex.Message);
                }
                if (++n == 10) n = 0;
                Thread.Sleep(10);
            }
        }

        private int PsFeedback => _psInfo[4];
        private bool PsCorrectionsApplied => _psInfo[14] == 1;
        private PsEngineState PsState => (PsEngineState)_psInfo[15];

        private void PsTimer1()
        {
            lock (_psLock)
            {
                Array.Copy(_psInfo, _psOldInfo, 16);
                fixed (int* p = _psInfo) puresignal.GetPSInfo(TxChannel, p);
                double maxtx;
                puresignal.GetPSMaxTX(TxChannel, &maxtx);
                _psMaxTx = maxtx;
            }

            switch (_psCmd)
            {
                case PsCmd.Off:
                    puresignal.SetPSControl(TxChannel, 1, 0, 0, 0);
                    if (_psEnabled) SetPsEnabled(false);
                    if (_psRestoreOn) _psCmd = PsCmd.InitiateRestoredCorrection;
                    else if (_psAutoOn) _psCmd = PsCmd.TurnOnAutoCalibrate;
                    else if (_psSingleCalOn) _psCmd = PsCmd.TurnOnSingleCalibrate;
                    _psOff = false;
                    break;
                case PsCmd.TurnOnAutoCalibrate:
                    puresignal.SetPSControl(TxChannel, 1, 0, 1, 0);
                    if (!_psEnabled) SetPsEnabled(true);
                    _psCmd = PsCmd.AutoCalibrate;
                    break;
                case PsCmd.AutoCalibrate:
                    if (_psOff) _psCmd = PsCmd.TurnOff;
                    else if (_psRestoreOn) _psCmd = PsCmd.InitiateRestoredCorrection;
                    else if (_psSingleCalOn) _psCmd = PsCmd.TurnOnSingleCalibrate;
                    break;
                case PsCmd.TurnOnSingleCalibrate:
                    _psAutoOn = false;
                    _psPerformingSingleCal = true;
                    puresignal.SetPSControl(TxChannel, 1, 1, 0, 0);
                    if (!_psEnabled) SetPsEnabled(true);
                    _psCmd = PsCmd.SingleCalibrate;
                    break;
                case PsCmd.SingleCalibrate:
                    _psSingleCalOn = false;
                    if (_psOff) _psCmd = PsCmd.TurnOff;
                    else if (_psRestoreOn) _psCmd = PsCmd.InitiateRestoredCorrection;
                    else if (_psAutoOn) _psCmd = PsCmd.TurnOnAutoCalibrate;
                    else if (PsCorrectionsApplied) _psCmd = PsCmd.StayOn;
                    break;
                case PsCmd.StayOn:
                    if (_psEnabled) SetPsEnabled(false);
                    if (_psOff) _psCmd = PsCmd.TurnOff;
                    else if (_psRestoreOn) _psCmd = PsCmd.InitiateRestoredCorrection;
                    else if (_psAutoOn) _psCmd = PsCmd.TurnOnAutoCalibrate;
                    else if (_psSingleCalOn) _psCmd = PsCmd.TurnOnSingleCalibrate;
                    else if (_psPerformingSingleCal)
                    {
                        // the single cal needed a new attenuation: try again (up to 5 times)
                        _psPerformingSingleCal = false;
                        if (!(PsFeedback > 128 && PsFeedback <= 181) && _psSingleCalRetries < 5)
                        {
                            _psSingleCalRetries++;
                            _psSingleCalOn = true;
                        }
                        else _psSingleCalRetries = 0;
                    }
                    break;
                case PsCmd.TurnOff:
                    if (!_psAutoCalEnabled) _psAutoOn = false;
                    puresignal.SetPSControl(TxChannel, 1, 0, 0, 0);
                    if (!_psEnabled) SetPsEnabled(true);
                    _psOff = false;
                    if (_psRestoreOn) _psCmd = PsCmd.InitiateRestoredCorrection;
                    else if (_psAutoOn) _psCmd = PsCmd.TurnOnAutoCalibrate;
                    else if (_psSingleCalOn) _psCmd = PsCmd.TurnOnSingleCalibrate;
                    else if (!PsCorrectionsApplied && PsState == PsEngineState.Reset) _psCmd = PsCmd.Off;
                    break;
                case PsCmd.InitiateRestoredCorrection:
                    _psAutoOn = false;
                    if (_psRestoreFile != null)
                    {
                        puresignal.PSRestoreCorr(TxChannel, _psRestoreFile);   // btnPSRestore_Click
                        _psRestoreFile = null;
                    }
                    puresignal.SetPSControl(TxChannel, 0, 0, 0, 1);
                    if (!_psEnabled) SetPsEnabled(true);
                    _psRestoreOn = false;
                    if (PsState == PsEngineState.StayOn) _psCmd = PsCmd.StayOn;
                    break;
            }
        }

        // PSForm.timer2code: auto-attenuate
        private void PsTimer2()
        {
            bool hl2 = _model == HPSDRModel.HERMESLITE;
            switch (_psAa)
            {
                case PsAa.Monitor:
                    bool recal = hl2
                        ? PsFeedback > 181 || (PsFeedback <= 128 && _txAttenuationDb > -28)
                        : PsFeedback > 181 || (PsFeedback <= 128 && _txAttenuationDb > 0);
                    // a calibration attempt finished since the last look (PSForm compares with the
                    // reading 10 ms earlier, so it only sees attempts that end in that window)
                    bool attempted = _psInfo[5] != _psAaCalCount;
                    _psAaCalCount = _psInfo[5];
                    if (_ps.AutoAttenuate && attempted && recal)
                    {
                        _psAa = PsAa.SetNewValues;
                        double ddB;
                        if (PsFeedback <= 256)
                        {
                            ddB = 20.0 * Math.Log10(PsFeedback / 152.293);
                            if (!hl2)
                            {
                                if (double.IsNaN(ddB)) ddB = 31.1;
                                if (ddB < -100.0) ddB = -100.0;
                                if (ddB > +100.0) ddB = +100.0;
                            }
                            else
                            {
                                if (double.IsNaN(ddB)) ddB = 10.0;
                                if (ddB < -100.0) ddB = -10.0;
                                if (ddB > +100.0) ddB = 10.0;
                            }
                        }
                        else ddB = hl2 ? 10.0 : 31.1;
                        _psDeltaDb = (int)Math.Round(ddB, MidpointRounding.AwayFromZero);
                        _psSaveAuto = _psCmd == PsCmd.AutoCalibrate ? 1 : 0;
                        _psSaveSingle = _psCmd == PsCmd.SingleCalibrate ? 1 : 0;
                        puresignal.SetPSControl(TxChannel, 1, 0, 0, 0);    // everything off and reset
                    }
                    break;
                case PsAa.SetNewValues:
                    _psAa = PsAa.RestoreOperation;
                    int oldAtt = _txAttenuationDb;
                    int newAtt = hl2 ? oldAtt + _psDeltaDb : Math.Max(0, oldAtt + _psDeltaDb);
                    newAtt = ClampTxAtt(newAtt);
                    if (newAtt != oldAtt)
                    {
                        _txAttenuationDb = newAtt;
                        lock (_txLock) ApplyTxAttenuation();
                        TxAttenuationChanged?.Invoke(newAtt);
                    }
                    break;
                case PsAa.RestoreOperation:
                    _psAa = PsAa.Monitor;
                    puresignal.SetPSControl(TxChannel, 0, _psSaveSingle, _psSaveAuto, 0);
                    break;
            }
        }

        /// <summary>Raised on the PureSignal thread when auto-attenuate changes the TX attenuator (to save it per band).</summary>
        public event Action<int> TxAttenuationChanged;
    }
}
