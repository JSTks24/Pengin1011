using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Services.AI;
using Pengin1011.Services.Discord;

namespace Pengin1011.Core;

public sealed class ExitCoordinator {
	public sealed record ExitReport(bool Success, IReadOnlyList<string> Failures, bool ResourcesSettled = false);

	internal Func<Task<bool>>? BusinessDrainOverrideForTest;

	internal static Func<Task>? PhaseBarrierForTest;

	internal static TimeSpan ControlSectionBudget = TimeSpan.FromSeconds(15);
	internal static TimeSpan BusinessDrainBudget = TimeSpan.FromSeconds(15);
	internal static TimeSpan BusinessCancelDrainBudget = TimeSpan.FromSeconds(10);
	internal static TimeSpan GatewayStopBudget = TimeSpan.FromSeconds(10);
	internal static TimeSpan LoggerForwarderBudget = TimeSpan.FromSeconds(5);
	internal static TimeSpan AiShutdownBudget = TimeSpan.FromSeconds(10);
	internal static TimeSpan DatabaseMaintenanceBudget = TimeSpan.FromSeconds(30);

	public async Task<ExitReport> RunAsync() {
		var failures = new List<string>();
		var resourcesSettled = false;

		InteractionHost.RequestShutdown();

		var controlSectionHeld = false;
		try {
			controlSectionHeld = await InteractionHost.EnterControlSectionAsync(ControlSectionBudget);
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, Loc.Get("ControlSectionWaitFailed"));
			failures.Add(Loc.Format("ControlSectionWaitFailedDetail", e.Message));
		}
		if (!controlSectionHeld) {
			var message = Loc.Format("ControlSectionTimeout", ControlSectionBudget.TotalSeconds);
			Logger.Error(typeof(ExitCoordinator), message);
			failures.Add(message);
		}

		try {
			if (PhaseBarrierForTest != null) {
				await PhaseBarrierForTest();
			}
			resourcesSettled = await RunCoreAsync(failures, controlSectionHeld);
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, Loc.Get("ExitOrchestrationFailed"));
			failures.Add(Loc.Format("ExitOrchestrationFailedDetail", e.Message));
		} finally {
			if (controlSectionHeld) {
				InteractionHost.LeaveControlSection();
			}
		}

		foreach (var failure in failures) {
			Logger.Error(typeof(ExitCoordinator), Loc.Format("ExitFailureItem", failure));
		}
		return new ExitReport(failures.Count == 0, failures, resourcesSettled);
	}

	internal static void ResetForTest() {
		ControlSectionBudget = TimeSpan.FromSeconds(15);
		BusinessDrainBudget = TimeSpan.FromSeconds(15);
		BusinessCancelDrainBudget = TimeSpan.FromSeconds(10);
		GatewayStopBudget = TimeSpan.FromSeconds(10);
		LoggerForwarderBudget = TimeSpan.FromSeconds(5);
		AiShutdownBudget = TimeSpan.FromSeconds(10);
		DatabaseMaintenanceBudget = TimeSpan.FromSeconds(30);
		PhaseBarrierForTest = null;
	}

	private async Task<bool> RunCoreAsync(List<string> failures, bool controlSectionHeld) {
		var businessSettled = await DrainBusinessAsync(failures);

		var blockers = new List<string>();
		if (!controlSectionHeld) {
			blockers.Add(Loc.Get("BlockerControlSection"));
		}
		if (!businessSettled) {
			blockers.Add(Loc.Get("BlockerBusinessTasks"));
		}

		var modulesSettled = blockers.Count == 0 && await StopModulesAsync(failures);
		if (blockers.Count > 0) {
			SkipModules(failures, blockers);
		} else if (!modulesSettled) {
			blockers.Add(Loc.Get("BlockerModuleCleanup"));
		}

		var componentsStopped = await StepAsync(failures, Loc.Get("ExitStepComponents"), async () => {
			Components.Detach();
			await Components.ShutdownAsync();
		});

		var forwarderSettled = await StopLoggerForwarderAsync(failures);
		if (!forwarderSettled) {
			blockers.Add(Loc.Get("BlockerLoggerForwarder"));
		}

		var gatewayStopped = false;
		if (blockers.Count == 0) {
			gatewayStopped = await StepAsync(failures, Loc.Get("ExitStepGatewayStop"), () => DiscordGateway.StopAsync().WaitAsync(GatewayStopBudget));
		} else {
			SkipStep(failures, Loc.Get("ExitStepGatewayStop"), blockers);
		}

		BackupResult? backup = null;
		var databaseStopped = await StepAsync(failures, Loc.Get("ExitStepDatabaseFinalize"), async () => {
			backup = await Databases.ShutdownAsync().WaitAsync(DatabaseMaintenanceBudget);
		});
		if (backup != null && !backup.Success) {
			failures.Add(Loc.Format("FinalBackupIncomplete", backup.Succeeded, backup.Total));
		}

		var interactionStopped = false;
		var aiStopped = false;
		if (blockers.Count == 0) {
			interactionStopped = await StepAsync(failures, Loc.Get("ExitStepInteractionRelease"), async () => {
				var release = await InteractionHost.ShutdownAsync();
				if (!release.Released) throw new InvalidOperationException(release.Reason);
			});
			aiStopped = await StopAiClientAsync(failures);
		} else {
			SkipStep(failures, Loc.Get("ExitStepInteractionRelease"), blockers);
			SkipStep(failures, Loc.Get("ExitStepAIRelease"), blockers);
		}
		return blockers.Count == 0 && componentsStopped && gatewayStopped && databaseStopped && interactionStopped && aiStopped;
	}

	private static async Task<bool> StopLoggerForwarderAsync(List<string> failures) {
		var settled = false;
		await StepAsync(failures, Loc.Get("ExitStepLoggerForwarder"), async () => {
			var result = await LoggerChannelForwarder.StopAsync(LoggerForwarderBudget);
			settled = result.Stopped && !result.Pending;
			if (!settled) failures.Add(Loc.Format("LoggerForwarderNotDrained", result.RemainingQueued, result.ActiveSends));
			if (result.EntriesDropped > 0) Logger.Warn(typeof(ExitCoordinator), Loc.Format("LoggerForwarderDroppedEntries", result.EntriesDropped));
			foreach (var failure in result.Failures) Logger.Warn(typeof(ExitCoordinator), Loc.Format("LoggerForwarderChannelFailure", $"{failure.ChannelId} ({failure.Count}): {failure.LastError}"));
		});
		return settled;
	}

	private static async Task<bool> StopAiClientAsync(List<string> failures) {
		var drained = false;
		var completed = await StepAsync(failures, Loc.Get("ExitStepAIRelease"), async () => {
			var result = await AIClient.ShutdownAsync(AiShutdownBudget);
			drained = result.Drained;
			if (!drained) failures.Add(Loc.Format("AIReleaseIncomplete", result.ActiveLeases));
		});
		return completed && drained;
	}

	private async Task<bool> DrainBusinessAsync(List<string> failures) {
		if (await DrainBusinessOnceAsync(BusinessDrainBudget)) {
			return true;
		}
		try {
			DiscordGateway.CancelActiveWork();
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, Loc.Get("CancelActiveWorkFailed"));
			failures.Add(Loc.Format("CancelActiveWorkFailedDetail", e.Message));
		}
		if (await DrainBusinessOnceAsync(BusinessCancelDrainBudget)) {
			return true;
		}
		failures.Add(Loc.Get("BusinessTasksUnfinishedAfterCancel"));
		return false;
	}

	private async Task<bool> DrainBusinessOnceAsync(TimeSpan budget) {
		if (BusinessDrainOverrideForTest != null) {
			return await BusinessDrainOverrideForTest();
		}
		return await DiscordGateway.DrainWorkAsync(budget);
	}

	private static async Task<bool> StopModulesAsync(List<string> failures) {
		try {
			var stopResults = await ModuleHost.StopAllAsync();
			foreach (var result in stopResults.Where(result => !result.Clean)) {
				failures.Add(Loc.Format("ModuleStopUnclean", result.Module.Name, result.Outcome, result.Reason));
			}
			return stopResults.All(result => result.Clean);
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, Loc.Get("ModuleStopSummaryFailed"));
			failures.Add(Loc.Get("ModuleStopSummaryFailed"));
			return false;
		}
	}

	private static void SkipModules(List<string> failures, IReadOnlyList<string> blockers) {
		var message = Loc.Format("ModuleCleanupSkipped", string.Join(Loc.Get("ListSeparatorItems"), blockers));
		Logger.Error(typeof(ExitCoordinator), message);
		failures.Add(message);
	}

	private static void SkipStep(List<string> failures, string name, IReadOnlyList<string> blockers) {
		var message = Loc.Format("ExitStepSkipped", name, string.Join(Loc.Get("ListSeparatorItems"), blockers));
		Logger.Error(typeof(ExitCoordinator), message);
		failures.Add(message);
	}

	private static async Task<bool> StepAsync(List<string> failures, string name, Func<Task> step) {
		try {
			await step();
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, Loc.Format("ExitStepFailed", name));
			failures.Add(Loc.Format("ExitStepFailedDetail", name, e.Message));
			return false;
		}
	}
}
