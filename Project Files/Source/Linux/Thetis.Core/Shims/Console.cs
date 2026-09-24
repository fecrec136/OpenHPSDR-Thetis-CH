/*  Console.cs

This file is part of a program that implements a Software-Defined Radio.

The one member of the Windows console form that Penny.cs (compiled
unmodified from upstream) reads: whether receiver 2 is enabled.  The class
name matches upstream, which is why upstream files write
System.Console.WriteLine in full.  Internal, so it does not clash with
System.Console in code that uses the library.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

namespace Thetis
{
    internal sealed class Console
    {
        private static readonly Console _instance = new Console();
        public static Console getConsole() => _instance;

        /// <summary>RX2 is not ported yet.</summary>
        public bool RX2Enabled { get; set; }
    }
}
