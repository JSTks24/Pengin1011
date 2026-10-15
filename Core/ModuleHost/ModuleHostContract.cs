using System.Reflection;

namespace Pengin1011.Core;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ModuleHostContractAttribute : Attribute {
	public ModuleHostContractAttribute(int version) {
		Version = version;
	}

	public int Version { get; }
}

public static class ModuleHostContract {
	public const int Current = 4;

	public static bool TryRead(System.Reflection.Assembly assembly, out int version, out string? failure) {
		failure = null;
		ModuleHostContractAttribute? attribute;
		try {
			attribute = assembly.GetCustomAttribute<ModuleHostContractAttribute>();
		} catch (Exception e) {
			version = 0;
			failure = e.Message;
			return false;
		}
		if (attribute == null) {
			version = 0;
			failure = "missing";
			return false;
		}
		version = attribute.Version;
		return true;
	}
}
