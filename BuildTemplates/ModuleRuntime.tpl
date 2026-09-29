using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;

namespace Pengin1011.Modules.__MODULE__;

public sealed class __MODULE__Runtime : IModuleRuntime {
	public __MODULE__Options Options { get; private set; } = new();

	public Task InitializeAsync(CancellationToken ct) {
		Options = __MODULE__Config.Load();
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct) {
		// 此 ct 是清理预算 token，与业务工作取消（模块 Lifecycle）不同；停止后台工作靠 Runtime 自己等待其任务结束，宿主不做第二次清理。
		return Task.CompletedTask;
	}
}
