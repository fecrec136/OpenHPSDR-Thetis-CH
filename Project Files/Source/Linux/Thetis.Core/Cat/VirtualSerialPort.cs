/*  VirtualSerialPort.cs

This file is part of a program that implements a Software-Defined Radio.

A serial port for programs on this computer (WSJT-X, fldigi, Hamlib's
rigctld, loggers) without a null-modem cable or com0com: a pseudo-terminal
whose other end is reached through a fixed name, e.g.
~/.local/share/thetis-linux/cat1 -> /dev/pts/3.  The program opens that
name as its radio's serial port, at any speed.

Thetis keeps the terminal end open itself, so a program can close and
reopen it; answers written while no program is reading are dropped rather
than blocking.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Thetis.Cat
{
    public sealed class VirtualSerialPort : IDisposable
    {
        private int _master = -1, _slave = -1;
        private readonly string _link;

        /// <summary>The terminal the name points to (/dev/pts/N).</summary>
        public string Device { get; }

        /// <summary>The name programs open.</summary>
        public string LinkPath => _link;

        public VirtualSerialPort(string linkPath)
        {
            _master = posix_openpt(O_RDWR | O_NOCTTY);
            if (_master < 0) throw new IOException("posix_openpt failed: " + Marshal.GetLastPInvokeErrorMessage());
            try
            {
                if (grantpt(_master) != 0 || unlockpt(_master) != 0)
                    throw new IOException("grantpt/unlockpt failed: " + Marshal.GetLastPInvokeErrorMessage());
                var buf = new byte[256];
                if (ptsname_r(_master, buf, (IntPtr)buf.Length) != 0) throw new IOException("ptsname failed");
                Device = Encoding.ASCII.GetString(buf, 0, Array.IndexOf(buf, (byte)0));

                // keep the far end open: reads then wait instead of failing while no program is attached
                _slave = open(Device, O_RDWR | O_NOCTTY);
                if (_slave < 0) throw new IOException("cannot open " + Device + ": " + Marshal.GetLastPInvokeErrorMessage());
                MakeRaw(_slave);
                MakeRaw(_master);
                int fl = fcntl(_master, F_GETFL, 0);
                fcntl(_master, F_SETFL, fl | O_NONBLOCK);

                _link = linkPath;
                if (!string.IsNullOrEmpty(_link))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_link));
                    var fi = new FileInfo(_link);
                    if (fi.Exists || fi.LinkTarget != null)
                    {
                        if (fi.LinkTarget == null) throw new IOException(_link + " exists and is not a link: choose another name");
                        fi.Delete();
                    }
                    File.CreateSymbolicLink(_link, Device);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Wait up to 'timeoutMs' for input; 0 = nothing yet, -1 = closed.</summary>
        public int Read(byte[] buffer, int timeoutMs)
        {
            int fd = _master;
            if (fd < 0) return -1;
            var p = new PollFd { fd = fd, events = POLLIN };
            int r = poll(ref p, 1, timeoutMs);
            if (r <= 0) return r < 0 && Marshal.GetLastPInvokeError() != EINTR ? -1 : 0;
            long n = read(fd, buffer, (IntPtr)buffer.Length);
            if (n < 0)
            {
                int e = Marshal.GetLastPInvokeError();
                return e == EAGAIN || e == EINTR || e == EIO ? 0 : -1;
            }
            return (int)n;
        }

        /// <summary>Write what the terminal takes now; the rest is dropped (no program reading).</summary>
        public void Write(byte[] data)
        {
            int fd = _master;
            if (fd < 0 || data.Length == 0) return;
            write(fd, data, (IntPtr)data.Length);
        }

        /// <summary>Drop input a program left unread (called when a new command arrives after a pause).</summary>
        public void FlushStale()
        {
            if (_slave >= 0) tcflush(_slave, TCIFLUSH);
        }

        public void Dispose()
        {
            if (_link != null)
            {
                try
                {
                    var fi = new FileInfo(_link);
                    if (fi.LinkTarget == Device) fi.Delete();
                }
                catch (Exception) { }
            }
            if (_slave >= 0) { close(_slave); _slave = -1; }
            if (_master >= 0) { close(_master); _master = -1; }
        }

        private static void MakeRaw(int fd)
        {
            var t = new byte[256];          // struct termios is 60 bytes on Linux; room to spare
            if (tcgetattr(fd, t) != 0) return;
            cfmakeraw(t);
            tcsetattr(fd, 0 /* TCSANOW */, t);
        }

        #region libc

        private const int O_RDWR = 2, O_NOCTTY = 0x100, O_NONBLOCK = 0x800, F_GETFL = 3, F_SETFL = 4;
        private const short POLLIN = 1;
        private const int EINTR = 4, EIO = 5, EAGAIN = 11, TCIFLUSH = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd { public int fd; public short events; public short revents; }

        [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
        [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int ptsname_r(int fd, byte[] buf, IntPtr len);
        [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] private static extern long read(int fd, byte[] buf, IntPtr count);
        [DllImport("libc", SetLastError = true)] private static extern long write(int fd, byte[] buf, IntPtr count);
        [DllImport("libc", SetLastError = true)] private static extern int poll(ref PollFd fds, uint nfds, int timeout);
        [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int cmd, int arg);
        [DllImport("libc", SetLastError = true)] private static extern int tcgetattr(int fd, byte[] termios);
        [DllImport("libc", SetLastError = true)] private static extern int tcsetattr(int fd, int action, byte[] termios);
        [DllImport("libc")] private static extern void cfmakeraw(byte[] termios);
        [DllImport("libc", SetLastError = true)] private static extern int tcflush(int fd, int queue);

        #endregion
    }
}
