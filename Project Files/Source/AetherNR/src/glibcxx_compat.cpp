/*  glibcxx_compat.cpp

GCC 13's <string> calls std::string::_M_replace_cold, which only libstdc++
13 and later export (GLIBCXX_3.4.31).  A library built with GCC 13 therefore
does not load on a system with an older libstdc++ -- Linux Mint 21 /
Ubuntu 22.04 have GLIBCXX_3.4.30 -- and Thetis then runs without noise
reduction.

This file defines that function inside libaethernr, from libstdc++ 13
(bits/basic_string.tcc).  The library's symbols are hidden and bound
locally (-Bsymbolic), so the library uses this copy and never asks the
system's libstdc++ for it.  Linking libstdc++ statically is not a way out:
GCC 13's static libstdc++ needs glibc 2.38.

The function is a member of std::string, so it is defined here under its
mangled name, with 'this' as the first argument (the Itanium C++ ABI).

This program is free software: you can redistribute it and/or modify it
under the terms of the GNU General Public License as published by the Free
Software Foundation, either version 3 of the License, or (at your option)
any later version.  (The algorithm is libstdc++'s, GPL v3 with the GCC
Runtime Library Exception.)

*/

#include <cstddef>              // (first: defines __GLIBCXX__ and _GLIBCXX_RELEASE)
#include <cstring>

#if defined(__GLIBCXX__) && defined(_GLIBCXX_RELEASE) && _GLIBCXX_RELEASE >= 13 && defined(__linux__)

extern "C" __attribute__((visibility("hidden"), used)) void
aethernr_string_replace_cold(void* self, char* p, std::size_t len1, const char* s,
                             std::size_t len2, std::size_t how_much)
    __asm__("_ZNSt7__cxx1112basic_stringIcSt11char_traitsIcESaIcEE15_M_replace_coldEPcmPKcmm");

// std::string::_M_replace_cold(char* p, size_t len1, const char* s, size_t len2, size_t how_much):
// replace len1 characters at p with the len2 characters at s, in place, where s
// may point into the string itself; how_much characters follow the replaced part.
extern "C" void aethernr_string_replace_cold(void*, char* p, std::size_t len1, const char* s,
                                             std::size_t len2, std::size_t how_much)
{
    if (len2 && len2 <= len1)
        std::memmove(p, s, len2);
    if (how_much && len1 != len2)
        std::memmove(p + len2, p + len1, how_much);
    if (len2 > len1)
    {
        if (s + len2 <= p + len1)
            std::memmove(p, s, len2);
        else if (s >= p + len1)
        {
            // the source moved along with the tail
            const std::size_t poff = (s - p) + (len2 - len1);
            std::memcpy(p, p + poff, len2);
        }
        else
        {
            const std::size_t nleft = (p + len1) - s;
            std::memmove(p, s, nleft);
            std::memcpy(p + nleft, p + len2, len2 - nleft);
        }
    }
}

#endif
