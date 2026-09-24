/*  extnr.c

This file is part of a program that implements a Software-Defined Radio.

External noise reduction in the RXA chain; see extnr.h.  The structure
follows rnnr.c (NR3): the filter works on the real part of the demodulated
audio and the result is written back as real audio.

A filter that cannot run at the current DSP rate (RN2 and DFNR need 48 kHz,
so not FM's 192 kHz) is not created, and the audio passes unchanged.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

#include "comm.h"

static extnr_create_fn  ext_create  = NULL;
static extnr_destroy_fn ext_destroy = NULL;
static extnr_process_fn ext_process = NULL;
static extnr_param_fn   ext_param   = NULL;

// Create a filter for the current type and rate, with the stored settings
static void* build_filter (EXTNR a)
{
	void* f;
	int i;
	if (a->run == 0 || !ext_create) return NULL;
	f = ext_create (a->run, a->rate);
	if (f && ext_param)
		for (i = 0; i < a->nparams; i++)
			ext_param (f, a->param_id[i], a->param_value[i]);
	return f;
}

// Replace the running filter; the old one is destroyed outside the lock
static void swap_filter (EXTNR a, void* f)
{
	void* old;
	EnterCriticalSection (&a->cs);
	old = a->filter;
	a->filter = f;
	LeaveCriticalSection (&a->cs);
	if (old && ext_destroy) ext_destroy (old);
}

static void alloc_buffers (EXTNR a)
{
	a->fin  = (float*) malloc0 (a->size * sizeof (float));
	a->fout = (float*) malloc0 (a->size * sizeof (float));
}

EXTNR create_extnr (int run, int position, int size, double* in, double* out, int rate)
{
	EXTNR a = (EXTNR) malloc0 (sizeof (extnr));
	InitializeCriticalSectionAndSpinCount (&a->cs, 2500);
	a->run = run;
	a->position = position;
	a->size = size;
	a->in = in;
	a->out = out;
	a->rate = rate;
	alloc_buffers (a);
	a->filter = build_filter (a);
	return a;
}

void destroy_extnr (EXTNR a)
{
	swap_filter (a, NULL);
	DeleteCriticalSection (&a->cs);
	_aligned_free (a->fin);
	_aligned_free (a->fout);
	_aligned_free (a);
}

void flush_extnr (EXTNR a)
{
	(void)a;			// the filters carry their own history; they settle within a frame
}

void xextnr (EXTNR a, int pos)
{
	int i;
	if (a->run && pos == a->position)
	{
		EnterCriticalSection (&a->cs);
		if (a->filter && ext_process)
		{
			for (i = 0; i < a->size; i++)
				a->fin[i] = (float)a->in[2 * i + 0];
			ext_process (a->filter, a->fin, a->fout, a->size);
			for (i = 0; i < a->size; i++)
			{
				a->out[2 * i + 0] = (double)a->fout[i];
				a->out[2 * i + 1] = 0.0;
			}
			LeaveCriticalSection (&a->cs);
			return;
		}
		LeaveCriticalSection (&a->cs);
	}
	if (a->out != a->in)
		memcpy (a->out, a->in, a->size * sizeof (complex));
}

void setBuffers_extnr (EXTNR a, double* in, double* out)
{
	a->in = in;
	a->out = out;
}

void setSamplerate_extnr (EXTNR a, int rate)
{
	if (a->rate == rate) return;
	a->rate = rate;
	swap_filter (a, NULL);
	swap_filter (a, build_filter (a));
}

void setSize_extnr (EXTNR a, int size)
{
	EnterCriticalSection (&a->cs);
	_aligned_free (a->fin);
	_aligned_free (a->fout);
	a->size = size;
	alloc_buffers (a);
	LeaveCriticalSection (&a->cs);
}

int getRun_extnr (EXTNR a)
{
	return a->run != 0;
}

/********************************************************************************************************
*																										*
*											RXA Properties												*
*																										*
********************************************************************************************************/

PORT
void SetExtNRFunctions (extnr_create_fn create, extnr_destroy_fn destroy,
	extnr_process_fn process, extnr_param_fn param)
{
	ext_create  = create;
	ext_destroy = destroy;
	ext_process = process;
	ext_param   = param;
}

PORT
void SetRXAExtNRRun (int channel, int type)
{
	EXTNR a = rxa[channel].extnr.p;
	void* f;
	if (a->run == type) return;
	// build the new filter first (loading a model can take a moment); the
	// running audio keeps the old one until the swap
	a->run = type;
	f = build_filter (a);
	RXAbp1Check (channel, rxa[channel].amd.p->run, rxa[channel].snba.p->run,
		rxa[channel].emnr.p->run, getRun_nnr(rxa[channel].nnr.p), rxa[channel].anf.p->run, rxa[channel].anr.p->run,
		rxa[channel].rnnr.p->run, rxa[channel].sbnr.p->run);
	EnterCriticalSection (&ch[channel].csDSP);
	swap_filter (a, f);
	RXAbp1Set (channel);
	LeaveCriticalSection (&ch[channel].csDSP);
}

PORT
void SetRXAExtNRPosition (int channel, int position)
{
	EnterCriticalSection (&ch[channel].csDSP);
	rxa[channel].extnr.p->position = position;
	LeaveCriticalSection (&ch[channel].csDSP);
}

PORT
void SetRXAExtNRParam (int channel, int param, double value)
{
	EXTNR a = rxa[channel].extnr.p;
	int i;
	EnterCriticalSection (&a->cs);
	for (i = 0; i < a->nparams && a->param_id[i] != param; i++);
	if (i < EXTNR_MAX_PARAMS)
	{
		a->param_id[i] = param;
		a->param_value[i] = value;
		if (i == a->nparams) a->nparams++;
	}
	if (a->filter && ext_param)
		ext_param (a->filter, param, value);
	LeaveCriticalSection (&a->cs);
}

PORT
int GetRXAExtNRActive (int channel)
{
	EXTNR a = rxa[channel].extnr.p;
	int active;
	EnterCriticalSection (&a->cs);
	active = a->filter != NULL;
	LeaveCriticalSection (&a->cs);
	return active;
}
