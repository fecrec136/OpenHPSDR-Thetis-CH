/*  asio_stub.c

Linux replacement for the functions ChannelMaster imports from cmASIO.dll.
ASIO is a Windows-only driver model; on Linux low-latency audio is provided
through PortAudio (ALSA / JACK / PipeWire), so every entry point here reports
"no ASIO driver" and ChannelMaster falls back to its other audio paths.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

#include "win32compat.h"

long getASIODriverString(void *szData)
{
    if (szData) *(char *)szData = 0;
    return -1;
}

long getASIOBlockNum(void *dwData)              { (void)dwData; return -1; }
long getASIOBaseInputChannel(void *dwData)      { (void)dwData; return -1; }
long getASIOBaseOutputChannel(void *dwData)     { (void)dwData; return -1; }
long getASIOInputMode(void *dwData)             { (void)dwData; return -1; }

int prepareASIO(int blocksize, int samplerate, char *asioDriverName,
                void (*CallbackASIO)(void *, void *, void *, void *),
                long input_base_channel, long output_base_channel)
{
    (void)blocksize; (void)samplerate; (void)asioDriverName; (void)CallbackASIO;
    (void)input_base_channel; (void)output_base_channel;
    return -1;
}

void unloadASIO(void)   { }
long asioStart(void)    { return -1; }
long asioStop(void)     { return -1; }
