/*  aethernr_support.h

This file is part of a program that implements a Software-Defined Radio.

Small replacements for the Qt facilities the AetherSDR noise-reduction code
used (QByteArray base64 / qUncompress, QCryptographicHash, Qt logging), so
the filters build without Qt for Thetis on Linux.

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

*/

#pragma once

#include <string>
#include <vector>

namespace aethernr {

/// Decode standard base64 (padding optional, whitespace ignored).
std::vector<unsigned char> fromBase64(const std::string& text);

/// Qt's qUncompress(): a 4-byte big-endian uncompressed length, then a zlib stream.
/// Returns an empty vector on any error.
std::vector<unsigned char> qUncompress(const std::vector<unsigned char>& data);

/// Lower-case hex SHA-256 of 'data'.
std::string sha256Hex(const std::vector<unsigned char>& data);

void logWarning(const std::string& message);
void logInfo(const std::string& message);

} // namespace aethernr
