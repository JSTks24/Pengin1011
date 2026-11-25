namespace Pengin1011.Core;

public sealed record ModuleStopResult(LoadedModule Module, ModuleStopOutcome Outcome, string? Reason) {
	public bool Clean => Outcome == ModuleStopOutcome.Clean;
}
