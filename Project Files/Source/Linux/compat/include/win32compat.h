/*  win32compat.h

This file is part of a program that implements a Software-Defined Radio.

Linux compatibility layer for building wdsp and ChannelMaster natively on
Linux (Linux Mint / Ubuntu / Debian).  It maps the small subset of the Win32
and Winsock API used by those libraries onto POSIX threads, semaphores and
BSD sockets so that the upstream sources can be compiled unmodified.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

*/

#ifndef _win32compat_h
#define _win32compat_h

#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif

#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include <errno.h>
#include <time.h>
#include <pthread.h>
/* note: <unistd.h> is deliberately NOT included here -- ChannelMaster has
   types named 'sync' and 'pipe' that collide with its declarations */
#include <sys/types.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <sys/select.h>

/* glibc 2.38+ maps the scanf family to new __isoc23_* symbols whenever
   _GNU_SOURCE is defined, which would make the libraries require glibc 2.38
   (Ubuntu 24.04 / Linux Mint 22) for no benefit: the C23 variants only add
   "%b" binary input, which Thetis does not use.  Binding to the C99 entry
   points keeps the libraries loadable on glibc 2.34+ (Linux Mint 21). */
#if defined(__GLIBC__) && (__GLIBC__ > 2 || (__GLIBC__ == 2 && __GLIBC_MINOR__ >= 38))
extern int __isoc99_fscanf(FILE *, const char *, ...);
extern int __isoc99_sscanf(const char *, const char *, ...);
#define fscanf __isoc99_fscanf
#define sscanf __isoc99_sscanf
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ------------------------------------------------------------------------ */
/* calling conventions / linkage                                            */
/* ------------------------------------------------------------------------ */

/* __declspec(x) dispatches on its first token: __declspec(align (16)) becomes
   __declspec_align (16) and so on. */
#define __declspec(x)               __declspec_##x
#define __declspec_dllexport        __attribute__((visibility("default")))
#define __declspec_dllimport        __attribute__((visibility("default")))
#define __declspec_align(n)         __attribute__((aligned(n)))
#define __declspec_noinline         __attribute__((noinline))
#define __declspec_noreturn         __attribute__((noreturn))
#define __declspec_thread           __thread
#define __declspec_selectany        __attribute__((weak))
#define __stdcall
#define __cdecl
#define WINAPI
#define CALLBACK
#define APIENTRY
#define __forceinline       static inline __attribute__((always_inline))

/* ------------------------------------------------------------------------ */
/* basic types                                                              */
/* ------------------------------------------------------------------------ */

typedef int                 BOOL;
typedef unsigned char       BOOLEAN;
typedef unsigned char       BYTE;
typedef unsigned char       byte;
typedef unsigned short      WORD;
typedef uint32_t            DWORD;
typedef int32_t             LONG;
typedef uint32_t            ULONG;
typedef uint32_t            UINT;
typedef int64_t             LONG64;
typedef int64_t             LONGLONG;
typedef uint64_t            ULONGLONG;
typedef int64_t             __int64;
typedef int64_t             INT64;
typedef uint64_t            UINT64;
typedef void               *PVOID;
typedef void               *LPVOID;
typedef const char         *LPCSTR;
typedef char               *LPSTR;
typedef void               *HANDLE;
typedef HANDLE              HINSTANCE;
typedef HANDLE              HMODULE;
typedef DWORD              *LPDWORD;

typedef union _LARGE_INTEGER
{
    struct { DWORD LowPart; LONG HighPart; };
    struct { DWORD LowPart; LONG HighPart; } u;
    LONGLONG QuadPart;
} LARGE_INTEGER, *PLARGE_INTEGER;

typedef struct _SYSTEMTIME
{
    WORD wYear;
    WORD wMonth;
    WORD wDayOfWeek;
    WORD wDay;
    WORD wHour;
    WORD wMinute;
    WORD wSecond;
    WORD wMilliseconds;
} SYSTEMTIME, *LPSYSTEMTIME;

#ifndef TRUE
#define TRUE                1
#endif
#ifndef FALSE
#define FALSE               0
#endif

#define INFINITE            0xFFFFFFFFu
#define WAIT_OBJECT_0       0x00000000u
#define WAIT_TIMEOUT        0x00000102u
#define WAIT_FAILED         0xFFFFFFFFu

#define TEXT(x)             x

#define DLL_PROCESS_DETACH  0
#define DLL_PROCESS_ATTACH  1
#define DLL_THREAD_ATTACH   2
#define DLL_THREAD_DETACH   3
#define MAX_PATH            4096

#ifndef __cplusplus
#ifndef max
#define max(a,b)            (((a) > (b)) ? (a) : (b))
#endif
#ifndef min
#define min(a,b)            (((a) < (b)) ? (a) : (b))
#endif
#endif

/* ------------------------------------------------------------------------ */
/* critical sections  (Windows critical sections are recursive)             */
/* ------------------------------------------------------------------------ */

typedef pthread_mutex_t     CRITICAL_SECTION, *LPCRITICAL_SECTION;

void InitializeCriticalSection(CRITICAL_SECTION *cs);
BOOL InitializeCriticalSectionAndSpinCount(CRITICAL_SECTION *cs, DWORD spin);
#define EnterCriticalSection(cs)        pthread_mutex_lock(cs)
#define LeaveCriticalSection(cs)        pthread_mutex_unlock(cs)
#define TryEnterCriticalSection(cs)     (pthread_mutex_trylock(cs) == 0)
#define DeleteCriticalSection(cs)       pthread_mutex_destroy(cs)

/* ------------------------------------------------------------------------ */
/* kernel objects: semaphores, events, threads, waitable timers             */
/* ------------------------------------------------------------------------ */

HANDLE CreateSemaphore(void *sa, LONG initial, LONG maximum, const char *name);
BOOL   ReleaseSemaphore(HANDLE h, LONG count, LONG *previous);
HANDLE CreateEvent(void *sa, BOOL manual_reset, BOOL initial_state, const char *name);
BOOL   SetEvent(HANDLE h);
BOOL   ResetEvent(HANDLE h);
HANDLE CreateWaitableTimer(void *sa, BOOL manual_reset, const char *name);
BOOL   SetWaitableTimer(HANDLE h, const LARGE_INTEGER *due, LONG period_ms,
                        void *completion, void *arg, BOOL resume);
BOOL   CancelWaitableTimer(HANDLE h);
DWORD  WaitForSingleObject(HANDLE h, DWORD ms);
/* wait_all takes the objects one after another rather than atomically: this
   is equivalent for the callers (one consumer thread per handle set, INFINITE
   timeout), but a timed-out wait_all may have consumed some of the objects. */
DWORD  WaitForMultipleObjects(DWORD n, const HANDLE *h, BOOL wait_all, DWORD ms);
BOOL   CloseHandle(HANDLE h);

#define CreateSemaphoreA    CreateSemaphore
#define CreateEventA        CreateEvent

/* _beginthread: detached thread, returns non-zero on success */
uintptr_t _beginthread(void (*start)(void *), unsigned stack_size, void *arg);
/* _beginthreadex: returns a waitable thread HANDLE (cast to uintptr_t) */
uintptr_t _beginthreadex(void *security, unsigned stack_size,
                         unsigned (*start)(void *), void *arg,
                         unsigned initflag, unsigned *thrdaddr);
void _endthread(void);
void _endthreadex(unsigned retval);

HANDLE GetCurrentThread(void);
DWORD  GetCurrentThreadId(void);

#define THREAD_PRIORITY_IDLE            -15
#define THREAD_PRIORITY_LOWEST          -2
#define THREAD_PRIORITY_BELOW_NORMAL    -1
#define THREAD_PRIORITY_NORMAL          0
#define THREAD_PRIORITY_ABOVE_NORMAL    1
#define THREAD_PRIORITY_HIGHEST         2
#define THREAD_PRIORITY_TIME_CRITICAL   15

BOOL SetThreadPriority(HANDLE thread, int priority);

/* MMCSS (avrt.h) -- mapped onto SCHED_FIFO when the user has rtprio rights */
typedef enum _AVRT_PRIORITY
{
    AVRT_PRIORITY_VERYLOW = -2,
    AVRT_PRIORITY_LOW,
    AVRT_PRIORITY_NORMAL,
    AVRT_PRIORITY_HIGH,
    AVRT_PRIORITY_CRITICAL
} AVRT_PRIORITY;

HANDLE AvSetMmThreadCharacteristics(const char *task, DWORD *task_index);
BOOL   AvSetMmThreadPriority(HANDLE h, int priority);
BOOL   AvRevertMmThreadCharacteristics(HANDLE h);
#define AvSetMmThreadCharacteristicsA   AvSetMmThreadCharacteristics

/* ------------------------------------------------------------------------ */
/* time                                                                     */
/* ------------------------------------------------------------------------ */

void      Sleep(DWORD ms);
BOOL      QueryPerformanceCounter(LARGE_INTEGER *count);
BOOL      QueryPerformanceFrequency(LARGE_INTEGER *freq);
ULONGLONG GetTickCount64(void);
DWORD     GetTickCount(void);
DWORD     timeGetTime(void);
void      GetLocalTime(SYSTEMTIME *st);
void      GetSystemTime(SYSTEMTIME *st);
#define   timeBeginPeriod(x)    (0)
#define   timeEndPeriod(x)      (0)

/* ------------------------------------------------------------------------ */
/* interlocked operations (generic over the operand width)                  */
/* ------------------------------------------------------------------------ */

/* The Win32 functions take LONG (32-bit) or LONG64 operands.  The variables
   they are applied to are often declared 'long', which is 64-bit on Linux
   (LP64) but 32-bit on Windows (LLP64), so these macros operate on the
   variable's real width but convert the operand exactly as Windows would:
   e.g. InterlockedAnd(&x, 0xffffffff) -- used as an atomic read -- must use a
   mask of (LONG)0xffffffff == -1, not 0x00000000ffffffff. */
#define _IL_V32(p, v)   ((__typeof__(*(p)))(LONG)(v))
#define _IL_V64(p, v)   ((__typeof__(*(p)))(LONG64)(v))

#define InterlockedBitTestAndSet(p, b) \
    ((BOOLEAN)((__atomic_fetch_or((p), (__typeof__(*(p)))1 << (b), __ATOMIC_SEQ_CST) >> (b)) & 1))
#define InterlockedBitTestAndReset(p, b) \
    ((BOOLEAN)((__atomic_fetch_and((p), ~((__typeof__(*(p)))1 << (b)), __ATOMIC_SEQ_CST) >> (b)) & 1))

#define InterlockedAnd(p, v)            __atomic_fetch_and((p), _IL_V32(p, v), __ATOMIC_SEQ_CST)
#define InterlockedOr(p, v)             __atomic_fetch_or((p), _IL_V32(p, v), __ATOMIC_SEQ_CST)
#define InterlockedXor(p, v)            __atomic_fetch_xor((p), _IL_V32(p, v), __ATOMIC_SEQ_CST)
#define InterlockedAnd64(p, v)          __atomic_fetch_and((p), _IL_V64(p, v), __ATOMIC_SEQ_CST)
#define InterlockedOr64(p, v)           __atomic_fetch_or((p), _IL_V64(p, v), __ATOMIC_SEQ_CST)
#define _InterlockedAnd                 InterlockedAnd
#define _InterlockedOr                  InterlockedOr
#define _InterlockedXor                 InterlockedXor

#define InterlockedExchange(p, v)       __atomic_exchange_n((p), _IL_V32(p, v), __ATOMIC_SEQ_CST)
#define InterlockedExchange64(p, v)     __atomic_exchange_n((p), _IL_V64(p, v), __ATOMIC_SEQ_CST)
#define _InterlockedExchange            InterlockedExchange
#define InterlockedExchangePointer(p, v) __atomic_exchange_n((p), (v), __ATOMIC_SEQ_CST)

#define InterlockedIncrement(p)         __atomic_add_fetch((p), 1, __ATOMIC_SEQ_CST)
#define InterlockedDecrement(p)         __atomic_sub_fetch((p), 1, __ATOMIC_SEQ_CST)
#define InterlockedIncrement64          InterlockedIncrement
#define InterlockedDecrement64          InterlockedDecrement
#define _InterlockedIncrement           InterlockedIncrement
#define _InterlockedDecrement           InterlockedDecrement

#define InterlockedAdd(p, v)            __atomic_add_fetch((p), _IL_V32(p, v), __ATOMIC_SEQ_CST)
#define InterlockedAdd64(p, v)          __atomic_add_fetch((p), _IL_V64(p, v), __ATOMIC_SEQ_CST)
#define InterlockedExchangeAdd(p, v)    __atomic_fetch_add((p), _IL_V32(p, v), __ATOMIC_SEQ_CST)
#define InterlockedExchangeAdd64(p, v)  __atomic_fetch_add((p), _IL_V64(p, v), __ATOMIC_SEQ_CST)

#define InterlockedCompareExchange(p, x, c) \
    __sync_val_compare_and_swap((p), _IL_V32(p, c), _IL_V32(p, x))
#define InterlockedCompareExchange64(p, x, c) \
    __sync_val_compare_and_swap((p), _IL_V64(p, c), _IL_V64(p, x))
#define InterlockedCompareExchangePointer(p, x, c) \
    __sync_val_compare_and_swap((p), (c), (x))
#define MemoryBarrier()                 __atomic_thread_fence(__ATOMIC_SEQ_CST)
#define _ReadWriteBarrier()             __atomic_signal_fence(__ATOMIC_SEQ_CST)

/* ------------------------------------------------------------------------ */
/* memory / CRT                                                             */
/* ------------------------------------------------------------------------ */

void *_aligned_malloc(size_t size, size_t alignment);
#define _aligned_free(p)                free(p)
#define ZeroMemory(p, n)                memset((p), 0, (n))
#define CopyMemory(d, s, n)             memcpy((d), (s), (n))

#define sprintf_s(buf, size, ...)       snprintf((buf), (size), __VA_ARGS__)
#define _snprintf                       snprintf
#define _vsnprintf                      vsnprintf
#define _stricmp                        strcasecmp
#define _strnicmp                       strncasecmp
#define _strdup                         strdup
#define strerror_s(buf, size, err)      (strerror_r((err), (buf), (size)), 0)
int  freopen_s(FILE **pf, const char *path, const char *mode, FILE *fp);  /* "conout$" keeps fp */
#define fopen_s(pf, path, mode)         ((*(pf) = fopen((path), (mode))) ? 0 : errno)
char *_itoa(int value, char *buf, int radix);
#define OutputDebugStringA(s)           fputs((s), stderr)
#define OutputDebugString               OutputDebugStringA

DWORD GetLastError(void);

/* console: there is no separate console window on Linux; output goes to the
   terminal (or log) that launched the application */
#define AllocConsole()                  (TRUE)
#define FreeConsole()                   (TRUE)

/* thread pool */
typedef DWORD (*LPTHREAD_START_ROUTINE)(void *);
#define WT_EXECUTEDEFAULT               0x00000000
#define WT_EXECUTELONGFUNCTION          0x00000010
BOOL QueueUserWorkItem(LPTHREAD_START_ROUTINE fn, void *context, ULONG flags);

/* ------------------------------------------------------------------------ */
/* Winsock                                                                  */
/* ------------------------------------------------------------------------ */

typedef int                 SOCKET;
typedef struct sockaddr     SOCKADDR;
typedef struct sockaddr_in  SOCKADDR_IN;
typedef struct sockaddr_in *PSOCKADDR_IN;
typedef struct in_addr      IN_ADDR;
typedef HANDLE              WSAEVENT;

#define INVALID_SOCKET      (-1)
#define SOCKET_ERROR        (-1)
int closesocket(SOCKET s);
#define MAKEWORD(a, b)      ((WORD)(((BYTE)(a)) | ((WORD)((BYTE)(b))) << 8))

#define WSAEWOULDBLOCK      EWOULDBLOCK
#define WSAEMSGSIZE         EMSGSIZE
#define WSAECONNRESET       ECONNRESET
#define WSAETIMEDOUT        ETIMEDOUT
#define WSAEINTR            EINTR
#define WSANOTINITIALISED   10093

#define FD_READ_BIT         0
#define FD_READ             (1 << FD_READ_BIT)
#define FD_WRITE_BIT        1
#define FD_WRITE            (1 << FD_WRITE_BIT)
#define FD_MAX_EVENTS       10

#define WSA_INFINITE        INFINITE
#define WSA_WAIT_EVENT_0    WAIT_OBJECT_0
#define WSA_WAIT_TIMEOUT    WAIT_TIMEOUT
#define WSA_WAIT_FAILED     WAIT_FAILED

typedef struct WSAData
{
    WORD wVersion;
    WORD wHighVersion;
    char szDescription[257];
    char szSystemStatus[129];
} WSADATA, *LPWSADATA;

typedef struct _WSANETWORKEVENTS
{
    long lNetworkEvents;
    int  iErrorCode[FD_MAX_EVENTS];
} WSANETWORKEVENTS, *LPWSANETWORKEVENTS;

int      WSAStartup(WORD version, WSADATA *data);
int      WSACleanup(void);
int      WSAGetLastError(void);
WSAEVENT WSACreateEvent(void);
BOOL     WSACloseEvent(WSAEVENT ev);
int      WSAEventSelect(SOCKET s, WSAEVENT ev, long events);
DWORD    WSAWaitForMultipleEvents(DWORD n, const WSAEVENT *evs, BOOL wait_all,
                                  DWORD ms, BOOL alertable);
int      WSAEnumNetworkEvents(SOCKET s, WSAEVENT ev, WSANETWORKEVENTS *ne);

/* Winsock's fd_set is a named struct ("struct fd_set" is valid); glibc's is
   an anonymous typedef.  Struct tags live in their own namespace in C, so a
   layout-compatible tag can be added. */
#ifndef __cplusplus
struct fd_set { fd_set set; };
#endif

/* iphlpapi */
typedef ULONG IPAddr;
#define ERROR_NOT_SUPPORTED 50
/* SendARP only pre-populates the ARP cache on Windows; the Linux kernel
   resolves the address itself on the first datagram sent. */
DWORD SendARP(IPAddr dest, IPAddr src, void *mac, ULONG *len);

/* Winsock takes SO_RCVTIMEO / SO_SNDTIMEO as a DWORD of milliseconds, BSD
   sockets take a struct timeval; translate transparently. */
int compat_setsockopt(int s, int level, int name, const void *val, socklen_t len);
#define setsockopt          compat_setsockopt

#ifdef __cplusplus
}
#endif

#endif
