/* Linux shim for <intrin.h> */
#include "win32compat.h"
#if defined(__x86_64__) || defined(__i386__)
#include <x86intrin.h>
#endif
