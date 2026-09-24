/*  aethernr_test.c

Checks libaethernr's four filters on 48 kHz mono audio: white noise with
synthetic voiced speech -- syllables of a 140 Hz harmonic series shaped by
three formants (pure tones are the wrong probe: RN2 and DFNR are trained on
speech and remove steady tones).  Each filter must reduce the noise between
syllables and keep the syllables.

usage: aethernr_test <libaethernr.so> <model dir>

*/

#define _GNU_SOURCE
#include <dlfcn.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define RATE    48000
#define BLOCK   1024            /* like a WDSP dsp_size */
#define SECONDS 12
#define N       (RATE * SECONDS)

typedef void  (*set_dir_t)(const char*);
typedef int   (*avail_t)(int, int);
typedef void* (*create_t)(int, int);
typedef void  (*destroy_t)(void*);
typedef void  (*process_t)(void*, const float*, float*, int);

static float in[N], out[N], clean[N];
static signed char mask[N];

static double lcg(unsigned* s) { *s = *s * 1664525u + 1013904223u; return (*s >> 8) / 16777216.0; }

int main(int argc, char** argv)
{
    void* lib;
    int type, failures = 0;
    static const char* names[] = { "", "NR2", "RN2", "NR4", "DFNR" };
    unsigned seed = 12345;
    if (argc < 3) { fprintf(stderr, "usage: %s <libaethernr.so> <model dir>\n", argv[0]); return 2; }
    lib = dlopen(argv[1], RTLD_NOW);
    if (!lib) { fprintf(stderr, "dlopen: %s\n", dlerror()); return 1; }
    set_dir_t set_dir = (set_dir_t)dlsym(lib, "aethernr_set_model_dir");
    avail_t available = (avail_t)dlsym(lib, "aethernr_available");
    create_t create = (create_t)dlsym(lib, "aethernr_create");
    destroy_t destroy = (destroy_t)dlsym(lib, "aethernr_destroy");
    process_t process = (process_t)dlsym(lib, "aethernr_process");
    set_dir(argv[2]);

    /* speech: syllables with a random vowel (formants), pitch, length and gap,
       at about +7 dB SNR (speech in the syllable cores vs the full-band noise)
       over -26 dBFS noise.  'clean' keeps the speech alone
       and 'mask' marks each syllable's middle (away from the edges and the
       filters' latency) or the middle of each gap. */
    {
        static const double vowels[5][3] = {
            { 730, 1090, 2440 }, { 530, 1840, 2480 }, { 270, 2290, 3010 },
            { 570, 840, 2410 }, { 300, 870, 2240 } };
        const double fbw[3] = { 90.0, 110.0, 170.0 };
        const double speech_gain = getenv("AETHERNR_TEST_SPEECH_GAIN") ? atof(getenv("AETHERNR_TEST_SPEECH_GAIN")) : 3.0;
        int i = 0;
        double phase = 0.0;
        while (i < N)
        {
            int len = RATE * (150 + (int)(lcg(&seed) * 200)) / 1000;
            int gap = RATE * (150 + (int)(lcg(&seed) * 250)) / 1000;
            const double* F = vowels[(int)(lcg(&seed) * 5) % 5];
            double f0 = 110.0 + lcg(&seed) * 80.0;
            for (int k = 0; k < len && i < N; k++, i++)
            {
                double env = sin(M_PI * k / len);
                double f = f0 * (1.0 + 0.08 * sin(M_PI * k / len));        /* pitch glide */
                double v = 0.0;
                phase += 2.0 * M_PI * f / RATE;
                for (int h = 1; h * f < 3800.0; h++)
                {
                    double fh = h * f, a = 0.0;
                    for (int m = 0; m < 3; m++) a += 1.0 / (1.0 + pow((fh - F[m]) / fbw[m], 2.0));
                    v += a * sin(h * phase) / h;
                }
                clean[i] = (float)(speech_gain * 0.12 * env * v);
                mask[i] = (k > len / 4 && k < 3 * len / 4) ? 1 : 0;
            }
            for (int k = 0; k < gap && i < N; k++, i++)
            {
                clean[i] = 0.0f;
                mask[i] = (k > RATE / 10 && k < gap - RATE / 20) ? -1 : 0;
            }
        }
        for (i = 0; i < N; i++)
            in[i] = clean[i] + (float)((lcg(&seed) - 0.5) * 0.05 * sqrt(12.0));
    }

    for (type = 1; type <= 4; type++)
    {
        if (!available(type, RATE))
        {
            printf("%-5s not available%s\n", names[type], type == 4 ? " (built without DFNR?)" : "");
            if (type != 4) failures++;
            continue;
        }
        void* f = create(type, RATE);
        if (!f) { printf("%-5s create failed\n", names[type]); failures++; continue; }
        for (int i = 0; i < N; i += BLOCK)
            process(f, in + i, out + i, BLOCK);
        destroy(f);

        /* measure over the second half (the filters have settled) */
        double pin_noise = 0, pout_noise = 0, pclean = 0, pout_speech = 0;
        for (int i = N / 2; i < N; i++)
        {
            if (mask[i] < 0) { pin_noise += in[i] * in[i]; pout_noise += out[i] * out[i]; }
            if (mask[i] > 0) { pclean += clean[i] * clean[i]; pout_speech += out[i] * out[i]; }
        }
        double noise_red = 10 * log10(pin_noise / (pout_noise + 1e-20));
        double burst_keep = 10 * log10(pout_speech / pclean);     /* output vs the clean speech */
        if (type == 1) printf("speech-to-noise %.1f dB\n", 10 * log10((pclean / pin_noise)));
        /* NR4's default reduction is capped at 10 dB; the others go much deeper */
        int ok = noise_red >= (type == 3 ? 5.0 : 15.0) && burst_keep >= -6.0 && burst_keep <= 3.0;
        printf("%-5s noise between syllables reduced %5.1f dB, speech kept at %5.1f dB  %s\n",
               names[type], noise_red, burst_keep, ok ? "ok" : "FAIL");
        if (!ok) failures++;
    }
    printf(failures ? "aethernr_test: %d failure(s)\n" : "aethernr_test: all filters ok\n", failures);
    return failures ? 1 : 0;
}
