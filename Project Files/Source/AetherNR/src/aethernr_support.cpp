/*  aethernr_support.cpp

This file is part of a program that implements a Software-Defined Radio.

See aethernr_support.h.  SHA-256 follows FIPS 180-4.

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

*/

#include "aethernr_support.h"

#include <zlib.h>

#include <cstdint>
#include <cstdio>

namespace aethernr {

std::vector<unsigned char> fromBase64(const std::string& text)
{
    static const auto value = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '+') return 62;
        if (c == '/') return 63;
        return -1;
    };
    std::vector<unsigned char> out;
    out.reserve(text.size() * 3 / 4);
    std::uint32_t acc = 0;
    int bits = 0;
    for (char c : text) {
        const int v = value(c);
        if (v < 0) continue;                  // '=', whitespace
        acc = (acc << 6) | static_cast<std::uint32_t>(v);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out.push_back(static_cast<unsigned char>((acc >> bits) & 0xff));
        }
    }
    return out;
}

std::vector<unsigned char> qUncompress(const std::vector<unsigned char>& data)
{
    if (data.size() < 4) return {};
    const uLongf expected = (static_cast<uLongf>(data[0]) << 24) | (static_cast<uLongf>(data[1]) << 16)
                          | (static_cast<uLongf>(data[2]) << 8) | static_cast<uLongf>(data[3]);
    std::vector<unsigned char> out(expected);
    uLongf size = expected;
    if (uncompress(out.data(), &size, data.data() + 4, static_cast<uLong>(data.size() - 4)) != Z_OK || size != expected)
        return {};
    return out;
}

namespace {

constexpr std::uint32_t K[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
};

inline std::uint32_t rotr(std::uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }

void block(std::uint32_t h[8], const unsigned char* p)
{
    std::uint32_t w[64];
    for (int i = 0; i < 16; i++)
        w[i] = (std::uint32_t(p[4 * i]) << 24) | (std::uint32_t(p[4 * i + 1]) << 16)
             | (std::uint32_t(p[4 * i + 2]) << 8) | std::uint32_t(p[4 * i + 3]);
    for (int i = 16; i < 64; i++) {
        const std::uint32_t s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
        const std::uint32_t s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    std::uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
    for (int i = 0; i < 64; i++) {
        const std::uint32_t S1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
        const std::uint32_t ch = (e & f) ^ (~e & g);
        const std::uint32_t t1 = hh + S1 + ch + K[i] + w[i];
        const std::uint32_t S0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
        const std::uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
        const std::uint32_t t2 = S0 + maj;
        hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
    }
    h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
}

} // namespace

std::string sha256Hex(const std::vector<unsigned char>& data)
{
    std::uint32_t h[8] = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
                           0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 };
    const std::size_t n = data.size();
    std::size_t i = 0;
    for (; i + 64 <= n; i += 64) block(h, data.data() + i);
    unsigned char tail[128] = {};
    const std::size_t rest = n - i;
    for (std::size_t k = 0; k < rest; k++) tail[k] = data[i + k];
    tail[rest] = 0x80;
    const std::size_t tailLen = rest + 9 <= 64 ? 64 : 128;
    const std::uint64_t bitLen = static_cast<std::uint64_t>(n) * 8;
    for (int k = 0; k < 8; k++) tail[tailLen - 1 - k] = static_cast<unsigned char>(bitLen >> (8 * k));
    block(h, tail);
    if (tailLen == 128) block(h, tail + 64);
    static const char* hex = "0123456789abcdef";
    std::string out;
    for (std::uint32_t v : h)
        for (int shift = 28; shift >= 0; shift -= 4) out += hex[(v >> shift) & 0xf];
    return out;
}

void logWarning(const std::string& message) { std::fprintf(stderr, "aethernr: %s\n", message.c_str()); }
void logInfo(const std::string& message) { std::fprintf(stderr, "aethernr: %s\n", message.c_str()); }

} // namespace aethernr
