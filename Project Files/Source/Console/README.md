# Upstream console files kept for the Linux build

The Windows console (the WinForms application) has been removed from this
repository. These files remain because the Linux build compiles them
unchanged:

| File | Used by |
|---|---|
| `Versions.cs` | `wdsp/version.h` and `ChannelMaster/version.h` (a C / C# polyglot) |
| `dsp.cs`, `ivac.cs`, `enums.cs`, `clsHardwareSpecific.cs`, `HPSDR/clsRadioDiscovery.cs`, `HPSDR/NetworkIOImports.cs`, `HPSDR/specHPSDR.cs`, `HPSDR/Penny.cs` | `Linux/Thetis.Core` (linked in `Thetis.Core.csproj`) |
| `CAT/CATStructs.xml` | `Linux/Thetis.Core` CAT command table (embedded) |

The rest of the console is in upstream Thetis: https://github.com/ramdor/Thetis
(`Linux/tools/sync_upstream.py` takes a checkout of it).
