namespace Pengin1011.Core;

internal sealed class EmptyServiceProvider : IServiceProvider {
	public static readonly EmptyServiceProvider Instance = new();

	public object? GetService(Type serviceType) {
		return null;
	}
}
