/*  CatCheck.cs

This file is part of a program that implements a Software-Defined Radio.

CAT and TCI checks against the simulator: the command interpreter's answers,
then each transport -- a serial port (a pseudo-terminal pair stands in for a
USB to RS232 adapter and its cable), a virtual port, the TCP CAT server with
auto information, and the TCI server -- and, if Hamlib's rigctl is
installed, Hamlib's own TS-2000 and PowerSDR/Thetis drivers.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Thetis;
using Thetis.Cat;
using Thetis.Radio;

internal static partial class Program
{
    private static void CatCheck(RadioController radio, string statusFile, string workDir)
    {
        Directory.CreateDirectory(workDir);
        var host = new RadioCatHost(radio);
        var engine = new CatEngine(host);
        var session = new CatSession();
        string Ask(string cmd) => engine.ExecuteAll(cmd, session, out _);
        void Expect(string cmd, string answer, string what) =>
            Check(Ask(cmd) == answer, $"{what}: {cmd} -> {Ask(cmd).TrimEnd()}" + (Ask(cmd) == answer ? "" : $" (expected {answer})"));

        Console.WriteLine("== CAT commands (Kenwood and ZZ)");
        Check(CatStructs.All.Count > 400, $"command table loaded ({CatStructs.All.Count} commands, {engine.Implemented.Count} implemented here)");
        Expect("ID;", "ID019;", "ID is TS-2000");
        Check(Ask("FA00014074000;") == "" && Math.Abs(radio.FrequencyMHz - 14.074) < 1e-9, "FA sets VFO A (14.074 MHz)");
        Expect("FA;", "FA00014074000;", "FA reads VFO A");
        Expect("ZZFA;", "ZZFA00014074000;", "ZZFA reads VFO A");
        Ask("MD2;");
        Check(radio.Mode == DSPMode.USB, "MD2 selects USB");
        Ask("ZZMD07;");
        Check(radio.Mode == DSPMode.DIGU, "ZZMD07 selects DIGU");
        Expect("MD;", "MD9;", "MD reads DIGU as 9");
        Expect("ZZMD;", "ZZMD07;", "ZZMD reads DIGU");
        engine.Options.DigUIsUsb = true;
        Expect("MD;", "MD2;", "with DIGU-as-USB, MD reads DIGU as 2");
        engine.Options.DigUIsUsb = false;
        Ask("ZZMD01;");
        string ifs = Ask("IF;");
        Check(ifs.Length == 38 && ifs.StartsWith("IF00014074000") && ifs[29] == '2', $"IF status: {ifs}");
        string zzif = Ask("ZZIF;");
        Check(zzif.Length == 41 && zzif.StartsWith("ZZIF00014074000") && zzif.Substring(31, 2) == "01", $"ZZIF status: {zzif}");
        Check(System.Text.RegularExpressions.Regex.IsMatch(Ask("SM0;"), @"^SM0\d{4};$"), $"SM0 S-meter: {Ask("SM0;")}");
        Check(System.Text.RegularExpressions.Regex.IsMatch(Ask("ZZSM0;"), @"^ZZSM0\d{3};$"), $"ZZSM0 S-meter: {Ask("ZZSM0;")}");
        Ask("ZZAG050;");
        Check(Math.Abs(radio.Volume - 0.5) < 1e-9, "ZZAG050 sets AF to 50 %");
        Expect("AG0;", "AG0128;", "AG reads 50 % as 128 of 255");
        Ask("ZZFH+2800;");
        Check(radio.Filter.High == 2800, "ZZFH sets the filter's high edge");
        Expect("ZZFH;", "ZZFH+2800;", "ZZFH reads it");
        Ask("ZZGT4;");
        Check(radio.Agc == AGCMode.FAST, "ZZGT4 selects AGC fast");
        Expect("GT;", "GT004;", "GT reads it");
        Ask("ZZAR+080;");
        Check(Math.Abs(radio.AgcTop - 80) < 1e-9, "ZZAR sets the AGC gain");
        Expect("ZZAR;", "ZZAR+080;", "ZZAR reads it");
        Ask("ZZRX10;");
        Check(radio.AttenuatorDb == 10, "ZZRX10 sets the attenuator");
        Expect("ZZRX;", "ZZRX10;", "ZZRX reads it");
        Ask("ZZRX00;");
        Ask("ZZPC035;");
        Check(radio.DrivePercent == 35, "ZZPC sets the drive");
        Expect("PC;", "PC035;", "PC reads it");
        Ask("ZZBS020;");
        Check(Math.Abs(radio.FrequencyMHz - 14.2) < 1e-9, "ZZBS020 goes to 20 m");
        Expect("ZZBS;", "ZZBS020;", "ZZBS reads the band");
        Ask("ZZAC08;");
        long f0 = (long)Math.Round(radio.FrequencyMHz * 1e6);
        Ask("UP;");
        Check((long)Math.Round(radio.FrequencyMHz * 1e6) == f0 + 1000, "ZZAC08 (1 kHz step) then UP tunes up 1 kHz");
        Expect("ZZST;", "ZZST0011;", "ZZST reads the step code");
        Ask("ZZNT1;");
        Check(radio.AutoNotch, "ZZNT1 turns ANF on");
        Ask("ZZNT0;");
        Ask("ZZNE1;");
        Check(radio.NoiseReductionType == NrType.NNR, "ZZNE1 selects NNR");
        Expect("ZZNR;", "ZZNR1;", "ZZNR reads it");
        Ask("ZZNE0;");
        Expect("XX;", "?;", "unknown command");
        Expect("FA123;", "?;", "wrong length");
        Expect("ZZNE7;", "?;", "value out of range");
        Check(Ask("FA;MD;ID;") == "FA00014201000;MD2;ID019;", $"three commands in one write: {Ask("FA;MD;ID;")}");
        Check(Ask(";;FA;") == "FA00014201000;", "leading terminators ignored (WriteLog)");

        Console.WriteLine("== CAT transmit");
        radio.TransmitAllowed = false;
        Ask("TX;");
        Check(!radio.Mox, "TX refused while transmitting is disabled");
        radio.TransmitAllowed = true;
        radio.Region = TxRegion.IaruRegion1;
        radio.DrivePercent = 10;
        Ask("FA00014200000;");
        Ask("TX;");
        Check(radio.Mox && WaitSim(statusFile, st => st.GetProperty("mox").GetBoolean(), 3000, out _), "TX keys the radio");
        Expect("ZZTX;", "ZZTX1;", "ZZTX reads it");
        Ask("RX;");
        Check(!radio.Mox && WaitSim(statusFile, st => !st.GetProperty("mox").GetBoolean(), 3000, out _), "RX unkeys it");
        Ask("TX0;");
        Check(radio.Mox && WaitSim(statusFile, st => st.GetProperty("mox").GetBoolean(), 3000, out _), "TX0 (Hamlib's TS-2000 PTT) keys the radio");
        Ask("RX;");
        WaitSim(statusFile, st => !st.GetProperty("mox").GetBoolean(), 3000, out _);
        Ask("ZZTU1;");
        Thread.Sleep(400);
        Check(radio.Tuning, "ZZTU1 tunes");
        Ask("ZZTU0;");
        Thread.Sleep(300);

        // ---------------- transports ----------------
        var svc = new CatService(host);
        var settings = new CatSettings { TcpEnabled = true, TcpPort = 31099, TciEnabled = true, TciPort = 50099 };
        settings.Normalize(workDir);
        // serial: a pseudo-terminal pair stands in for a USB to RS232 adapter and its cable
        using var cable = new VirtualSerialPort(Path.Combine(workDir, "ttyTest"));
        settings.Ports[0].Enabled = true;
        settings.Ports[0].Kind = CatPortKind.Serial;
        settings.Ports[0].Device = cable.LinkPath;
        settings.Ports[0].Baud = 38400;
        settings.Ports[1].Enabled = true;
        settings.Ports[1].Kind = CatPortKind.Virtual;
        svc.Apply(settings);
        Thread.Sleep(800);
        foreach (var (name, st, ok) in svc.Status()) Console.WriteLine($"  {name}: {st}");

        Console.WriteLine("== CAT on a serial port");
        string SerialAsk(string cmd)
        {
            var buf = new byte[1024];
            while (cable.Read(buf, 0) > 0) { }
            cable.Write(Encoding.ASCII.GetBytes(cmd));
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                int n = cable.Read(buf, 100);
                if (n > 0) sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.Length > 0 && sb[^1] == ';') break;
            }
            return sb.ToString();
        }
        Check(SerialAsk("ID;") == "ID019;", "serial port answers ID");
        SerialAsk("FA00007074000;");
        Thread.Sleep(100);
        Check(Math.Abs(radio.FrequencyMHz - 7.074) < 1e-9 && SerialAsk("FA;") == "FA00007074000;", "serial port sets and reads VFO A");

        Console.WriteLine("== CAT on a virtual port");
        string vpath = settings.Ports[1].VirtualPath;
        Check(File.Exists(vpath) || new FileInfo(vpath).LinkTarget != null, $"virtual port {vpath} -> {new FileInfo(vpath).LinkTarget}");
        using (var sp = new SerialPort(SerialPorts.Resolve(vpath), 9600) { ReadTimeout = 1500 })
        {
            sp.Open();
            sp.Write("FA;");
            string ans = ReadTo(sp, ';');
            Check(ans == "FA00007074000;", $"a program on the virtual port reads VFO A ({ans})");
            sp.Write("ZZMD00;");
            Thread.Sleep(150);
            Check(radio.Mode == DSPMode.LSB, "and sets the mode (LSB)");
        }
        // a second program opens it later: nothing stale in the way
        using (var sp = new SerialPort(SerialPorts.Resolve(vpath), 9600) { ReadTimeout = 1500 })
        {
            sp.Open();
            sp.Write("MD;");
            string ans = ReadTo(sp, ';');
            Check(ans == "MD1;", $"reopened by another program: MD -> {ans}");
        }

        Console.WriteLine("== CAT over TCP, auto information");
        using (var tcp = new TcpClient("127.0.0.1", 31099))
        {
            var s = tcp.GetStream();
            s.ReadTimeout = 2000;
            void Send(string t) => s.Write(Encoding.ASCII.GetBytes(t));
            string Read()
            {
                var sb = new StringBuilder();
                var b = new byte[1];
                while (s.Read(b, 0, 1) == 1) { sb.Append((char)b[0]); if (b[0] == ';') break; }
                return sb.ToString();
            }
            Send("ZZFA;");
            Check(Read() == "ZZFA00007074000;", "TCP client reads VFO A");
            Send("AI1;");
            Thread.Sleep(300);
            radio.FrequencyMHz = 7.0745;
            string ai = Read();
            Check(ai == "FA00007074500;", $"with AI1, a tuning change is sent unasked ({ai})");
            Send("AI0;");
        }

        Console.WriteLine("== TCI");
        TciCheck(radio, statusFile);

        Console.WriteLine("== Hamlib (rigctl)");
        HamlibCheck(radio, vpath);

        svc.Stop();
        Thread.Sleep(500);          // each port closes on its own thread
        Check(new FileInfo(vpath).LinkTarget == null && !File.Exists(vpath), "virtual port name removed when CAT stops");
        radio.TransmitAllowed = false;
        radio.Region = TxRegion.None;
    }

    private static string ReadTo(SerialPort sp, char end)
    {
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                int c = sp.ReadChar();
                sb.Append((char)c);
                if (c == end) break;
            }
        }
        catch (TimeoutException) { }
        return sb.ToString();
    }

    private static void TciCheck(RadioController radio, string statusFile)
    {
        using var ws = new ClientWebSocket();
        ws.ConnectAsync(new Uri("ws://127.0.0.1:50099"), CancellationToken.None).Wait(3000);
        Check(ws.State == WebSocketState.Open, "TCI client connected");
        if (ws.State != WebSocketState.Open) return;
        var got = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var cts = new CancellationTokenSource();
        var reader = System.Threading.Tasks.Task.Run(async () =>
        {
            var buf = new byte[4096];
            while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try
                {
                    var r = await ws.ReceiveAsync(buf, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    got.Enqueue(Encoding.UTF8.GetString(buf, 0, r.Count));
                }
                catch (Exception) { break; }
            }
        });
        string Wait(Func<string, bool> pred, int ms = 2000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                while (got.TryDequeue(out string m)) if (pred(m)) return m;
                Thread.Sleep(20);
            }
            return null;
        }
        void Send(string t) => ws.SendAsync(Encoding.UTF8.GetBytes(t), WebSocketMessageType.Text, true, CancellationToken.None).Wait();

        var init = new System.Collections.Generic.List<string>();
        Wait(m => { init.Add(m); return m == "ready;"; }, 3000);
        Check(init.Contains("protocol:Thetis,2.0;") && init.Last() == "ready;", $"initial state ({init.Count} messages) ends with ready;");
        Check(init.Contains($"vfo:0,0,{(long)Math.Round(radio.FrequencyMHz * 1e6)};"), "initial state has VFO A");
        Check(init.Contains("start;"), "initial state: radio running (start;)");

        Send("vfo:0,0,14100000;");
        Check(Wait(m => m == "vfo:0,0,14100000;") != null && Math.Abs(radio.FrequencyMHz - 14.1) < 1e-9, "vfo sets VFO A and is confirmed");
        Send("modulation:0,cw;");
        Check(Wait(m => m == "modulation:0,CWU;") != null && radio.Mode == DSPMode.CWU, "modulation cw on 20 m selects CWU");
        radio.Mode = DSPMode.USB;
        Check(Wait(m => m == "modulation:0,USB;") != null, "a mode change made elsewhere is sent to the client");
        Send("rx_filter_band:0,200,2700;");
        Check(Wait(m => m == "rx_filter_band:0,200,2700;") != null && radio.Filter.Low == 200 && radio.Filter.High == 2700, "rx_filter_band sets the filter");
        Send("volume:-30;");
        Check(Wait(m => m == "volume:-30.0;") != null && Math.Abs(radio.Volume - 0.5) < 0.011, "volume -30 dB = AF 50 %");
        Send("rx_sensors_enable:true,100;");
        string sm = Wait(m => m.StartsWith("rx_sensors:0,"));
        Check(sm != null, $"rx_sensors sent ({sm})");
        Send("rx_sensors_enable:false;");
        Send("trx:0,true;");
        Check(Wait(m => m == "trx:0,true;") != null, "trx:0,true keys the radio");
        Check(radio.Mox && WaitSim(statusFile, st => st.GetProperty("mox").GetBoolean(), 3000, out _), "the simulator is transmitting");
        Send("trx:0,false;");
        Check(Wait(m => m == "trx:0,false;") != null, "trx:0,false unkeys");
        Thread.Sleep(300);
        cts.Cancel();
        try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000); } catch (Exception) { }
    }

    private static void HamlibCheck(RadioController radio, string vpath)
    {
        string rigctl = new[] { "/usr/bin/rigctl", "/usr/local/bin/rigctl" }.FirstOrDefault(File.Exists);
        if (rigctl == null) { Console.WriteLine("  rigctl not installed: skipped"); return; }
        string Run(string args)
        {
            var psi = new ProcessStartInfo(rigctl, args) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return o.Trim();
        }
        foreach (var (model, name) in new[] { ("2014", "TS-2000"), ("2048", "PowerSDR/Thetis") })
        {
            string r = $"-m {model} -r {vpath} -s 9600";
            Run(r + " F 7074000");
            Check(Math.Abs(radio.FrequencyMHz - 7.074) < 1e-9, $"Hamlib {name}: set frequency 7.074 MHz");
            string f = Run(r + " f");
            Check(f == "7074000", $"Hamlib {name}: get frequency ({f})");
            Run(r + " M USB 0");
            string m = Run(r + " m");
            Check(radio.Mode == DSPMode.USB && m.StartsWith("USB"), $"Hamlib {name}: mode USB ({m.Replace('\n', ' ')})");
            Run(r + " T 1");
            Thread.Sleep(300);
            string t1 = Run(r + " t");
            bool keyed = radio.Mox;
            Run(r + " T 0");
            Thread.Sleep(300);
            Check(keyed && t1 == "1" && !radio.Mox, $"Hamlib {name}: PTT on and off (t -> {t1})");
            Run(r + " F 14074000");
            string both = Run(r + " f m");
            Check(both.StartsWith("14074000"), $"Hamlib {name}: frequency and mode in one session ({both.Replace('\n', ' ')})");
        }
        // over TCP, Hamlib's network form of the port
        string t = Run("-m 2014 -r 127.0.0.1:31099 f");
        Check(t == "14074000", $"Hamlib TS-2000 over TCP: get frequency ({t})");
    }
}
