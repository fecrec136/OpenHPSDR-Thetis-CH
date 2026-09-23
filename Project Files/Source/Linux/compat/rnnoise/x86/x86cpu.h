/* Linux stand-in for rnnoise's src/x86/x86cpu.h (not bundled).
   Only needed for run-time CPU dispatch (RNN_ENABLE_X86_RTCD), which this
   build does not use: the SSE2/AVX kernels are selected at compile time. */
#ifndef X86CPU_H
#define X86CPU_H
#endif
