namespace Pengin1011.Core;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ModuleDatabaseProviderAttribute(Type providerType) : Attribute {
	public Type ProviderType { get; } = providerType;
}
