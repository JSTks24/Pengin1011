using System.Diagnostics;

namespace Pengin1011.Tests;

internal static class TestChildProcess {
	private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

	public static async Task RunAsync(string mode) {
		var appDll = Path.Combine(AppContext.BaseDirectory, "Pengin1011.dll");
		if (!File.Exists(appDll)) {
			throw new InvalidOperationException($"宿主程序集缺失：{appDll}（测试资产应由构建复制保障）");
		}
		var startInfo = new ProcessStartInfo("dotnet") {
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		startInfo.ArgumentList.Add("exec");
		startInfo.ArgumentList.Add(appDll);
		foreach (var part in mode.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
			startInfo.ArgumentList.Add(part);
		}
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("子进程启动失败");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		using var budget = new CancellationTokenSource(Budget);
		try {
			await process.WaitForExitAsync(budget.Token);
		} catch (OperationCanceledException) {
			process.Kill(true);
			throw new TimeoutException($"子进程 {mode} 未在 {Budget.TotalMinutes} 分钟内结束，已强制结束");
		}
		var output = await stdout;
		var error = await stderr;
		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"子进程 {mode} 退出码 {process.ExitCode}：{output}{error}");
		}
	}

	public static void RemoveStaleArtifacts(params string[] directories) {
		foreach (var directory in directories) {
			if (Directory.Exists(directory)) {
				Directory.Delete(directory, true);
			}
		}
	}
}
