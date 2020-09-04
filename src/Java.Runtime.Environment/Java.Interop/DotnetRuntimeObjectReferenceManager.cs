using System;
using System.Collections.Generic;
using System.Text;

namespace Java.Interop
{
	class DotnetRuntimeObjectReferenceManager : JniRuntime.JniObjectReferenceManager
	{
		int globalCount = 0;
		int weakGlobalCount = 0;
		public override int GlobalReferenceCount { get { return globalCount; } }

		public override int WeakGlobalReferenceCount { get { return weakGlobalCount; } }
	}
}
