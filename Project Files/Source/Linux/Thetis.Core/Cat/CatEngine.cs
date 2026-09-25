/*  CatEngine.cs

This file is part of a program that implements a Software-Defined Radio.

The CAT command interpreter: Kenwood TS-2000 commands and Thetis' extended
"ZZ" commands, as the Windows console's CATParser / CATCommands answer them
(Console/CAT).  The command table (CatStructs) decides which lengths are a
set or a read, so answers have the console's formats; the commands
implemented are those for the features this version has, and the others
answer "?;" as an unknown command does.

Command reference: the console's CAT documentation and CATCommands.cs.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Thetis.Radio;

namespace Thetis.Cat
{
    /// <summary>Options that change answers (Setup > CAT).</summary>
    public sealed class CatOptions
    {
        /// <summary>What ID answers: 19 = TS-2000 (default), 20 = TS-480, 13 = TS-50S, 900 = SDR-1000.</summary>
        public int RigId { get; set; } = 19;
        /// <summary>MD: report DIGU / DIGL as USB / LSB, and set DIGU / DIGL for USB / LSB (console DigUIsUSB).</summary>
        public bool DigUIsUsb { get; set; }
        /// <summary>AI / ZZAI allowed (console AllowFreqBroadcast).</summary>
        public bool AllowAutoInformation { get; set; } = true;
    }

    /// <summary>One connection's state (auto information).</summary>
    public sealed class CatSession
    {
        public bool AutoInformation { get; set; }
    }

    public sealed class CatEngine
    {
        public const string Error1 = "?;";
        public const string Error3 = "O;";

        private readonly ICatHost _host;
        private readonly CatOptions _options;
        private readonly Dictionary<string, Func<string, CatSession, string>> _commands;
        private static readonly Regex SuffixPattern = new Regex("^[+-]?[Vv0-9]*$", RegexOptions.Compiled);
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // console TuneStepList (index = ZZAC value)
        public static readonly int[] Steps =
        {
            1, 2, 10, 25, 50, 100, 250, 500, 1000, 2000, 2500, 5000, 6250, 9000, 10000, 12500,
            15000, 20000, 25000, 30000, 50000, 100000, 250000, 500000, 1000000, 10000000,
        };

        // CATCommands.Step2String
        private static readonly string[] StepCodes =
        {
            "0000", "0000", "0001", "0001", "1000", "0010", "1001", "1010", "0011", "0011", "0011", "1011", "0011",
            "1100", "0100", "0100", "0100", "0100", "0100", "0100", "0100", "0101", "1101", "1110", "0110", "0111",
        };

        // CATCommands.Step2Freq (ZZAD / ZZAU)
        private static readonly double[] StepFreqs =
        {
            1, 10, 25, 50, 100, 250, 500, 1000, 5000, 9000, 10000, 100000, 250000, 500000, 1000000, 10000000,
        };

        // BandPlan names <-> ZZBS codes (CATCommands.String2Band / Band2String)
        private static readonly (string name, string code)[] BandCodes =
        {
            ("160", "160"), ("80", "080"), ("60", "060"), ("40", "040"), ("30", "030"), ("20", "020"),
            ("17", "017"), ("15", "015"), ("12", "012"), ("10", "010"), ("6", "006"), ("WWV", "999"),
        };

        // current command
        private CatStruct _struct;
        private readonly object _lock = new object();

        public CatEngine(ICatHost host, CatOptions options = null)
        {
            _host = host;
            _options = options ?? new CatOptions();
            _commands = BuildCommands();
        }

        public CatOptions Options => _options;

        /// <summary>The commands this engine implements (for the documentation and the tests).</summary>
        public IReadOnlyCollection<string> Implemented => _commands.Keys;

        /// <summary>
        /// Run every complete command in 'input' (terminated by ';') and return the
        /// answers; 'rest' is an unterminated tail to keep for the next read.
        /// </summary>
        public string ExecuteAll(string input, CatSession session, out string rest)
        {
            var sb = new StringBuilder();
            int start = 0;
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] != ';') continue;
                string cmd = input.Substring(start, i - start).Trim('\r', '\n', ' ', '\0');
                start = i + 1;
                if (cmd.Length == 0) continue;          // leading or doubled terminators (WriteLog)
                sb.Append(Execute(cmd + ";", session));
            }
            rest = input.Substring(start);
            if (rest.Length > 1024) rest = "";          // junk without terminators
            return sb.ToString();
        }

        /// <summary>One command, with or without its ';'.  The answer ends with ';', or is "" for a set.</summary>
        public string Execute(string command, CatSession session)
        {
            // connections run on their own threads; one command at a time
            lock (_lock) return ExecuteLocked(command, session ?? new CatSession());
        }

        private string ExecuteLocked(string command, CatSession session)
        {
            string cat = command.TrimStart(';');
            int term = cat.IndexOf(';');
            if (term < 0) cat += ";";
            term = cat.IndexOf(';');
            if (term < 2 || cat.Length < 3) return Error1;

            // CATParser.CheckFormat: 4-letter ZZ commands, else 2 letters
            bool extended = cat.Length > 3 && cat.Substring(0, 2).ToUpperInvariant() == "ZZ" && term >= 4;
            int plen = extended ? 4 : 2;
            string code = cat.Substring(0, plen).ToUpperInvariant();
            if (!CatStructs.TryGet(code, out _struct) || !_struct.Active) return Error1;
            string suffix = cat.Substring(plen, term - plen);
            if (code != "KY" && code != "ZZKY" && !SuffixPattern.IsMatch(suffix)) return Error1;
            bool kenwoodTx = (code == "TX" || code == "RX") && suffix.Length == 1 && char.IsDigit(suffix[0]);
            if (suffix.Length != _struct.NSet && suffix.Length != _struct.NGet && !kenwoodTx &&
                !(code == "ZZMG" && suffix.Length == _struct.NSet + 1))      // ZZMG takes a sign too
                return Error1;
            if (!_commands.TryGetValue(code, out var handler)) return Error1;

            string ans;
            try
            {
                ans = _host.Invoke(() => handler(suffix, session));
            }
            catch (Exception)
            {
                return Error1;
            }
            if (ans == null) return "";
            if (ans.Contains(Error1)) return Error1;

            if (!extended)
            {
                // CATParser.Get: a standard answer must have its length
                if (ans.Length == _struct.NAns && _struct.NAns > 0) return code + ans + ";";
                if (_struct.NAns == -1 || ans == "") return "";
                return Error3;
            }
            // CATParser.ParseExtended: an answer of the table's length is framed; others are complete already
            if (ans.Length == _struct.NAns && _struct.NAns > 0) return code + suffix + ans + ";";
            return ans;
        }

        #region formatting helpers

        private bool IsSet(string s) => s.Length == _struct.NSet || (_struct.Code == "ZZMG" && s.Length == _struct.NSet + 1);
        private bool IsGet(string s) => s.Length == _struct.NGet;

        private string Pad(int n) => n.ToString(Inv).PadLeft(_struct.NAns, '0');

        private string Signed(int n) => (n < 0 ? "-" : "+") + Math.Abs(n).ToString(Inv).PadLeft(_struct.NAns, '0').Substring(1);

        private static string Freq11(long hz) => Math.Max(0, hz).ToString(Inv).PadLeft(11, '0');

        private static int Int(string s) => int.Parse(s, NumberStyles.AllowLeadingSign, Inv);

        private string Bool(string s, Func<bool> get, Action<bool> set)
        {
            if (IsSet(s) && (s == "0" || s == "1")) { set(s == "1"); return ""; }
            if (IsGet(s)) return get() ? "1" : "0";
            return Error1;
        }

        private string Number(string s, Func<int> get, Action<int> set, int min, int max, bool signed = false)
        {
            if (IsSet(s)) { set(Math.Clamp(Int(s), min, max)); return ""; }
            if (IsGet(s)) return signed ? Signed(get()) : Pad(get());
            return Error1;
        }

        private static string Mode2String(DSPMode m) => m switch
        {
            DSPMode.LSB => "00", DSPMode.USB => "01", DSPMode.DSB => "02", DSPMode.CWL => "03", DSPMode.CWU => "04",
            DSPMode.FM => "05", DSPMode.AM => "06", DSPMode.DIGU => "07", DSPMode.SPEC => "08", DSPMode.DIGL => "09",
            DSPMode.SAM => "10", DSPMode.DRM => "11", _ => Error1,
        };

        private static DSPMode? String2Mode(string s) => s switch
        {
            "00" => DSPMode.LSB, "01" => DSPMode.USB, "02" => DSPMode.DSB, "03" => DSPMode.CWL, "04" => DSPMode.CWU,
            "05" => DSPMode.FM, "06" => DSPMode.AM, "07" => DSPMode.DIGU, "09" => DSPMode.DIGL, "10" => DSPMode.SAM,
            _ => null,              // SPEC and DRM are not offered here
        };

        private string Mode2KString(DSPMode m) => m switch
        {
            DSPMode.LSB => "1", DSPMode.USB => "2", DSPMode.CWU => "3", DSPMode.FM => "4", DSPMode.AM => "5",
            DSPMode.DIGL => _options.DigUIsUsb ? "1" : "6", DSPMode.CWL => "7",
            DSPMode.DIGU => _options.DigUIsUsb ? "2" : "9",
            _ => Error1,
        };

        private DSPMode KString2Mode(string s) => s switch
        {
            "1" => _options.DigUIsUsb ? DSPMode.DIGL : DSPMode.LSB,
            "2" => _options.DigUIsUsb ? DSPMode.DIGU : DSPMode.USB,
            "3" => DSPMode.CWU, "4" => DSPMode.FM, "5" => DSPMode.AM, "6" => DSPMode.DIGL, "7" => DSPMode.CWL,
            "9" => DSPMode.DIGU,
            _ => DSPMode.USB,
        };

        private int StepIndex()
        {
            int hz = _host.StepHz;
            int best = 0;
            for (int i = 0; i < Steps.Length; i++)
                if (Math.Abs(Steps[i] - hz) < Math.Abs(Steps[best] - hz)) best = i;
            return best;
        }

        private void Retune(double deltaHz) => _host.FrequencyHz = Math.Max(0, _host.FrequencyHz + (long)Math.Round(deltaHz));

        private string BandCode() => BandCodes.FirstOrDefault(b => b.name == _host.BandName).code ?? "888";

        private void StepBand(int dir)
        {
            var names = BandPlan.Bands.Where(b => b.Name != "WWV").Select(b => b.Name).ToList();
            int i = names.IndexOf(_host.BandName ?? "");
            i = i < 0 ? (dir > 0 ? 0 : names.Count - 1) : (i + dir + names.Count) % names.Count;
            _host.SelectBand(names[i]);
        }

        // console CATCommands.IF / ZZIF: VFO, step, RIT/XIT, memory, TX, mode, FR/FT, scan, split, balance
        private string Status(bool extended)
        {
            string mode = extended ? Mode2String(_host.Mode) : Mode2KString(_host.Mode);
            if (!extended && mode == Error1) mode = "2";
            return Freq11(_host.FrequencyHz) + StepCodes[StepIndex()] + "+00000" + "0" + "0" + "000" +
                   (_host.Mox ? "1" : "0") + mode + "0" + "0" + "0" + "0000";
        }

        // console CATCommands.Frequency2Code / Code2Frequency (SH / SL)
        private static bool Dsb(DSPMode m) => m == DSPMode.AM || m == DSPMode.DRM || m == DSPMode.DSB || m == DSPMode.FM || m == DSPMode.SAM;

        private static readonly int[] SslEdges = { 25, 75, 150, 250, 350, 450, 550, 650, 750, 850, 950 };
        private static readonly int[] SshEdges = { 1500, 1700, 1900, 2100, 2300, 2500, 2700, 2900, 3200, 3700, 4500 };
        private static readonly int[] DslEdges = { 50, 150, 350 };
        private static readonly int[] DshEdges = { 2750, 3500, 4500 };
        private static readonly int[] SslFreq = { 0, 50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000 };
        private static readonly int[] SshFreq = { 1400, 1600, 1800, 2000, 2200, 2400, 2600, 2800, 3000, 3400, 4000, 5000 };
        private static readonly int[] DslFreq = { 0, 100, 200, 500 };
        private static readonly int[] DshFreq = { 2500, 3000, 4000, 5000 };

        private string Frequency2Code(int f, bool high)
        {
            f = Math.Abs(f);
            int[] edges = Dsb(_host.Mode) ? (high ? DshEdges : DslEdges) : (high ? SshEdges : SslEdges);
            int code = 0;
            while (code < edges.Length && f > edges[code]) code++;
            return code.ToString("00", Inv);
        }

        private int Code2Frequency(string c, bool high)
        {
            int[] f = Dsb(_host.Mode) ? (high ? DshFreq : DslFreq) : (high ? SshFreq : SslFreq);
            int i = Int(c);
            return i >= 0 && i < f.Length ? f[i] : 0;
        }

        private string FilterEdge(string s, bool high)
        {
            var (lo, hi) = _host.Filter;
            DSPMode m = _host.Mode;
            bool lower = m == DSPMode.LSB || m == DSPMode.CWL || m == DSPMode.DIGL;
            if (IsGet(s)) return Frequency2Code(high ? (lower ? lo : hi) : (lower ? hi : lo), high);
            if (!IsSet(s)) return Error1;
            // console CATCommands.SetFilter
            int f = Code2Frequency(s, high);
            if (Dsb(m))
            {
                if (high) _host.SetFilterEdges(-f / 2, f / 2);
                else
                {
                    int width = Code2Frequency(Frequency2Code(hi * 2, true), true);
                    _host.SetFilterEdges(-width / 2 + f, width / 2);
                }
            }
            else if (lower) _host.SetFilterEdges(high ? -f : lo, high ? hi : -f);
            else _host.SetFilterEdges(high ? lo : f, high ? f : hi);
            return "";
        }

        private string Meter(string s)
        {
            // console CATReadSigStrength etc. (ZZRM: padded to 20)
            string v = null;
            if (!_host.Mox)
            {
                v = s switch
                {
                    "0" or "1" => _host.SignalDbm.ToString("f1", Inv) + " dBm",
                    "2" or "3" => _host.AdcDbfs.ToString("f1", Inv) + " dBFS",
                    _ => null,
                };
            }
            else
            {
                v = s switch
                {
                    "4" => Math.Max(-20.0, _host.AlcDb).ToString("f1", Inv) + " dB",
                    "5" => _host.ForwardWatts.ToString("f0", Inv) + " W",
                    "7" => _host.ReflectedWatts.ToString("f0", Inv) + " W",
                    "8" => _host.Swr.ToString("f1", Inv) + " : 1",
                    _ => null,
                };
            }
            return v == null ? Error1 : v.PadLeft(20);
        }

        // console CATCommands.SM (0..30) and ZZSM (0..260, half-dB steps from -140 dBm)
        private string SMeter(string s, bool extended)
        {
            if (extended)
            {
                if (s != "0" && s != "1") return Error1;
                if (s == "1") return "000";         // no second receiver yet
                float num = _host.PowerOn ? _host.SignalDbm : -140f;
                num = Math.Clamp(num, -140f, -10f);
                return (((int)num + 140) * 2).ToString(Inv).PadLeft(3, '0');
            }
            if (s != "0" && s != "2") return Error1;
            float n = _host.PowerOn ? _host.SignalDbm : -140f;
            n = Math.Clamp(n, -140f, -10f);
            double sx = Math.Max(0, (n + 127) / 6);
            int sm = sx <= 9.0 ? Math.Abs((int)(sx * 1.6667)) : 15 + (int)(n + 73);
            return Math.Clamp(sm, 0, 30).ToString(Inv).PadLeft(5, '0');
        }

        private string Tx(bool on)
        {
            _host.SetMox(on, out _);
            return "";
        }

        // NR selection: the console's ZZNE values; 2 = NR2, 3 = RN2 (RNNoise, the console's NR3),
        // 4 = NR4; DFNR and NNR (not in the Windows console) read back as 1, and 1 selects NNR
        private string NrCode()
        {
            switch (_host.NoiseReduction)
            {
                case NrType.Off: return "0";
                case NrType.NR2: return "2";
                case NrType.RN2: return "3";
                case NrType.NR4: return "4";
                default: return "1";
            }
        }

        private static NrType? NrFromCode(string s) => s switch
        {
            "0" => NrType.Off, "1" => NrType.NNR, "2" => NrType.NR2, "3" => NrType.RN2, "4" => NrType.NR4, _ => null,
        };

        #endregion

        private Dictionary<string, Func<string, CatSession, string>> BuildCommands()
        {
            var c = new Dictionary<string, Func<string, CatSession, string>>(StringComparer.Ordinal);

            // ---------------- Kenwood ----------------
            c["AG"] = (s, _) =>
            {
                if (IsSet(s)) { _host.VolumePercent = Math.Clamp((int)Math.Round(Int(s.Substring(1)) / 2.55), 0, 100); return ""; }
                if (IsGet(s)) return Pad((int)Math.Round(_host.VolumePercent / 0.392));
                return Error1;
            };
            c["AI"] = (s, ss) => c["ZZAI"](s, ss);
            c["BD"] = (s, _) => { StepBand(-1); return ""; };
            c["BU"] = (s, _) => { StepBand(+1); return ""; };
            c["DN"] = (s, _) => { Retune(-_host.StepHz); return ""; };
            c["UP"] = (s, _) => { Retune(+_host.StepHz); return ""; };
            c["FA"] = (s, ss) => c["ZZFA"](s, ss);
            c["FB"] = (s, ss) => c["ZZFB"](s, ss);
            c["FR"] = (s, _) => IsSet(s) ? "" : IsGet(s) ? "0" : Error1;
            c["FT"] = (s, ss) => c["ZZSP"](s, ss);
            c["GT"] = (s, ss) => { string r = c["ZZGT"](s, ss); return r.Length > 0 && r != Error1 ? r.PadLeft(3, '0') : r; };
            c["ID"] = (s, _) => _options.RigId == 900 ? "900" : _options.RigId.ToString("000", Inv);
            c["IF"] = (s, _) => Status(false);
            c["MD"] = (s, _) =>
            {
                if (IsSet(s))
                {
                    int n = Int(s);
                    if (n <= 0 || n > 9) return Error1;
                    _host.Mode = KString2Mode(s);
                    return "";
                }
                if (IsGet(s)) return Mode2KString(_host.Mode);
                return Error1;
            };
            c["MG"] = (s, _) =>
            {
                // Kenwood 0..100 <-> console mic gain 0..70 dB
                if (IsSet(s)) { _host.MicGainDb = Math.Round(Math.Clamp(Int(s), 0, 100) / 1.43); return ""; }
                if (IsGet(s)) return Pad((int)Math.Round(Math.Max(0, _host.MicGainDb) / .7));
                return Error1;
            };
            c["NB"] = (s, ss) => c["ZZNA"](s, ss);
            c["NT"] = (s, ss) => c["ZZNT"](s, ss);
            c["PC"] = (s, ss) => c["ZZPC"](s, ss);
            c["PR"] = (s, ss) => c["ZZCP"](s, ss);
            c["PS"] = (s, ss) => c["ZZPS"](s, ss);
            c["RT"] = (s, ss) => c["ZZRT"](s, ss);
            c["XT"] = (s, ss) => c["ZZXS"](s, ss);
            // TX; as the console takes it, and TX0; TX1; TX2; (Kenwood: microphone, data, tune -- all key MOX here),
            // which Hamlib's TS-2000 / TS-480 drivers send and the console's table refuses
            c["RX"] = (s, _) => Tx(false);
            c["TX"] = (s, _) => Tx(true);
            c["SH"] = (s, _) => FilterEdge(s, true);
            c["SL"] = (s, _) => FilterEdge(s, false);
            c["SM"] = (s, _) => SMeter(s, false);

            // ---------------- ZZ ----------------
            c["ZZAG"] = (s, _) => Number(s, () => _host.VolumePercent, v => _host.VolumePercent = v, 0, 100);
            c["ZZAI"] = (s, ss) =>
            {
                if (!_options.AllowAutoInformation) return Error1;
                if (IsSet(s)) { ss.AutoInformation = s != "0"; return ""; }
                if (IsGet(s)) return ss.AutoInformation ? "1" : "0";
                return Error1;
            };
            c["ZZAR"] = (s, _) => Number(s, () => (int)Math.Round(_host.AgcTopDb), v => _host.AgcTopDb = v, -20, 120, signed: true);
            c["ZZAC"] = (s, _) => Number(s, StepIndex, v => _host.StepHz = Steps[v], 0, Steps.Length - 1);
            c["ZZAD"] = (s, _) => { int i = Int(s); if (i < 0 || i >= StepFreqs.Length) return Error1; Retune(-StepFreqs[i]); return ""; };
            c["ZZAU"] = (s, _) => { int i = Int(s); if (i < 0 || i >= StepFreqs.Length) return Error1; Retune(+StepFreqs[i]); return ""; };
            c["ZZAE"] = (s, _) => { Retune(-_host.StepHz * (double)Int(s)); return ""; };
            c["ZZAF"] = (s, _) => { Retune(+_host.StepHz * (double)Int(s)); return ""; };
            c["ZZBD"] = (s, _) => { StepBand(-1); return ""; };
            c["ZZBU"] = (s, _) => { StepBand(+1); return ""; };
            c["ZZBS"] = (s, _) =>
            {
                if (IsSet(s))
                {
                    string name = BandCodes.FirstOrDefault(b => b.code == s.ToUpperInvariant()).name;
                    if (name == null) return Error1;
                    _host.SelectBand(name);
                    return "";
                }
                if (IsGet(s)) return BandCode();
                return Error1;
            };
            c["ZZCP"] = (s, _) => Bool(s, () => _host.Compressor, v => _host.Compressor = v);
            c["ZZCT"] = (s, _) => Number(s, () => (int)Math.Round(_host.CompressorDb), v => _host.CompressorDb = v, 0, 20);
            c["ZZET"] = (s, _) => Bool(s, () => _host.TxEq, v => _host.TxEq = v);
            c["ZZFA"] = (s, _) =>
            {
                if (IsSet(s)) { _host.FrequencyHz = long.Parse(s, Inv); return ""; }
                if (IsGet(s)) return Freq11(_host.FrequencyHz);
                return Error1;
            };
            c["ZZFB"] = (s, _) =>
            {
                if (IsSet(s)) { _host.VfoBHz = long.Parse(s, Inv); return ""; }
                if (IsGet(s)) return Freq11(_host.VfoBHz);
                return Error1;
            };
            c["ZZFT"] = (s, _) => IsGet(s) ? Freq11(_host.FrequencyHz) : Error1;     // no split: TX on VFO A
            c["ZZFH"] = (s, _) =>
            {
                if (IsSet(s)) { var (lo, _) = _host.Filter; _host.SetFilterEdges(lo, Math.Clamp(Int(s), -10000, 10000)); return ""; }
                if (IsGet(s)) return Signed(_host.Filter.high);
                return Error1;
            };
            c["ZZFL"] = (s, _) =>
            {
                if (IsSet(s)) { var (_, hi) = _host.Filter; _host.SetFilterEdges(Math.Clamp(Int(s), -10000, 10000), hi); return ""; }
                if (IsGet(s)) return Signed(_host.Filter.low);
                return Error1;
            };
            c["ZZFI"] = (s, _) =>
            {
                // console Filter enum: F1..F10 = 0..9, VAR1 = 10
                if (IsSet(s)) { int n = Int(s); if (n < 0 || n > 9) return Error1; _host.FilterIndex = n; return ""; }
                if (IsGet(s)) return Pad(_host.FilterIndex < 0 ? 10 : _host.FilterIndex);
                return Error1;
            };
            c["ZZGT"] = (s, _) =>
            {
                if (IsSet(s))
                {
                    int n = Int(s);
                    if (n <= (int)AGCMode.FIRST || n >= (int)AGCMode.LAST) return Error1;
                    _host.Agc = (AGCMode)n;
                    return "";
                }
                if (IsGet(s)) return ((int)_host.Agc).ToString(Inv);
                return Error1;
            };
            c["ZZID"] = (s, _) => "";
            c["ZZIF"] = (s, _) => Status(true);
            c["ZZLI"] = (s, _) => Bool(s, () => _host.PureSignal, v => _host.PureSignal = v);
            c["ZZMA"] = (s, _) => Bool(s, () => _host.Mute, v => _host.Mute = v);
            c["ZZMD"] = (s, _) =>
            {
                if (IsSet(s))
                {
                    DSPMode? m = String2Mode(s);
                    if (m == null) return Error1;
                    _host.Mode = m.Value;
                    return "";
                }
                if (IsGet(s)) return Mode2String(_host.Mode);
                return Error1;
            };
            c["ZZMG"] = (s, _) => Number(s, () => (int)Math.Round(_host.MicGainDb), v => _host.MicGainDb = v, -40, 70, signed: true);
            c["ZZML"] = (s, _) =>
            {
                // console CATCommands.ZZML: NAMEnn: entries, each padded to 7
                var sb = new StringBuilder();
                foreach (DSPMode m in Enum.GetValues(typeof(DSPMode)))
                {
                    if (m == DSPMode.FIRST || m == DSPMode.LAST) continue;
                    sb.Append((m.ToString() + ((int)m).ToString("00", Inv) + ":").PadLeft(7, ' '));
                }
                string list = sb.ToString().TrimEnd(':');
                return list.Length == _struct.NAns ? list : "ZZML" + list + ";";
            };
            c["ZZNA"] = (s, _) => IsGet(s) ? "0" : IsSet(s) && s == "0" ? "" : Error1;       // no noise blanker yet
            c["ZZNE"] = (s, _) =>
            {
                if (IsSet(s)) { NrType? t = NrFromCode(s); if (t == null) return Error1; _host.NoiseReduction = t.Value; return ""; }
                if (IsGet(s)) return NrCode();
                return Error1;
            };
            c["ZZNR"] = (s, _) => Bool(s, () => _host.NoiseReduction == NrType.NNR,
                                       v => { if (v) _host.NoiseReduction = NrType.NNR; else if (_host.NoiseReduction == NrType.NNR) _host.NoiseReduction = NrType.Off; });
            c["ZZNS"] = (s, _) => Bool(s, () => _host.NoiseReduction == NrType.NR2,
                                       v => { if (v) _host.NoiseReduction = NrType.NR2; else if (_host.NoiseReduction == NrType.NR2) _host.NoiseReduction = NrType.Off; });
            c["ZZNT"] = (s, _) => Bool(s, () => _host.AutoNotch, v => _host.AutoNotch = v);
            c["ZZPC"] = (s, _) => Number(s, () => _host.DrivePercent, v => _host.DrivePercent = v, 0, 100);
            c["ZZPS"] = (s, _) =>
            {
                if (IsSet(s) && (s == "0" || s == "1")) { _host.SetPower(s == "1", out string _); return ""; }
                if (IsGet(s)) return _host.PowerOn ? "1" : "0";
                return Error1;
            };
            c["ZZRM"] = (s, _) => Meter(s);
            c["ZZRT"] = (s, _) => IsGet(s) ? "0" : IsSet(s) && s == "0" ? "" : Error1;       // no RIT yet
            c["ZZXS"] = (s, _) => IsGet(s) ? "0" : IsSet(s) && s == "0" ? "" : Error1;       // no XIT yet
            c["ZZRX"] = (s, _) =>
            {
                var (min, max) = _host.AttenuatorRange;
                return Number(s, () => Math.Max(0, _host.AttenuatorDb), v => _host.AttenuatorDb = Math.Clamp(v, Math.Max(0, min), max), 0, 31);
            };
            c["ZZSA"] = (s, _) => { Retune(-_host.StepHz); return ""; };
            c["ZZSB"] = (s, _) => { Retune(+_host.StepHz); return ""; };
            c["ZZSD"] = (s, _) => { _host.StepHz = Steps[Math.Max(0, StepIndex() - 1)]; return ""; };
            c["ZZSU"] = (s, _) => { _host.StepHz = Steps[Math.Min(Steps.Length - 1, StepIndex() + 1)]; return ""; };
            c["ZZSM"] = (s, _) => SMeter(s, true);
            c["ZZSP"] = (s, _) => IsGet(s) ? "0" : IsSet(s) && s == "0" ? "" : Error1;       // no split yet
            c["ZZST"] = (s, _) => StepCodes[StepIndex()];
            c["ZZTH"] = (s, _) => Number(s, () => _host.TxFilter.high, v => _host.TxFilter = (_host.TxFilter.low, v), 500, 20000);
            c["ZZTL"] = (s, _) => Number(s, () => _host.TxFilter.low, v => _host.TxFilter = (v, _host.TxFilter.high), 0, 2000);
            c["ZZTO"] = (s, _) => Number(s, () => _host.TunePercent, v => _host.TunePercent = v, 0, 100);
            c["ZZTU"] = (s, _) => Bool(s, () => _host.Tuning, v => _host.SetTune(v, out string _));
            c["ZZTX"] = (s, _) => Bool(s, () => _host.Mox, v => _host.SetMox(v, out string _));
            c["ZZUS"] = (s, _) => { _host.PureSignalSingleCal(); return ""; };
            c["ZZUT"] = (s, _) => Bool(s, () => _host.TwoTone, v => _host.SetTwoTone(v, out string _));
            c["ZZVA"] = (s, _) => Bool(s, () => _host.PcAudio, v => _host.PcAudio = v);
            c["ZZVE"] = (s, _) => Bool(s, () => _host.Vox, v => _host.Vox = v);
            c["ZZXH"] = (s, _) => Number(s, () => _host.VoxHoldMs, v => _host.VoxHoldMs = v, 0, 4000);
            c["ZZVN"] = (s, _) => (_host.Version ?? "0").PadLeft(12, '0');
            c["ZZZM"] = (s, _) => "ZZZM" + _host.ModelName + ";";
            c["ZZZV"] = (s, _) => "ZZZV" + (_host.Version ?? "").Replace(";", "") + ";";
            c["ZZXN"] = (s, _) =>
            {
                // console CATCommands.ZZXN: AGC, preamp, squelch, NB1, NB2, NR, NR2, SNB, ANF bits
                int n = (int)_host.Agc & 7;
                NrType nr = _host.NoiseReduction;
                if (nr == NrType.NNR) n += 1 << 9;
                if (nr != NrType.Off && nr != NrType.NNR) n += 1 << 10;
                if (_host.AutoNotch) n += 1 << 12;
                return Pad(n);
            };
            c["ZZXV"] = (s, _) =>
            {
                // console CATCommands.ZZXV: RIT, locks, split, CTUN, MOX (bit 6), TUNE (bit 7), XIT, sync
                int n = 0;
                if (_host.Mox) n += 1 << 6;
                if (_host.Tuning) n += 1 << 7;
                return Pad(n);
            };
            return c;
        }
    }
}
