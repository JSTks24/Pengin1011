using Pengin1011.Core.Modules;

namespace Pengin1011.Core;

public interface IModuleBridgeProvider {
	void Register(ModuleBridgeRegistrar registrar, IModuleRuntime runtime);
}
