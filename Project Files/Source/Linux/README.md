# Thetis on Linux (Linux Mint / Ubuntu / Debian)

This directory holds the native Linux version of Thetis. It is being built
in stages:

| Stage | Component | Status |
|---|---|---|
| 1 | Native libraries: `wdsp`, `ChannelMaster`, `PA19` (PortAudio) | **Done**: builds and passes tests |
| 2 | .NET 8 + Avalonia application, receive | **First milestone**: see [Stage 2](#stage-2-the-avalonia-application) |
| 3 | Transmit, PureSignal, full setup, CAT/TCI, meters, RX2, skins | Not started |

## Quick start (Linux Mint 22)

```sh
cd "Project Files/Source/Linux"
./build.sh --deps        # once: build tools, audio and FFTW libraries, .NET 8 SDK
./build.sh --app         # native libraries + application into dist/thetis
./install.sh             # menu entry "Thetis" and the 'thetis' command
```

The first start optimises the FFTs for your computer (FFTW "wisdom"). This
takes several minutes, and later starts are quick. Then pick your radio and
its model at the top, press **POWER**, and choose the sound device for
receive audio at the bottom.

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

The upstream C sources are compiled **unmodified** apart from the three small
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
3. `wdsp/iobuffs.c`, `dexchange()`: fixes a race that also exists on
   Windows. With `bfo` ("block until output available", which ChannelMaster
   uses for every channel), the producer may run one DSP block ahead.
   `dexchange()` released `Sem_OutReady` *before* copying its input block out
   of the two-slot `r1` ring, so a woken producer could overwrite that slot
   first. The DSP then processed the wrong block, which is heard as a click
   followed by about 40 ms of filter ringing. The copy now happens before the
   release. Under CPU load the smoke test failed in 2 of 61 runs before the
   fix and 0 of 90 after it.

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

## Stage 2: the Avalonia application

The Windows console is a .NET Framework 4.8 WinForms application of about
430,000 lines, drawing with SharpDX (Direct2D). Neither exists on Linux, so
the user interface is being rebuilt on .NET 8 and
[Avalonia](https://avaloniaui.net/). The radio logic underneath is reused
from the Windows sources as far as possible.

![Thetis on Linux, receiving from the radio simulator](docs/screenshot.png)

### Layout

| Directory | What it is |
|---|---|
| `Thetis.Core/` | UI-independent radio library (net8.0) |
| `Thetis.Core/Upstream/*.g.cs` | Blocks copied verbatim from the Windows sources by `tools/sync_upstream.py` (the ChannelMaster P/Invoke table, the router tables, the PureSignal imports) |
| `Thetis.Core/Shims/` | Small stand-ins for the parts of the Windows console those files reference (`Display`, `NetworkIO`, `cmaster` start-up, `Win32.memcpy`) |
| `Thetis.Core/Radio/RadioController.cs` | Power on/off, tuning, mode, filter, AGC, volume, NR, sample rate, S-meter, panadapter data, VAC. It follows the call order of the Windows console. |
| `Thetis.Desktop/` | The Avalonia application |
| `Tools/Thetis.RadioSim/` | Protocol 1 radio simulator (see below) |
| `Tools/Thetis.CoreCheck/` | Headless end-to-end test of `Thetis.Core` |

These Windows source files are compiled into `Thetis.Core` **unmodified**, so
upstream changes flow in automatically: `HPSDR/clsRadioDiscovery.cs`,
`HPSDR/NetworkIOImports.cs`, `HPSDR/specHPSDR.cs`, `dsp.cs`, `ivac.cs`,
`enums.cs` and `clsHardwareSpecific.cs`. The only change to them is in
`clsRadioDiscovery.cs`: two network-interface properties that .NET does not
support on Linux (`IsDhcpEnabled`, `Speed`) are read inside `try`, which
behaves the same on Windows.

`NativeLibraries.cs` maps the Windows names in `[DllImport]` (`wdsp.dll`,
`WDSP.dll`, `ChannelMaster.dll`, `PA19.dll`) to the `.so` files. It looks next
to the application, then in `$THETIS_NATIVE_DIR`, then on the normal library
path.

### What works in this milestone

* Discovery of Protocol 1 and Protocol 2 radios on every network interface
* Power on/off with the model selected (DDC assignment, router tables and
  audio mixer states per model, as `console.cs` does them)
* VFO: wheel over a digit, double-click to type, arrow keys and page up/down,
  wheel or click on the panadapter; tuning steps from 1 Hz to 100 kHz
* Band buttons that remember the last frequency and mode per band
* Modes LSB, USB, DSB, CWL, CWU, FM, AM, SAM, DIGL, DIGU, with the Windows
  filter presets (F1..F10)
* AGC (fixed, long, slow, medium, fast) and AGC gain, volume, NR2, auto-notch
* Sample rates of 48, 96, 192 and 384 kHz
* S-meter, and a panadapter and waterfall with zoom, adjustable dB scale and
  a resizable split
* Receive audio to a PC sound device through VAC (PulseAudio/PipeWire, ALSA
  or JACK), or to the radio's own audio output
* Settings saved in `~/.config/thetis-linux/settings.json`

### Testing without a radio

`Tools/Thetis.RadioSim` emulates a Hermes board over Protocol 1. It answers
discovery, streams I/Q with test carriers at fixed RF frequencies that follow
the host's tuning, and decodes the audio the host sends back to the radio
(its rate, level and dominant tone). It sends I/Q with the spectrum
orientation that the unmodified Thetis receive chain expects from OpenHPSDR
hardware (`--textbook-iq` gives the opposite). It also stops streaming when
the host goes quiet, as the Hermes firmware watchdog does.

`Tools/Thetis.CoreCheck` drives `Thetis.Core` against it. It checks
discovery, connecting, DDC tuning and retuning, the audio returned to the
radio (48 kHz, the tone at the expected pitch, and following a retune),
sideband rejection (about 59 dB), the S-meter, the position and height of the
spectrum peak, 48 to 192 kHz rate changes, and a clean power-off. All 22
checks pass:

```sh
dotnet run --project Tools/Thetis.RadioSim -- --status-file /tmp/sim.json &
THETIS_NATIVE_DIR=$PWD/build dotnet run --project Tools/Thetis.CoreCheck -- ~/.local/share/thetis-linux /tmp/sim.json
```

To use the simulator with the application, start the application with
`THETIS_DISCOVER_LOOPBACK=1` so that discovery also searches the loopback
interface.

**Not yet tested:** a real radio, and Protocol 2. The simulator speaks
Protocol 1 only, and this development environment has no radio or sound
card. The Protocol 2 path uses the same ported code (router tables, DDC and
mixer set-up per model), but it has not been exercised.

Found while porting (the Windows build behaves the same way):

* Receiver 2's ChannelMaster input rate has to match the radio rate even when
  RX2 is off. On P1 its audio is always in the output mixer, and the mixer
  waits for every active input, so a stale rate throttled the audio sent to
  the radio to a quarter of what it needs. The Windows console sets it from
  the setup form at start-up. `RadioController` sets it with RX1's.
* `NetworkIO.VFOfreq` truncates instead of rounding, so 7.1005 MHz became
  7,100,499 Hz. The Linux copy rounds.

### Not yet ported

Transmit (MOX, TUNE, microphone, CW keyer), PureSignal, diversity, RX2 and
the sub-receiver, the full setup form (calibration, Alex/BPF relay tables,
ADC assignment, antenna selection, attenuator and preamp), band stacking, the
MeterManager meters, CAT, TCI, MIDI, recording, and skins.

Notes for those stages:

* PortAudio's `unsigned long` fields (`PaSampleFormat`, `framesPerBuffer`,
  callback status flags) are 64-bit on Linux. Any C# declaration of PA19
  stream structs or callbacks must use `nuint`/`ulong`. `AudioDevices.cs`
  only reads `PaDeviceInfo`/`PaHostApiInfo`, which contain no `long` fields.
* The upstream C# declaration of `nativeInitMetis` omits the last C
  parameter (`p2hw_uses_differnt_ports`), so the native side reads an
  undefined value on Windows too. `Thetis.Core` declares the full signature.
