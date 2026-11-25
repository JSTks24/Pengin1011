using Pengin1011.Core;

namespace Pengin1011.Modules.FakeModule;

public static class FakeModuleBridgeExports {
	public static int StaticCallCount;

	public static void ResetCalls() {
		StaticCallCount = 0;
	}

	[ModuleBridgeExport]
	public static Task<string> QueryGreetingAsync(ulong userId) {
		StaticCallCount++;
		return Task.FromResult($"bridge-hello-{userId}");
	}
}
