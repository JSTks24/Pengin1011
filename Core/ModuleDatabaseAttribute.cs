namespace Pengin1011.Core;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ModuleDatabaseAttribute(string id) : Attribute {
	public string Id { get; } = id;
}
