using System;
using System.Reflection;
using MonoMod.Utils;

namespace BuildingOverhaul
{
	public static class ReflectionUtils
	{
		const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		public static object GetFieldValue(object instance, string name)
			=> instance.GetType().GetField(name, FLAGS).GetValue(instance);
		public static T GetFieldValue<T>(object instance, string name)
			=> (T)GetFieldValue(instance, name);

		public static Delegate CreateDelegate(object instance, string name, Type delegateType)
			=> instance.GetType().GetMethod(name, FLAGS).CreateDelegate(delegateType, instance);
		public static T CreateDelegate<T>(object instance, string name) where T : Delegate
			=> (T)instance.GetType().GetMethod(name, FLAGS).CreateDelegate<T>(instance);

		public static Action BuildAction(object instance, string name)
			=> CreateDelegate<Action>(instance, name);
		public static Action<T> BuildAction<T>(object instance, string name)
			=> CreateDelegate<Action<T>>(instance, name);
		public static Action<T0, T1> BuildAction<T0, T1>(object instance, string name)
			=> CreateDelegate<Action<T0, T1>>(instance, name);
		public static Action<T0, T1, T2> BuildAction<T0, T1, T2>(object instance, string name)
			=> CreateDelegate<Action<T0, T1, T2>>(instance, name);
		public static Action<T0, T1, T2, T3> BuildAction<T0, T1, T2, T3>(object instance, string name)
			=> CreateDelegate<Action<T0, T1, T2, T3>>(instance, name);
		public static Action<T0, T1, T2, T3, T4> BuildAction<T0, T1, T2, T3, T4>(object instance, string name)
			=> CreateDelegate<Action<T0, T1, T2, T3, T4>>(instance, name);
		public static Action<T0, T1, T2, T3, T4, T5> BuildAction<T0, T1, T2, T3, T4, T5>(object instance, string name)
			=> CreateDelegate<Action<T0, T1, T2, T3, T4, T5>>(instance, name);

		public static Func<R> BuildFunc<R>(object instance, string name)
			=> CreateDelegate<Func<R>>(instance, name);
		public static Func<T, R> BuildFunc<T, R>(object instance, string name)
			=> CreateDelegate<Func<T, R>>(instance, name);
		public static Func<T0, T1, R> BuildFunc<T0, T1, R>(object instance, string name)
			=> CreateDelegate<Func<T0, T1, R>>(instance, name);
		public static Func<T0, T1, T2, R> BuildFunc<T0, T1, T2, R>(object instance, string name)
			=> CreateDelegate<Func<T0, T1, T2, R>>(instance, name);
		public static Func<T0, T1, T2, T3, R> BuildFunc<T0, T1, T2, T3, R>(object instance, string name)
			=> CreateDelegate<Func<T0, T1, T2, T3, R>>(instance, name);
		public static Func<T0, T1, T2, T3, T4, R> BuildFunc<T0, T1, T2, T3, T4, R>(object instance, string name)
			=> CreateDelegate<Func<T0, T1, T2, T3, T4, R>>(instance, name);
		public static Func<T0, T1, T2, T3, T4, T5, R> BuildFunc<T0, T1, T2, T3, T4, T5, R>(object instance, string name)
			=> CreateDelegate<Func<T0, T1, T2, T3, T4, T5, R>>(instance, name);
	}
}
