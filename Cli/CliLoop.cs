using Pengin1011.Core;
using Pengin1011.Core.Logging;

namespace Pengin1011;

public static class CliLoop {
	public static Task RunAsync(Action onExit, CancellationToken token) {
		return Task.Run(async () => {
			while (!token.IsCancellationRequested) {
				Console.Write("> ");
				var line = Console.ReadLine();
				if (line == null) break;
				if (token.IsCancellationRequested) {
					Logger.Info(typeof(CliLoop), Loc.Format("CliInputDiscarded", CommandName(line)));
					break;
				}
				CliResult result;
				try {
					result = await CliDispatcher.ExecuteAsync(line);
				} catch (Exception e) {
					Logger.Error(typeof(CliLoop), Loc.Format("CliCommandFailed", CommandName(line)));
					result = new CliResult(Loc.Format("CliCommandError", e.Message), false);
				}
				if (result.Output.Length > 0) Console.WriteLine(result.Output);
				if (result.ShouldExit) {
					onExit();
					break;
				}
			}
		}, token);
	}

	private static string CommandName(string line) {
		var command = CliDispatcher.Tokenize(line).FirstOrDefault()?.ToLowerInvariant();
		return command is "help" or "status" or "modules" or "reload" or "db" or "preset" or "sync" or "exit" or "quit" ? command : "?";
	}
}
