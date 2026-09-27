using QingQiu1011.Core.Logging;

namespace QingQiu1011;

public static class CliLoop {
	public static Task RunAsync(Action onExit, CancellationToken token, Func<bool> isStopping) {
		return Task.Run(async () => {
			while (!token.IsCancellationRequested) {
				Console.Write("> ");
				var line = Console.ReadLine();
				if (line == null) break;
				if (isStopping() || token.IsCancellationRequested) {
					Logger.Info(typeof(CliLoop), $"停止进行中，丢弃 CLI 输入：{line}");
					break;
				}
				CliResult result;
				try {
					result = await CliDispatcher.ExecuteAsync(line);
				} catch (Exception e) {
					Logger.Error(typeof(CliLoop), e, $"CLI 命令执行失败：{line}");
					result = new CliResult($"命令执行失败：{e.Message}", false);
				}
				if (result.Output.Length > 0) Console.WriteLine(result.Output);
				if (result.ShouldExit) {
					onExit();
					break;
				}
			}
		}, token);
	}
}
