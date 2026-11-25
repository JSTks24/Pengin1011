using Pengin1011.Core;

namespace Pengin1011.Modules.FakeSecond;

public static class FakeSecondBridgeExports {
	[ModuleBridgeExport]
	public static long AddValues(int a, int b) {
		return a + (long)b;
	}
}
