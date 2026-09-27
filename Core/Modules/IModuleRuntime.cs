namespace QingQiu1011.Core.Modules;

public interface IModuleRuntime {
	Task InitializeAsync(CancellationToken ct);
	Task StopAsync(CancellationToken ct);
}
