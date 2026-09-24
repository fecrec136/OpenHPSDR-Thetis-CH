/*  extnr.h

This file is part of a program that implements a Software-Defined Radio.

External noise reduction in the RXA chain: runs a noise reduction filter
supplied by another library (on Linux, libaethernr with AetherSDR's NR2,
RN2, NR4 and DFNR) on the demodulated audio, before or after AGC, in the
same place as NR3/NR4.

The host registers the filter functions once with SetExtNRFunctions(), so
WDSP does not link against the filter library.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

#ifndef _extnr_h
#define _extnr_h

typedef void* (*extnr_create_fn)  (int type, int rate);
typedef void  (*extnr_destroy_fn) (void* filter);
typedef void  (*extnr_process_fn) (void* filter, const float* in, float* out, int n);
typedef void  (*extnr_param_fn)   (void* filter, int param, double value);

#define EXTNR_MAX_PARAMS 64

typedef struct _extnr
{
	int run;							// filter type requested, 0 = off
	int position;						// 0 before AGC, 1 after AGC
	int size;
	double* in;
	double* out;
	int rate;
	void* filter;						// external filter, NULL if off or unavailable
	float* fin;
	float* fout;
	CRITICAL_SECTION cs;				// guards 'filter' against the DSP thread
	int nparams;						// settings, re-applied when the filter is re-created
	int param_id[EXTNR_MAX_PARAMS];
	double param_value[EXTNR_MAX_PARAMS];
} extnr, *EXTNR;

extern EXTNR create_extnr (int run, int position, int size, double* in, double* out, int rate);

extern void destroy_extnr (EXTNR a);

extern void flush_extnr (EXTNR a);

extern void xextnr (EXTNR a, int pos);

extern void setBuffers_extnr (EXTNR a, double* in, double* out);

extern void setSamplerate_extnr (EXTNR a, int rate);

extern void setSize_extnr (EXTNR a, int size);

extern int getRun_extnr (EXTNR a);

// RXA Properties

extern __declspec (dllexport) void SetExtNRFunctions (extnr_create_fn create, extnr_destroy_fn destroy,
	extnr_process_fn process, extnr_param_fn param);

extern __declspec (dllexport) void SetRXAExtNRRun (int channel, int type);

extern __declspec (dllexport) void SetRXAExtNRPosition (int channel, int position);

extern __declspec (dllexport) void SetRXAExtNRParam (int channel, int param, double value);

extern __declspec (dllexport) int GetRXAExtNRActive (int channel);

#endif
