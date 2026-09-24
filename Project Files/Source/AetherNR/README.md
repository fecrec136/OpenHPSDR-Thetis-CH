# AetherNR: AetherSDR's receive noise reduction for Thetis

`libaethernr` provides four receive noise-reduction filters from
[AetherSDR](https://github.com/aethersdr/AetherSDR), taken at commit
`2859fdef4c5121fe0d18f5d08f819ecbd9b33fc3`. WDSP runs the selected filter in
its receive chain through `wdsp/extnr.c`.

| Filter | What it is | Source |
|---|---|---|
| **NR2** | AetherSDR's spectral noise reduction, an extended port of WDSP's EMNR with gain limits, smoothing and psychoacoustic post-processing | `src/SpectralNR.*`, `src/Nr2GammaTables.inc` (AetherSDR `src/core`) |
| **RN2** | RNNoise, with AetherSDR's spectral dry-mix control | Thetis' bundled RNNoise (`lib/NR_Algorithms_x64/src/rnnoise`), which is the same xiph snapshot AetherSDR pins (`70f1d256`), plus AetherSDR's dry-mix patch (see `AETHERSDR-PATCHES.md` there) |
| **NR4** | libspecbleach spectral noise reduction | `third_party/libspecbleach`, AetherSDR's snapshot (newer than the one inside libwdsp) |
| **DFNR** | DeepFilterNet3 neural noise reduction | `third_party/deepfilter/include` (the C API). The static library and model are downloaded by CMake from AetherSDR's repository at the commit above and checked against SHA-256 hashes (see `Linux/CMakeLists.txt`). DeepFilterNet is MIT / Apache-2.0. |

`src/aethernr.cpp` puts the four filters behind a C interface
(`include/aethernr.h`) that takes mono float audio. It follows AetherSDR's
wrappers (`RNNoiseFilter`, `SpecbleachFilter`, `DeepFilterFilter`, and the
NR2 set-up in `AudioEngine`): the same filter geometry, scaling, defaults
and NR4 noise-learning period. AetherSDR works on 24 or 48 kHz stereo audio
from a FlexRadio. Here each filter sees WDSP's mono receive audio at 48 kHz,
so its stereo adapters and 24 kHz resamplers are not used.

## Changes from AetherSDR

* `SpectralNR.cpp` no longer uses Qt. `aethernr_support.cpp` replaces Qt's
  base64 decoding, `qUncompress` (via zlib), SHA-256 and logging. `Q_ASSERT`
  became `assert`. The NR2 algorithm is unchanged.
* NR4 learns its noise profile for one second, counted in samples.
  AetherSDR counts 25 of its roughly 40 ms blocks. WDSP's blocks are larger
  (4096 samples), so counting blocks would learn for two seconds.
* Frame-based filters (RN2, DFNR) return each call's samples one frame late,
  so every call returns as many samples as it was given.
* `AETHERNR_DEBUG=1` logs each filter's input and output level every two
  seconds.

## Licence

AetherSDR is GPL v3, and so is this library (`LICENSE`). Thetis is GPL v2
or later, so a build that includes `libaethernr` is distributed under GPL v3
as a whole. libspecbleach is LGPL 2.1 or later. DeepFilterNet is MIT or
Apache-2.0 (`third_party/deepfilter/LICENSE*`).

## Symbols

libwdsp contains its own, older libspecbleach and RNNoise with the same
function names. `libaethernr` exports only its `aethernr_*` functions and is
linked with `-Bsymbolic` and `--exclude-libs,ALL`, so neither library can
bind to the other's copies.
