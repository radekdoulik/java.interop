using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Java.Interop {

	public class ExportedMemberBuilder : IExportedMemberBuilder
	{
		public ExportedMemberBuilder (JavaVM javaVM)
		{
			if (javaVM == null)
				throw new ArgumentNullException ("javaVM");
			JavaVM = javaVM;
		}

		public JavaVM JavaVM {get; private set;}

		public IEnumerable<JniNativeMethodRegistration> GetExportedMemberRegistrations (Type declaringType)
		{
			if (declaringType == null)
				throw new ArgumentNullException ("declaringType");
			return CreateExportedMemberRegistrationIterator (declaringType);
		}

		IEnumerable<JniNativeMethodRegistration> CreateExportedMemberRegistrationIterator (Type declaringType)
		{
			const BindingFlags methodScope = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
			foreach (var method in declaringType.GetMethods (methodScope)) {
				var exports = (ExportAttribute[]) method.GetCustomAttributes (typeof(ExportAttribute), inherit:false);
				if (exports == null || exports.Length == 0)
					continue;
				var export  = exports [0];
				yield return CreateMarshalFromJniMethodRegistration (export, declaringType, method);
			}
		}

		public JniNativeMethodRegistration CreateMarshalFromJniMethodRegistration (ExportAttribute export, Type type, MethodInfo method)
		{
			if (export == null)
				throw new ArgumentNullException ("export");
			if (type == null)
				throw new ArgumentNullException ("type");
			if (method == null)
				throw new ArgumentNullException ("method");

			string signature = GetJniMethodSignature (export, method);
			return new JniNativeMethodRegistration () {
				Name        = GetJniMethodName (export, method),
				Signature   = signature,
				Marshaler   = CreateJniMethodMarshaler (export, type, method),
			};
		}

		protected virtual string GetJniMethodName (ExportAttribute export, MethodInfo method)
		{
			return export.Name ?? "n_" + method.Name;
		}

		public virtual string GetJniMethodSignature (ExportAttribute export, MethodInfo method)
		{
			if (export == null)
				throw new ArgumentNullException ("export");
			if (method == null)
				throw new ArgumentNullException ("method");

			if (export.Signature != null)
				return export.Signature;

			var signature = new StringBuilder ().Append ("(");
			foreach (var p in method.GetParameters ()) {
				var info = JavaVM.GetJniTypeInfoForType (p.ParameterType);
				if (info.JniTypeName == null)
					throw new NotSupportedException ("Don't know how to determine JNI signature for parameter type: " + p.ParameterType.FullName + ".");
				signature.Append (info.JniTypeReference);
			}
			signature.Append (")");
			var ret = JavaVM.GetJniTypeInfoForType (method.ReturnType);
			if (ret.JniTypeName == null)
				throw new NotSupportedException ("Don't know how to determine JNI signature for return type: " + method.ReturnType.FullName + ".");
			signature.Append (ret.JniTypeReference);
			return export.Signature = signature.ToString ();
		}

		Delegate CreateJniMethodMarshaler (ExportAttribute export, Type type, MethodInfo method)
		{
			var e = CreateMarshalFromJniMethodExpression (export, type, method);
			return e.Compile ();
		}

		// TODO: make internal, and add [InternalsVisibleTo] for Java.Interop.Export-Tests
		public virtual LambdaExpression CreateMarshalFromJniMethodExpression (ExportAttribute export, Type type, MethodInfo method)
		{
			if (export == null)
				throw new ArgumentNullException ("export");
			if (type == null)
				throw new ArgumentNullException ("type");
			if (method == null)
				throw new ArgumentNullException ("method");

			var methodParameters = method.GetParameters ();

			CheckMarshalTypesMatch (method, export.Signature, methodParameters);

			var jnienv  = Expression.Parameter (typeof (IntPtr), "__jnienv");
			var context = Expression.Parameter (typeof (IntPtr), "__context");

			var jvm         = Expression.Variable (typeof (JavaVM), "__jvm");
			var variables   = new List<ParameterExpression> () {
				jvm,
			};

			var marshalBody = new List<Expression> () {
				CheckJnienv (jnienv),
				Expression.Assign (jvm, GetJavaVM ()),
			};

			ParameterExpression self = null;
			if (!method.IsStatic) {
				self    = Expression.Variable (type, "__this");
				variables.Add (self);
				marshalBody.Add (Expression.Assign (self, GetThis (jvm, type, context)));
			}

			var marshalParameters   = new List<ParameterExpression> (methodParameters.Length);
			var invokeParameters    = new List<ParameterExpression> (methodParameters.Length);
			for (int i = 0; i < methodParameters.Length; ++i) {
				var jni = GetMarshalFromJniParameterType (methodParameters [i].ParameterType);
				if (jni == methodParameters [i].ParameterType) {
					var p   = Expression.Parameter (jni, methodParameters [i].Name);
					marshalParameters.Add (p);
					invokeParameters.Add (p);
				}
				else {
					var np      = Expression.Parameter (jni, "native_" + methodParameters [i].Name);
					var p       = Expression.Variable (methodParameters [i].ParameterType, methodParameters [i].Name);
					var fromJni = GetMarshalFromJniExpression (jvm, p.Type, np);
					if (fromJni == null)
						throw new NotSupportedException (string.Format ("Cannot convert from '{0}' to '{1}'.", jni, methodParameters [i].ParameterType));
					variables.Add (p);
					marshalParameters.Add (np);
					invokeParameters.Add (p);
					marshalBody.Add (Expression.Assign (p, fromJni));
				}
			}

			Expression invoke = method.IsStatic
				? Expression.Call (method, invokeParameters)
				: Expression.Call (self, method, invokeParameters);
			ParameterExpression ret = null;
			if (method.ReturnType == typeof (void)) {
				marshalBody.Add (invoke);
			} else {
				var jniRType    = GetMarshalToJniReturnType (method.ReturnType);
				var exit        = Expression.Label (jniRType, "__exit");
				ret             = Expression.Variable (jniRType, "__jret");
				var mret        = Expression.Variable (method.ReturnType, "__mret");
				variables.Add (ret);
				variables.Add (mret);
				marshalBody.Add (Expression.Assign (mret, invoke));
				if (jniRType == method.ReturnType)
					marshalBody.Add (Expression.Assign (ret, mret));
				else {
					var marshalExpr = GetMarshalToJniExpression (method.ReturnType, mret);
					if (marshalExpr == null)
						throw new NotSupportedException (string.Format ("Don't know how to marshal '{0}' to '{1}'.",
								method.ReturnType, jniRType));
					marshalBody.Add (Expression.Assign (ret, marshalExpr));
				}
				marshalBody.Add (Expression.Return (exit, ret));
				marshalBody.Add (Expression.Label (exit, ret));
			}


			var funcTypeParams = new List<Type> () {
				typeof (IntPtr),
				typeof (IntPtr),
			};
			foreach (var p in marshalParameters)
				funcTypeParams.Add (p.Type);
			if (ret != null)
				funcTypeParams.Add (ret.Type);
			var marshalerType = ret == null
				? Expression.GetActionType (funcTypeParams.ToArray ())
				: Expression.GetFuncType (funcTypeParams.ToArray ());

			var bodyParams = new List<ParameterExpression> { jnienv, context };
			bodyParams.AddRange (marshalParameters);
			var body = Expression.Block (variables, marshalBody);
			return Expression.Lambda (marshalerType, body, bodyParams);
		}

		void CheckMarshalTypesMatch (MethodInfo method, string signature, ParameterInfo[] methodParameters)
		{
			if (signature == null)
				return;

			var mptypes = JniSignature.GetMarshalParameterTypes (signature).ToList ();
			int len     = Math.Min (methodParameters.Length, mptypes.Count);
			for (int i = 0; i < len; ++i) {
				var jni = GetMarshalFromJniParameterType (methodParameters [i].ParameterType);
				if (mptypes [i] != jni)
					throw new ArgumentException (
							string.Format ("JNI parameter type mismatch. Type '{0}' != '{1}.", jni, mptypes [i]),
							"signature");
			}

			if (mptypes.Count != methodParameters.Length)
				throw new ArgumentException (
						string.Format ("JNI parametr count mismatch: signature contains {0} parameters, method contains {1}.",
							mptypes.Count, methodParameters.Length),
						"signature");

			var jrinfo = JniSignature.GetMarshalReturnType (signature);
			var mrinfo = GetMarshalToJniReturnType (method.ReturnType);
			if (mrinfo != jrinfo)
				throw new ArgumentException (
						string.Format ("JNI return type mismatch. Type '{0}' != '{1}.", jrinfo, mrinfo),
						"signature");
		}

		protected virtual Type GetMarshalFromJniParameterType (Type type)
		{
			if (JniBuiltinTypes.Contains (type))
				return type;
			return typeof (IntPtr);
		}

		protected virtual Type GetMarshalToJniReturnType (Type type)
		{
			if (JniBuiltinTypes.Contains (type))
				return type;
			return typeof (IntPtr);
		}

		protected virtual Expression GetMarshalFromJniExpression (Expression jvm, Type targetType, Expression jniParameter)
		{
			MarshalInfo v;
			if (Marshalers.TryGetValue (targetType, out v))
				return v.FromJni (jvm, targetType, jniParameter);
			if (typeof (IJavaObject).IsAssignableFrom (targetType))
				return Marshalers [typeof (IJavaObject)].FromJni (jvm, targetType, jniParameter);
			return null;
		}

		protected virtual Expression GetMarshalToJniExpression (Type sourceType, Expression managedParameter)
		{
			MarshalInfo v;
			if (Marshalers.TryGetValue (sourceType, out v))
				return v.ToJni (managedParameter);
			if (typeof (IJavaObject).IsAssignableFrom (sourceType))
				return Marshalers [typeof (IJavaObject)].ToJni (managedParameter);
			return null;
		}

		static readonly Dictionary<Type, MarshalInfo> Marshalers = new Dictionary<Type, MarshalInfo> () {
			{ typeof (string), new MarshalInfo {
					FromJni = (vm, t, p) => Expression.Call (F<IntPtr, string> (JniEnvironment.Strings.ToString).Method, p),
					ToJni   = p => Expression.Call (F<string, JniLocalReference> (JniEnvironment.Strings.NewString).Method, p)
			} },
			{ typeof (IJavaObject), new MarshalInfo {
					FromJni = (vm, t, p) => GetThis (vm, t, p),
					ToJni   = p => Expression.Call (F<IJavaObject, IntPtr> (JniEnvironment.Handles.NewReturnToJniRef).Method, p)
			} },
		};

		static Func<T, TRet> F<T, TRet> (Func<T, TRet> func)
		{
			return func;
		}

		static Expression CheckJnienv (ParameterExpression jnienv)
		{
			Action<IntPtr> a = JniEnvironment.CheckCurrent;
			return Expression.Call (null, a.Method, jnienv);
		}

		static Expression GetThis (Expression vm, Type targetType, Expression context)
		{
			return Expression.Call (
					vm,
					"GetObject",
					new[]{targetType},
					context);
		}

		static Expression GetJavaVM ()
		{
			var env     = typeof (JniEnvironment);
			var cenv    = Expression.Property (null, env, "Current");
			var vm      = Expression.Property (cenv, "JavaVM");
			return vm;
		}

		static readonly ISet<Type> JniBuiltinTypes = new HashSet<Type> {
			typeof (IntPtr),
			typeof (void),
			typeof (bool),
			typeof (sbyte),
			typeof (char),
			typeof (short),
			typeof (int),
			typeof (long),
			typeof (float),
			typeof (double),
		};

	}

	class MarshalInfo {

		public Func<Expression /* vm */, Type /* targetType */, Expression /* value */, Expression /* managed rep */>    FromJni;
		public Func<Expression /* managed rep */, Expression /* jni rep */>    ToJni;
	}

	static class JniSignature {

		public static Type GetMarshalReturnType (string signature)
		{
			int idx = signature.LastIndexOf (')') + 1;
			return ExtractMarshalTypeFromSignature (signature, ref idx);
		}

		public static IEnumerable<Type> GetMarshalParameterTypes (string signature)
		{
			if (signature.StartsWith ("(", StringComparison.Ordinal)) {
				int e = signature.IndexOf (")", StringComparison.Ordinal);
				signature = signature.Substring (1, e >= 0 ? e-1 : signature.Length-1);
			}
			int i = 0;
			Type t;
			while ((t = ExtractMarshalTypeFromSignature (signature, ref i)) != null)
				yield return t;
		}

		// as per: http://java.sun.com/j2se/1.5.0/docs/guide/jni/spec/types.html
		static Type ExtractMarshalTypeFromSignature (string signature, ref int index)
		{
			#if false
			if (index >= signature.Length)
				return null;
			var i = index++;
			switch (signature [i]) {
			case 'B':   return typeof (sbyte);
			case 'C':   return typeof (char);
			case 'D':   return typeof (double);
			case 'F':   return typeof (float);
			case 'I':   return typeof (int);
			case 'J':   return typeof (long);
			case 'S':   return typeof (short);
			case 'V':   return typeof (void);
			case 'Z':   return typeof (bool);
			case '[':
			case 'L':   return typeof (IntPtr);
			default:
				throw new ArgumentException ("Unknown JNI Type '" + signature [i] + "' within: " + signature, "signature");
			}
			#else
			if (index >= signature.Length)
				return null;
			var i = index++;
			switch (signature [i]) {
			case 'B':   return typeof (sbyte);
			case 'C':   return typeof (char);
			case 'D':   return typeof (double);
			case 'F':   return typeof (float);
			case 'I':   return typeof (int);
			case 'J':   return typeof (long);
			case 'S':   return typeof (short);
			case 'V':   return typeof (void);
			case 'Z':   return typeof (bool);
			case '[':
				++i;
				if (i >= signature.Length)
					throw new ArgumentException ("Missing array type after '[' at index " + i + " in: " + signature, "signature");
				ExtractMarshalTypeFromSignature (signature, ref index);
				return typeof (IntPtr);
			case 'L': {
				var e = signature.IndexOf (";", index, StringComparison.Ordinal);
				if (e <= 0)
					throw new ArgumentException ("Missing reference type after 'L' at index " + i + "in: " + signature, "signature");
				index = e + 1;
				return typeof (IntPtr);
			}
			default:
				throw new ArgumentException ("Unknown JNI Type '" + signature [i] + "' within: " + signature, "signature");
			}
			#endif
		}
	}
}
