using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Xamarin.Android.Tools.JniMarshalMethodGenerator
{
	internal abstract class DynamicHelper
	{
		abstract public Type [] Locals { get; }
		abstract public Type [] Parameters { get; }
		abstract public Type ReturnType { get; }
		abstract public ExceptionInfo [] Exceptions { get; }
		abstract public int MaxStackSize { get; }

		abstract public byte [] GetILCode ();
		abstract public MethodBase ResolveMethod (int token);
		abstract public Type ResolveType (int token);
		abstract public FieldInfo ResolveField (int token);
		abstract public string ResolveString (int token);
		abstract public IMetadataTokenProvider ResolveToken (int token, AssemblyDefinition assembly);

		public class ExceptionInfo
		{
			static readonly Type exceptionInfoType = Type.GetType ("System.Reflection.Emit.__ExceptionInfo", true);
			static readonly MethodInfo getStartAddress = exceptionInfoType.GetMethod ("GetStartAddress", BindingFlags.Instance | BindingFlags.NonPublic);
			static readonly MethodInfo getEndAddress = exceptionInfoType.GetMethod ("GetEndAddress", BindingFlags.Instance | BindingFlags.NonPublic);
			static readonly MethodInfo getNumberOfCatches = exceptionInfoType.GetMethod ("GetNumberOfCatches", BindingFlags.Instance | BindingFlags.NonPublic);
			static readonly MethodInfo getCatchAddresses = exceptionInfoType.GetMethod ("GetCatchAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
			static readonly MethodInfo getCatchEndAddresses = exceptionInfoType.GetMethod ("GetCatchEndAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
			static readonly MethodInfo getCatchClass = exceptionInfoType.GetMethod ("GetCatchClass", BindingFlags.Instance | BindingFlags.NonPublic);
			static readonly MethodInfo getExceptionTypes = exceptionInfoType.GetMethod ("GetExceptionTypes", BindingFlags.Instance | BindingFlags.NonPublic);

			//object info;

			public int startOffset;
			public int endOffset;
			public int handlersCount;
			public int [] handlerStartOffsets;
			public int [] handlerEndOffsets;
			public Type [] handlerClasses;
			int [] handlerTypes;

			public ExceptionInfo (object info)
			{
				//this.info = info;

				startOffset = (int) getStartAddress.Invoke (info, null);
				endOffset = (int) getEndAddress.Invoke (info, null);

				handlersCount = (int) getNumberOfCatches.Invoke (info, null);
				handlerStartOffsets = (int []) getCatchAddresses.Invoke (info, null);
				handlerEndOffsets = (int []) getCatchEndAddresses.Invoke (info, null);
				handlerClasses = (Type []) getCatchClass.Invoke (info, null);
				handlerTypes = (int []) getExceptionTypes.Invoke (info, null);

				Console.WriteLine ($"exception info start {startOffset} end {endOffset}");
			}

			public ExceptionHandlerType GetType (int index)
			{
				switch (handlerTypes [index]) {
					case 0:
						return ExceptionHandlerType.Catch;
					case 1:
						return ExceptionHandlerType.Filter;
					case 2:
						return ExceptionHandlerType.Finally;
					case 3:
						return ExceptionHandlerType.Fault;
				}

				throw new NotSupportedException ("Unexpected handler type");
			}
		}

	}
}
