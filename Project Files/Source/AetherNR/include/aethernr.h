/*  aethernr.h

This file is part of a program that implements a Software-Defined Radio.

C interface to the AetherSDR noise reduction filters, for use inside the
WDSP receive chain (see wdsp/extnr.c):

  NR2   AetherSDR's spectral noise reduction (an extended port of WDSP's EMNR)
  RN2   RNNoise, with AetherSDR's spectral dry-mix control
  NR4   libspecbleach (AetherSDR's newer snapshot)
  DFNR  DeepFilterNet3 neural noise reduction

Every filter takes and returns mono float audio, normalised to +/-1, in
blocks of any size.  Output has the same length as input; the filters that
work in fixed frames delay the audio by one frame to do so.  RN2 and DFNR
work at 48 kHz only.

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

*/

#ifndef AETHERNR_H
#define AETHERNR_H

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  define AETHERNR_API __declspec(dllexport)
#else
#  define AETHERNR_API __attribute__((visibility("default")))
#endif

enum aethernr_type
{
    AETHERNR_NR2 = 1,
    AETHERNR_RN2 = 2,
    AETHERNR_NR4 = 3,
    AETHERNR_DFNR = 4,
};

enum aethernr_param
{
    /* NR2 (defaults as AetherSDR's Nr2SettingsModel) */
    AETHERNR_NR2_GAIN_METHOD = 100,     /* 0..3, default 2 */
    AETHERNR_NR2_NPE_METHOD,            /* 0..2, default 0 */
    AETHERNR_NR2_AE_FILTER,             /* 0/1, default 1 */
    AETHERNR_NR2_GAIN_MAX,              /* default 1.0 */
    AETHERNR_NR2_GAIN_FLOOR,            /* default 0.0 */
    AETHERNR_NR2_GAIN_SMOOTH,           /* default 0.85 */
    AETHERNR_NR2_QSPP,                  /* default 0.20 */
    AETHERNR_NR2_POST2_RUN,             /* 0/1, default 0 */
    AETHERNR_NR2_POST2_FACTOR,          /* default 0.15 */
    AETHERNR_NR2_POST2_NLEVEL,          /* default 0.15 */
    AETHERNR_NR2_POST2_TAPER_HZ,        /* default 2871 */
    AETHERNR_NR2_POST2_DECAY_S,         /* default 5.0 */

    /* RN2 */
    AETHERNR_RN2_DRY_MIX = 200,         /* 0..0.5, default 0 */

    /* NR4 */
    AETHERNR_NR4_REDUCTION_DB = 300,    /* 0..40, default 10 */
    AETHERNR_NR4_SMOOTHING_PCT,         /* 0..100, default 0 */
    AETHERNR_NR4_WHITENING_PCT,         /* 0..100, default 0 */
    AETHERNR_NR4_ADAPTIVE,              /* 0/1, default 1 */
    AETHERNR_NR4_NOISE_METHOD,          /* 0 SPP-MMSE, 1 Brandt, 2 Martin; default 0 */
    AETHERNR_NR4_MASKING_DEPTH,         /* 0..1, default 0.5 */
    AETHERNR_NR4_SUPPRESSION,           /* 0..1, default 0.5 */

    /* DFNR */
    AETHERNR_DFNR_ATTEN_LIMIT_DB = 400, /* 0..100, default 100 */
    AETHERNR_DFNR_POST_FILTER_BETA,     /* 0..0.3, default 0 */
};

/* Directory holding DeepFilterNet3_onnx.tar.gz (DFNR's model); call before creating DFNR. */
AETHERNR_API void aethernr_set_model_dir(const char* dir);

/* 1 if 'type' can be created at 'rate' (compiled in, model present, rate supported). */
AETHERNR_API int aethernr_available(int type, int rate);

AETHERNR_API void* aethernr_create(int type, int rate);
AETHERNR_API void aethernr_destroy(void* filter);
AETHERNR_API void aethernr_reset(void* filter);
AETHERNR_API void aethernr_set_param(void* filter, int param, double value);
AETHERNR_API void aethernr_process(void* filter, const float* in, float* out, int n);

#ifdef __cplusplus
}
#endif

#endif
