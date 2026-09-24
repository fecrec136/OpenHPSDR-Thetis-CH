/*  pscompat.c

This file is part of a program that implements a Software-Defined Radio.

PureSignal entry points that the Thetis console (PSForm.cs) calls and that
WDSP 2.10 no longer provides.  WDSP 2.10 rewrote the PureSignal calibration
(calcc.c): it has no pin/map/stabilize modes, no phase tolerance and chooses
its own intervals and samples per interval, so those settings are accepted
and ignored here.  psccF(), the float form of pscc(), is kept working.

GetPSDisp: 2.10 changed it to return correction curves (11 arguments; here
renamed GetPSDisp2).  The console's AmpView still calls the 7-argument form,
and passing it 7 arguments would make WDSP write through four stray
pointers, so GetPSDisp keeps the old signature: the collected samples (up to
AmpView's 4096) and zeroed coefficient arrays (2.10 has no such
coefficients).

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

#include "comm.h"

/* GetPSDisp (the 1.29 signature) is in calcc.c, which owns struct _calcc */

PORT void SetPSIntsAndSpi (int channel, int ints, int spi) { (void)channel; (void)ints; (void)spi; }
PORT void SetPSMapMode (int channel, int map) { (void)channel; (void)map; }
PORT void SetPSPinMode (int channel, int pin) { (void)channel; (void)pin; }
PORT void SetPSPtol (int channel, double ptol) { (void)channel; (void)ptol; }
PORT void SetPSStabilize (int channel, int stbl) { (void)channel; (void)stbl; }

/* float TX/RX sample buffers (I and Q separately) -> pscc()'s interleaved doubles */
PORT
void psccF (int channel, int size, float *Itxbuff, float *Qtxbuff, float *Irxbuff, float *Qrxbuff, int mox, int solidmox)
{
	int i;
	double* tx = (double*) malloc (size * sizeof (complex));
	double* rx = (double*) malloc (size * sizeof (complex));
	(void)mox; (void)solidmox;			// unused by 1.29's psccF as well
	if (tx && rx)
	{
		for (i = 0; i < size; i++)
		{
			tx[2 * i + 0] = (double)Itxbuff[i];
			tx[2 * i + 1] = (double)Qtxbuff[i];
			rx[2 * i + 0] = (double)Irxbuff[i];
			rx[2 * i + 1] = (double)Qrxbuff[i];
		}
		pscc (channel, size, tx, rx);
	}
	free (tx);
	free (rx);
}
