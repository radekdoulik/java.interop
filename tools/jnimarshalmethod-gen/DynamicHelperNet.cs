using System;
using System.Reflection;
using System.Reflection.Emit;

using Mono.Cecil;

namespace Xamarin.Android.Tools.JniMarshalMethodGenerator
{
	internal class DynamicHelperNet : DynamicHelper
	{
		override public Type [] Locals { get; }
		override public Type [] Parameters { get; }
		override public Type ReturnType { get; }
		override public ExceptionInfo [] Exceptions { get; }
		override public int MaxStackSize { get; }

		public DynamicHelperNet (MethodBuilder mb)
		{
			var body = mb.GetMethodBody ();
			Locals = new Type [body.LocalVariables.Count];
			for (int i=0; i< Locals.Length; i++) {
				Locals [i] = body.LocalVariables [i].GetType ();
			}
		}

		override public byte [] GetILCode ()
		{
			return null;
		}

		override public MethodBase ResolveMethod (int token)
		{
			return null;
		}

		override public Type ResolveType (int token)
		{
			return null;
		}

		override public FieldInfo ResolveField (int token)
		{
			return null;
		}

		override public string ResolveString (int token)
		{
			return null;
		}

		override public IMetadataTokenProvider ResolveToken (int token, AssemblyDefinition assembly)
		{
			return null;
		}
	}
}
