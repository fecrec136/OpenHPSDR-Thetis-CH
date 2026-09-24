/*  win32compat.c

This file is part of a program that implements a Software-Defined Radio.

POSIX implementation of the Win32 / Winsock subset used by wdsp and
ChannelMaster.  See win32compat.h.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

*/

#include "win32compat.h"

#include <poll.h>
#include <unistd.h>
#include <sched.h>
#include <sys/time.h>
#include <sys/syscall.h>

/* ------------------------------------------------------------------------ */
/* helpers                                                                  */
/* ------------------------------------------------------------------------ */

static void abs_deadline(struct timespec *ts, DWORD ms)
{
    clock_gettime(CLOCK_MONOTONIC, ts);
    ts->tv_sec  += ms / 1000;
    ts->tv_nsec += (long)(ms % 1000) * 1000000L;
    if (ts->tv_nsec >= 1000000000L)
    {
        ts->tv_sec  += 1;
        ts->tv_nsec -= 1000000000L;
    }
}

static void init_cond_monotonic(pthread_cond_t *cond)
{
    pthread_condattr_t attr;
    pthread_condattr_init(&attr);
    pthread_condattr_setclock(&attr, CLOCK_MONOTONIC);
    pthread_cond_init(cond, &attr);
    pthread_condattr_destroy(&attr);
}

/* ------------------------------------------------------------------------ */
/* critical sections                                                        */
/* ------------------------------------------------------------------------ */

void InitializeCriticalSection(CRITICAL_SECTION *cs)
{
    pthread_mutexattr_t attr;
    pthread_mutexattr_init(&attr);
    pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_RECURSIVE);
    pthread_mutex_init(cs, &attr);
    pthread_mutexattr_destroy(&attr);
}

BOOL InitializeCriticalSectionAndSpinCount(CRITICAL_SECTION *cs, DWORD spin)
{
    (void)spin;
    InitializeCriticalSection(cs);
    return TRUE;
}

/* ------------------------------------------------------------------------ */
/* kernel objects                                                           */
/* ------------------------------------------------------------------------ */

enum { H_SEMAPHORE = 1, H_EVENT, H_THREAD, H_TIMER, H_WSAEVENT, H_PSEUDO };

typedef struct compat_handle
{
    int             type;
    int             refs;           /* owner(s), a running thread, and threads waiting on it */
    int             closed;         /* CloseHandle() called: the handle value is no longer valid */
    struct compat_handle *next_zombie;
    struct timespec released;
    pthread_mutex_t mtx;
    pthread_cond_t  cond;
    /* semaphore */
    LONG            count;
    LONG            maximum;
    /* event */
    int             manual_reset;
    int             signaled;
    /* thread */
    pthread_t       thread;
    int             finished;
    unsigned      (*start_ex)(void *);
    void          (*start)(void *);
    void           *arg;
    /* waitable timer */
    struct timespec due;
    LONG            period_ms;
    int             armed;
    /* WSA event */
    int             sock;
    long            netevents;
} compat_handle;

static compat_handle pseudo_thread = { H_PSEUDO };

static compat_handle *new_handle(int type)
{
    compat_handle *h = (compat_handle *)calloc(1, sizeof(compat_handle));
    if (!h) return NULL;
    h->type = type;
    h->refs = 1;
    h->sock = -1;
    pthread_mutex_init(&h->mtx, NULL);
    init_cond_monotonic(&h->cond);
    return h;
}

/* Objects nobody references any more are not freed at once.  On Windows a
   HANDLE is an index into a table, so using one after CloseHandle() fails
   cleanly with ERROR_INVALID_HANDLE -- and upstream code relies on that:
   IOThreadStop() closes the semaphores of the Protocol 1 send thread
   without waiting for the thread, which may be just about to wait on them.
   Here a HANDLE is a pointer, so the memory is kept for a grace period and
   the 'closed' flag makes such a late wait fail as it would on Windows. */
#define ZOMBIE_GRACE_S 10

static pthread_mutex_t zombie_mtx = PTHREAD_MUTEX_INITIALIZER;
static compat_handle  *zombie_head, *zombie_tail;

static void free_handle(compat_handle *h)
{
    pthread_cond_destroy(&h->cond);
    pthread_mutex_destroy(&h->mtx);
    free(h);
}

static void destroy_handle(compat_handle *h)
{
    struct timespec now;
    compat_handle *reap = NULL;
    clock_gettime(CLOCK_MONOTONIC, &now);
    h->released = now;
    h->next_zombie = NULL;
    pthread_mutex_lock(&zombie_mtx);
    if (zombie_tail) zombie_tail->next_zombie = h;
    else             zombie_head = h;
    zombie_tail = h;
    /* the list is in release order: take the ones past their grace period */
    while (zombie_head && now.tv_sec - zombie_head->released.tv_sec > ZOMBIE_GRACE_S)
    {
        compat_handle *z = zombie_head;
        zombie_head = z->next_zombie;
        if (!zombie_head) zombie_tail = NULL;
        z->next_zombie = reap;
        reap = z;
    }
    pthread_mutex_unlock(&zombie_mtx);
    while (reap)
    {
        compat_handle *z = reap;
        reap = z->next_zombie;
        free_handle(z);
    }
}

/* Drop one reference.  Like a Windows kernel object, a handle stays alive
   while anyone holds a reference to it -- including a thread blocked in
   WaitForSingleObject() -- so CloseHandle() on an object another thread is
   waiting for does not destroy it underneath the waiter. */
static void release_handle(compat_handle *h)
{
    int refs;
    pthread_mutex_lock(&h->mtx);
    refs = --h->refs;
    pthread_mutex_unlock(&h->mtx);
    if (refs == 0) destroy_handle(h);
}

HANDLE CreateSemaphore(void *sa, LONG initial, LONG maximum, const char *name)
{
    compat_handle *h;
    (void)sa; (void)name;
    h = new_handle(H_SEMAPHORE);
    if (!h) return NULL;
    h->count = initial;
    h->maximum = maximum;
    return h;
}

BOOL ReleaseSemaphore(HANDLE hh, LONG count, LONG *previous)
{
    compat_handle *h = (compat_handle *)hh;
    BOOL ok = TRUE;
    if (!h || h->type != H_SEMAPHORE) return FALSE;
    pthread_mutex_lock(&h->mtx);
    if (previous) *previous = h->count;
    if (h->closed || count <= 0 || h->count + count > h->maximum)
        ok = FALSE;
    else
    {
        h->count += count;
        if (count == 1) pthread_cond_signal(&h->cond);
        else            pthread_cond_broadcast(&h->cond);
    }
    pthread_mutex_unlock(&h->mtx);
    return ok;
}

HANDLE CreateEvent(void *sa, BOOL manual_reset, BOOL initial_state, const char *name)
{
    compat_handle *h;
    (void)sa; (void)name;
    h = new_handle(H_EVENT);
    if (!h) return NULL;
    h->manual_reset = manual_reset;
    h->signaled = initial_state;
    return h;
}

BOOL SetEvent(HANDLE hh)
{
    compat_handle *h = (compat_handle *)hh;
    if (!h || (h->type != H_EVENT && h->type != H_WSAEVENT)) return FALSE;
    pthread_mutex_lock(&h->mtx);
    if (h->closed) { pthread_mutex_unlock(&h->mtx); return FALSE; }
    h->signaled = 1;
    if (h->manual_reset) pthread_cond_broadcast(&h->cond);
    else                 pthread_cond_signal(&h->cond);
    pthread_mutex_unlock(&h->mtx);
    return TRUE;
}

BOOL ResetEvent(HANDLE hh)
{
    compat_handle *h = (compat_handle *)hh;
    if (!h || (h->type != H_EVENT && h->type != H_WSAEVENT)) return FALSE;
    pthread_mutex_lock(&h->mtx);
    h->signaled = 0;
    pthread_mutex_unlock(&h->mtx);
    return TRUE;
}

HANDLE CreateWaitableTimer(void *sa, BOOL manual_reset, const char *name)
{
    (void)sa; (void)manual_reset; (void)name;
    return new_handle(H_TIMER);
}

BOOL SetWaitableTimer(HANDLE hh, const LARGE_INTEGER *due, LONG period_ms,
                      void *completion, void *arg, BOOL resume)
{
    compat_handle *h = (compat_handle *)hh;
    LONGLONG t100ns;
    (void)completion; (void)arg; (void)resume;
    if (!h || h->type != H_TIMER || !due) return FALSE;
    /* negative due time = relative, in 100ns units; positive = absolute
       FILETIME, which is not used by the callers, so treat it as "now" */
    t100ns = due->QuadPart < 0 ? -due->QuadPart : 0;
    pthread_mutex_lock(&h->mtx);
    clock_gettime(CLOCK_MONOTONIC, &h->due);
    h->due.tv_sec  += (time_t)(t100ns / 10000000LL);
    h->due.tv_nsec += (long)(t100ns % 10000000LL) * 100L;
    if (h->due.tv_nsec >= 1000000000L)
    {
        h->due.tv_sec  += 1;
        h->due.tv_nsec -= 1000000000L;
    }
    h->period_ms = period_ms;
    h->armed = 1;
    pthread_cond_broadcast(&h->cond);
    pthread_mutex_unlock(&h->mtx);
    return TRUE;
}

BOOL CancelWaitableTimer(HANDLE hh)
{
    compat_handle *h = (compat_handle *)hh;
    if (!h || h->type != H_TIMER) return FALSE;
    pthread_mutex_lock(&h->mtx);
    h->armed = 0;
    pthread_cond_broadcast(&h->cond);
    pthread_mutex_unlock(&h->mtx);
    return TRUE;
}

static int ts_before(const struct timespec *a, const struct timespec *b)
{
    return a->tv_sec < b->tv_sec || (a->tv_sec == b->tv_sec && a->tv_nsec < b->tv_nsec);
}

static DWORD wait_sock(int sock, DWORD ms)
{
    struct pollfd pfd;
    int rc;
    pfd.fd = sock;
    pfd.events = POLLIN;
    pfd.revents = 0;
    do
        rc = poll(&pfd, 1, ms == INFINITE ? -1 : (int)ms);
    while (rc < 0 && errno == EINTR);
    if (rc < 0)  return WAIT_FAILED;
    if (rc == 0) return WAIT_TIMEOUT;
    return WAIT_OBJECT_0;
}

DWORD WaitForSingleObject(HANDLE hh, DWORD ms)
{
    compat_handle *h = (compat_handle *)hh;
    struct timespec deadline = { 0, 0 };
    int rc = 0;
    DWORD result = WAIT_OBJECT_0;

    if (!h || h->type == H_PSEUDO) return WAIT_FAILED;
    if (h->type == H_WSAEVENT && h->sock >= 0)
        return wait_sock(h->sock, ms);
    if (ms != INFINITE) abs_deadline(&deadline, ms);

    pthread_mutex_lock(&h->mtx);
    if (h->closed)                          /* invalid handle, as on Windows */
    {
        pthread_mutex_unlock(&h->mtx);
        return WAIT_FAILED;
    }
    h->refs++;                              /* keep the object alive while we wait */
    switch (h->type)
    {
    case H_SEMAPHORE:
        while (h->count == 0 && rc != ETIMEDOUT)
            rc = ms == INFINITE ? pthread_cond_wait(&h->cond, &h->mtx)
                                : pthread_cond_timedwait(&h->cond, &h->mtx, &deadline);
        if (h->count > 0) h->count--;
        else result = WAIT_TIMEOUT;
        break;
    case H_EVENT:
    case H_WSAEVENT:
        while (!h->signaled && rc != ETIMEDOUT)
            rc = ms == INFINITE ? pthread_cond_wait(&h->cond, &h->mtx)
                                : pthread_cond_timedwait(&h->cond, &h->mtx, &deadline);
        if (h->signaled) { if (!h->manual_reset) h->signaled = 0; }
        else result = WAIT_TIMEOUT;
        break;
    case H_THREAD:
        while (!h->finished && rc != ETIMEDOUT)
            rc = ms == INFINITE ? pthread_cond_wait(&h->cond, &h->mtx)
                                : pthread_cond_timedwait(&h->cond, &h->mtx, &deadline);
        if (!h->finished) result = WAIT_TIMEOUT;
        break;
    case H_TIMER:
        for (;;)
        {
            struct timespec now, until;
            clock_gettime(CLOCK_MONOTONIC, &now);
            if (h->armed && !ts_before(&now, &h->due))
            {
                /* fired: schedule the next period or disarm */
                if (h->period_ms > 0)
                {
                    h->due.tv_sec  += h->period_ms / 1000;
                    h->due.tv_nsec += (long)(h->period_ms % 1000) * 1000000L;
                    if (h->due.tv_nsec >= 1000000000L)
                    {
                        h->due.tv_sec  += 1;
                        h->due.tv_nsec -= 1000000000L;
                    }
                    if (ts_before(&h->due, &now)) h->due = now;   /* don't burst after a stall */
                }
                else
                    h->armed = 0;
                break;
            }
            if (ms != INFINITE && !ts_before(&now, &deadline)) { result = WAIT_TIMEOUT; break; }
            if (h->armed)
            {
                until = h->due;
                if (ms != INFINITE && ts_before(&deadline, &until)) until = deadline;
                pthread_cond_timedwait(&h->cond, &h->mtx, &until);
            }
            else if (ms == INFINITE)
                pthread_cond_wait(&h->cond, &h->mtx);
            else
                pthread_cond_timedwait(&h->cond, &h->mtx, &deadline);
        }
        break;
    default:
        result = WAIT_FAILED;
        break;
    }
    {
        int last = (--h->refs == 0);
        pthread_mutex_unlock(&h->mtx);
        if (last) destroy_handle(h);
    }
    return result;
}

DWORD WaitForMultipleObjects(DWORD n, const HANDLE *h, BOOL wait_all, DWORD ms)
{
    DWORD i;
    if (n == 0 || !h) return WAIT_FAILED;
    if (wait_all)
    {
        /* All callers use a single consumer thread per handle set, so taking
           the objects one after another is equivalent to the atomic wait. */
        struct timespec start, now;
        clock_gettime(CLOCK_MONOTONIC, &start);
        for (i = 0; i < n; i++)
        {
            DWORD left = ms;
            if (ms != INFINITE)
            {
                long elapsed;
                clock_gettime(CLOCK_MONOTONIC, &now);
                elapsed = (now.tv_sec - start.tv_sec) * 1000L + (now.tv_nsec - start.tv_nsec) / 1000000L;
                left = elapsed >= (long)ms ? 0 : ms - (DWORD)elapsed;
            }
            DWORD r = WaitForSingleObject(h[i], left);
            if (r != WAIT_OBJECT_0) return r;
        }
        return WAIT_OBJECT_0;
    }
    else
    {
        /* wait-any: poll the objects with a short back-off */
        struct timespec start, now;
        clock_gettime(CLOCK_MONOTONIC, &start);
        for (;;)
        {
            for (i = 0; i < n; i++)
            {
                DWORD r = WaitForSingleObject(h[i], 0);
                if (r == WAIT_OBJECT_0) return WAIT_OBJECT_0 + i;
                if (r == WAIT_FAILED)   return WAIT_FAILED;
            }
            if (ms != INFINITE)
            {
                long elapsed;
                clock_gettime(CLOCK_MONOTONIC, &now);
                elapsed = (now.tv_sec - start.tv_sec) * 1000L + (now.tv_nsec - start.tv_nsec) / 1000000L;
                if (elapsed >= (long)ms) return WAIT_TIMEOUT;
            }
            Sleep(1);
        }
    }
}

BOOL CloseHandle(HANDLE hh)
{
    compat_handle *h = (compat_handle *)hh;
    if (!h) return FALSE;
    if (h->type == H_PSEUDO) return TRUE;
    pthread_mutex_lock(&h->mtx);
    if (h->closed) { pthread_mutex_unlock(&h->mtx); return FALSE; }
    h->closed = 1;
    pthread_mutex_unlock(&h->mtx);
    release_handle(h);
    return TRUE;
}

/* ------------------------------------------------------------------------ */
/* threads                                                                  */
/* ------------------------------------------------------------------------ */

static void thread_finish(void *p)
{
    compat_handle *h = (compat_handle *)p;
    pthread_mutex_lock(&h->mtx);
    h->finished = 1;
    pthread_cond_broadcast(&h->cond);
    pthread_mutex_unlock(&h->mtx);
    release_handle(h);
}

static void *thread_trampoline(void *p)
{
    compat_handle *h = (compat_handle *)p;
    pthread_cleanup_push(thread_finish, h);
    if (h->start_ex) h->start_ex(h->arg);
    else             h->start(h->arg);
    pthread_cleanup_pop(1);
    return NULL;
}

static compat_handle *spawn(void (*start)(void *), unsigned (*start_ex)(void *),
                            unsigned stack_size, void *arg)
{
    pthread_attr_t attr;
    compat_handle *h = new_handle(H_THREAD);
    if (!h) return NULL;
    h->refs = 2;                        /* caller's handle + running thread */
    h->start = start;
    h->start_ex = start_ex;
    h->arg = arg;
    pthread_attr_init(&attr);
    pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
    if (stack_size >= PTHREAD_STACK_MIN) pthread_attr_setstacksize(&attr, stack_size);
    if (pthread_create(&h->thread, &attr, thread_trampoline, h) != 0)
    {
        pthread_attr_destroy(&attr);
        h->refs = 1;
        release_handle(h);
        return NULL;
    }
    pthread_attr_destroy(&attr);
    return h;
}

uintptr_t _beginthread(void (*start)(void *), unsigned stack_size, void *arg)
{
    compat_handle *h = spawn(start, NULL, stack_size, arg);
    if (!h) return (uintptr_t)-1;
    /* _beginthread handles are closed automatically on thread exit */
    release_handle(h);
    return (uintptr_t)h;
}

uintptr_t _beginthreadex(void *security, unsigned stack_size,
                         unsigned (*start)(void *), void *arg,
                         unsigned initflag, unsigned *thrdaddr)
{
    compat_handle *h;
    (void)security; (void)initflag;
    h = spawn(NULL, start, stack_size, arg);
    if (thrdaddr) *thrdaddr = h ? (unsigned)(uintptr_t)h : 0;
    return (uintptr_t)h;
}

void _endthread(void)
{
    pthread_exit(NULL);
}

void _endthreadex(unsigned retval)
{
    (void)retval;
    pthread_exit(NULL);
}

HANDLE GetCurrentThread(void)
{
    return &pseudo_thread;
}

DWORD GetCurrentThreadId(void)
{
    return (DWORD)syscall(SYS_gettid);
}

static int set_rt_priority(pthread_t thread, int prio)
{
    struct sched_param sp;
    int lo = sched_get_priority_min(SCHED_FIFO);
    int hi = sched_get_priority_max(SCHED_FIFO);
    if (prio < lo) prio = lo;
    if (prio > hi) prio = hi;
    sp.sched_priority = prio;
    /* fails with EPERM unless the user has an rtprio limit (e.g. membership of
       the 'audio' group with /etc/security/limits.d/audio.conf); in that case
       the thread just keeps running at normal priority */
    return pthread_setschedparam(thread, SCHED_FIFO, &sp) == 0;
}

BOOL SetThreadPriority(HANDLE thread, int priority)
{
    /* GetCurrentThread() pseudo handle -> the caller; a thread handle from
       _beginthread[ex] -> that thread (valid until it exits, as on Windows) */
    compat_handle *h = (compat_handle *)thread;
    pthread_t target;
    if (!h) return FALSE;
    if (h->type == H_PSEUDO)
        target = pthread_self();
    else if (h->type == H_THREAD)
        target = h->thread;
    else
        return FALSE;
    if (priority >= THREAD_PRIORITY_TIME_CRITICAL)  set_rt_priority(target, 80);
    else if (priority >= THREAD_PRIORITY_HIGHEST)   set_rt_priority(target, 60);
    return TRUE;
}

HANDLE AvSetMmThreadCharacteristics(const char *task, DWORD *task_index)
{
    (void)task;
    if (task_index) *task_index = 0;
    set_rt_priority(pthread_self(), 70);
    return &pseudo_thread;
}

BOOL AvSetMmThreadPriority(HANDLE h, int priority)
{
    (void)h;
    set_rt_priority(pthread_self(), 70 + 5 * priority);
    return TRUE;
}

BOOL AvRevertMmThreadCharacteristics(HANDLE h)
{
    struct sched_param sp;
    (void)h;
    sp.sched_priority = 0;
    pthread_setschedparam(pthread_self(), SCHED_OTHER, &sp);
    return TRUE;
}

/* ------------------------------------------------------------------------ */
/* time                                                                     */
/* ------------------------------------------------------------------------ */

void Sleep(DWORD ms)
{
    struct timespec ts;
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    while (nanosleep(&ts, &ts) != 0 && errno == EINTR) ;
}

BOOL QueryPerformanceCounter(LARGE_INTEGER *count)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    count->QuadPart = (LONGLONG)ts.tv_sec * 1000000000LL + ts.tv_nsec;
    return TRUE;
}

BOOL QueryPerformanceFrequency(LARGE_INTEGER *freq)
{
    freq->QuadPart = 1000000000LL;
    return TRUE;
}

ULONGLONG GetTickCount64(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (ULONGLONG)ts.tv_sec * 1000ULL + (ULONGLONG)(ts.tv_nsec / 1000000L);
}

DWORD GetTickCount(void)
{
    return (DWORD)GetTickCount64();
}

DWORD timeGetTime(void)
{
    return (DWORD)GetTickCount64();
}

static void fill_systemtime(SYSTEMTIME *st, int utc)
{
    struct timespec ts;
    struct tm tm;
    clock_gettime(CLOCK_REALTIME, &ts);
    if (utc) gmtime_r(&ts.tv_sec, &tm);
    else     localtime_r(&ts.tv_sec, &tm);
    st->wYear = (WORD)(tm.tm_year + 1900);
    st->wMonth = (WORD)(tm.tm_mon + 1);
    st->wDayOfWeek = (WORD)tm.tm_wday;
    st->wDay = (WORD)tm.tm_mday;
    st->wHour = (WORD)tm.tm_hour;
    st->wMinute = (WORD)tm.tm_min;
    st->wSecond = (WORD)tm.tm_sec;
    st->wMilliseconds = (WORD)(ts.tv_nsec / 1000000L);
}

void GetLocalTime(SYSTEMTIME *st)  { fill_systemtime(st, 0); }
void GetSystemTime(SYSTEMTIME *st) { fill_systemtime(st, 1); }

/* ------------------------------------------------------------------------ */
/* memory / CRT                                                             */
/* ------------------------------------------------------------------------ */

void *_aligned_malloc(size_t size, size_t alignment)
{
    void *p = NULL;
    if (alignment < sizeof(void *)) alignment = sizeof(void *);
    if (posix_memalign(&p, alignment, size ? size : 1) != 0) return NULL;
    return p;
}

char *_itoa(int value, char *buf, int radix)
{
    char tmp[34];
    char *t = tmp;
    unsigned v;
    int neg = (radix == 10 && value < 0);
    if (radix < 2 || radix > 36) { buf[0] = 0; return buf; }
    v = neg ? (unsigned)(-(long)value) : (unsigned)value;
    do { int d = (int)(v % (unsigned)radix); *t++ = (char)(d < 10 ? '0' + d : 'a' + d - 10); v /= (unsigned)radix; } while (v);
    {
        char *o = buf;
        if (neg) *o++ = '-';
        while (t > tmp) *o++ = *--t;
        *o = 0;
    }
    return buf;
}

DWORD GetLastError(void)
{
    return (DWORD)errno;
}

int freopen_s(FILE **pf, const char *path, const char *mode, FILE *fp)
{
    /* "conout$" is the Windows console device: keep writing to the stream */
    if (path && strcasecmp(path, "conout$") == 0)
    {
        *pf = fp;
        return 0;
    }
    *pf = freopen(path, mode, fp);
    return *pf ? 0 : errno;
}

/* ------------------------------------------------------------------------ */
/* QueueUserWorkItem: a small, lazily started, fixed-size worker pool       */
/* ------------------------------------------------------------------------ */

typedef struct work_item
{
    LPTHREAD_START_ROUTINE fn;
    void *context;
    struct work_item *next;
} work_item;

static pthread_mutex_t pool_mtx = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t  pool_cond = PTHREAD_COND_INITIALIZER;
static work_item      *pool_head = NULL;
static work_item      *pool_tail = NULL;
static work_item      *pool_free = NULL;     /* recycled items: no malloc in steady state */
static int             pool_started = 0;

static void *pool_worker(void *arg)
{
    (void)arg;
    for (;;)
    {
        work_item *w;
        LPTHREAD_START_ROUTINE fn;
        void *ctx;
        pthread_mutex_lock(&pool_mtx);
        while (!pool_head) pthread_cond_wait(&pool_cond, &pool_mtx);
        w = pool_head;
        pool_head = w->next;
        if (!pool_head) pool_tail = NULL;
        fn = w->fn;
        ctx = w->context;
        w->next = pool_free;
        pool_free = w;
        pthread_mutex_unlock(&pool_mtx);
        fn(ctx);
    }
    return NULL;
}

BOOL QueueUserWorkItem(LPTHREAD_START_ROUTINE fn, void *context, ULONG flags)
{
    work_item *w;
    (void)flags;
    if (!fn) return FALSE;
    pthread_mutex_lock(&pool_mtx);
    if (!pool_started)
    {
        long ncpu = sysconf(_SC_NPROCESSORS_ONLN);
        int i, nthreads = (int)(ncpu < 2 ? 4 : ncpu * 2);
        if (nthreads > 32) nthreads = 32;
        for (i = 0; i < nthreads; i++)
        {
            pthread_t t;
            pthread_attr_t attr;
            pthread_attr_init(&attr);
            pthread_attr_setdetachstate(&attr, PTHREAD_CREATE_DETACHED);
            if (pthread_create(&t, &attr, pool_worker, NULL) == 0) pool_started++;
            pthread_attr_destroy(&attr);
        }
        if (!pool_started) { pthread_mutex_unlock(&pool_mtx); return FALSE; }
    }
    if (pool_free) { w = pool_free; pool_free = w->next; }
    else if (!(w = (work_item *)malloc(sizeof(work_item)))) { pthread_mutex_unlock(&pool_mtx); return FALSE; }
    w->fn = fn;
    w->context = context;
    w->next = NULL;
    if (pool_tail) pool_tail->next = w;
    else           pool_head = w;
    pool_tail = w;
    pthread_cond_signal(&pool_cond);
    pthread_mutex_unlock(&pool_mtx);
    return TRUE;
}

/* ------------------------------------------------------------------------ */
/* Winsock                                                                  */
/* ------------------------------------------------------------------------ */

int WSAStartup(WORD version, WSADATA *data)
{
    if (data)
    {
        memset(data, 0, sizeof(*data));
        data->wVersion = version;
        data->wHighVersion = version;
    }
    return 0;
}

int WSACleanup(void)      { return 0; }
int closesocket(SOCKET s) { return close(s); }

DWORD SendARP(IPAddr dest, IPAddr src, void *mac, ULONG *len)
{
    (void)dest; (void)src; (void)mac; (void)len;
    return ERROR_NOT_SUPPORTED;
}
int WSAGetLastError(void) { return errno; }

WSAEVENT WSACreateEvent(void)
{
    compat_handle *h = new_handle(H_WSAEVENT);
    if (h) h->manual_reset = 1;
    return h;
}

BOOL WSACloseEvent(WSAEVENT ev)
{
    return CloseHandle(ev);
}

int WSAEventSelect(SOCKET s, WSAEVENT ev, long events)
{
    compat_handle *h = (compat_handle *)ev;
    if (!h || h->type != H_WSAEVENT) { errno = EINVAL; return SOCKET_ERROR; }
    h->sock = s;
    h->netevents = events;
    return 0;
}

DWORD WSAWaitForMultipleEvents(DWORD n, const WSAEVENT *evs, BOOL wait_all,
                               DWORD ms, BOOL alertable)
{
    struct pollfd pfd[64];
    DWORD i;
    int rc;
    (void)wait_all; (void)alertable;
    if (n == 0 || n > 64 || !evs) return WSA_WAIT_FAILED;
    for (i = 0; i < n; i++)
    {
        compat_handle *h = (compat_handle *)evs[i];
        if (!h || h->type != H_WSAEVENT) return WSA_WAIT_FAILED;
        if (h->sock < 0)
            return WaitForMultipleObjects(n, evs, FALSE, ms);
        pfd[i].fd = h->sock;
        pfd[i].events = POLLIN;
        pfd[i].revents = 0;
    }
    do
        rc = poll(pfd, n, ms == INFINITE ? -1 : (int)ms);
    while (rc < 0 && errno == EINTR);
    if (rc < 0)  return WSA_WAIT_FAILED;
    if (rc == 0) return WSA_WAIT_TIMEOUT;
    for (i = 0; i < n; i++)
        if (pfd[i].revents) return WSA_WAIT_EVENT_0 + i;
    return WSA_WAIT_TIMEOUT;
}

int WSAEnumNetworkEvents(SOCKET s, WSAEVENT ev, WSANETWORKEVENTS *ne)
{
    struct pollfd pfd;
    (void)ev;
    memset(ne, 0, sizeof(*ne));
    pfd.fd = s;
    pfd.events = POLLIN;
    pfd.revents = 0;
    if (poll(&pfd, 1, 0) > 0)
    {
        if (pfd.revents & POLLIN) ne->lNetworkEvents |= FD_READ;
        if (pfd.revents & (POLLERR | POLLNVAL))
        {
            ne->lNetworkEvents |= FD_READ;
            ne->iErrorCode[FD_READ_BIT] = pfd.revents & POLLNVAL ? EBADF : EIO;
        }
    }
    return 0;
}

#undef setsockopt
int compat_setsockopt(int s, int level, int name, const void *val, socklen_t len)
{
    if (level == SOL_SOCKET && (name == SO_RCVTIMEO || name == SO_SNDTIMEO) &&
        len == sizeof(DWORD))
    {
        struct timeval tv;
        DWORD ms = *(const DWORD *)val;
        tv.tv_sec = ms / 1000;
        tv.tv_usec = (long)(ms % 1000) * 1000L;
        return setsockopt(s, level, name, &tv, sizeof(tv));
    }
    return setsockopt(s, level, name, val, len);
}
