namespace Pengin1011.Core;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ModuleBridgeProviderAttribute : Attribute {
	public ModuleBridgeProviderAttribute(Type providerType) {
		ProviderType = providerType;
	}

	public Type ProviderType { get; }
}
