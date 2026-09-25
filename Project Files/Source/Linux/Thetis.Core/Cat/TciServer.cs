/*  TciServer.cs

This file is part of a program that implements a Software-Defined Radio.

TCI (Expert Electronics' Transceiver Control Interface) over WebSocket,
answering as the Windows console's TCIServer.cs does (protocol "Thetis",
2.0; its spellings: modes in capitals, AGC "normal", volume in dB -60..0,
default port 50001).  Control only: VFO / DDS / IF, modulation, filter,
transmit (trx), tune, drive, volume and mute, NR, ANF, AGC, power
(start / stop), and the receive and transmit sensors.  Audio and I/Q
streaming are not offered yet.

Every client gets the radio's state when it connects (ending with
"ready;") and every change after that, whoever made it.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Thetis.Radio;

namespace Thetis.Cat
{
    public sealed class TciServer : IDisposable
    {
        private readonly ICatHost _host;
        private readonly IPAddress _address;
        private readonly int _port;
        private TcpListener _listener;
        private readonly List<Client> _clients = new List<Client>();
        private readonly object _lock = new object();
        private volatile bool _stopping;
        private Thread _acceptThread, _pollThread;
        private Dictionary<string, string> _last = new Dictionary<string, string>();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public TciServer(ICatHost host, IPAddress address, int port)
        {
            _host = host;
            _address = address;
            _port = port;
        }

        public volatile string Status = "starting";
        public volatile bool Ok;

        /// <summary>(client, "&lt;" received / "&gt;" sent, text).</summary>
        public event Action<string, string, string> Traffic;

        public int ClientCount { get { lock (_lock) return _clients.Count; } }

        public void Start()
        {
            try
            {
                _listener = new TcpListener(_address, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();
            }
            catch (Exception ex)
            {
                Status = $"cannot listen on port {_port}: {ex.Message}";
                Ok = false;
                return;
            }
            Ok = true;
            SetStatus();
            _acceptThread = new Thread(Accept) { IsBackground = true, Name = "TCI accept" };
            _acceptThread.Start();
            _pollThread = new Thread(Poll) { IsBackground = true, Name = "TCI poll" };
            _pollThread.Start();
        }

        public void Dispose()
        {
            _stopping = true;
            try { _listener?.Stop(); } catch (Exception) { }
            List<Client> cs;
            lock (_lock) { cs = _clients.ToList(); _clients.Clear(); }
            foreach (var c in cs) c.Close();
            Ok = false;
            Status = "closed";
        }

        private void SetStatus()
        {
            string where = _address.Equals(IPAddress.Any) ? $"all interfaces, port {_port}" : $"{_address}:{_port}";
            int n = ClientCount;
            Status = $"listening on ws://{where}, {n} program{(n == 1 ? "" : "s")} connected";
        }

        private void Accept()
        {
            while (!_stopping)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch (Exception) { break; }
                tcp.NoDelay = true;
                _ = Task.Run(() => ServeAsync(tcp));
            }
        }

        #region websocket

        private async Task ServeAsync(TcpClient tcp)
        {
            Client client = null;
            try
            {
                var stream = tcp.GetStream();
                string key = await ReadHandshakeAsync(stream);
                if (key == null) { tcp.Close(); return; }
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                byte[] resp = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
                await stream.WriteAsync(resp);
                var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
                client = new Client(this, tcp, ws, "TCI " + (tcp.Client.RemoteEndPoint as IPEndPoint)?.Port);
                lock (_lock) _clients.Add(client);
                SetStatus();
                SendInitial(client);
                await client.ReceiveLoopAsync();
            }
            catch (Exception) { }
            finally
            {
                if (client != null)
                {
                    client.Close();
                    lock (_lock) _clients.Remove(client);
                }
                try { tcp.Close(); } catch (Exception) { }
                if (!_stopping) SetStatus();
            }
        }

        private static async Task<string> ReadHandshakeAsync(Stream s)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (sb.Length < 8192)
            {
                int n = await s.ReadAsync(one, cts.Token);
                if (n <= 0) return null;
                sb.Append((char)one[0]);
                if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') break;
            }
            foreach (string line in sb.ToString().Split("\r\n"))
            {
                int c = line.IndexOf(':');
                if (c > 0 && line.Substring(0, c).Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(c + 1).Trim();
            }
            return null;
        }

        private sealed class Client
        {
            private readonly TciServer _server;
            private readonly TcpClient _tcp;
            private readonly WebSocket _ws;
            private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
            public readonly string Name;
            public volatile bool RxSensors, TxSensors;
            public int RxSensorsMs = 200, TxSensorsMs = 200;
            public DateTime NextRx, NextTx;

            public Client(TciServer server, TcpClient tcp, WebSocket ws, string name)
            {
                _server = server; _tcp = tcp; _ws = ws; Name = name;
            }

            public void Send(string text)
            {
                if (_ws.State != WebSocketState.Open) return;
                _server.Traffic?.Invoke(Name, ">", text);
                byte[] b = Encoding.UTF8.GetBytes(text);
                _sendLock.Wait();
                try { _ws.SendAsync(b, WebSocketMessageType.Text, true, CancellationToken.None).Wait(2000); }
                catch (Exception) { }
                finally { _sendLock.Release(); }
            }

            public async Task ReceiveLoopAsync()
            {
                var buf = new byte[4096];
                var msg = new MemoryStream();
                while (_ws.State == WebSocketState.Open && !_server._stopping)
                {
                    var r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    msg.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    if (r.MessageType == WebSocketMessageType.Text)
                    {
                        string text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                        _server.Traffic?.Invoke(Name, "<", text);
                        foreach (string cmd in text.Split(';'))
                        {
                            string c = cmd.Trim();
                            if (c.Length > 0) _server.Handle(this, c);
                        }
                    }
                    msg.SetLength(0);
                }
            }

            public void Close()
            {
                try { _ws.Abort(); } catch (Exception) { }
                try { _tcp.Close(); } catch (Exception) { }
            }
        }

        #endregion

        #region state

        private static string B(bool v) => v ? "true" : "false";

        private static string ModeName(DSPMode m) => m.ToString().ToUpperInvariant();

        private static DSPMode? ParseMode(string s, long hz) => s.Trim().ToUpperInvariant() switch
        {
            "LSB" => DSPMode.LSB, "USB" => DSPMode.USB, "DSB" => DSPMode.DSB, "CWL" => DSPMode.CWL, "CWU" => DSPMode.CWU,
            "CW" => hz >= 10_000_000 ? DSPMode.CWU : DSPMode.CWL,
            "FM" or "NFM" => DSPMode.FM, "AM" => DSPMode.AM, "SAM" => DSPMode.SAM,
            "DIGU" => DSPMode.DIGU, "DIGL" => DSPMode.DIGL,
            _ => null,
        };

        private static string AgcName(AGCMode m) => m switch
        {
            AGCMode.FIXD => "off", AGCMode.LONG => "long", AGCMode.SLOW => "slow", AGCMode.FAST => "fast",
            AGCMode.CUSTOM => "custom", _ => "normal",
        };

        private static AGCMode ParseAgc(string s) => s.Trim().ToLowerInvariant() switch
        {
            "off" or "fixd" or "fixed" => AGCMode.FIXD, "long" => AGCMode.LONG, "slow" => AGCMode.SLOW,
            "fast" => AGCMode.FAST, "custom" => AGCMode.CUSTOM, _ => AGCMode.MED,
        };

        // TCIServer.linearToDbVolume / dbToLinearVolume
        private static double VolumeDb(int percent) => Math.Clamp(percent * 0.6 - 60.0, -60.0, 0.0);
        private static int VolumePercent(double db) => (int)Math.Clamp((db + 60.0) / 0.6, 0, 100);

        /// <summary>The state messages, keyed so changes can be found.</summary>
        private Dictionary<string, string> Snapshot()
        {
            return _host.Invoke(() =>
            {
                var d = new Dictionary<string, string>();
                long a = _host.FrequencyHz;
                var (lo, hi) = _host.Filter;
                bool mox = _host.Mox, tune = _host.Tuning;
                d["dds"] = $"dds:0,{a};";
                d["if0"] = "if:0,0,0;";
                d["vfo0"] = $"vfo:0,0,{a};";
                d["vfo1"] = $"vfo:0,1,{_host.VfoBHz};";
                d["mod"] = $"modulation:0,{ModeName(_host.Mode)};";
                d["filt"] = $"rx_filter_band:0,{lo},{hi};";
                d["trx"] = $"trx:0,{B(mox && !tune)};";
                d["tune"] = $"tune:0,{B(tune)};";
                d["drive"] = $"drive:0,{_host.DrivePercent};";
                d["tdrive"] = $"tune_drive:0,{_host.TunePercent};";
                d["vol"] = "volume:" + VolumeDb(_host.VolumePercent).ToString("F1", Inv) + ";";
                d["mute"] = $"mute:{B(_host.Mute)};";
                d["rxmute"] = $"rx_mute:0,{B(_host.Mute)};";
                d["nr"] = $"rx_nr_enable:0,{B(_host.NoiseReduction != NrType.Off)};";
                d["anf"] = $"rx_anf_enable:0,{B(_host.AutoNotch)};";
                d["nb"] = "rx_nb_enable:0,false;";
                d["agc"] = $"agc_mode:0,{AgcName(_host.Agc)};";
                d["agcg"] = $"agc_gain:0,{(int)Math.Round(_host.AgcTopDb)};";
                d["split"] = "split_enable:0,false;";
                d["txen"] = $"tx_enable:0,{B(!mox)};";
                d["rxen"] = "rx_enable:0,true;";
                d["run"] = _host.PowerOn ? "start;" : "stop;";
                return d;
            });
        }

        private void SendInitial(Client c)
        {
            int half = 0;
            try { half = _host.Invoke(() => _host.SampleRate) / 2; } catch (Exception) { }
            var sb = new StringBuilder();
            sb.Append("protocol:Thetis,2.0;");
            string model = "HPSDR";
            try { model = _host.Invoke(() => _host.ModelName); } catch (Exception) { }
            sb.Append($"device:{model};");
            sb.Append("receive_only:false;");
            sb.Append("trx_count:1;");
            sb.Append("channels_count:2;");
            sb.Append("vfo_limits:0,61440000;");
            sb.Append($"if_limits:{-half},{half};");
            sb.Append("modulations_list:AM,SAM,DSB,LSB,USB,NFM,FM,DIGL,DIGU,CWL,CWU;");
            foreach (string m in sb.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries)) c.Send(m + ";");
            Dictionary<string, string> snap;
            try { snap = Snapshot(); } catch (Exception) { snap = new Dictionary<string, string>(); }
            foreach (var kv in snap) if (kv.Key != "run") c.Send(kv.Value);
            if (snap.TryGetValue("run", out string run)) c.Send(run);
            c.Send("ready;");
        }

        private void Broadcast(string text)
        {
            List<Client> cs;
            lock (_lock) cs = _clients.ToList();
            foreach (var c in cs) c.Send(text);
        }

        private void Poll()
        {
            while (!_stopping)
            {
                Thread.Sleep(50);
                List<Client> cs;
                lock (_lock) cs = _clients.ToList();
                if (cs.Count == 0) { _last = new Dictionary<string, string>(); continue; }
                Dictionary<string, string> now;
                try { now = Snapshot(); }
                catch (Exception) { continue; }
                foreach (var kv in now)
                {
                    if (_last.Count > 0 && (!_last.TryGetValue(kv.Key, out string old) || old != kv.Value))
                        Broadcast(kv.Value);
                }
                _last = now;

                // sensors
                DateTime t = DateTime.UtcNow;
                foreach (var c in cs)
                {
                    if (c.RxSensors && t >= c.NextRx)
                    {
                        c.NextRx = t.AddMilliseconds(c.RxSensorsMs);
                        float dbm = _host.Invoke(() => _host.PowerOn ? _host.SignalDbm : -140f);
                        string v = dbm.ToString("F1", Inv);
                        c.Send($"rx_sensors:0,{v};");
                        c.Send($"rx_channel_sensors:0,0,{v};");
                    }
                    if (c.TxSensors && t >= c.NextTx)
                    {
                        c.NextTx = t.AddMilliseconds(c.TxSensorsMs);
                        var (mox, mic, fwd, swr) = _host.Invoke(() => (_host.Mox, _host.MicDbfs, _host.ForwardWatts, _host.Swr));
                        if (mox)
                            c.Send(string.Format(Inv, "tx_sensors:0,{0:F1},{1:F1},{2:F1},{3:F1};", Math.Max(-100f, mic), fwd, fwd, swr));
                    }
                }
            }
        }

        #endregion

        #region commands

        private void Handle(Client c, string cmd)
        {
            int colon = cmd.IndexOf(':');
            string name = (colon < 0 ? cmd : cmd.Substring(0, colon)).Trim().ToLowerInvariant();
            string[] a = colon < 0 ? Array.Empty<string>() : cmd.Substring(colon + 1).Split(',').Select(x => x.Trim()).ToArray();
            try
            {
                string reply = _host.Invoke(() => Execute(c, name, a));
                if (reply != null) c.Send(reply);
            }
            catch (Exception) { }
        }

        private static bool Bool(string s) => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
        private static long L(string s) => (long)double.Parse(s, Inv);
        private static int I(string s) => (int)Math.Round(double.Parse(s, Inv));

        // returns the state message to send back (a read, or the new value after a set), or null
        private string Execute(Client c, string name, string[] a)
        {
            switch (name)
            {
                case "vfo":
                    if (a.Length >= 3 && a[0] == "0")
                    {
                        if (a[1] == "0") _host.FrequencyHz = L(a[2]); else _host.VfoBHz = L(a[2]);
                    }
                    if (a.Length >= 2 && a[0] == "0") return $"vfo:0,{a[1]},{(a[1] == "0" ? _host.FrequencyHz : _host.VfoBHz)};";
                    return null;
                case "dds":
                    if (a.Length >= 2 && a[0] == "0") _host.FrequencyHz = L(a[1]);
                    return a.Length >= 1 && a[0] == "0" ? $"dds:0,{_host.FrequencyHz};" : null;
                case "if":
                    // no click tuning yet: an IF offset moves the VFO, and the IF stays 0
                    if (a.Length >= 3 && a[0] == "0" && a[1] == "0") _host.FrequencyHz += L(a[2]);
                    return a.Length >= 2 && a[0] == "0" ? $"if:0,{a[1]},0;" : null;
                case "modulation":
                    if (a.Length >= 2 && a[0] == "0")
                    {
                        DSPMode? m = ParseMode(a[1], _host.FrequencyHz);
                        if (m != null) _host.Mode = m.Value;
                    }
                    return a.Length >= 1 && a[0] == "0" ? $"modulation:0,{ModeName(_host.Mode)};" : null;
                case "rx_filter_band":
                    if (a.Length >= 3 && a[0] == "0") _host.SetFilterEdges(I(a[1]), I(a[2]));
                    return a.Length >= 1 && a[0] == "0" ? $"rx_filter_band:0,{_host.Filter.low},{_host.Filter.high};" : null;
                case "trx":
                    if (a.Length >= 2 && a[0] == "0") _host.SetMox(Bool(a[1]), out string _);
                    return a.Length >= 1 && a[0] == "0" ? $"trx:0,{B(_host.Mox && !_host.Tuning)};" : null;
                case "tune":
                    if (a.Length >= 2 && a[0] == "0") _host.SetTune(Bool(a[1]), out string _);
                    return a.Length >= 1 && a[0] == "0" ? $"tune:0,{B(_host.Tuning)};" : null;
                case "drive":
                    // drive:trx,value (2.0) or drive:value (1.x); drive:trx alone reads
                    if (a.Length >= 2) _host.DrivePercent = Math.Clamp(I(a[1]), 0, 100);
                    else if (a.Length == 1 && a[0].Length > 1) _host.DrivePercent = Math.Clamp(I(a[0]), 0, 100);
                    return $"drive:0,{_host.DrivePercent};";
                case "tune_drive":
                    if (a.Length >= 2) _host.TunePercent = Math.Clamp(I(a[1]), 0, 100);
                    else if (a.Length == 1 && a[0].Length > 1) _host.TunePercent = Math.Clamp(I(a[0]), 0, 100);
                    return $"tune_drive:0,{_host.TunePercent};";
                case "volume":
                    if (a.Length >= 1) _host.VolumePercent = VolumePercent(double.Parse(a[0], Inv));
                    return "volume:" + VolumeDb(_host.VolumePercent).ToString("F1", Inv) + ";";
                case "mute":
                    if (a.Length >= 1) _host.Mute = Bool(a[0]);
                    return $"mute:{B(_host.Mute)};";
                case "rx_mute":
                    if (a.Length >= 2 && a[0] == "0") _host.Mute = Bool(a[1]);
                    return $"rx_mute:0,{B(_host.Mute)};";
                case "rx_nr_enable":
                    if (a.Length >= 2 && a[0] == "0")
                    {
                        bool on = Bool(a[1]);
                        if (on && _host.NoiseReduction == NrType.Off) _host.NoiseReduction = NrType.NNR;
                        else if (!on) _host.NoiseReduction = NrType.Off;
                    }
                    return $"rx_nr_enable:0,{B(_host.NoiseReduction != NrType.Off)};";
                case "rx_anf_enable":
                    if (a.Length >= 2 && a[0] == "0") _host.AutoNotch = Bool(a[1]);
                    return $"rx_anf_enable:0,{B(_host.AutoNotch)};";
                case "agc_mode":
                    if (a.Length >= 2 && a[0] == "0") _host.Agc = ParseAgc(a[1]);
                    return $"agc_mode:0,{AgcName(_host.Agc)};";
                case "agc_gain":
                    if (a.Length >= 2 && a[0] == "0") _host.AgcTopDb = Math.Clamp(I(a[1]), -20, 120);
                    return $"agc_gain:0,{(int)Math.Round(_host.AgcTopDb)};";
                case "split_enable":
                    return "split_enable:0,false;";
                case "start":
                    _host.SetPower(true, out string _);
                    return null;
                case "stop":
                    _host.SetPower(false, out string _);
                    return null;
                case "rx_sensors_enable":
                    c.RxSensors = a.Length >= 1 && Bool(a[0]);
                    if (a.Length >= 2) c.RxSensorsMs = Math.Clamp(I(a[1]), 30, 1000);
                    return null;
                case "tx_sensors_enable":
                    c.TxSensors = a.Length >= 1 && Bool(a[0]);
                    if (a.Length >= 2) c.TxSensorsMs = Math.Clamp(I(a[1]), 30, 1000);
                    return null;
                case "rx_smeter":
                    return $"rx_smeter:0,0,{(_host.PowerOn ? _host.SignalDbm : -140f).ToString("F1", Inv)};";
                default:
                    return null;
            }
        }

        #endregion
    }
}
