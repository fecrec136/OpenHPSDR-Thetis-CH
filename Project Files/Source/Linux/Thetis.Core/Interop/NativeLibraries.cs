/*  NativeLibraries.cs

This file is part of a program that implements a Software-Defined Radio.

Maps the Windows DLL names used in the [DllImport] declarations shared with
the Windows build ("wdsp.dll", "WDSP.dll", "ChannelMaster.dll", "PA19.dll")
onto the native Linux libraries built by Linux/CMakeLists.txt.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Thetis
{
    public static class NativeLibraries
    {
        /// <summary>Environment variable that points at the directory holding the .so files.</summary>
        public const string DirectoryVariable = "THETIS_NATIVE_DIR";

        private static readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "wdsp", "libwdsp.so" },
            { "ChannelMaster", "libChannelMaster.so" },
            { "PA19", "libPA19.so" },
            { "aethernr", "libaethernr.so" },       // AetherSDR noise reduction (optional)
        };

        private static readonly Dictionary<string, IntPtr> _loaded = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();

        [ModuleInitializer]
        internal static void Register()
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
        }

        /// <summary>Register the resolver for another assembly that also P/Invokes these libraries.</summary>
        public static void RegisterFor(Assembly assembly)
        {
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }

        /// <summary>Directories searched for the native libraries, in order.</summary>
        public static IEnumerable<string> SearchDirectories()
        {
            string env = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (!string.IsNullOrEmpty(env)) yield return env;
            yield return AppContext.BaseDirectory;
            yield return Path.Combine(AppContext.BaseDirectory, "native");
        }

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
        {
            string key = Path.GetFileNameWithoutExtension(name);   // "wdsp.dll" -> "wdsp"
            if (!_map.TryGetValue(key, out string file))
                return IntPtr.Zero;                                  // e.g. cmASIO.dll: not on Linux

            lock (_lock)
            {
                if (_loaded.TryGetValue(file, out IntPtr h)) return h;
                foreach (string dir in SearchDirectories())
                {
                    string candidate = Path.Combine(dir, file);
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out h))
                    {
                        _loaded[file] = h;
                        return h;
                    }
                }
                // fall back to the system loader (LD_LIBRARY_PATH, /usr/lib/thetis via ld.so.conf, ...)
                if (NativeLibrary.TryLoad(file, out h))
                {
                    _loaded[file] = h;
                    return h;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>Load one of the mapped libraries; returns its handle and the file it was loaded from.</summary>
        public static bool TryLoad(string name, out IntPtr handle, out string path)
        {
            handle = Resolve(name, typeof(NativeLibraries).Assembly, null);
            path = null;
            if (handle == IntPtr.Zero) return false;
            string file = _map[Path.GetFileNameWithoutExtension(name)];
            foreach (string dir in SearchDirectories())
            {
                string candidate = Path.Combine(dir, file);
                if (File.Exists(candidate)) { path = candidate; break; }
            }
            return true;
        }

        /// <summary>
        /// Why a mapped library does not load: the system loader's message for the
        /// first copy found (e.g. a missing dependency or symbol version), or "not found".
        /// </summary>
        public static string LoadError(string name)
        {
            string file = _map[Path.GetFileNameWithoutExtension(name)];
            foreach (string dir in SearchDirectories())
            {
                string candidate = Path.Combine(dir, file);
                if (!File.Exists(candidate)) continue;
                try
                {
                    NativeLibrary.Free(NativeLibrary.Load(candidate));
                    return null;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            }
            return file + " not found";
        }

        /// <summary>True if all three native libraries can be loaded.</summary>
        public static bool TryLoadAll(out string error)
        {
            foreach (string lib in new[] { "wdsp.dll", "ChannelMaster.dll", "PA19.dll" })
            {
                if (Resolve(lib, typeof(NativeLibraries).Assembly, null) == IntPtr.Zero)
                {
                    error = $"Could not load {_map[Path.GetFileNameWithoutExtension(lib)]}. " +
                            $"Build it with Linux/build.sh and set {DirectoryVariable} or copy it next to the application.";
                    return false;
                }
            }
            error = null;
            return true;
        }
    }
}
