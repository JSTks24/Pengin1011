using System.Text.RegularExpressions;

namespace Pengin1011.Services.AI;

internal static class GoogleAIEndpoint {
	public static bool TryResolve(AIPreset preset, out string baseUrl) {
		baseUrl = preset.BaseUrl.Trim();
		if (baseUrl.Length == 0) {
			if (preset.Provider == AIPresetProvider.Gemini) {
				baseUrl = "https://generativelanguage.googleapis.com";
			} else if (preset.Provider == AIPresetProvider.Vertex) {
				var location = preset.Location.Trim().ToLowerInvariant();
				baseUrl = location switch {
					"" or "global" => "https://aiplatform.googleapis.com",
					"us" or "eu" => $"https://aiplatform.{location}.rep.googleapis.com",
					_ => $"https://{location}-aiplatform.googleapis.com",
				};
			} else {
				return false;
			}
		}
		foreach (var character in baseUrl) {
			if (char.IsWhiteSpace(character) || char.IsControl(character) || character is '\\' or '@') return false;
		}
		if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
		var pathStart = baseUrl.IndexOf('/', baseUrl.IndexOf("://", StringComparison.Ordinal) + 3);
		if (pathStart >= 0 && pathStart != baseUrl.Length - 1) return false;
		var validHost = preset.Provider switch {
			AIPresetProvider.Gemini => uri.Host == "generativelanguage.googleapis.com",
			AIPresetProvider.Vertex => uri.Host is "aiplatform.googleapis.com" or "aiplatform.us.rep.googleapis.com" or "aiplatform.eu.rep.googleapis.com"
				|| Regex.IsMatch(uri.Host, @"\A[a-z]+(?:-[a-z]+)+[0-9]+-aiplatform\.googleapis\.com\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
			_ => false,
		};
		if (!validHost) return false;
		baseUrl = uri.GetLeftPart(UriPartial.Authority);
		return true;
	}
}
