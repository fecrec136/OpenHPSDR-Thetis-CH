/* Linux shim for <pa_win_wasapi.h>
   WASAPI does not exist on Linux, so no PortAudio host API ever reports
   paWASAPI and the code paths that use these definitions are never taken.
   Only the declarations needed to compile ivac.c are provided. */
#ifndef PA_WIN_WASAPI_H
#define PA_WIN_WASAPI_H
#include "portaudio.h"
#include "win32compat.h"

typedef enum PaWasapiFlags
{
    paWinWasapiExclusive      = (1 << 0),
    paWinWasapiRedirectHostProcessor = (1 << 1),
    paWinWasapiUseChannelMask = (1 << 2),
    paWinWasapiPolling        = (1 << 3),
    paWinWasapiThreadPriority = (1 << 4)
} PaWasapiFlags;

typedef enum PaWasapiThreadPriority
{
    eThreadPriorityNone = 0,
    eThreadPriorityAudio,
    eThreadPriorityCapture,
    eThreadPriorityDistribution,
    eThreadPriorityGames,
    eThreadPriorityPlayback,
    eThreadPriorityProAudio,
    eThreadPriorityWindowManager
} PaWasapiThreadPriority;

typedef struct PaWasapiStreamInfo
{
    unsigned long size;
    PaHostApiTypeId hostApiType;
    unsigned long version;
    unsigned long flags;
    unsigned int channelMask;
    void *hostProcessorOutput;
    void *hostProcessorInput;
    PaWasapiThreadPriority threadPriority;
    int streamCategory;
    int streamOption;
} PaWasapiStreamInfo;
#endif
