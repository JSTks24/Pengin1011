namespace Pengin1011.Core;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ModuleHostContractAttribute : Attribute {
	public ModuleHostContractAttribute(int version) {
		Version = version;
	}

	public int Version { get; }
}
