using Pengin1011.Core;

namespace Pengin1011.Modules.FakeSecond;

public static class FakeSecondTexts {
	public static string GetText(string key) {
		return Loc.Get(key);
	}
}
