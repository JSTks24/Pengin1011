namespace Pengin1011.Services.AI;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AIPresetUsageProviderAttribute : Attribute {
	public AIPresetUsageProviderAttribute(Type providerType) {
		ProviderType = providerType;
	}

	public Type ProviderType { get; }
}
