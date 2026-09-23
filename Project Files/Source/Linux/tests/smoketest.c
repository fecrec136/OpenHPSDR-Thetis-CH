/*  smoketest.c

End-to-end check of the Linux native libraries:
  - libwdsp.so, libChannelMaster.so and libPA19.so load with every symbol resolved
  - a wdsp receive channel runs on its own DSP thread and filters correctly:
    a tone inside the USB passband passes, the same tone in the opposite
    sideband is rejected
  - the PortAudio fork initialises and keeps Thetis' paFloat64 extension

usage: thetis_smoketest <libwdsp.so> <libChannelMaster.so> <libPA19.so>

*/

#include <dlfcn.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define IN_SIZE   1024
#define RATE      48000
#define BLOCKS    120
#define SETTLE    40

typedef void   (*OpenChannel_t)(int, int, int, int, int, int, int, int, double, double, double, double, int);
typedef void   (*CloseChannel_t)(int);
typedef void   (*fexchange0_t)(int, double *, double *, int *);
typedef void   (*SetRXAMode_t)(int, int);
typedef void   (*SetRXABandpassFreqs_t)(int, double, double);
typedef void   (*SetRXAAGCMode_t)(int, int);
typedef int    (*GetWDSPVersion_t)(void);
typedef int    (*GetCMVersion_t)(void);
typedef int    (*PA_Initialize_t)(void);
typedef int    (*PA_Terminate_t)(void);
typedef int    (*PA_GetDeviceCount_t)(void);
typedef const char *(*PA_GetVersionText_t)(void);
typedef int    (*PA_GetSampleSize_t)(unsigned long);

static void *need(void *lib, const char *name)
{
    void *p = dlsym(lib, name);
    if (!p)
    {
        fprintf(stderr, "missing symbol %s\n", name);
        exit(1);
    }
    return p;
}

static void *load(const char *path)
{
    void *h = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
    if (!h)
    {
        fprintf(stderr, "dlopen %s: %s\n", path, dlerror());
        exit(1);
    }
    return h;
}

/* run a complex tone at 'freq' Hz through channel 0 and return output RMS */
static double run_tone(fexchange0_t fexchange0, double freq)
{
    static double in[2 * IN_SIZE], out[2 * IN_SIZE];
    double phase = 0.0, dphi = 2.0 * M_PI * freq / RATE, acc = 0.0;
    long n = 0;
    int b, i, err;
    for (b = 0; b < BLOCKS; b++)
    {
        for (i = 0; i < IN_SIZE; i++)
        {
            in[2 * i + 0] = 0.1 * cos(phase);
            in[2 * i + 1] = 0.1 * sin(phase);
            phase += dphi;
        }
        phase = fmod(phase, 2.0 * M_PI);
        fexchange0(0, in, out, &err);
        if (b >= SETTLE)
            for (i = 0; i < IN_SIZE; i++, n++)
                acc += out[2 * i] * out[2 * i] + out[2 * i + 1] * out[2 * i + 1];
    }
    return sqrt(acc / (double)n);
}

int main(int argc, char **argv)
{
    void *wdsp, *cm, *pa;
    double pass, reject, rejection_db;
    int failures = 0;

    if (argc < 4)
    {
        fprintf(stderr, "usage: %s <libwdsp.so> <libChannelMaster.so> <libPA19.so>\n", argv[0]);
        return 2;
    }
    wdsp = load(argv[1]);
    pa   = load(argv[3]);
    cm   = load(argv[2]);

    printf("wdsp version %d, ChannelMaster version %d\n",
           ((GetWDSPVersion_t)need(wdsp, "GetWDSPVersion"))(),
           ((GetCMVersion_t)need(cm, "GetCMVersion"))());

    /* --- wdsp receive chain ---------------------------------------------- */
    {
        OpenChannel_t OpenChannel = (OpenChannel_t)need(wdsp, "OpenChannel");
        CloseChannel_t CloseChannel = (CloseChannel_t)need(wdsp, "CloseChannel");
        fexchange0_t fexchange0 = (fexchange0_t)need(wdsp, "fexchange0");
        SetRXAMode_t SetRXAMode = (SetRXAMode_t)need(wdsp, "SetRXAMode");
        SetRXABandpassFreqs_t SetRXABandpassFreqs = (SetRXABandpassFreqs_t)need(wdsp, "SetRXABandpassFreqs");
        SetRXAAGCMode_t SetRXAAGCMode = (SetRXAAGCMode_t)need(wdsp, "SetRXAAGCMode");

        /* type 0 = RX, state 1 = running, bfo 1 = block until output is ready */
        OpenChannel(0, IN_SIZE, IN_SIZE, RATE, RATE, RATE, 0, 1, 0.010, 0.025, 0.0, 0.010, 1);
        SetRXAMode(0, 1);                          /* USB */
        SetRXABandpassFreqs(0, 200.0, 3000.0);
        SetRXAAGCMode(0, 0);                       /* AGC off: fixed gain */

        pass   = run_tone(fexchange0, +1000.0);    /* upper sideband: in passband */
        reject = run_tone(fexchange0, -1000.0);    /* lower sideband: must be rejected */
        CloseChannel(0);

        rejection_db = 20.0 * log10(pass / (reject > 1e-15 ? reject : 1e-15));
        printf("USB passband RMS %.3e, opposite sideband RMS %.3e, rejection %.1f dB\n",
               pass, reject, rejection_db);
        if (!(pass > 1e-4))       { fprintf(stderr, "FAIL: no output in passband\n"); failures++; }
        if (!(rejection_db > 60)) { fprintf(stderr, "FAIL: sideband rejection too low\n"); failures++; }
    }

    /* --- PortAudio fork ---------------------------------------------------- */
    {
        PA_Initialize_t PA_Initialize = (PA_Initialize_t)need(pa, "PA_Initialize");
        PA_Terminate_t PA_Terminate = (PA_Terminate_t)need(pa, "PA_Terminate");
        PA_GetDeviceCount_t PA_GetDeviceCount = (PA_GetDeviceCount_t)need(pa, "PA_GetDeviceCount");
        PA_GetVersionText_t PA_GetVersionText = (PA_GetVersionText_t)need(pa, "PA_GetVersionText");
        PA_GetSampleSize_t PA_GetSampleSize = (PA_GetSampleSize_t)need(pa, "PA_GetSampleSize");
        int rc = PA_Initialize();
        printf("%s: init %d, %d device(s)\n", PA_GetVersionText(), rc, rc == 0 ? PA_GetDeviceCount() : -1);
        if (rc != 0) { fprintf(stderr, "FAIL: PA_Initialize returned %d\n", rc); failures++; }
        /* Thetis' fork renumbers the formats: paFloat64 == 0x1 and is 8 bytes */
        if (PA_GetSampleSize(0x1) != 8) { fprintf(stderr, "FAIL: paFloat64 is not 8 bytes\n"); failures++; }
        if (rc == 0) PA_Terminate();
    }

    if (failures)
    {
        fprintf(stderr, "smoketest: %d failure(s)\n", failures);
        return 1;
    }
    printf("smoketest: all checks passed\n");
    return 0;
}
