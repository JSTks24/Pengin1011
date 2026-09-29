using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011;

public static class CliLoop {
	public static Task RunAsync(Action onExit, CancellationToken token, Func<bool> isStopping) {
		return Task.Run(async () => {
			while (!token.IsCancellationRequested) {
				Console.Write("> ");
				var line = Console.ReadLine();
				if (line == null) break;
				if (isStopping() || token.IsCancellationRequested) {
					Logger.Info(typeof(CliLoop), Localizer.Format("CliInputDiscarded", line));
					break;
				}
				CliResult result;
				try {
					result = await CliDispatcher.ExecuteAsync(line);
				} catch (Exception e) {
					Logger.Error(typeof(CliLoop), e, Localizer.Format("CliCommandFailed", line));
					result = new CliResult(Localizer.Format("CliCommandError", e.Message), false);
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
