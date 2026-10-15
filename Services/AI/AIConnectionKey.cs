using System.Text;
using Pengin1011.Services.AI;

namespace Pengin1011.Services.AI;

public readonly struct AIConnectionKey : IEquatable<AIConnectionKey> {
	public AIConnectionKey(AIPreset preset) {
		Provider = preset.Provider;
		ApiKey = preset.ApiKey;
		BaseUrl = preset.BaseUrl;
		Project = preset.Project;
		Location = preset.Location;
	}

	public AIPresetProvider Provider { get; }

	public string ApiKey { get; }

	public string BaseUrl { get; }

	public string Project { get; }

	public string Location { get; }

	public string Describe() {
		var builder = new StringBuilder();
		builder.Append(Provider);
		builder.Append('|');
		builder.Append(BaseUrl);
		builder.Append('|');
		builder.Append(Project);
		builder.Append('|');
		builder.Append(Location);
		if (ApiKey.Length > 0) {
			builder.Append("|key:");
			builder.Append(ApiKey.Length);
			builder.Append(" chars");
		}
		return builder.ToString();
	}

	public bool Equals(AIConnectionKey other) {
		return Provider == other.Provider
			&& string.Equals(ApiKey, other.ApiKey, StringComparison.Ordinal)
			&& string.Equals(BaseUrl, other.BaseUrl, StringComparison.Ordinal)
			&& string.Equals(Project, other.Project, StringComparison.Ordinal)
			&& string.Equals(Location, other.Location, StringComparison.Ordinal);
	}

	public override bool Equals(object? obj) {
		return obj is AIConnectionKey other && Equals(other);
	}

	public override int GetHashCode() {
		return HashCode.Combine((int)Provider, ApiKey, BaseUrl, Project, Location);
	}

	public override string ToString() {
		return Describe();
	}
}
