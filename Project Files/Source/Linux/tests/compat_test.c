/*  compat_test.c

Unit tests for the Win32 compatibility layer: checks that the emulated
primitives behave like their Windows counterparts in the ways wdsp and
ChannelMaster rely on.

*/

#include "win32compat.h"

#include <math.h>

static int failures = 0;

#define CHECK(cond) do { if (!(cond)) { fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); failures++; } } while (0)

static double now_ms(void)
{
    LARGE_INTEGER c, f;
    QueryPerformanceCounter(&c);
    QueryPerformanceFrequency(&f);
    return (double)c.QuadPart * 1000.0 / (double)f.QuadPart;
}

static void test_interlocked(void)
{
    volatile long l = -5;            /* 64-bit on Linux, 32-bit on Windows */
    volatile int i = 0;
    volatile LONG64 q = 0;

    /* the "atomic read" idiom must not modify the value */
    CHECK(_InterlockedAnd(&l, 0xffffffff) == -5);
    CHECK(l == -5);

    l = 0;
    CHECK(InterlockedBitTestAndSet(&l, 0) == 0);
    CHECK(l == 1);
    CHECK(InterlockedBitTestAndSet(&l, 0) == 1);
    CHECK(InterlockedBitTestAndReset(&l, 0) == 1);
    CHECK(l == 0);
    CHECK(InterlockedBitTestAndReset(&l, 0) == 0);

    CHECK(InterlockedIncrement(&i) == 1);
    CHECK(InterlockedDecrement(&i) == 0);
    CHECK(InterlockedExchange(&i, 7) == 0 && i == 7);
    CHECK(InterlockedCompareExchange(&i, 9, 7) == 7 && i == 9);
    CHECK(InterlockedCompareExchange(&i, 1, 7) == 9 && i == 9);
    CHECK(InterlockedAdd64(&q, 5) == 5);
    CHECK(InterlockedExchange64(&q, 1) == 5 && q == 1);
}

static void test_semaphore(void)
{
    HANDLE s = CreateSemaphore(NULL, 0, 2, NULL);
    LONG prev = -1;
    double t0;
    CHECK(s != NULL);
    t0 = now_ms();
    CHECK(WaitForSingleObject(s, 50) == WAIT_TIMEOUT);
    CHECK(now_ms() - t0 >= 45.0);
    CHECK(ReleaseSemaphore(s, 2, &prev) && prev == 0);
    CHECK(!ReleaseSemaphore(s, 1, NULL));            /* would exceed maximum */
    CHECK(WaitForSingleObject(s, 0) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(s, 0) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(s, 0) == WAIT_TIMEOUT);
    /* the drain idiom used by wdsp: while (!WaitForSingleObject(s, 1)); */
    ReleaseSemaphore(s, 2, NULL);
    while (!WaitForSingleObject(s, 1)) ;
    CHECK(WaitForSingleObject(s, 0) == WAIT_TIMEOUT);
    CloseHandle(s);
}

static void test_event(void)
{
    HANDLE a = CreateEvent(NULL, FALSE, FALSE, NULL);   /* auto reset */
    HANDLE m = CreateEvent(NULL, TRUE, TRUE, NULL);     /* manual reset, signalled */
    CHECK(WaitForSingleObject(a, 0) == WAIT_TIMEOUT);
    SetEvent(a);
    CHECK(WaitForSingleObject(a, 0) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(a, 0) == WAIT_TIMEOUT);
    CHECK(WaitForSingleObject(m, 0) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(m, 0) == WAIT_OBJECT_0);
    ResetEvent(m);
    CHECK(WaitForSingleObject(m, 0) == WAIT_TIMEOUT);
    CloseHandle(a);
    CloseHandle(m);
}

static HANDLE handoff;
static volatile long worker_ran = 0;

static unsigned worker_ex(void *arg)
{
    Sleep(20);
    InterlockedExchange(&worker_ran, (long)(intptr_t)arg);
    return 0;
}

static void worker(void *arg)
{
    (void)arg;
    ReleaseSemaphore(handoff, 1, NULL);
    _endthread();
    CHECK(0);                                           /* not reached */
}

static void test_threads(void)
{
    HANDLE t;
    handoff = CreateSemaphore(NULL, 0, 1, NULL);
    CHECK(_beginthread(worker, 0, NULL) != (uintptr_t)-1);
    CHECK(WaitForSingleObject(handoff, 2000) == WAIT_OBJECT_0);

    t = (HANDLE)_beginthreadex(NULL, 0, worker_ex, (void *)(intptr_t)42, 0, NULL);
    CHECK(t != NULL);
    CHECK(WaitForSingleObject(t, 0) == WAIT_TIMEOUT);
    CHECK(WaitForSingleObject(t, 2000) == WAIT_OBJECT_0);
    CHECK(worker_ran == 42);
    CHECK(WaitForSingleObject(t, 0) == WAIT_OBJECT_0);  /* stays signalled */
    CloseHandle(t);
    CloseHandle(handoff);
}

static HANDLE blocked_on;
static void block_forever(void *arg)
{
    (void)arg;
    WaitForSingleObject(blocked_on, INFINITE);     /* never signalled, as in IOThreadStop() */
}

/* ChannelMaster's IOThreadStop() closes semaphores a thread is still blocked
   on.  Windows keeps the object alive for the waiter; CloseHandle() must not
   block or destroy it underneath the waiter. */
static void test_close_while_waiting(void)
{
    double t0;
    blocked_on = CreateSemaphore(NULL, 0, 1, NULL);
    _beginthread(block_forever, 0, NULL);
    Sleep(50);                                      /* let the thread start waiting */
    t0 = now_ms();
    CHECK(CloseHandle(blocked_on));
    CHECK(now_ms() - t0 < 100.0);
}

/* Waiting on a handle after CloseHandle() fails like an invalid handle on
   Windows instead of touching freed memory (ChannelMaster's IOThreadStop
   closes the send thread's semaphores while that thread may still wait). */
static void test_wait_after_close(void)
{
    HANDLE s = CreateSemaphore(NULL, 0, 1, NULL), e = CreateEvent(NULL, FALSE, FALSE, NULL);
    HANDLE both[2];
    both[0] = s; both[1] = e;
    CHECK(CloseHandle(s));
    CHECK(!CloseHandle(s));
    CHECK(WaitForSingleObject(s, INFINITE) == WAIT_FAILED);
    CHECK(!ReleaseSemaphore(s, 1, NULL));
    CHECK(WaitForMultipleObjects(2, both, TRUE, INFINITE) == WAIT_FAILED);
    CHECK(WaitForMultipleObjects(2, both, FALSE, INFINITE) == WAIT_FAILED);
    CHECK(SetEvent(e));
    CHECK(CloseHandle(e));
    CHECK(!SetEvent(e));
}

static void test_wait_multiple(void)
{
    HANDLE h[2];
    h[0] = CreateSemaphore(NULL, 1, 1, NULL);
    h[1] = CreateSemaphore(NULL, 0, 1, NULL);
    CHECK(WaitForMultipleObjects(2, h, FALSE, 0) == WAIT_OBJECT_0 + 0);
    CHECK(WaitForMultipleObjects(2, h, FALSE, 20) == WAIT_TIMEOUT);
    ReleaseSemaphore(h[1], 1, NULL);
    CHECK(WaitForMultipleObjects(2, h, FALSE, 0) == WAIT_OBJECT_0 + 1);
    /* wait-all succeeds once every object is signalled, and takes them all */
    ReleaseSemaphore(h[0], 1, NULL);
    ReleaseSemaphore(h[1], 1, NULL);
    CHECK(WaitForMultipleObjects(2, h, TRUE, 1000) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(h[0], 0) == WAIT_TIMEOUT);
    CHECK(WaitForSingleObject(h[1], 0) == WAIT_TIMEOUT);
    CloseHandle(h[0]);
    CloseHandle(h[1]);
}

static void test_timer(void)
{
    HANDLE t = CreateWaitableTimer(NULL, FALSE, NULL);
    LARGE_INTEGER due;
    double t0, t1, t2;
    due.QuadPart = -300000LL;                            /* 30 ms, relative */
    t0 = now_ms();
    CHECK(SetWaitableTimer(t, &due, 20, NULL, NULL, 0));
    CHECK(WaitForSingleObject(t, INFINITE) == WAIT_OBJECT_0);
    t1 = now_ms();
    CHECK(WaitForSingleObject(t, INFINITE) == WAIT_OBJECT_0);
    t2 = now_ms();
    CHECK(t1 - t0 >= 28.0 && t1 - t0 < 200.0);
    CHECK(t2 - t1 >= 15.0 && t2 - t1 < 200.0);
    CancelWaitableTimer(t);
    CHECK(WaitForSingleObject(t, 30) == WAIT_TIMEOUT);
    CloseHandle(t);
}

static volatile long pool_count = 0;
static DWORD pool_item(void *arg)
{
    InterlockedAdd(&pool_count, (long)(intptr_t)arg);
    return 0;
}

static void test_threadpool(void)
{
    int k;
    for (k = 1; k <= 100; k++)
        CHECK(QueueUserWorkItem(pool_item, (void *)(intptr_t)k, 0));
    for (k = 0; k < 200 && _InterlockedAnd(&pool_count, 0xffffffff) != 5050; k++)
        Sleep(10);
    CHECK(pool_count == 5050);
}

static void test_misc(void)
{
    char buf[40];
    void *p = _aligned_malloc(1000, 64);
    CHECK(p && ((uintptr_t)p & 63) == 0);
    _aligned_free(p);
    CHECK(strcmp(_itoa(-1234, buf, 10), "-1234") == 0);
    CHECK(strcmp(_itoa(255, buf, 16), "ff") == 0);
    CHECK(max(3, 4) == 4 && min(3, 4) == 3);
    CHECK(GetTickCount64() > 0);
    {
        __declspec(align(64)) static double aligned[4];
        CHECK(((uintptr_t)aligned & 63) == 0);
    }
}

int main(void)
{
    test_interlocked();
    test_semaphore();
    test_event();
    test_threads();
    test_wait_multiple();
    test_close_while_waiting();
    test_wait_after_close();
    test_timer();
    test_threadpool();
    test_misc();
    if (failures)
    {
        fprintf(stderr, "compat_test: %d failure(s)\n", failures);
        return 1;
    }
    printf("compat_test: all checks passed\n");
    return 0;
}
