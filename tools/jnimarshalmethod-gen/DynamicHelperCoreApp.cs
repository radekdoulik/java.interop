using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

using Mono.Cecil;

namespace Xamarin.Android.Tools.JniMarshalMethodGenerator
{
	internal class DynamicHelperCoreApp : DynamicHelper
	{
		override public Type [] Locals { get; }
		override public Type [] Parameters { get; }
		override public Type ReturnType { get; }
		override public ExceptionInfo [] Exceptions { get; }
		override public int MaxStackSize { get; }

		Delegate compiled;
		DynamicMethod dynamicMethod;
		public ILGenerator Generator { get; }
		object dynamicScope;
		SignatureHelper signatureHelper;

		static readonly Type dynamicScopeType = Type.GetType ("System.Reflection.Emit.DynamicScope", true);
		static readonly PropertyInfo indexer = dynamicScopeType.GetProperty ("Item", BindingFlags.Instance | BindingFlags.NonPublic);
		static readonly MethodInfo getTypeFromHandleMethod = typeof (Type).GetMethod ("GetTypeFromHandleUnsafe", BindingFlags.Static | BindingFlags.NonPublic);
		static readonly Type genericFieldInfoType = Type.GetType ("System.Reflection.Emit.GenericFieldInfo", true);
		static readonly FieldInfo genericFieldHandle = genericFieldInfoType.GetField ("m_fieldHandle", BindingFlags.Instance | BindingFlags.NonPublic);

		static readonly FieldInfo genericFieldContext = genericFieldInfoType.GetField ("m_context", BindingFlags.Instance | BindingFlags.NonPublic);
		static readonly FieldInfo generatorExceptionCount = typeof (ILGenerator).GetField ("m_exceptionCount", BindingFlags.Instance | BindingFlags.NonPublic);
		static readonly FieldInfo generatorExceptions = typeof (ILGenerator).GetField ("m_exceptions", BindingFlags.Instance | BindingFlags.NonPublic);
		static readonly MethodInfo generatorGetMaxStackSize = typeof (ILGenerator).GetMethod ("GetMaxStackSize", BindingFlags.Instance | BindingFlags.NonPublic);

		public DynamicHelperCoreApp (Delegate compiled)
		{
			this.compiled = compiled;

			Console.WriteLine ($"module handle: {compiled.GetMethodInfo ().Module.ModuleHandle}");

			dynamicMethod = compiled.Method.GetType ().GetField ("m_owner", BindingFlags.Instance | BindingFlags.NonPublic).GetValue (compiled.Method) as DynamicMethod;
			Parameters = typeof (DynamicMethod).GetField ("m_parameterTypes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue (dynamicMethod) as Type [];
			ReturnType = typeof (DynamicMethod).GetField ("m_returnType", BindingFlags.Instance | BindingFlags.NonPublic).GetValue (dynamicMethod) as Type;
			Console.WriteLine ($"rodo: {dynamicMethod}");
			//Console.WriteLine ($"rodo: module {dynamicMethod.GetType ().GetField ("m_module", BindingFlags.Instance | BindingFlags.NonPublic).GetValue (dynamicMethod) as Module}");
			//module = dynamicMethod.GetType ().GetField ("m_module", BindingFlags.Instance | BindingFlags.NonPublic).GetValue (dynamicMethod) as Module;
			//module = dynamicMethod.GetDynamicMethodsModule ();
			Generator = dynamicMethod.GetILGenerator ();

			//var parameters = compiled.GetMethodInfo ().GetParameters ();
			//var par0 = parameters.Length > 0 ? parameters [0].ParameterType : null;
			Console.WriteLine ($"rodo: {Generator} parameters: {Parameters.Length} ret: {ReturnType} par0: {Parameters [1].FullName} :: {Type.GetTypeFromHandle (Parameters [0].TypeHandle)}");


			dynamicScope = Generator.GetType ().GetField ("m_scope", BindingFlags.Instance | BindingFlags.NonPublic).GetValue (Generator);
			signatureHelper = Generator.GetType ().BaseType.GetField ("m_localSignature", BindingFlags.Instance | BindingFlags.NonPublic).GetValue (Generator) as SignatureHelper;
			LocalCount = (int) Generator.GetType ().BaseType.GetField ("m_localCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue (Generator);
			var exceptionsCount = (int) generatorExceptionCount.GetValue (Generator);
			var exceptionsArray = generatorExceptions.GetValue (Generator) as object [];

			Exceptions = new ExceptionInfo [exceptionsCount];
			for (int i = 0; i < exceptionsCount; i++)
				Exceptions [i] = new ExceptionInfo (exceptionsArray [i]);

			MaxStackSize = (int) generatorGetMaxStackSize.Invoke (Generator, null);

			Locals = ParseLocalSignature ();

			Console.WriteLine ($"g: {Generator} dynamicScope: {dynamicScope} indexer: {indexer} i: {dynamicScopeType.GetProperty ("Item", BindingFlags.Instance | BindingFlags.NonPublic)}");
		}

		int ParseData ()
		{
			var b0 = signature [sigIndex++];
			if (b0 <= 0x7f)
				return (int) b0;

			var b1 = signature [sigIndex++];
			if ((b0 & 0x40) == 0)
				return ((int) (b0 & 0x3f) << 8) | b1;

			var b2 = signature [sigIndex++];
			var b3 = signature [sigIndex++];
			if ((b0 & 0x20) == 0)
				return (((int) b0 & 0x1f) << 24) | ((int) b1 << 16) | ((int) b2 << 8) | b3;

			throw new NotSupportedException ("Unexpected format when parsing signature data");
		}

		byte [] signature;
		int sigIndex = 0;

		IntPtr ParseIntPtr ()
		{
			if (IntPtr.Size == 4) {
				var ptr32 = new IntPtr (BitConverter.ToInt32 (signature, sigIndex));
				sigIndex += 4;

				return ptr32;
			} else if (IntPtr.Size == 8) {
				var ptr64 = new IntPtr (BitConverter.ToInt64 (signature, sigIndex));
				sigIndex += 8;

				return ptr64;
			}

			throw new NotSupportedException ("Unknown IntPtr size");
		}

		Type ParseLocal ()
		{
			var elementType = signature [sigIndex++];
			Console.WriteLine ($"rodo: et 0x{elementType:X}");

			switch (elementType) {
				case 0x21:
					var ptr = ParseIntPtr ();
					Console.WriteLine ($"internal type handle ptr: {ptr.ToInt64 ():X} mi: {getTypeFromHandleMethod}");
					System.Threading.Thread.Sleep (10000);
					var type = getTypeFromHandleMethod.Invoke (null, new object [] { ptr }) as Type;
					Console.WriteLine ($"internal type: {type}");
					return type;
				case 0x02:
					Console.WriteLine ("type: bool");
					return typeof (bool);
				case 0x08:
					Console.WriteLine ("type: I4");
					return typeof (Int32);
				case 0x0a:
					Console.WriteLine ("type: I8");
					return typeof (Int64);
				case 0x0e:
					Console.WriteLine ("type: string");
					return typeof (string);
				case 0x18:
					Console.WriteLine ("type: IntPtr");
					return typeof (IntPtr);
				default:
					throw new NotImplementedException ();
			}

			throw new NotSupportedException ("Unexpected format when parsing signature data");
		}

		Type[] ParseLocalSignature ()
		{
			if (LocalCount <= 0)
				return null;

			signature = signatureHelper.GetSignature ();

			Console.WriteLine ("signature bytes:");
			for (int off = 0; off < signature.Length; off++)
				Console.Write ($" 0x{signature [off]:X}");
			Console.WriteLine ();

			sigIndex = 0;
			if (signature [sigIndex++] != 7)
				return null;

			var count = ParseData ();
			if (count != LocalCount)
				throw new InvalidDataException ("decoded locals count differ");

			Console.WriteLine ($"rodo: locals count {count}");

			var localVars = new Type [count];
			for (int i = 0; i < count; i++) {
				localVars [i] = ParseLocal ();
			}

			return localVars;
		}

		object this [int token] => indexer.GetValue (dynamicScope, new object [] { token });

		override public MethodBase ResolveMethod (int token)
		{
			var item = this [token];

			Console.WriteLine ($"rodo: item {item}");

			return MethodBase.GetMethodFromHandle ((RuntimeMethodHandle) item);
		}

		override public Type ResolveType (int token)
		{
			var item = this [token];

			Console.WriteLine ($"rodo: item {item}");

			return Type.GetTypeFromHandle ((RuntimeTypeHandle) item);
		}

		override public IMetadataTokenProvider ResolveToken (int token, AssemblyDefinition assembly)
		{
			var item = this [token];

			Console.WriteLine ($"rodo: token item {item}");

			if ((token & 0x7000000) != 0) {
				var type = ResolveType (token);
				return assembly.MainModule.ImportReference (type);
			}

			throw new NotSupportedException ($"Unsupported token: 0x{token:X}");
		}

		override public FieldInfo ResolveField (int token)
		{
			var item = this [token];

			Console.WriteLine ($"rodo: item {item} of type: {item.GetType ()}");

			if (item.GetType ().IsAssignableFrom (genericFieldInfoType))
				return FieldInfo.GetFieldFromHandle ((RuntimeFieldHandle) genericFieldHandle.GetValue (item), (RuntimeTypeHandle) genericFieldContext.GetValue (item));

			return FieldInfo.GetFieldFromHandle ((RuntimeFieldHandle) item);
		}

		override public string ResolveString (int token)
		{
			var item = this [token];

			Console.WriteLine ($"rodo: string token {token} item {item} of type: {item.GetType ()}");

			return item as string;
		}

		public int LocalCount { get; }

		public Type GetLocalType (int idx)
		{
			return Locals [idx];
		}

		override public byte [] GetILCode ()
		{
			var bakeMethod = typeof (ILGenerator).GetMethod ("BakeByteArray", BindingFlags.Instance | BindingFlags.NonPublic);
			try {
				return (byte []) bakeMethod.Invoke (Generator, null);
			} catch (TargetInvocationException) {
				return typeof (ILGenerator).BaseType.GetField ("m_ILStream", BindingFlags.Instance | BindingFlags.NonPublic).GetValue (Generator) as byte [];
			}
		}
	}
}
