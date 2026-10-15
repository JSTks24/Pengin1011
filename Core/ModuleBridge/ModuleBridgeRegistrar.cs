namespace Pengin1011.Core;

public sealed class ModuleBridgeRegistrar {
	private readonly LoadedModule _owner;
	private readonly List<(string Module, string Name)> _batch;

	internal ModuleBridgeRegistrar(LoadedModule owner, List<(string Module, string Name)> batch) {
		_owner = owner;
		_batch = batch;
	}

	public void Add(string name, Delegate handler) {
		ModuleBridgeDispatcher.AddExport(_owner, name, handler, _batch);
	}
}
