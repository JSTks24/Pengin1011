namespace Pengin1011.Core;

public static class Secret {
	public const string Mask = "********";

	public static string Reveal(string? value) {
		return string.IsNullOrEmpty(value) ? Loc.Get("SecretNotConfigured") : Mask;
	}
}
