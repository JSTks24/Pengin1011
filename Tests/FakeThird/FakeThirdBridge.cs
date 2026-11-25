using Pengin1011.Core;

namespace Pengin1011.Modules.FakeThird;

public static class FakeThirdBridge {
	public static Task<string> QueryGreeting(ulong userId) {
		return ModuleBridge.FakeModule.QueryGreetingAsync(userId);
	}

	public static string DescribeRuntimeState() {
		return ModuleBridge.FakeModule.DescribeState();
	}

	public static long AddValues(int a, int b) {
		return ModuleBridge.FakeSecond.AddValues(a, b);
	}
}
