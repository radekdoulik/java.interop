#include <stdio.h>
#include "java-interop-mono.h"

#if defined (ANDROID) || defined (DYLIB_MONO)

static struct DylibMono mono;
static int java_interop_mono_initialized = 0;

struct DylibMono*
java_interop_get_dylib (void)
{
	if (!java_interop_mono_initialized)
		java_interop_mono_initialized = monodroid_dylib_mono_init (&mono, NULL);

	return &mono;
}

#endif  /* !defined (ANDROID) && !defined (DYLIB_MONO) */
