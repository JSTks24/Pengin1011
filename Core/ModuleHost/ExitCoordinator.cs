using QingQiu1011.Core.Logging;
using QingQiu1011.Services.AI;
using QingQiu1011.Services.Discord;

namespace QingQiu1011.Core;

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
			Logger.Error(typeof(ExitCoordinator), e, "等待控制操作退出临界段失败");
			failures.Add($"等待控制操作退出临界段失败：{e.Message}");
		}
		if (!controlSectionHeld) {
			var message = $"控制操作未在 {ControlSectionBudget.TotalSeconds}s 内退出临界段";
			Logger.Error(typeof(ExitCoordinator), message);
			failures.Add(message);
		}

		try {
			if (PhaseBarrierForTest != null) {
				await PhaseBarrierForTest();
			}
			await RunCoreAsync(failures, controlSectionHeld);
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, "退出编排异常，按不完整退出处理");
			failures.Add($"退出编排异常：{e.Message}");
		} finally {
			if (controlSectionHeld) {
				InteractionHost.LeaveControlSection();
			}
		}

		foreach (var failure in failures) {
			Logger.Error(typeof(ExitCoordinator), $"退出失败：{failure}");
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
			blockers.Add("控制操作未在预算内退出临界段");
		}
		if (!businessSettled) {
			blockers.Add("业务任务未确认结束");
		}

		var modulesSettled = blockers.Count == 0 && await StopModulesAsync(failures);
		if (blockers.Count > 0) {
			SkipModules(failures, blockers);
		} else if (!modulesSettled) {
			blockers.Add("模块清理未确认完成");
		}

		await StepAsync(failures, "组件清理", async () => {
			Components.Detach();
			await Components.ShutdownAsync();
		});

		if (blockers.Count == 0) {
			await StepAsync(failures, "网关停止", DiscordGateway.StopAsync);
		} else {
			SkipStep(failures, "网关停止", blockers);
		}

		BackupResult? backup = null;
		await StepAsync(failures, "数据库收尾", async () => {
			backup = await Databases.ShutdownAsync();
		});
		if (backup != null && !backup.Success) {
			failures.Add($"终备份不完整：成功 {backup.Succeeded}/{backup.Total} 库");
		}

		if (blockers.Count == 0) {
			await StepAsync(failures, "交互服务释放", async () => {
				var release = await InteractionHost.ShutdownAsync();
				if (!release.Released && release.Reason != null) {
					failures.Add($"交互服务释放未完成：{release.Reason}");
				}
			});
			await StepAsync(failures, "AI 释放", () => {
				AIClient.Shutdown();
				return Task.CompletedTask;
			});
		} else {
			SkipStep(failures, "交互服务释放", blockers);
			SkipStep(failures, "AI 释放", blockers);
		}
	}

	private async Task<bool> DrainBusinessAsync(List<string> failures) {
		if (await DrainBusinessOnceAsync(BusinessDrainBudget)) {
			return true;
		}
		try {
			DiscordGateway.CancelActiveWork();
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, "取消在途业务工作失败");
			failures.Add($"取消在途业务工作失败：{e.Message}");
		}
		if (await DrainBusinessOnceAsync(BusinessCancelDrainBudget)) {
			return true;
		}
		failures.Add("业务任务在取消后仍未全部完成（执行尽力备份，不承诺包含这些任务的后续写入）");
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
				failures.Add($"模块 {result.Module.Name} 停止结果 {result.Outcome}：{result.Reason}");
			}
			return stopResults.All(result => result.Clean);
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, "模块停止汇总失败");
			failures.Add("模块停止汇总失败");
			return false;
		}
	}

	private static void SkipModules(List<string> failures, IReadOnlyList<string> blockers) {
		var message = $"模块清理未开始（{string.Join("、", blockers)}）：运行记录、模块资源与在途任务保持原样，不标记为已停止，也不记为清理失败";
		Logger.Error(typeof(ExitCoordinator), message);
		failures.Add(message);
	}

	private static void SkipStep(List<string> failures, string name, IReadOnlyList<string> blockers) {
		var message = $"跳过{name}：{string.Join("、", blockers)}；相关资源保持原样，由进程结束回收";
		Logger.Error(typeof(ExitCoordinator), message);
		failures.Add(message);
	}

	private static async Task StepAsync(List<string> failures, string name, Func<Task> step) {
		try {
			await step();
		} catch (Exception e) {
			Logger.Error(typeof(ExitCoordinator), e, $"退出清理步骤失败：{name}");
			failures.Add($"清理步骤 {name} 失败：{e.Message}");
		}
	}
}
