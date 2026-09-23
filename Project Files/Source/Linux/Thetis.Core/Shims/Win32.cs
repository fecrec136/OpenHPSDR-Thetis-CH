/*  Win32.cs

This file is part of a program that implements a Software-Defined Radio.

Managed replacements for the members of the Console's Win32 class
(Console/win32.cs) that shared upstream code uses.  Upstream calls into
msvcrt.dll / kernel32.dll, which do not exist on Linux.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;

namespace Thetis
{
    public static unsafe class Win32
    {
        public static void memcpy(void* dest, void* src, int count)
        {
            Buffer.MemoryCopy(src, dest, count, count);
        }
    }
}
