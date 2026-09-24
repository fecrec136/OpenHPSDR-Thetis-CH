#!/bin/sh
# abi_check.sh <objdump> <library>...
#
# Fails if a library needs a newer glibc or libstdc++ than Linux Mint 21 /
# Ubuntu 22.04 provide (glibc 2.35, GLIBCXX_3.4.30, CXXABI_1.3.13): such a
# library does not load there, and Thetis runs without it.
objdump="$1"; shift
fail=0
for lib in "$@"; do
    bad="$("$objdump" -T "$lib" | grep -oE '\((GLIBC_2\.[0-9]+|GLIBCXX_3\.4\.[0-9]+|CXXABI_1\.3\.[0-9]+)\)' | tr -d '()' | sort -u |
        awk -F'[_.]' '
            /^GLIBC_/   && $3 > 35 { print }
            /^GLIBCXX_/ && $4 > 30 { print }
            /^CXXABI_/  && $4 > 13 { print }')"
    if [ -n "$bad" ]; then
        echo "$(basename "$lib") needs: $(echo $bad)" >&2
        "$objdump" -T "$lib" | grep -E "$(echo $bad | tr ' ' '|')" | sed 's/^/    /' >&2
        fail=1
    else
        echo "$(basename "$lib"): ok"
    fi
done
exit $fail
