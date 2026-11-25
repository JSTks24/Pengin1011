using Pengin1011.Core;
using Pengin1011.Core.Modules;

[assembly: ModuleBridgeProvider(typeof(Pengin1011.Modules.FakeBridgeBad.BadBridgeProvider))]

namespace Pengin1011.Modules.FakeBridgeBad;

public sealed class BadBridgeProvider : IModuleBridgeProvider {
	public void Register(ModuleBridgeRegistrar registrar, IModuleRuntime runtime) {
		registrar.Add("HalfRegistered", (Func<string>)(() => "should-not-survive"));
		throw new InvalidOperationException("故意损坏的桥提供者");
	}
}
