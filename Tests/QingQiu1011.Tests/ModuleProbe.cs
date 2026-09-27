using System.Reflection;
using QingQiu1011.Core;
using QingQiu1011.Core.Modules;

namespace QingQiu1011.Tests;

internal static class ModuleProbe {
	public static Type RuntimeType(LoadedModule run) {
		return run.Assembly.GetTypes().Single(type => !type.IsAbstract && !type.IsInterface && typeof(IModuleRuntime).IsAssignableFrom(type));
	}

	public static int RuntimeStaticInt(LoadedModule run, string field) {
		return (int)RuntimeType(run).GetField(field, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
	}

	public static T RuntimeStatic<T>(LoadedModule run, string field) {
		return (T)RuntimeType(run).GetField(field, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
	}

	public static void SetRuntimeStatic(LoadedModule run, string field, object? value) {
		RuntimeType(run).GetField(field, BindingFlags.Public | BindingFlags.Static)!.SetValue(null, value);
	}

	public static int CommandStaticInt(LoadedModule run, string typeName, string field) {
		var type = run.Assembly.GetTypes().Single(type => type.Name == typeName);
		return (int)type.GetField(field, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
	}

	public static void SetCommandStatic(LoadedModule run, string typeName, string field, object? value) {
		var type = run.Assembly.GetTypes().Single(type => type.Name == typeName);
		type.GetField(field, BindingFlags.Public | BindingFlags.Static)!.SetValue(null, value);
	}
}
