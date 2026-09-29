using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Services.AI;
using Pengin1011.Services.Discord;

namespace Pengin1011.Core;

public sealed class ExitCoordinator {
	public sealed record ExitReport(bool Success, IReadOnlyList<string> Failures);

	internal Func<Task<bool>>? BusinessDrainOverrideForTest;

	internal static Func<Task>? PhaseBarrierForTest;

	internal static TimeSpan ControlSectionBudget = TimeSpan.FromSeconds(15);
	internal static TimeSpan BusinessDrainBudget = TimeSpan.FromSeconds(15);
	internal static TimeSpan BusinessCancelDrainBudget = TimeSpan.FromSeconds(10);

	public async Task<ExitReport> RunAsync() {
		var failures = new List<string>();

		InteractionHost.RequestShutdown();

		var controlSectionHeld = false;
		try {
			controlSectionHeld = await InteractionHost.EnterControlSectionAsync(ControlSectionBudget);
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, Localizer.Get("ControlSectionWaitFailed"));
			failures.Add(Localizer.Format("ControlSectionWaitFailedDetail", e.Message));
		}
		if (!controlSectionHeld) {
			var message = Localizer.Format("ControlSectionTimeout", ControlSectionBudget.TotalSeconds);
			Logger.Error(typeof(ExitCoordinator), message);
			failures.Add(message);
		}

		try {
			if (PhaseBarrierForTest != null) {
				await PhaseBarrierForTest();
			}
			await RunCoreAsync(failures, controlSectionHeld);
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, Localizer.Get("ExitOrchestrationFailed"));
			failures.Add(Localizer.Format("ExitOrchestrationFailedDetail", e.Message));
		} finally {
			if (controlSectionHeld) {
				InteractionHost.LeaveControlSection();
			}
		}

		foreach (var failure in failures) {
			Logger.Error(typeof(ExitCoordinator), Localizer.Format("ExitFailureItem", failure));
		}
		return new ExitReport(failures.Count == 0, failures);
	}

	internal static void ResetForTest() {
		ControlSectionBudget = TimeSpan.FromSeconds(15);
		BusinessDrainBudget = TimeSpan.FromSeconds(15);
		BusinessCancelDrainBudget = TimeSpan.FromSeconds(10);
		PhaseBarrierForTest = null;
	}

	private async Task RunCoreAsync(List<string> failures, bool controlSectionHeld) {
		var businessSettled = await DrainBusinessAsync(failures);

		var blockers = new List<string>();
		if (!controlSectionHeld) {
			blockers.Add(Localizer.Get("BlockerControlSection"));
		}
		if (!businessSettled) {
			blockers.Add(Localizer.Get("BlockerBusinessTasks"));
		}

		var modulesSettled = blockers.Count == 0 && await StopModulesAsync(failures);
		if (blockers.Count > 0) {
			SkipModules(failures, blockers);
		} else if (!modulesSettled) {
			blockers.Add(Localizer.Get("BlockerModuleCleanup"));
		}

		await StepAsync(failures, Localizer.Get("ExitStepComponents"), async () => {
			Components.Detach();
			await Components.ShutdownAsync();
		});

		if (blockers.Count == 0) {
			await StepAsync(failures, Localizer.Get("ExitStepGatewayStop"), DiscordGateway.StopAsync);
		} else {
			SkipStep(failures, Localizer.Get("ExitStepGatewayStop"), blockers);
		}

		BackupResult? backup = null;
		await StepAsync(failures, Localizer.Get("ExitStepDatabaseFinalize"), async () => {
			backup = await Databases.ShutdownAsync();
		});
		if (backup != null && !backup.Success) {
			failures.Add(Localizer.Format("FinalBackupIncomplete", backup.Succeeded, backup.Total));
		}

		if (blockers.Count == 0) {
			await StepAsync(failures, Localizer.Get("ExitStepInteractionRelease"), async () => {
				var release = await InteractionHost.ShutdownAsync();
				if (!release.Released && release.Reason != null) {
					failures.Add(Localizer.Format("InteractionReleaseIncomplete", release.Reason));
				}
			});
			await StepAsync(failures, Localizer.Get("ExitStepAIRelease"), () => {
				AIClient.Shutdown();
				return Task.CompletedTask;
			});
		} else {
			SkipStep(failures, Localizer.Get("ExitStepInteractionRelease"), blockers);
			SkipStep(failures, Localizer.Get("ExitStepAIRelease"), blockers);
		}
	}

	private async Task<bool> DrainBusinessAsync(List<string> failures) {
		if (await DrainBusinessOnceAsync(BusinessDrainBudget)) {
			return true;
		}
		try {
			DiscordGateway.CancelActiveWork();
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, Localizer.Get("CancelActiveWorkFailed"));
			failures.Add(Localizer.Format("CancelActiveWorkFailedDetail", e.Message));
		}
		if (await DrainBusinessOnceAsync(BusinessCancelDrainBudget)) {
			return true;
		}
		failures.Add(Localizer.Get("BusinessTasksUnfinishedAfterCancel"));
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
				failures.Add(Localizer.Format("ModuleStopUnclean", result.Module.Name, result.Outcome, result.Reason));
			}
			return stopResults.All(result => result.Clean);
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, Localizer.Get("ModuleStopSummaryFailed"));
			failures.Add(Localizer.Get("ModuleStopSummaryFailed"));
			return false;
		}
	}

	private static void SkipModules(List<string> failures, IReadOnlyList<string> blockers) {
		var message = Localizer.Format("ModuleCleanupSkipped", string.Join(Localizer.Get("ListSeparatorItems"), blockers));
		Logger.Error(typeof(ExitCoordinator), message);
		failures.Add(message);
	}

	private static void SkipStep(List<string> failures, string name, IReadOnlyList<string> blockers) {
		var message = Localizer.Format("ExitStepSkipped", name, string.Join(Localizer.Get("ListSeparatorItems"), blockers));
		Logger.Error(typeof(ExitCoordinator), message);
		failures.Add(message);
	}

	private static async Task StepAsync(List<string> failures, string name, Func<Task> step) {
		try {
			await step();
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, Localizer.Format("ExitStepFailed", name));
			failures.Add(Localizer.Format("ExitStepFailedDetail", name, e.Message));
		}
	}
}
