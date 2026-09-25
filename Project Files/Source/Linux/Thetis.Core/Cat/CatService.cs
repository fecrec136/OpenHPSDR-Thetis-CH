/*  CatService.cs

This file is part of a program that implements a Software-Defined Radio.

Runs CAT on the ports and servers chosen in CatSettings: serial ports
(motherboard or USB to RS232), virtual ports for programs on this computer,
the TCP CAT server, and the TCI server.  Each port or network connection has
its own session (auto information on or off); all share one CatEngine.

A serial port that disappears (a USB adapter unplugged) is reopened when it
comes back.  A serial port can also key the transmitter from its CTS, DSR
or DCD input.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Thetis.Cat
{
    public sealed class CatService : IDisposable
    {
        private readonly ICatHost _host;
        private readonly CatEngine _engine;
        private readonly List<Endpoint> _endpoints = new List<Endpoint>();
        private readonly List<Connection> _connections = new List<Connection>();
        private readonly object _lock = new object();
        private TciServer _tci;
        private Thread _poller;
        private volatile bool _stopPoller;
        private long _lastA = -1, _lastB = -1;

        public CatService(ICatHost host)
        {
            _host = host;
            _engine = new CatEngine(host);
        }

        public CatEngine Engine => _engine;

        /// <summary>Every command and answer: (port, "&lt;" received / "&gt;" sent, text).  Only raised while TrafficEnabled.</summary>
        public event Action<string, string, string> Traffic;
        public volatile bool TrafficEnabled;

        internal void Log(string port, string dir, string text)
        {
            if (TrafficEnabled && text.Length > 0) Traffic?.Invoke(port, dir, text);
        }

        /// <summary>Stop what runs and start what 'settings' asks for.</summary>
        public void Apply(CatSettings settings)
        {
            Stop();
            _engine.Options.RigId = settings.RigId;
            _engine.Options.DigUIsUsb = settings.DigUIsUsb;
            _engine.Options.AllowAutoInformation = settings.AllowAutoInformation;

            for (int i = 0; i < settings.Ports.Count; i++)
            {
                var p = settings.Ports[i];
                if (p == null || !p.Enabled) continue;
                string name = "CAT" + (i + 1);
                Endpoint e = p.Kind == CatPortKind.Virtual ? new VirtualEndpoint(this, name, p) : new SerialEndpoint(this, name, p);
                lock (_lock) _endpoints.Add(e);
                e.Start();
            }
            if (settings.TcpEnabled)
            {
                var e = new TcpEndpoint(this, "TCP", settings.TcpAllInterfaces ? IPAddress.Any : IPAddress.Loopback, settings.TcpPort);
                lock (_lock) _endpoints.Add(e);
                e.Start();
            }
            if (settings.TciEnabled)
            {
                _tci = new TciServer(_host, settings.TciAllInterfaces ? IPAddress.Any : IPAddress.Loopback, settings.TciPort);
                _tci.Traffic += (who, dir, text) => Log(who, dir, text);
                _tci.Start();
            }
            _stopPoller = false;
            _poller = new Thread(Poll) { IsBackground = true, Name = "CAT poll" };
            _poller.Start();
        }

        /// <summary>One line per port or server: what it is doing.</summary>
        public IReadOnlyList<(string name, string status, bool ok)> Status()
        {
            var list = new List<(string, string, bool)>();
            lock (_lock) foreach (var e in _endpoints) list.Add((e.Name, e.Status, e.Ok));
            if (_tci != null) list.Add(("TCI", _tci.Status, _tci.Ok));
            return list;
        }

        public void Stop()
        {
            _stopPoller = true;
            List<Endpoint> eps;
            lock (_lock) { eps = _endpoints.ToList(); _endpoints.Clear(); }
            foreach (var e in eps) e.Stop();
            _tci?.Dispose();
            _tci = null;
            _lastA = _lastB = -1;
        }

        public void Dispose() => Stop();

        internal void Register(Connection c) { lock (_lock) _connections.Add(c); }
        internal void Unregister(Connection c) { lock (_lock) _connections.Remove(c); }

        // auto information (AI1 / ZZAI1): VFO changes are sent without being asked, as the console does
        private void Poll()
        {
            while (!_stopPoller)
            {
                Thread.Sleep(100);
                List<Connection> ai;
                lock (_lock) ai = _connections.Where(c => c.Session.AutoInformation).ToList();
                if (ai.Count == 0) { _lastA = _lastB = -1; continue; }
                long a, b;
                try { (a, b) = _host.Invoke(() => (_host.FrequencyHz, _host.VfoBHz)); }
                catch (Exception) { continue; }
                var sb = new StringBuilder();
                if (a != _lastA && _lastA >= 0) sb.Append("FA" + a.ToString().PadLeft(11, '0') + ";");
                if (b != _lastB && _lastB >= 0) sb.Append("FB" + b.ToString().PadLeft(11, '0') + ";");
                _lastA = a; _lastB = b;
                if (sb.Length == 0) continue;
                foreach (var c in ai) c.Send(sb.ToString());
            }
        }

        #region endpoints

        internal abstract class Endpoint
        {
            protected readonly CatService Service;
            protected volatile bool Stopping;
            private Thread _thread;

            protected Endpoint(CatService service, string name) { Service = service; Name = name; }

            public string Name { get; }
            public volatile string Status = "starting";
            public volatile bool Ok;

            public void Start()
            {
                _thread = new Thread(Run) { IsBackground = true, Name = Name };
                _thread.Start();
            }

            public virtual void Stop() => Stopping = true;

            protected abstract void Run();
        }

        /// <summary>A command stream: one session, answers written back.</summary>
        internal sealed class Connection
        {
            private readonly CatService _service;
            private readonly string _name;
            private readonly Action<byte[]> _write;
            private string _pending = "";

            public Connection(CatService service, string name, Action<byte[]> write)
            {
                _service = service;
                _name = name;
                _write = write;
            }

            public CatSession Session { get; } = new CatSession();

            public void Received(byte[] buf, int n)
            {
                string text = Encoding.ASCII.GetString(buf, 0, n);
                _service.Log(_name, "<", text);
                string answers = _service._engine.ExecuteAll(_pending + text, Session, out _pending);
                Send(answers);
            }

            public void Send(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                _service.Log(_name, ">", text);
                try { _write(Encoding.ASCII.GetBytes(text)); }
                catch (Exception) { }
            }
        }

        private sealed class SerialEndpoint : Endpoint
        {
            private readonly CatPortSettings _cfg;
            private SerialPort _port;
            private bool _pttKeyed;

            public SerialEndpoint(CatService s, string name, CatPortSettings cfg) : base(s, name) => _cfg = cfg;

            public override void Stop()
            {
                base.Stop();
                try { _port?.Close(); } catch (Exception) { }
            }

            protected override void Run()
            {
                string settings = $"{_cfg.Baud} {_cfg.DataBits}{_cfg.Parity.ToString()[0]}{_cfg.StopBits}";
                while (!Stopping)
                {
                    if (string.IsNullOrEmpty(_cfg.Device)) { Status = "no port chosen"; Ok = false; return; }
                    string dev = SerialPorts.Resolve(_cfg.Device);
                    if (dev == null)
                    {
                        Status = $"{_cfg.Device} is not there (unplugged?): waiting for it";
                        Ok = false;
                        Sleep(2000);
                        continue;
                    }
                    Connection conn = null;
                    try
                    {
                        _port = new SerialPort(dev, _cfg.Baud,
                                               _cfg.Parity switch { CatParity.Odd => Parity.Odd, CatParity.Even => Parity.Even, _ => Parity.None },
                                               _cfg.DataBits, _cfg.StopBits == 2 ? StopBits.Two : StopBits.One)
                        {
                            Handshake = _cfg.Handshake switch { CatHandshake.RtsCts => Handshake.RequestToSend, CatHandshake.XonXoff => Handshake.XOnXOff, _ => Handshake.None },
                            ReadTimeout = 50,
                            WriteTimeout = 1000,
                        };
                        _port.Open();
                        // ports without modem control lines (some adapters, a pseudo-terminal) refuse these
                        try { if (_cfg.Handshake != CatHandshake.RtsCts) _port.RtsEnable = _cfg.Rts; } catch (IOException) { }
                        try { _port.DtrEnable = _cfg.Dtr; } catch (IOException) { }
                        _port.DiscardInBuffer();
                        var port = _port;
                        conn = new Connection(Service, Name, b => port.Write(b, 0, b.Length));
                        Service.Register(conn);
                        Status = $"open: {(dev.StartsWith("/dev/") ? dev.Substring(5) : dev)} {settings}" + (_cfg.Ptt != PttLine.None ? $", PTT on {_cfg.Ptt.ToString().ToUpperInvariant()}" : "");
                        Ok = true;
                        var buf = new byte[512];
                        while (!Stopping)
                        {
                            int n = 0;
                            try { n = port.Read(buf, 0, buf.Length); }
                            catch (TimeoutException) { }
                            if (n > 0) conn.Received(buf, n);
                            CheckPtt(port);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Status = $"{dev}: permission denied. " + SerialPorts.DialoutHint;
                        Ok = false;
                        Sleep(5000);
                    }
                    catch (Exception ex) when (!Stopping)
                    {
                        Status = $"{dev}: {ex.Message}";
                        Ok = false;
                        Sleep(2000);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        if (conn != null) Service.Unregister(conn);
                        ReleasePtt();
                        try { _port?.Close(); } catch (Exception) { }
                        _port = null;
                    }
                }
                Status = "closed";
                Ok = false;
            }

            private void CheckPtt(SerialPort port)
            {
                if (_cfg.Ptt == PttLine.None) return;
                bool on;
                try
                {
                    on = _cfg.Ptt switch { PttLine.Cts => port.CtsHolding, PttLine.Dsr => port.DsrHolding, _ => port.CDHolding };
                }
                catch (Exception) { return; }
                if (_cfg.PttInvert) on = !on;
                if (on == _pttKeyed) return;
                _pttKeyed = on;
                string why = null;
                bool ok = Service._host.Invoke(() => Service._host.SetMox(on, out why));
                Service.Log(Name, "<", $"[PTT {_cfg.Ptt.ToString().ToUpperInvariant()} {(on ? "on" : "off")}{(ok ? "" : ": " + why)}]");
            }

            private void ReleasePtt()
            {
                if (!_pttKeyed) return;
                _pttKeyed = false;
                try { Service._host.Invoke(() => Service._host.SetMox(false, out string _)); } catch (Exception) { }
            }

            private void Sleep(int ms)
            {
                for (int t = 0; t < ms && !Stopping; t += 100) Thread.Sleep(100);
            }
        }

        private sealed class VirtualEndpoint : Endpoint
        {
            private readonly CatPortSettings _cfg;

            public VirtualEndpoint(CatService s, string name, CatPortSettings cfg) : base(s, name) => _cfg = cfg;

            protected override void Run()
            {
                VirtualSerialPort vp;
                try
                {
                    vp = new VirtualSerialPort(_cfg.VirtualPath);
                }
                catch (Exception ex)
                {
                    Status = "cannot create the virtual port: " + ex.Message;
                    Ok = false;
                    return;
                }
                using (vp)
                {
                    var conn = new Connection(Service, Name, vp.Write);
                    Service.Register(conn);
                    Status = $"ready: programs open {vp.LinkPath} ({vp.Device})";
                    Ok = true;
                    var buf = new byte[512];
                    DateTime last = DateTime.MinValue;
                    while (!Stopping)
                    {
                        int n = vp.Read(buf, 100);
                        if (n < 0) { Status = "the virtual port closed"; Ok = false; break; }
                        if (n == 0) continue;
                        // a program that went away may have left answers unread: drop them before answering the next one
                        if ((DateTime.UtcNow - last).TotalSeconds > 2) vp.FlushStale();
                        last = DateTime.UtcNow;
                        conn.Received(buf, n);
                    }
                    Service.Unregister(conn);
                }
                if (Stopping) { Status = "closed"; Ok = false; }
            }
        }

        private sealed class TcpEndpoint : Endpoint
        {
            private readonly IPAddress _address;
            private readonly int _port;
            private TcpListener _listener;
            private int _clients;
            private readonly List<TcpClient> _open = new List<TcpClient>();

            public TcpEndpoint(CatService s, string name, IPAddress address, int port) : base(s, name)
            {
                _address = address;
                _port = port;
            }

            public override void Stop()
            {
                base.Stop();
                try { _listener?.Stop(); } catch (Exception) { }
                lock (_open) foreach (var c in _open) try { c.Close(); } catch (Exception) { }
            }

            private void SetStatus()
            {
                string where = _address.Equals(IPAddress.Any) ? $"all interfaces, port {_port}" : $"{_address}:{_port}";
                Status = $"listening on {where}, {_clients} program{(_clients == 1 ? "" : "s")} connected";
            }

            protected override void Run()
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
                while (!Stopping)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch (Exception) { break; }
                    client.NoDelay = true;
                    lock (_open) _open.Add(client);
                    var t = new Thread(() => Serve(client)) { IsBackground = true, Name = "CAT TCP client" };
                    t.Start();
                }
                Ok = false;
                Status = "closed";
            }

            private void Serve(TcpClient client)
            {
                string who = "TCP " + (client.Client.RemoteEndPoint as IPEndPoint)?.Port;
                Interlocked.Increment(ref _clients);
                SetStatus();
                var stream = client.GetStream();
                var conn = new Connection(Service, who, b => stream.Write(b, 0, b.Length));
                Service.Register(conn);
                try
                {
                    var buf = new byte[1024];
                    while (!Stopping)
                    {
                        int n = stream.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        conn.Received(buf, n);
                    }
                }
                catch (Exception) { }
                finally
                {
                    Service.Unregister(conn);
                    lock (_open) _open.Remove(client);
                    try { client.Close(); } catch (Exception) { }
                    Interlocked.Decrement(ref _clients);
                    if (!Stopping) SetStatus();
                }
            }
        }

        #endregion
    }
}
