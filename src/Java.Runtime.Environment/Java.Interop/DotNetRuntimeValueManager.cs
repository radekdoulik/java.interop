using System;
using System.Collections.Generic;

namespace Java.Interop
{
	class DotNetRuntimeValueManager : JniRuntime.JniValueManager
	{
		public override void AddPeer (IJavaPeerable value)
		{
			throw new NotImplementedException ();
		}

		public override void CollectPeers ()
		{
			throw new NotImplementedException ();
		}

		public override void FinalizePeer (IJavaPeerable value)
		{
			throw new NotImplementedException ();
		}

		public override List<JniSurfacedPeerInfo> GetSurfacedPeers ()
		{
			throw new NotImplementedException ();
		}

		public override IJavaPeerable PeekPeer (JniObjectReference reference)
		{
			throw new NotImplementedException ();
		}

		public override void RemovePeer (IJavaPeerable value)
		{
			throw new NotImplementedException ();
		}

		public override void WaitForGCBridgeProcessing ()
		{
			throw new NotImplementedException ();
		}
	}
}
