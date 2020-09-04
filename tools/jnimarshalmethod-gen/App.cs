using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

using Java.Interop;

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Options;
using Mono.Collections.Generic;
using Java.Interop.Tools.Cecil;
using OpCodes = Mono.Cecil.Cil.OpCodes;
using System.Linq;
using System.Collections;

#if _DUMP_REGISTER_NATIVE_MEMBERS
using Mono.Linq.Expressions;
#endif  // _DUMP_REGISTER_NATIVE_MEMBERS

namespace Xamarin.Android.Tools.JniMarshalMethodGenerator {

	class App : MarshalByRefObject
	{

		internal const string Name = "jnimarshalmethod-gen";
		static DirectoryAssemblyResolver resolver = new DirectoryAssemblyResolver (logger: (l, v) => { Console.WriteLine (v); }, loadDebugSymbols: true, loadReaderParameters: new ReaderParameters () { ReadSymbols = true, InMemory = true });
		static readonly TypeDefinitionCache cache = new TypeDefinitionCache ();
		static Dictionary<string, TypeBuilder> definedTypes = new Dictionary<string, TypeBuilder> ();
		static Dictionary<string, TypeDefinition> typeMap = new Dictionary<string, TypeDefinition> ();
		static List<string> references = new List<string> ();
		static public bool Debug;
		static public bool Verbose;
		static bool keepTemporary;
		static bool forceRegeneration;
		static List<Regex> typeNameRegexes = new List<Regex> ();
		static string jvmDllPath;
		List<string> FilesToDelete = new List<string> ();
		static string outDirectory;

		static Assembly CustomResolveHandler (object sender, ResolveEventArgs args)
		{
			Console.WriteLine ($"args name: {args.Name} req: {args.RequestingAssembly}");
			return null;
		}

		public static int Main (string [] args)
		{
			//var domain = AppDomain.CreateDomain ("workspace");
			var app = new App ();

			AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler (CustomResolveHandler);

			app.AddMonoPathToResolverSearchDirectories ();

			var assemblies = app.ProcessArguments (args);
			app.ProcessAssemblies (assemblies);
			var filesToDelete = app.FilesToDelete;

			//AppDomain.Unload (domain);

			foreach (var path in filesToDelete)
				File.Delete (path);

			return 0;
		}

		void AddMonoPathToResolverSearchDirectories ()
		{
			var monoPath = Environment.GetEnvironmentVariable ("MONO_PATH");
			if (string.IsNullOrWhiteSpace (monoPath))
				return;

			foreach (var path in monoPath.Split (new char [] { Path.PathSeparator })) {
				resolver.SearchDirectories.Add (path);

				var facadesDirectory = Path.Combine (path, "Facades");
				if (Directory.Exists (facadesDirectory))
					resolver.SearchDirectories.Add (facadesDirectory);
			}
		}

		List<string> ProcessArguments (string [] args)
		{
			var help = false;
			var options = new OptionSet {
				$"Usage: {Name}.exe OPTIONS* ASSEMBLY+ [@RESPONSE-FILES]",
				"",
				"Generates helper marshaling methods for specified assemblies.",
				"",
				"Copyright 2018 Microsoft Corporation",
				"",
				"Options:",
				{ "d|debug",
				  "Inject debug messages",
				  v => Debug = true },
				{ "f",
				  "Force regeneration of marshal methods",
				  v => forceRegeneration = true },
				{ "jvm=",
				  "{JVM} shared library path.",
				  v => jvmDllPath = v },
				{ "keeptemp",
				  "Keep temporary *-JniMarshalMethod.dll files.",
				  v => keepTemporary = true },
				{ "L=",
				  "{DIRECTORY} to resolve assemblies from.",
				  v => resolver.SearchDirectories.Add (v) },
				{ "h|help|?",
				  "Show this message and exit",
				  v => help = v != null },
				{ "o=",
				  "{DIRECTORY} to write updated assemblies",
				  v => outDirectory = v },
				{ "r|reference=",
				  "Reference {ASSEMBLY} to use. Can be used multiple times.",
				  v => references.Add (v)
				},
				{ "types=",
				  "Generate marshaling methods only for types whose names match regex patterns listed {FILE}.\n" +
				  "One regex pattern per line.\n" +
				  "Empty lines and lines starting with '#' character are ignored as comments.",
				  v => LoadTypes (v) },
				{ "t|type=",
				  "Generate marshaling methods only for types whose names match {TYPE-REGEX}.",
				  v => typeNameRegexes.Add (new Regex (v)) },
				{ "v|verbose",
				  "Output information about progress during the run of the tool",
				  v => Verbose = true },
				new ResponseFileSource(),
			};

			var assemblies = options.Parse (args);
			if (help || args.Length < 1) {
				options.WriteOptionDescriptions (Console.Out);

				Environment.Exit (0);
			}

			if (assemblies.Count < 1) {
				Error ("Please specify at least one ASSEMBLY to process.");
				Environment.Exit (2);
			}

			return assemblies;
		}

		void LoadTypes (string typesPath)
		{
			try {
				foreach (var line in File.ReadLines (typesPath)) {
					if (string.IsNullOrWhiteSpace (line))
						continue;

					if (line [0] == '#')
						continue;

					typeNameRegexes.Add (new Regex (line));
				}
			} catch (Exception e) {
				Error ($"Unable to read profile '{typesPath}'.{Environment.NewLine}{e}");
				Environment.Exit (4);
			}
		}

		void ProcessAssemblies (List<string> assemblies)
		{
			CreateJavaVM (jvmDllPath);

			var readerParameters = new ReaderParameters {
				AssemblyResolver   = resolver,
				InMemory           = true,
				ReadSymbols        = true,
				ReadWrite          = string.IsNullOrEmpty (outDirectory),
			};
			var readerParametersNoSymbols = new ReaderParameters {
				AssemblyResolver   = resolver,
				InMemory           = true,
				ReadSymbols        = false,
				ReadWrite          = string.IsNullOrEmpty (outDirectory),
			};

			foreach (var r in references) {
				try {
					Assembly.LoadFile (Path.GetFullPath (r));
				} catch (Exception) {
					Error ($"Unable to preload reference '{r}'.");
					Environment.Exit (1);
				}
				resolver.SearchDirectories.Add (Path.GetDirectoryName (r));
			}

			foreach (var assembly in assemblies) {
				if (!File.Exists (assembly)) {
					Error ($"Path '{assembly}' does not exist.");
					Environment.Exit (1);
				}

				resolver.SearchDirectories.Add (Path.GetDirectoryName (assembly));
				AssemblyDefinition ad;
				try {
					ad = AssemblyDefinition.ReadAssembly (assembly, readerParameters);
					resolver.AddToCache (ad);
				} catch (Exception) {
					if (Verbose)
						Warning ($"Unable to read assembly '{assembly}' with symbols. Retrying to load it without them.");
					ad = AssemblyDefinition.ReadAssembly (assembly, readerParametersNoSymbols);
					resolver.AddToCache (ad);
				}

				Extensions.MethodMap.Clear ();
			}

			foreach (var assembly in assemblies) {
				try {
					CreateMarshalMethodAssembly (assembly);
					definedTypes.Clear ();
				} catch (Exception e) {
					Error ($"Unable to process assembly '{assembly}'{Environment.NewLine}{e.Message}{Environment.NewLine}{e}");
					Environment.Exit (1);
				}
			}
		}

		void CreateJavaVM (string jvmDllPath)
		{
			var builder = new JreRuntimeOptions {
				JvmLibraryPath  = jvmDllPath,
			};

			try {
				builder.CreateJreVM ();
			} catch (Exception e) {
				Error ($"Unable to create Java VM{Environment.NewLine}{e}");
				Environment.Exit (3);
			}
		}

		static JniRuntime.JniMarshalMemberBuilder CreateExportedMemberBuilder ()
		{
			return JniEnvironment.Runtime.MarshalMemberBuilder;
		}

		static TypeBuilder GetTypeBuilder (ModuleBuilder mb, Type type)
		{
			if (definedTypes.ContainsKey (type.FullName))
				return definedTypes [type.FullName];

			if (type.IsNested) {
				var outer = GetTypeBuilder (mb, type.DeclaringType);
				var nested = outer.DefineNestedType (type.Name, System.Reflection.TypeAttributes.NestedPublic);
				definedTypes [type.FullName] = nested;
				return nested;
			}

			var tb = mb.DefineType (type.FullName, System.Reflection.TypeAttributes.Public);
			definedTypes [type.FullName] = tb;

			return tb;
		}

		class MethodsComparer : IComparer<MethodInfo>
		{
			readonly Type type;
			readonly TypeDefinition td;

			public MethodsComparer (Type type, TypeDefinition td)
			{
				this.type = type;
				this.td = td;
			}

			public int Compare (MethodInfo a, MethodInfo b)
			{
				if (a.DeclaringType != type)
					return 1;

				var atd = td.GetMethodDefinition (a);
				if (atd == null)
					return 1;

				if (b.DeclaringType != type)
					return -1;

				var btd = td.GetMethodDefinition (b);
				if (btd == null)
					return -1;

				if (atd.HasOverrides ^ btd.HasOverrides)
					return btd.HasOverrides ? -1 : 1;

				return string.Compare (a.Name, b.Name);
			}
		}

		static HashSet<string> addedMethods = new HashSet<string> ();

		class ILBuffer
		{
			public byte [] buffer;
			public int position;
			public ILBuffer (byte[] bytes)
			{
				buffer = bytes;
				position = 0;
			}

			public byte ReadByte ()
			{
				return buffer [position++];
			}

			public sbyte ReadInt8 ()
			{
				return (sbyte) buffer [position++];
			}

			public bool End {
				get { return position >= buffer.Length;  }
			}

			public UInt32 ReadUInt32 ()
			{
				Console.WriteLine ($"read token: {buffer [position]:X}, {buffer [position + 1]:X}, {buffer [position+2]:X}, {buffer [position+3]:X}, ");
				return (UInt32) (buffer [position++]
					| (buffer [position++] << 8)
					| (buffer [position++] << 16)
					| (buffer [position++] << 24));
			}

			public Int32 ReadInt32 ()
			{
				Console.WriteLine ($"read token: {buffer [position]:X}, {buffer [position + 1]:X}, {buffer [position+2]:X}, {buffer [position+3]:X}, ");
				return (Int32) (buffer [position++]
					| (buffer [position++] << 8)
					| (buffer [position++] << 16)
					| (buffer [position++] << 24));
			}

			public int ReadToken ()
			{
				return (int) ReadUInt32 ();
			}
		}

		int GetIndexAtOffset (List<(int offset, int index)> offsets, int offset)
		{
			for (int i = 0; i < offsets.Count; i++) {
				//Console.WriteLine ($"i: {i} {offsets [i]}");
				if (offset == offsets [i].offset) {
					return i;
				}
			}

			return -1;
		}

		void ReconstructBody (MethodDefinition method, byte[] ILcode, AssemblyDefinition cas, DynamicHelper resolver)
		{
			var instructions = method.Body.Instructions;
			var locals = method.Body.Variables;
			var parameters = method.Parameters;

			instructions.Clear ();
			locals.Clear ();
			parameters.Clear ();

			for (int i = 0; i < resolver.Locals.Length; i++)
				locals.Add (new VariableDefinition (cas.MainModule.ImportReference (resolver.Locals [i])));

			for (int i = 1; i < resolver.Parameters.Length; i++)
				parameters.Add (new ParameterDefinition (cas.MainModule.ImportReference (resolver.Parameters [i])));

			Console.WriteLine ($"parameters: {parameters.Count}");

			method.ReturnType = cas.MainModule.ImportReference (resolver.ReturnType);

			var buffer = new ILBuffer (ILcode);
			int token;
			MethodBase resolvedMethod;

			Console.WriteLine ("IL bytes:");
			for (int off = 0; off < ILcode.Length; off++)
				Console.Write ($" 0x{ILcode [off]:X}");
			Console.WriteLine ();

			bool retReached = false;
			int index = 0;
			List<(int index, int target)> targets = new List<(int index, int target)> ();
			List<(int offset, int index)> offsets = new List<(int offset, int index)> ();

			while (!buffer.End) {
				byte opcode = buffer.ReadByte ();
				if (opcode != 0)
					Console.WriteLine ($"IL 1st byte 0x{opcode:X}");

				switch (opcode) {
					case 0:
						if (!retReached)
							Console.WriteLine ("warning: 0 IL");
						break;
					case 0x02: // ldarg.0
						Console.WriteLine ("warning: ldarg.0 reached");
						//throw new NotSupportedException ("Unexpected instruction when parsing delegate IL");
						instructions.Add (Instruction.Create (OpCodes.Ldarg_0));
						break;
					case 0x03: // ldarg.1
						instructions.Add (Instruction.Create (OpCodes.Ldarg_0));
						break;
					case 0x04: // ldarg.2
						instructions.Add (Instruction.Create (OpCodes.Ldarg_1));
						break;
					case 0x05: // ldarg.3
						instructions.Add (Instruction.Create (OpCodes.Ldarg_2));
						break;
					case 0x06: // ldloc.0
						instructions.Add (Instruction.Create (OpCodes.Ldloc_0));
						break;
					case 0x07: // ldloc.1
						instructions.Add (Instruction.Create (OpCodes.Ldloc_1));
						break;
					case 0x08: // ldloc.2
						instructions.Add (Instruction.Create (OpCodes.Ldloc_2));
						break;
					case 0x09: // ldloc.3
						instructions.Add (Instruction.Create (OpCodes.Ldloc_3));
						break;
					case 0x0a: // stloc.0
						instructions.Add (Instruction.Create (OpCodes.Stloc_0));
						break;
					case 0x0b: // stloc.1
						instructions.Add (Instruction.Create (OpCodes.Stloc_1));
						break;
					case 0x0c: // stloc.2
						instructions.Add (Instruction.Create (OpCodes.Stloc_2));
						break;
					case 0x0d: // stloc.3
						instructions.Add (Instruction.Create (OpCodes.Stloc_3));
						break;
					case 0x0e: // ldarg.s
						var argIndex = buffer.ReadByte ();
						Console.WriteLine ($"rodo: IL ldarg.s <idx> = {argIndex}");
						if (argIndex == 4)
							instructions.Add (Instruction.Create (OpCodes.Ldarg_3));
						else
							instructions.Add (Instruction.Create (OpCodes.Ldarg_S, parameters [argIndex - 1]));
						break;
					case 0x0f: // ldarga.s
						argIndex = buffer.ReadByte ();
						Console.WriteLine ($"rodo: IL ldarga.s <idx> = {argIndex}");
						if (argIndex == 2)
							instructions.Add (Instruction.Create (OpCodes.Ldarg_1));
						else
							instructions.Add (Instruction.Create (OpCodes.Ldarga_S, parameters [argIndex - 1]));
						break;
					case 0x11: // ldloc.s
						var varIndex = buffer.ReadByte ();
						Console.WriteLine ($"rodo: IL ldloc.s <idx> = {varIndex}");
						instructions.Add (Instruction.Create (OpCodes.Ldloc_S, locals [varIndex]));
						break;
					case 0x12: // ldloca.s
						varIndex = buffer.ReadByte ();
						Console.WriteLine ($"rodo: IL ldloca.s <idx> = {varIndex}");
						instructions.Add (Instruction.Create (OpCodes.Ldloca_S, locals [varIndex]));
						break;
					case 0x13: // stloc.s
						varIndex = buffer.ReadByte ();
						Console.WriteLine ($"rodo: IL stloc.s <idx> = {varIndex}");
						instructions.Add (Instruction.Create (OpCodes.Stloc_S, locals [varIndex]));
						break;
					case 0x16: // ldc.i4.0
						instructions.Add (Instruction.Create (OpCodes.Ldc_I4_0));
						break;
					case 0x17: // ldc.i4.1
						instructions.Add (Instruction.Create (OpCodes.Ldc_I4_1));
						break;
					case 0x18: // ldc.i4.2
						instructions.Add (Instruction.Create (OpCodes.Ldc_I4_2));
						break;
					case 0x19: // ldc.i4.3
						instructions.Add (Instruction.Create (OpCodes.Ldc_I4_3));
						break;
					case 0x1a: // ldc.i4.4
						instructions.Add (Instruction.Create (OpCodes.Ldc_I4_4));
						break;
					case 0x1b: // ldc.i4.5
						instructions.Add (Instruction.Create (OpCodes.Ldc_I4_5));
						break;
					case 0x1c: // ldc.i4.6
						instructions.Add (Instruction.Create (OpCodes.Ldc_I4_6));
						break;
					case 0x1d: // ldc.i4.7
						instructions.Add (Instruction.Create (OpCodes.Ldc_I4_7));
						break;
					case 0x1e: // ldc.i4.8
						instructions.Add (Instruction.Create (OpCodes.Ldc_I4_8));
						break;
					case 0x1f: // ldc.i4.s <int8>
						var int8 = buffer.ReadInt8 ();
						Console.WriteLine ($"rodo: IL ldc.i4.s <int8> = {int8}");
						instructions.Add (Instruction.Create (OpCodes.Ldc_I4_S, int8));
						break;
					case 0x25: // dup
						instructions.Add (Instruction.Create (OpCodes.Dup));
						break;
					case 0x26: // pop
						instructions.Add (Instruction.Create (OpCodes.Pop));
						break;
					case 0x2a: // ret
						instructions.Add (Instruction.Create (OpCodes.Ret));
						retReached = true;
						break;
					case 0x28: // call <method>
						token = buffer.ReadToken ();
						Console.WriteLine ($"rodo: call <method> IL, token {token:X}");
						//resolvedMethod = module.ResolveMethod ((int) token);
						resolvedMethod = resolver.ResolveMethod ((int) token);
						Console.WriteLine ($"rodo: <ctor> IL, token {token:X} method: {resolvedMethod}");
						instructions.Add (Instruction.Create (OpCodes.Call, cas.MainModule.ImportReference (resolvedMethod)));
						break;
					case 0x38: // br <target>
						var target = buffer.ReadInt32 ();
						Console.WriteLine ($"rodo: IL br <target> = {target}");
						instructions.Add (Instruction.Create (OpCodes.Br, instructions [0]));
						targets.Add ((index, target));
						break;
					case 0x3a: // brtrue <target>
						target = buffer.ReadInt32 ();
						Console.WriteLine ($"rodo: IL brtrue <target> = {target}");
						instructions.Add (Instruction.Create (OpCodes.Brtrue, instructions [0]));
						targets.Add ((index, target));
						break;
					case 0x6a: // conv.i8
						instructions.Add (Instruction.Create (OpCodes.Conv_I8));
						break;
					case 0x6f: // callvirt <method>
						token = buffer.ReadToken ();
						Console.WriteLine ($"rodo: call <method> IL, token {token:X}");
						//resolvedMethod = module.ResolveMethod ((int) token);
						resolvedMethod = resolver.ResolveMethod ((int) token);
						Console.WriteLine ($"rodo: <ctor> IL, token {token:X} method: {resolvedMethod}");
						instructions.Add (Instruction.Create (OpCodes.Callvirt, cas.MainModule.ImportReference (resolvedMethod)));
						break;
					case 0x72: // ldstr <string>
						token = buffer.ReadToken ();
						var str = resolver.ResolveString ((int)token);
						Console.WriteLine ($"rodo: ldstr <string> IL, token {token:X} str: {str}");
						instructions.Add (Instruction.Create (OpCodes.Ldstr, str));
						break;
					case 0x73: // newobj <ctor>
						token = buffer.ReadToken ();
						//var ctor = module.ResolveMethod ((int)token);
						var ctor = resolver.ResolveMethod ((int) token);
						Console.WriteLine ($"rodo: <ctor> IL, token {token:X} ctor: {ctor}");
						instructions.Add (Instruction.Create (OpCodes.Newobj, cas.MainModule.ImportReference (ctor)));
						break;
					case 0x74: // castclass <class>
						token = buffer.ReadToken ();
						//var ctor = module.ResolveMethod ((int)token);
						var type = resolver.ResolveType ((int) token);
						Console.WriteLine ($"rodo: castclass, token {token:X} type: {type}");
						instructions.Add (Instruction.Create (OpCodes.Castclass, cas.MainModule.ImportReference (type)));
						break;
					case 0x75: // isinst <class>
						token = buffer.ReadToken ();
						//var ctor = module.ResolveMethod ((int)token);
						type = resolver.ResolveType ((int) token);
						Console.WriteLine ($"rodo: isinst, token {token:X} type: {type}");
						instructions.Add (Instruction.Create (OpCodes.Isinst, cas.MainModule.ImportReference (type)));
						break;
					case 0x7b: // isinst <class>
						token = buffer.ReadToken ();
						//var ctor = module.ResolveMethod ((int)token);
						var field = resolver.ResolveField ((int) token);
						Console.WriteLine ($"rodo: ldfld <field>, token {token:X} field: {field}");
						instructions.Add (Instruction.Create (OpCodes.Ldfld, cas.MainModule.ImportReference (field)));
						break;
					case 0x8d: // newarr <etype>
						token = buffer.ReadToken ();
						//var ctor = module.ResolveMethod ((int)token);
						type = resolver.ResolveType ((int) token);
						Console.WriteLine ($"rodo: newarr <etype>, token {token:X} type: {type}");
						instructions.Add (Instruction.Create (OpCodes.Newarr, cas.MainModule.ImportReference (type)));
						break;
					case 0xa4: // stelem <type>
						token = buffer.ReadToken ();
						//var ctor = module.ResolveMethod ((int)token);
						type = resolver.ResolveType ((int) token);
						Console.WriteLine ($"rodo: stelem <type>, token {token:X} type: {type}");
						instructions.Add (Instruction.Create (OpCodes.Stelem_Any, cas.MainModule.ImportReference (type)));
						break;
					case 0xd0: // ldtoken <token>
						token = buffer.ReadToken ();
						//var ctor = module.ResolveMethod ((int)token);
						var resolvedToken = resolver.ResolveToken ((int) token, cas);
						Console.WriteLine ($"rodo: ldtoken <token>, token {token:X} --> {resolvedToken:X}");

						if (resolvedToken is TypeReference) {
							instructions.Add (Instruction.Create (OpCodes.Ldtoken, resolvedToken as TypeReference));
						} else
							throw new NotSupportedException ($"Unsupported token: {resolvedToken} type: {resolvedToken.GetType ()}");
						break;
					case 0xdc: // endfinally
						instructions.Add (Instruction.Create (OpCodes.Endfinally));
						break;
					case 0xdd: // leave <target>
						target = buffer.ReadInt32 ();
						//var ctor = module.ResolveMethod ((int)token);
						Console.WriteLine ($"rodo: leave <target>: {target}");
						instructions.Add (Instruction.Create (OpCodes.Leave, instructions [0]));
						targets.Add ((index, target));
						break;
					case 0xfe:
						var sb = buffer.ReadByte ();
						Console.WriteLine ($"IL 2nd byte: 0x{sb:X}");
						switch (sb) {
							case 0x11: // endfilter
								instructions.Add (Instruction.Create (OpCodes.Endfilter));
								break;
							case 0x15:
								token = buffer.ReadToken ();
								type = resolver.ResolveType ((int) token);
								Console.WriteLine ($"rodo: initobj token {token:X} type: {type}");
								instructions.Add (Instruction.Create (OpCodes.Initobj, cas.MainModule.ImportReference (type)));
								break;
							default:
								Console.WriteLine ($"rodo: unhandled IL");
								break;
						}
						break;
					default:
						Console.WriteLine ($"rodo: unhandled IL");
						break;
				}

				offsets.Add ((buffer.position, index));

				index++;
			}

			foreach (var t in targets) {
				var instruction = instructions [t.index];
				var pos = offsets [t.index].offset + t.target;
				int targetIndex = GetIndexAtOffset (offsets, pos);


				//for (int i = 0; i < offsets.Count; i++) {
				//	Console.WriteLine ($"i: {i} {offsets [i]}");
				//	if (pos == offsets [i].offset) {
				//		targetIndex = i;
				//		break;
				//	}
				//}

				Console.WriteLine ($"target offset: {pos} index: {targetIndex}");

				if (targetIndex == -1)
					throw new NotSupportedException ("Unexpected format, unable to calculate target offset");

				instruction.Operand = instructions [targetIndex + 1];
			}

			var exceptions = resolver.Exceptions;
			for (int i = 0; i < exceptions.Length; i++) {
				var exc = exceptions [i];
				for (int j = 0; j < exc.handlersCount; j++) {
					var type = exc.handlerClasses [j];
					Console.WriteLine ($"j: {j} type: {exc.GetType (j)} class: {type} start: {GetIndexAtOffset (offsets, exceptions [i].startOffset)} end: {GetIndexAtOffset (offsets, exceptions [i].endOffset)}");

					var handler = new Mono.Cecil.Cil.ExceptionHandler (exc.GetType (j)) {
						TryStart = instructions [GetIndexAtOffset (offsets, exc.startOffset) + 1],
						TryEnd = instructions [GetIndexAtOffset (offsets, exc.endOffset) + 1],
						CatchType = type != null ? cas.MainModule.ImportReference (type) : null
					};

					if (exc.GetType (j) == ExceptionHandlerType.Filter) {
						Console.WriteLine ($"filter: {exc.handlerStartOffsets [j]} - ({exc.handlerEndOffsets [j]})");
						handler.HandlerStart = instructions [GetIndexAtOffset (offsets, exc.handlerStartOffsets [j]) + 1];
						handler.HandlerEnd = instructions [GetIndexAtOffset (offsets, exc.handlerEndOffsets [j]) + 1];
						handler.FilterStart = handler.TryEnd;
					} else if (exc.GetType (j) != ExceptionHandlerType.Catch) {
						Console.WriteLine ($"{exc.GetType (j)}: {exc.handlerStartOffsets [j]} - {exc.handlerEndOffsets [j]}");
						handler.HandlerStart = instructions [GetIndexAtOffset (offsets, exc.handlerStartOffsets [j]) + 1];
						handler.HandlerEnd = instructions [GetIndexAtOffset (offsets, exc.handlerEndOffsets [j]) + 1];
						handler.TryEnd = handler.HandlerStart;
					}

					Console.WriteLine ($"exception handler type: {handler.HandlerType}\n\tstart: {handler.TryStart} end: {handler.TryEnd}\n\thstart: {handler.HandlerStart} hend: {handler.HandlerEnd}\n\tfstart: {handler.FilterStart}\n\tclass: {handler.CatchType}");

					method.Body.ExceptionHandlers.Add (handler);
					method.Body.MaxStackSize = resolver.MaxStackSize;
					method.Body.InitLocals = true;

					//method.Body.ExceptionHandlers.Add (new ExceptionHandler (exc.GetType (j)) {
					//	TryStart = instructions [GetIndexAtOffset (offsets, exceptions [i].startOffset) + 1],
					//	TryEnd = instructions [GetIndexAtOffset (offsets, exceptions [i].endOffset) + 1],
					//	HandlerStart = instructions [GetIndexAtOffset (offsets, exc.handlerStartOffsets [j]) + 1],
					//	FilterStart = instructions [GetIndexAtOffset (offsets, exc.handlerStartOffsets [j]) + 1],
					//	HandlerEnd = instructions [GetIndexAtOffset (offsets, exc.handlerEndOffsets [j]) + 1],
					//	CatchType = type != null ? cas.MainModule.ImportReference (type) : null
					//});
				}
			}
		}

		MethodDefinition LambdaToStaticMethod (LambdaExpression lambda, string methodName, AssemblyDefinition assembly, MethodBuilder mb)
		{
			DynamicHelper helper;
			byte [] ILcode;
#if NETCOREAPP
			helper = new DynamicHelperCoreApp (lambda.Compile ());
#else
			lambda.CompileToMethod (mb);
			var tb = mb.DeclaringType as TypeBuilder;
			if (tb != null)
				tb.CreateType ();
			helper = new DynamicHelperNet (mb);
#endif
			ILcode = helper.GetILCode ();

			//ILcode = GetILCodeFromCompiledMethod (compiled, out dynamicResolver);
			var cecilMethod = new MethodDefinition (methodName, Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.Public, assembly.MainModule.ImportReference (typeof (void)));

			Console.WriteLine ($"rodo: ReconstructBody of method: {methodName}");

			ReconstructBody (cecilMethod, ILcode, assembly, helper);

			return cecilMethod;
		}

		void CreateMarshalMethodAssembly (string path)
		{
			//var assembly        = Assembly.LoadFile (Path.GetFullPath (path));
			var fullPath = Path.GetFullPath (path);
			var assembly        = Assembly.Load (File.ReadAllBytes (fullPath));

			//var tempFile = Path.GetTempFileName ();
			//File.Copy (Path.GetFullPath (path), tempFile, true);
			//var assembly = Assembly.LoadFile (tempFile);
			//var fullPath = Path.GetFullPath (path);
			//var tempFile = Path.GetTempFileName ();
			//File.Copy (Path.GetFullPath (path), tempFile, true);
			//var assembly = Assembly.Load (File.ReadAllBytes (tempFile));

			var baseName        = Path.GetFileNameWithoutExtension (path);
			var assemblyName    = new AssemblyName (baseName + "-JniMarshalMethods");
			var fileName        = assemblyName.Name + ".dll";
			var destDir         = string.IsNullOrEmpty (outDirectory) ? Path.GetDirectoryName (path) : outDirectory;
			var builder         = CreateExportedMemberBuilder ();
			var matchType       = typeNameRegexes.Count > 0;

			if (Verbose)
				ColorWriteLine ($"Preparing marshal method assembly '{assemblyName}'", ConsoleColor.Cyan);

			//var cas = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition (assemblyName.Name, new Version (0, 0, 1)), assemblyName.Name, ModuleKind.Dll);
			var cas = AssemblyDefinition.ReadAssembly (fullPath, new ReaderParameters (ReadingMode.Immediate) { ReadWrite = true });

			var da = AssemblyBuilder.DefineDynamicAssembly (
					assemblyName,
					AssemblyBuilderAccess.Run);

			var dm = da.DefineDynamicModule ("<default>");

			var ad = resolver.GetAssembly (path);

			PrepareTypeMap (ad.MainModule);

			Type[] types = null;
			try {
				types = assembly.GetTypes ();
			} catch (ReflectionTypeLoadException e) {
				types = e.Types;
				foreach (var le in e.LoaderExceptions)
					Warning ($"Type Load exception{Environment.NewLine}{le}");
			}

			foreach (var systemType in types) {
				if (systemType == null)
					continue;

				var type = systemType.GetTypeInfo ();

				if (matchType) {
					var matched = false;

					foreach (var r in typeNameRegexes)
						matched |= r.IsMatch (type.FullName);

					if (!matched)
						continue;
				}

				if (type.IsInterface || type.IsGenericType || type.IsGenericTypeDefinition)
					continue;

				var td = FindType (type);

				if (td == null) {
					if (Verbose)
						Warning ($"Unable to find cecil's TypeDefinition of type {type}");
					continue;
				}
				if (!td.ImplementsInterface ("Java.Interop.IJavaPeerable", cache))
					continue;

				var existingMarshalMethodsType = td.GetNestedType (TypeMover.NestedName);
				if (existingMarshalMethodsType != null && !forceRegeneration) {
					Warning ($"Marshal methods type '{existingMarshalMethodsType.GetAssemblyQualifiedName (cache)}' already exists. Skipped generation of marshal methods in assembly '{assemblyName}'. Use -f to force regeneration when desired.");

					return;
				}

				if (Verbose)
					ColorWriteLine ($"Processing {type} type", ConsoleColor.Yellow);

				var registrationElements    = new List<Expression> ();
				var targetType              = Expression.Variable (typeof(Type), "targetType");
				TypeBuilder dt = null;
				TypeDefinition cecilType = null;

				var flags = BindingFlags.Public | BindingFlags.NonPublic |
						BindingFlags.Instance | BindingFlags.Static;

				var methods = type.GetMethods (flags);
				Array.Sort (methods, new MethodsComparer (type, td));

				addedMethods.Clear ();

				foreach (var method in methods) {
					// TODO: Constructors
					var export  = method.GetCustomAttribute<JavaCallableAttribute> ();
					string signature = null;
					string name = null;
					string methodName = method.Name;

					if (export == null) {
						if (method.IsGenericMethod || method.ContainsGenericParameters || method.IsGenericMethodDefinition || method.ReturnType.IsGenericType)
							continue;

						if (method.DeclaringType != type)
							continue;

						var md = td.GetMethodDefinition (method);

						if (md == null) {
							if (Verbose)
								Warning ($"Unable to find cecil's MethodDefinition of method {method}");
							continue;
						}

						if (!md.NeedsMarshalMethod (resolver, cache, method, ref name, ref methodName, ref signature))
							continue;
					}

					if (dt == null) {
						dt = GetTypeBuilder (dm, type);
						//var cecilParentType = new TypeDefinition (type.Namespace, type.Name, Mono.Cecil.TypeAttributes.Class);
						var cecilParentType = cas.MainModule.GetType (type.Namespace, type.Name);
						cecilType = new TypeDefinition ("", TypeMover.NestedName, Mono.Cecil.TypeAttributes.Class | Mono.Cecil.TypeAttributes.NestedPrivate, cecilType);
						//cas.MainModule.Types.Add (cecilParentType);
						cecilParentType.NestedTypes.Add (cecilType);
					}

					if (addedMethods.Contains (methodName))
						continue;

					if (Verbose) {
						Console.Write ("Adding marshal method for ");
						ColorWriteLine ($"{method}", ConsoleColor.Green );
					}

					var mb = dt.DefineMethod (
							methodName,
							System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static);

					//cecilMethod.Body.GetILProcessor ().
					
					var lambda  = builder.CreateMarshalToManagedExpression (method);



					cecilType.Methods.Add (LambdaToStaticMethod (lambda, methodName, cas, mb));

					if (export != null) {
						name = export.Name;
						signature = export.Signature;
					}

					if (signature == null)
						signature = builder.GetJniMethodSignature (method);

					registrationElements.Add (CreateRegistration (name, signature, lambda, targetType, methodName));

					addedMethods.Add (methodName);
				}
				if (dt != null) {
					var mb = dt.DefineMethod (
							"__RegisterNativeMembers",
							System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static);
					cecilType.Methods.Add (LambdaToStaticMethod (AddRegisterNativeMembers (dt, targetType, registrationElements), "__RegisterNativeMembers", cas, mb));
				}
			}

			//foreach (var tb in definedTypes)
			//	tb.Value.CreateType ();

			//da.Save (fileName);

			//Console.WriteLine ($"rodo: write assembly {cas} to {fileName}");
			//cas.Write (fileName);
			Console.WriteLine ($"rodo: write assembly {cas} to {fullPath}");
			//cas.Write ();
			cas.Write ("test.dll");
			//cas.Write (fullPath);

			//File.Delete (tempFile);

			return;

			if (Verbose)
				ColorWriteLine ($"Marshal method assembly '{assemblyName}' created", ConsoleColor.Cyan);

			resolver.SearchDirectories.Add (destDir);
			var dstAssembly = resolver.GetAssembly (fileName);

			if (!string.IsNullOrEmpty (outDirectory))
				path = Path.Combine (outDirectory, Path.GetFileName (path));

			var mover = new TypeMover (dstAssembly, ad, path, definedTypes, resolver, cache);
			mover.Move ();

			if (!keepTemporary)
				FilesToDelete.Add (dstAssembly.MainModule.FileName);
		}

		static  readonly    MethodInfo          Delegate_CreateDelegate             = typeof (Delegate).GetMethod ("CreateDelegate", new[] {
			typeof (Type),
			typeof (Type),
			typeof (string),
		});
		static  readonly    ConstructorInfo     JniNativeMethodRegistration_ctor    = typeof (JniNativeMethodRegistration).GetConstructor (new[] {
			typeof (string),
			typeof (string),
			typeof (Delegate),
		});
		static  readonly    MethodInfo          JniNativeMethodRegistrationArguments_AddRegistrations = typeof (JniNativeMethodRegistrationArguments).GetMethod ("AddRegistrations", new[] {
			typeof (IEnumerable<JniNativeMethodRegistration>),
		});
		static  readonly    MethodInfo          Type_GetType                        = typeof (Type).GetMethod ("GetType", new[] {
			typeof (string),
		});

		static Expression CreateRegistration (string method, string signature, LambdaExpression lambda, ParameterExpression targetType, string methodName)
		{
			Expression registrationDelegateType = null;
			if (lambda.Type.Assembly == typeof (object).Assembly ||
					lambda.Type.Assembly == typeof (System.Linq.Enumerable).Assembly) {
				registrationDelegateType = Expression.Constant (lambda.Type, typeof (Type));
			}
			else {
				Func<string, bool, Type> getType = Type.GetType;
				registrationDelegateType = Expression.Call (getType.GetMethodInfo (),
						Expression.Constant (lambda.Type.FullName, typeof (string)),
						Expression.Constant (true, typeof (bool)));
				registrationDelegateType = Expression.Convert (registrationDelegateType, typeof (Type));
			}

			var d = Expression.Call (Delegate_CreateDelegate, registrationDelegateType, targetType, Expression.Constant (methodName));
			return Expression.New (JniNativeMethodRegistration_ctor,
					Expression.Constant (method),
					Expression.Constant (signature),
					d);
		}

		static LambdaExpression AddRegisterNativeMembers (TypeBuilder dt, ParameterExpression targetType, List<Expression> registrationElements)
		{
			var args    = Expression.Parameter (typeof (JniNativeMethodRegistrationArguments),   "args");

			var body = Expression.Block (
					new[]{targetType},
					Expression.Assign (targetType, Expression.Call (Type_GetType, Expression.Constant (dt.FullName))),
					Expression.Call (args, JniNativeMethodRegistrationArguments_AddRegistrations, Expression.NewArrayInit (typeof (JniNativeMethodRegistration), registrationElements.ToArray ())));

			var lambda  = Expression.Lambda<Action<JniNativeMethodRegistrationArguments>> (body, new[]{ args });

			var rb = dt.DefineMethod ("__RegisterNativeMembers",
					System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static);
			rb.SetCustomAttribute (new CustomAttributeBuilder (typeof (JniAddNativeMethodRegistrationAttribute).GetConstructor (Type.EmptyTypes), new object[0]));

#if _DUMP_REGISTER_NATIVE_MEMBERS
			Console.WriteLine ($"## Dumping contents of `{dt.FullName}::__RegisterNativeMembers`: ");
			Console.WriteLine (lambda.ToCSharpCode ());
#endif  // _DUMP_REGISTER_NATIVE_MEMBERS
			//lambda.CompileToMethod (rb);
			//DynamicResolver dynamicResolver;
			//var ILcode = GetILCodeFromCompiledMethod (lambda.Compile (), out dynamicResolver);

			return lambda;
		}

		static void ColorMessage (string message, ConsoleColor color, TextWriter writer, bool writeLine = true)
		{
			Console.ForegroundColor = color;
			if (writeLine)
				writer.WriteLine (message);
			else
				writer.Write (message);
			Console.ResetColor ();
		}

		public static void ColorWriteLine (string message, ConsoleColor color) => ColorMessage (message, color, Console.Out);

		public static void ColorWrite (string message, ConsoleColor color) => ColorMessage (message, color, Console.Out, false);

		public static void Error (string message) => ColorMessage ($"Error: {Name}: {message}", ConsoleColor.Red, Console.Error);

		public static void Warning (string message) => ColorMessage ($"Warning: {Name}: {message}", ConsoleColor.Yellow, Console.Error);

		static void AddToTypeMap (TypeDefinition type)
		{
			typeMap [type.FullName] = type;

			if (!type.HasNestedTypes)
				return;

			foreach (var nested in type.NestedTypes)
				AddToTypeMap (nested);
		}

		static void PrepareTypeMap (ModuleDefinition md)
		{
			typeMap.Clear ();

			foreach (var type in md.Types)
				AddToTypeMap (type);
		}

		static TypeDefinition FindType (Type type)
		{
			TypeDefinition rv;
			string cecilName = type.GetCecilName ();

			typeMap.TryGetValue (cecilName, out rv);

			return rv;
		}
	}

	internal static class Extensions
	{
		public static string GetCecilName (this Type type)
		{
			return type.FullName?.Replace ('+', '/');
		}

		static bool CompareTypes (Type reflectionType, TypeReference cecilType)
		{
			return cecilType.ToString () == reflectionType.GetCecilName ();
		}

		static bool MethodsAreEqual (MethodInfo methodInfo, MethodDefinition methodDefinition)
		{
			if (methodInfo.Name != methodDefinition.Name)
				return false;

			if (!CompareTypes (methodInfo.ReturnType, methodDefinition.ReturnType))
				return false;


			var parameters = methodInfo.GetParameters ();
			int infoParametersCount = parameters?.Length ?? 0;
			if (!methodDefinition.HasParameters && infoParametersCount == 0)
				return true;

			if (infoParametersCount != (methodDefinition.Parameters?.Count ?? 0))
				return false;


			int i = 0;
			foreach (var parameter in methodDefinition.Parameters) {
				if (!CompareTypes (parameters [i].ParameterType, parameter.ParameterType))
					return false;
				i++;
			}

			return true;
		}

		internal static Dictionary<MethodInfo, MethodDefinition> MethodMap = new Dictionary<MethodInfo, MethodDefinition> ();

		public static MethodDefinition GetMethodDefinition (this TypeDefinition td, MethodInfo method)
		{
			if (MethodMap.TryGetValue (method, out var md))
				return md;

			foreach (var m in td.Methods)
				if (MethodsAreEqual (method, m)) {
					MethodMap [method] = m;

					return m;
				}

			return null;
		}

		static bool CheckMethod (MethodDefinition m, ref string name, ref string methodName, ref string signature)
		{
			foreach (var registerAttribute in m.GetCustomAttributes ("Android.Runtime.RegisterAttribute")) {
				if (registerAttribute == null || !registerAttribute.HasConstructorArguments)
					continue;

				var constructorParameters = registerAttribute.Constructor.Parameters.ToArray ();
				var constructorArguments = registerAttribute.ConstructorArguments.ToArray ();

				for (int i = 0; i < constructorArguments.Length; i++) {
					switch (constructorParameters [i].Name) {
					case "name":
						name = constructorArguments [i].Value.ToString ();
						break;
					case "signature":
						signature = constructorArguments [i].Value.ToString ();
						break;
					}

				}

				if ((string.IsNullOrEmpty (name) || string.IsNullOrEmpty (signature)) && constructorArguments.Length != 3)
					continue;

				if (string.IsNullOrEmpty (name))
					name = constructorArguments [0].Value.ToString ();

				if (string.IsNullOrEmpty (signature))
					signature = constructorArguments [1].Value.ToString ();

				if (string.IsNullOrEmpty (name) || string.IsNullOrEmpty (signature))
					continue;

				methodName = MarshalMemberBuilder.GetMarshalMethodName (name, signature);
				name = $"n_{name}";

				return true;
			}

			return false;
		}

		public static bool NeedsMarshalMethod (this MethodDefinition md, DirectoryAssemblyResolver resolver, TypeDefinitionCache cache, MethodInfo method, ref string name, ref string methodName, ref string signature)
		{
			var m = md;

			while (m != null) {
				if (CheckMethod (m, ref name, ref methodName, ref signature))
					return true;

				m = m.GetBaseDefinition (cache);

				if (m == md)
					break;

				md = m;
			}

			foreach (var iface in method.DeclaringType.GetInterfaces ()) {
				if (iface.IsGenericType)
					continue;

				var ifaceMap = method.DeclaringType.GetInterfaceMap (iface);
				var ad = resolver.GetAssembly (iface.Assembly.Location);
				var id = ad.MainModule.GetType (iface.GetCecilName ());

				if (id == null) {
					App.Warning ($"Couln't find iterface {iface.FullName}");
					continue;
				}

				for (int i = 0; i < ifaceMap.TargetMethods.Length; i++)
					if (ifaceMap.TargetMethods [i] == method) {
						var imd = id.GetMethodDefinition (ifaceMap.InterfaceMethods [i]);

						if (CheckMethod (imd, ref name, ref methodName, ref signature))
							return true;
					}
			}

			return false;
		}
	}
}
