#!/usr/bin/env python3
"""Check that every native entry point the C# code P/Invokes into wdsp,
ChannelMaster and PA19 is exported by the Linux shared libraries.

usage: check_exports.py <Console source dir> <build dir>
"""
import re, subprocess, sys, pathlib

src, build = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
libs = {"wdsp": "libwdsp.so", "channelmaster": "libChannelMaster.so", "pa19": "libPA19.so"}

ws = r'(?:\s|//[^\n]*(?=\n))*'          # whitespace and // comments
pat = re.compile(r'\[\s*DllImport\s*\(\s*"([^"]+)"([^\]]*)\]' + ws + r'(?:\[(?!\s*DllImport)[^\]]*\]' + ws + r')*'
                 r'(?:(?:public|private|internal|protected|static|extern|unsafe|new)\s+)+'
                 r'[\w<>\[\],.*\s]+?\s+(\w+)\s*\(', re.S)
wanted = {k: {} for k in libs}
for f in src.rglob("*.cs"):
    text = f.read_text(encoding="utf-8", errors="replace")
    for m in pat.finditer(text):
        dll = m.group(1).lower().removesuffix(".dll")
        if dll not in libs:
            continue
        ep = re.search(r'EntryPoint\s*=\s*"([^"]+)"', m.group(2))
        name = ep.group(1) if ep else m.group(3)
        wanted[dll].setdefault(name, f.name)

# referenced by the C# code but not implemented in the upstream C sources on
# any platform (calls would fail on Windows too)
KNOWN_UPSTREAM_MISSING = {"GetTXACFCOMPGainAndMask"}

missing = 0
for dll, so in libs.items():
    out = subprocess.run(["nm", "-D", "--defined-only", str(build / so)],
                         capture_output=True, text=True, check=True).stdout
    have = {l.split()[-1] for l in out.splitlines() if l.strip()}
    miss = sorted(n for n in wanted[dll] if n not in have and n not in KNOWN_UPSTREAM_MISSING)
    print(f"{so}: {len(wanted[dll])} entry points referenced, {len(miss)} missing")
    for n in miss:
        print(f"    {n}  (used in {wanted[dll][n]})")
    missing += len(miss)
sys.exit(1 if missing else 0)
