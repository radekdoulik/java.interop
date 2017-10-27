﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Text;

namespace Java.Interop {

	[JniTypeSignature (JniTypeName)]
	/* static */ sealed class ManagedPeer : JavaObject {

		internal const string JniTypeName = "com/xamarin/java_interop/ManagedPeer";


		static  readonly    JniPeerMembers  _members        = new JniPeerMembers (JniTypeName, typeof (ManagedPeer));

		static ManagedPeer ()
		{
			_members.JniPeerType.RegisterNativeMethods (
					new JniNativeMethodRegistration (
						"construct",
						ConstructSignature,
						(Action<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>) Construct),
					new JniNativeMethodRegistration (
						"registerNativeMembers",
						RegisterNativeMembersSignature,
						(Action<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>) RegisterNativeMembers)
			);
		}

		ManagedPeer ()
		{
		}

		internal static void Init ()
		{
			// Present so that JniRuntime has _something_ to reference to
			// prompt invocation of the static constructor & registration
		}

		public override JniPeerMembers JniPeerMembers {
			get {return _members;}
		}

		const string ConstructSignature = "(Ljava/lang/Object;Ljava/lang/String;Ljava/lang/String;[Ljava/lang/Object;)V";

		// TODO: Keep in sync with the code generated by ExportedMemberBuilder
		static void Construct (
				IntPtr jnienv,
				IntPtr klass,
				IntPtr n_self,
				IntPtr n_assemblyQualifiedName,
				IntPtr n_constructorSignature,
				IntPtr n_constructorArguments)
		{
			var envp = new JniTransition (jnienv);
			try {
				var runtime = JniEnvironment.Runtime;
				var r_self  = new JniObjectReference (n_self);
				var self    = runtime.ValueManager.PeekPeer (r_self);
				if (self != null) {
					var state   = self.JniManagedPeerState;
					if ((state & JniManagedPeerStates.Activatable) != JniManagedPeerStates.Activatable &&
							(state & JniManagedPeerStates.Replaceable) != JniManagedPeerStates.Replaceable) {
						return;
					}
				}

				if (JniEnvironment.WithinNewObjectScope) {
					if (runtime.ObjectReferenceManager.LogGlobalReferenceMessages) {
						runtime.ObjectReferenceManager.WriteGlobalReferenceLine (
								"Warning: Skipping managed constructor invocation for PeerReference={0} IdentityHashCode=0x{1} Java.Type={2}. " +
								"Please use JniPeerMembers.InstanceMethods.StartCreateInstance() + JniPeerMembers.InstanceMethods.FinishCreateInstance() instead of " +
								"JniEnvironment.Object.NewObject().",
								r_self,
								runtime.ValueManager.GetJniIdentityHashCode (r_self).ToString ("x"),
								JniEnvironment.Types.GetJniTypeNameFromInstance (r_self));
					}
					return;
				}

				var type    = Type.GetType (JniEnvironment.Strings.ToString (n_assemblyQualifiedName), throwOnError: true);
				var info    = type.GetTypeInfo ();
				if (info.IsGenericTypeDefinition) {
					throw new NotSupportedException (
							"Constructing instances of generic types from Java is not supported, as the type parameters cannot be determined.",
							CreateJniLocationException ());
				}

				var ptypes  = GetParameterTypes (JniEnvironment.Strings.ToString (n_constructorSignature));
				var pvalues = GetValues (runtime, new JniObjectReference (n_constructorArguments), ptypes);
				var ctor    = info.DeclaredConstructors
					.FirstOrDefault (c => !c.IsStatic &&
						c.GetParameters ().Select (p => p.ParameterType).SequenceEqual (ptypes));
				if (ctor == null) {
					throw CreateMissingConstructorException (type, ptypes);
				}
				if (self != null) {
					ctor.Invoke (self, pvalues);
					return;
				}

				try {
					var f = JniEnvironment.Runtime.MarshalMemberBuilder.CreateConstructActivationPeerFunc (ctor);
					f (ctor, new JniObjectReference (n_self), pvalues);
				}
				catch (Exception e) {
					var m = string.Format ("Could not activate {{ PeerReference={0} IdentityHashCode=0x{1} Java.Type={2} }} for managed type '{3}'.",
							r_self,
							runtime.ValueManager.GetJniIdentityHashCode (r_self).ToString ("x"),
							JniEnvironment.Types.GetJniTypeNameFromInstance (r_self),
							type.FullName);
					Debug.WriteLine (m);

					throw new NotSupportedException (m, e);
				}
			}
			catch (Exception e) when (JniEnvironment.Runtime.ExceptionShouldTransitionToJni (e)) {
				envp.SetPendingException (e);
			}
			finally {
				envp.Dispose ();
			}
		}

		static Exception CreateJniLocationException ()
		{
			using (var e = new JavaException ()) {
				return new JniLocationException (e.ToString ());
			}
		}

		static Exception CreateMissingConstructorException (Type type, Type[] ptypes)
		{
			var message = new StringBuilder ();
			message.Append ("Unable to find constructor ");
			message.Append (type.FullName);
			message.Append ("(");
			if (ptypes.Length > 0) {
				message.Append (ptypes [0].FullName);
				for (int i = 1; i < ptypes.Length; ++i)
					message.Append (", ").Append (ptypes [i].FullName);
			}
			message.Append (")");
			message.Append (". Please provide the missing constructor.");
			return new NotSupportedException (message.ToString (), CreateJniLocationException ());
		}

		static Type[] GetParameterTypes (string signature)
		{
			if (string.IsNullOrEmpty (signature))
				return new Type[0];
			var typeNames   = signature.Split (':');
			var ptypes      = new Type [typeNames.Length];
			for (int i = 0; i < typeNames.Length; i++)
				ptypes [i] = Type.GetType (typeNames [i], throwOnError:true);
			return ptypes;
		}

		static object[] GetValues (JniRuntime runtime, JniObjectReference values, Type[] types)
		{
			if (!values.IsValid)
				return null;

			int len = JniEnvironment.Arrays.GetArrayLength (values);
			Debug.Assert (len == types.Length,
					string.Format ("Unexpected number of parameter types! Expected {0}, got {1}", types.Length, len));
			var pvalues = new object [types.Length];
			for (int i = 0; i < pvalues.Length; ++i) {
				var n_value = JniEnvironment.Arrays.GetObjectArrayElement (values, i);
				var value   = runtime.ValueManager.GetValue (ref n_value, JniObjectReferenceOptions.CopyAndDispose, types [i]);
				pvalues [i] = value;
			}

			return pvalues;
		}

		const   string  RegisterNativeMembersSignature  = "(Ljava/lang/Class;Ljava/lang/String;Ljava/lang/String;)V";

		static void RegisterNativeMembers (
				IntPtr jnienv,
				IntPtr klass,
				IntPtr n_nativeClass,
				IntPtr n_assemblyQualifiedName,
				IntPtr n_methods)
		{
			var envp = new JniTransition (jnienv);
			try {
				var r_nativeClass   = new JniObjectReference (n_nativeClass);
				var nativeClass     = new JniType (ref r_nativeClass, JniObjectReferenceOptions.Copy);

				var assemblyQualifiedName   = JniEnvironment.Strings.ToString (new JniObjectReference (n_assemblyQualifiedName));
				var methods                 = JniEnvironment.Strings.ToString (new JniObjectReference (n_methods));

				var type    = Type.GetType (assemblyQualifiedName, throwOnError: true);

				JniEnvironment.Runtime.TypeManager.RegisterNativeMembers (nativeClass, type, methods);
			}
			catch (Exception e) when (JniEnvironment.Runtime.ExceptionShouldTransitionToJni (e)) {
				Debug.WriteLine (e.ToString ());
				envp.SetPendingException (e);
			}
			finally {
				envp.Dispose ();
			}
		}
	}

	sealed class JniLocationException : Exception {

		string stackTrace;

		public JniLocationException (string stackTrace)
		{
			this.stackTrace = stackTrace;
		}

		public override string StackTrace {
			get {
				return stackTrace;
			}
		}
	}
}

