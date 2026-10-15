namespace Pengin1011.Services.AI;

public enum PatchState {
	Absent,
	Set,
	Clear,
}

public readonly struct Patch<T> {
	private readonly T? _value;

	private Patch(PatchState state, T? value) {
		State = state;
		_value = value;
	}

	public PatchState State { get; }

	public bool IsAbsent => State == PatchState.Absent;

	public bool IsSet => State == PatchState.Set;

	public bool IsClear => State == PatchState.Clear;

	public static Patch<T> Absent => default;

	public static Patch<T> Clear => new(PatchState.Clear, default);

	public static Patch<T> Of(T? value) {
		return value is null ? Clear : new Patch<T>(PatchState.Set, value);
	}

	public static Patch<T> SetTo(T value) {
		return new Patch<T>(PatchState.Set, value);
	}

	public bool TryGet(out T value) {
		if (State == PatchState.Set) {
			value = _value!;
			return true;
		}
		value = default!;
		return false;
	}
}

public sealed class AIPresetPatch {
	public Patch<string> Name { get; init; }
	public Patch<AIPresetProvider> Provider { get; init; }
	public Patch<string> ApiKey { get; init; }
	public Patch<string> BaseUrl { get; init; }
	public Patch<string> Project { get; init; }
	public Patch<string> Location { get; init; }
	public Patch<string> Model { get; init; }
	public Patch<float> Temperature { get; init; }
	public Patch<float> TopP { get; init; }
	public Patch<int> MaxOutputTokens { get; init; }

	public bool IsEmpty {
		get {
			return Name.IsAbsent && Provider.IsAbsent && ApiKey.IsAbsent && BaseUrl.IsAbsent && Project.IsAbsent
				&& Location.IsAbsent && Model.IsAbsent && Temperature.IsAbsent && TopP.IsAbsent && MaxOutputTokens.IsAbsent;
		}
	}

	public AIPreset Apply(AIPreset source) {
		return new AIPreset {
			Name = Text(Name, source.Name),
			Provider = Provider.TryGet(out var provider) ? provider : source.Provider,
			ApiKey = Text(ApiKey, source.ApiKey),
			BaseUrl = Text(BaseUrl, source.BaseUrl),
			Project = Text(Project, source.Project),
			Location = Text(Location, source.Location),
			Model = Text(Model, source.Model),
			Temperature = Number(Temperature, source.Temperature),
			TopP = Number(TopP, source.TopP),
			MaxOutputTokens = Integer(MaxOutputTokens, source.MaxOutputTokens),
		};
	}

	private static string Text(Patch<string> patch, string current) {
		if (patch.TryGet(out var value)) return value ?? "";
		return patch.IsClear ? "" : current;
	}

	private static float? Number(Patch<float> patch, float? current) {
		if (patch.TryGet(out var value)) return value;
		return patch.IsClear ? null : current;
	}

	private static int? Integer(Patch<int> patch, int? current) {
		if (patch.TryGet(out var value)) return value;
		return patch.IsClear ? null : current;
	}
}
