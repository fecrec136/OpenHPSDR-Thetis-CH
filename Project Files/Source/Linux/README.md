# Thetis on Linux (Linux Mint / Ubuntu / Debian)

This directory holds the native Linux build of Thetis. It is being done in
stages:

| Stage | Component | Status |
|---|---|---|
| 1 | Native libraries: `wdsp`, `ChannelMaster`, `PA19` (PortAudio) | **Done**: builds and passes tests |
| 2 | C# console (WinForms + SharpDX/Direct2D UI) | Not started, see [Next steps](#next-steps-the-c-console) |

## Stage 1: native libraries

| Linux library | Windows equivalent | What it does |
|---|---|---|
| `libwdsp.so` (+ `libWDSP.so` alias) | `wdsp.dll` | DSP engine (NR0V), including NR3 (rnnoise) and NR4 (libspecbleach), both linked in statically |
| `libChannelMaster.so` | `ChannelMaster.dll` | Protocol 1/2 networking, audio routing, VAC |
| `libPA19.so` | `PA19.dll` | Thetis' PortAudio fork with the `PA_*` wrappers, using ALSA, PulseAudio/PipeWire and JACK host APIs |

### Build

Tested on Ubuntu 24.04, the base of Linux Mint 22.

```sh
cd "Project Files/Source/Linux"
./build.sh --deps        # first time: installs the build packages with apt
./build.sh               # later builds
./build.sh --native      # optional: -march=native for this CPU
```

The packages it needs are `build-essential cmake pkg-config libfftw3-dev
libasound2-dev libpulse-dev libjack-jackd2-dev`. The libraries land in
`build/`. `ChannelMaster` finds `wdsp` and `PA19` next to itself (`$ORIGIN`
rpath), so keep the three together.

### Tests

`build.sh` runs them automatically. You can also run `ctest` in `build/`.

* **compat**: unit tests for the Win32 compatibility layer (semaphores,
  events, thread handles, waitable timers, thread pool, Interlocked
  semantics).
* **smoketest**: loads the three libraries with every symbol resolved, then
  runs a wdsp receive channel on its DSP thread. A USB tone must pass and the
  same tone in the opposite sideband must be rejected by more than 60 dB
  (measured: about 139 dB). It also initialises PortAudio and checks the
  fork's `paFloat64` extension.
* `tools/check_exports.py <Console dir> build`: checks that every entry point
  the C# code P/Invokes into these libraries is exported (611 of 611, apart
  from `GetTXACFCOMPGainAndMask`, which is missing upstream on Windows too).

### How the port works

The upstream C sources are compiled **unmodified** apart from the two small
changes listed below. This keeps merges from official Thetis easy.

* `compat/include/win32compat.h` and `compat/win32compat.c` implement the
  Win32/Winsock subset the code uses on top of POSIX: critical sections
  (recursive mutexes), semaphores, events, `_beginthread[ex]` handles,
  waitable timers, `QueueUserWorkItem`, MMCSS priority (`SCHED_FIFO`),
  `WSAEventSelect`/`WSAWaitForMultipleEvents` (`poll()`), Interlocked
  operations, `__declspec`, and similar. `compat/include/` also provides
  stand-ins for `<Windows.h>`, `<avrt.h>`, `<ws2tcpip.h>` and the others.
  The header is force-included into every file, as MSVC keywords are always
  available there.
* **LP64 vs LLP64:** C `long` is 64-bit on Linux but 32-bit on Windows. The
  Interlocked macros convert their operands exactly as Windows does (for
  example, `InterlockedAnd(&x, 0xffffffff)` is an atomic read, not a mask
  that clears the upper half). The exported API was checked: no function the
  C# code calls takes a `long`.
* ASIO is Windows-only. `compat/asio_stub.c` reports "no ASIO driver", so
  ChannelMaster uses its other audio paths, just as it does on a Windows
  machine without ASIO.
* The bundled rnnoise tree is missing its `x86/` headers (they fall under a
  `.gitignore` rule). Empty stand-ins are in `compat/rnnoise/x86/`, because
  those headers only matter to MSVC and runtime CPU dispatch.

Changes to shared sources:

1. `wdsp/eq.h`: the `eq_mults()` prototype gained the missing `double* Q`
   parameter so it matches its definition. MSVC only warns about the mismatch
   (C4029); GCC rejects it. The fix is correct on Windows too.
2. `lib/portaudio-19.7.0/src/hostapi/pulseaudio/pa_linux_pulseaudio.c`: a
   missing PulseAudio/PipeWire server no longer makes `Pa_Initialize()` fail
   for every host API. It is skipped instead, as the JACK backend already
   does. This file is not compiled on Windows.

### Real-time priority

The audio and network threads ask for real-time scheduling, as they use
MMCSS "Pro Audio" on Windows. On Linux this needs an rtprio limit.
Otherwise the threads silently run at normal priority. To allow it:

```sh
sudo usermod -aG audio "$USER"
echo '@audio - rtprio 95
@audio - memlock unlimited' | sudo tee /etc/security/limits.d/audio.conf
# log out and back in
```

## Next steps: the C# console

The console is a .NET Framework 4.8 WinForms application of about 430,000
lines. Its spectrum, waterfall and meters are drawn with SharpDX
(Direct2D/DXGI), which does not exist on Linux, and it P/Invokes about 60
`user32`/`kernel32` functions. Porting it is a separate, larger piece of
work. The native libraries above are needed whichever route is taken.

Points already known for that stage:

* PortAudio's `unsigned long` fields (`PaSampleFormat`, `framesPerBuffer`,
  callback status flags) are 64-bit on Linux. The C# PA19 struct and delegate
  declarations must use `nuint`/`ulong` there.
* Mono resolves `DllImport("wdsp.dll")` to `libwdsp.so` on its own.
  .NET 8+ needs a `NativeLibrary.SetDllImportResolver` mapping.
* The wisdom file directory passed to `WDSPwisdom()` must use `/`.
