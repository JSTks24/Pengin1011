namespace Pengin1011.Services.AI;

public sealed class AIPresetEditResult {
	private AIPresetEditResult(bool success, string? error, long committedVersion) {
		Success = success;
		Error = error;
		CommittedVersion = committedVersion;
	}

	public bool Success { get; }

	public string? Error { get; }

	public long CommittedVersion { get; }

	public static AIPresetEditResult Ok(long version) {
		return new AIPresetEditResult(true, null, version);
	}

	public static AIPresetEditResult Fail(string error) {
		return new AIPresetEditResult(false, error, 0);
	}
}
