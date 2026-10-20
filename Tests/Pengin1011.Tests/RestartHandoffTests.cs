using System.Diagnostics;
using System.Globalization;
using Pengin1011.Core;
using Xunit.Abstractions;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class RestartHandoffTests {
	private readonly ITestOutputHelper _testOutput;

	public RestartHandoffTests(ITestOutputHelper testOutput) {
		_testOutput = testOutput;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CommittedChild_WaitsForParentExitBeforeEnteringTheTerminal(bool abruptExit) {
		await using var fixture = new ProbeFixture(_testOutput);
		fixture.Start(abruptExit ? "abort-after-commit" : "success");
		await WaitMarkerAsync(fixture.Root, "result");
		Assert.Equal("True|True", ReadMarker(fixture.Root, "result"));
		Assert.False(fixture.Parent!.HasExited);
		Assert.False(File.Exists(Path.Combine(fixture.Root, "child-terminal")));
		AssertDirectoryLocked(fixture.Root);
		using var child = OpenRecordedProcess(fixture.Root, "child");
		Assert.NotNull(child);

		WriteMarker(fixture.Root, "exit-parent");
		await fixture.Parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
		Assert.Equal(abruptExit ? 37 : 0, fixture.Parent.ExitCode);
		await WaitMarkerAsync(fixture.Root, "child-terminal");
		Assert.False(child!.HasExited);
		AssertDirectoryLocked(fixture.Root);
		WriteMarker(fixture.Root, "release-child");
		await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
		Assert.Equal(0, child.ExitCode);
		AssertDirectoryAvailable(fixture.Root);
	}

	[Theory]
	[InlineData("exit-before-connect")]
	[InlineData("crash-after-connect")]
	[InlineData("maintenance")]
	[InlineData("timeout")]
	public async Task FailedChild_IsStoppedBeforeTheOriginalOwnerResumes(string scenario) {
		await using var fixture = new ProbeFixture(_testOutput);
		fixture.Start(scenario);
		await WaitMarkerAsync(fixture.Root, "result");

		Assert.Equal("False|False", ReadMarker(fixture.Root, "result"));
		Assert.False(File.Exists(Path.Combine(fixture.Root, "child-terminal")));
		using var child = OpenRecordedProcess(fixture.Root, "child");
		Assert.True(child == null || child.HasExited);
		AssertDirectoryLocked(fixture.Root);
		if (scenario == "timeout") {
			Assert.True(File.Exists(Path.Combine(fixture.Root, "child-connected")));
			Assert.True(File.Exists(Path.Combine(fixture.Root, "child-stop-requested")));
		}
	}

	[Fact]
	public async Task ChildIgnoringStop_IsTerminatedWithoutTerminatingItsOwnChild() {
		await using var fixture = new ProbeFixture(_testOutput);
		fixture.Start("ignore-stop");
		await WaitMarkerAsync(fixture.Root, "result");

		using var child = OpenRecordedProcess(fixture.Root, "child");
		using var bystander = OpenRecordedProcess(fixture.Root, "bystander");
		var available = DataDirectoryOwnership.TryAcquire(Path.Combine(fixture.Root, "data"), out var ownership, out var failure);
		ownership?.Dispose();
		_testOutput.WriteLine($"Result: {ReadMarker(fixture.Root, "result")}; child running: {child is { HasExited: false }}; bystander running: {bystander is { HasExited: false }}; data lock available: {available}; lock failure: {failure}");
		Assert.Equal("False|False", ReadMarker(fixture.Root, "result"));
		Assert.True(File.Exists(Path.Combine(fixture.Root, "child-ignored-stop")));
		Assert.True(child == null || child.HasExited);
		Assert.NotNull(bystander);
		Assert.False(bystander!.HasExited);
		AssertDirectoryLocked(fixture.Root);
		WriteMarker(fixture.Root, "release-bystander");
		await bystander.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
		Assert.Equal(0, bystander.ExitCode);
	}

	[Fact]
	public async Task ParentExitBeforeCommit_StopsTheUncommittedChildAndReleasesOwnership() {
		await using var fixture = new ProbeFixture(_testOutput);
		fixture.Start("abort-before-commit");
		await WaitMarkerAsync(fixture.Root, "child-connected");
		AssertDirectoryLocked(fixture.Root);
		using var child = OpenRecordedProcess(fixture.Root, "child");
		Assert.NotNull(child);

		fixture.Parent!.Kill();
		await fixture.Parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
		await child!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

		Assert.Equal(0, child.ExitCode);
		Assert.True(File.Exists(Path.Combine(fixture.Root, "child-stop-requested")));
		Assert.False(File.Exists(Path.Combine(fixture.Root, "child-terminal")));
		AssertDirectoryAvailable(fixture.Root);
	}

	[Fact]
	public async Task ThirdProcessWinningOwnership_KeepsOriginalDataCommandsPaused() {
		await using var fixture = new ProbeFixture(_testOutput);
		fixture.Start("lock-race");
		await WaitMarkerAsync(fixture.Root, "result");

		Assert.Equal("False|True", ReadMarker(fixture.Root, "result"));
		Assert.Equal("True", ReadMarker(fixture.Root, "data-command-blocked"));
		Assert.True(File.Exists(Path.Combine(fixture.Root, "child-lock-rejected")));
		using var child = OpenRecordedProcess(fixture.Root, "child");
		Assert.True(child == null || child.HasExited);
		using var contender = OpenRecordedProcess(fixture.Root, "contender");
		Assert.NotNull(contender);
		Assert.False(contender!.HasExited);
		AssertDirectoryLocked(fixture.Root);
		WriteMarker(fixture.Root, "release-contender");
		await contender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
		Assert.Equal(0, contender.ExitCode);
		AssertDirectoryAvailable(fixture.Root);
	}

	internal static async Task<int> RunProbeAsync(string mode, string root) {
		var role = mode.StartsWith("parent-", StringComparison.Ordinal) ? "parent"
			: mode.StartsWith("child-", StringComparison.Ordinal) ? "child" : mode;
		using var current = Process.GetCurrentProcess();
		WriteMarker(root, role + ".pid", $"{current.Id}|{current.StartTime.ToUniversalTime().Ticks}");
		try {
			if (role == "parent") return await RunParentAsync(mode[7..], root);
			if (role == "child") return await RunChildAsync(mode[6..], root);
			if (role == "contender") {
				if (!DataDirectoryOwnership.TryAcquire(Path.Combine(root, "data"), out var ownership, out var failure)) {
					throw new IOException(failure);
				}
				using (ownership) {
					WriteMarker(root, "contender-owned");
					await WaitMarkerAsync(root, "release-contender");
				}
				return 0;
			}
			if (role == "bystander") {
				WriteMarker(root, "bystander-ready");
				await WaitMarkerAsync(root, "release-bystander");
				return 0;
			}
			return 2;
		} catch (Exception e) {
			WriteMarker(root, role + ".error", e.ToString());
			return 91;
		}
	}

	private static async Task<int> RunParentAsync(string scenario, string root) {
		var data = Path.Combine(root, "data");
		Directory.CreateDirectory(data);
		Databases.ConfigureDataDirectory(data);
		Databases.Init();
		await Databases.ShutdownAsync();
		try {
			var info = TestChildProcess.StartInfo("restart-probe", "child-" + scenario, root);
			var restarted = await RestartHandoff.RunAsync(info,
				startupBudget: TimeSpan.FromSeconds(scenario == "timeout" ? 5 : 15),
				shutdownBudget: TimeSpan.FromSeconds(1));
			var command = await CliDispatcher.ExecuteAsync("db status");
			WriteMarker(root, "data-command-blocked", (command.Output == L.Get("MaintenanceBusy")).ToString());
			WriteMarker(root, "result", $"{restarted}|{HostMaintenance.DataAccessPaused}");
			await WaitMarkerAsync(root, "exit-parent");
			if (scenario == "abort-after-commit") Environment.Exit(37);
			return 0;
		} finally {
			Databases.ReleaseOwnership();
		}
	}

	private static async Task<int> RunChildAsync(string scenario, string root) {
		if (scenario == "exit-before-connect") return 31;
		using var stop = new CancellationTokenSource();
		await using var session = await RestartHandoff.ConnectAsync(() => {
			WriteMarker(root, "child-stop-requested");
			if (scenario != "ignore-stop") stop.Cancel();
		}, stop.Token);
		if (session == null) throw new InvalidOperationException("Restart child did not receive a handoff session");
		Databases.ConfigureDataDirectory(Path.Combine(root, "data"));
		try {
			if (scenario == "lock-race") {
				using var contender = Process.Start(TestChildProcess.StartInfo("restart-probe", "contender", root));
				if (contender == null) throw new InvalidOperationException("Contender did not start");
				await WaitMarkerAsync(root, "contender-owned", stop.Token);
				try {
					Databases.Init();
					throw new InvalidOperationException("Restart child unexpectedly acquired the contender's lock");
				} catch (IOException e) {
					WriteMarker(root, "child-lock-rejected", e.Message);
				}
				if (await session.CompleteStartupAsync(false, stop.Token)) throw new InvalidOperationException("Lock failure was reported as successful startup");
				return 0;
			}
			Databases.Init();
			WriteMarker(root, "child-connected");
			if (scenario == "crash-after-connect") Environment.Exit(33);
			if (scenario is "timeout" or "abort-before-commit") {
				await WaitMarkerAsync(root, "allow-child-ready", stop.Token);
				return 0;
			}
			if (scenario == "ignore-stop") {
				using var bystander = Process.Start(TestChildProcess.StartInfo("restart-probe", "bystander", root));
				if (bystander == null) throw new InvalidOperationException("Bystander did not start");
				await WaitMarkerAsync(root, "bystander-ready");
				if (await session.CompleteStartupAsync(false, CancellationToken.None)) throw new InvalidOperationException("Maintenance mode was reported as successful startup");
				WriteMarker(root, "child-ignored-stop");
				await WaitMarkerAsync(root, "release-child");
				return 0;
			}
			if (!await session.CompleteStartupAsync(scenario != "maintenance", stop.Token)) return 0;
			WriteMarker(root, "child-terminal");
			await WaitMarkerAsync(root, "release-child", stop.Token);
			return 0;
		} catch (OperationCanceledException) when (stop.IsCancellationRequested) {
			WriteMarker(root, "child-stopped");
			return 0;
		} finally {
			await Databases.ShutdownAsync();
			Databases.ReleaseOwnership();
		}
	}

	private static void AssertDirectoryLocked(string root) {
		var acquired = DataDirectoryOwnership.TryAcquire(Path.Combine(root, "data"), out var ownership, out _);
		ownership?.Dispose();
		Assert.False(acquired);
	}

	private static void AssertDirectoryAvailable(string root) {
		Assert.True(DataDirectoryOwnership.TryAcquire(Path.Combine(root, "data"), out var ownership, out var failure), failure);
		ownership!.Dispose();
	}

	private static string ReadMarker(string root, string name) {
		return File.ReadAllText(Path.Combine(root, name));
	}

	private static void WriteMarker(string root, string name, string value = "ready") {
		var path = Path.Combine(root, name);
		File.WriteAllText(path + ".tmp", value);
		File.Move(path + ".tmp", path, true);
	}

	private static async Task WaitMarkerAsync(string root, string name, CancellationToken ct = default) {
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
		budget.CancelAfter(TimeSpan.FromSeconds(30));
		while (!File.Exists(Path.Combine(root, name))) {
			var errors = Directory.GetFiles(root, "*.error");
			if (errors.Length > 0) throw new InvalidOperationException(string.Join("\n", errors.Select(File.ReadAllText)));
			await Task.Delay(20, budget.Token);
		}
	}

	private static Process? OpenRecordedProcess(string root, string role) {
		var path = Path.Combine(root, role + ".pid");
		if (!File.Exists(path)) return null;
		var identity = File.ReadAllText(path).Split('|');
		Process process;
		try {
			process = Process.GetProcessById(int.Parse(identity[0], CultureInfo.InvariantCulture));
		} catch (ArgumentException) {
			return null;
		}
		try {
			if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != long.Parse(identity[1], CultureInfo.InvariantCulture)) {
				process.Dispose();
				return null;
			}
			return process;
		} catch (InvalidOperationException) {
			process.Dispose();
			return null;
		}
	}

	private sealed class ProbeFixture : IAsyncDisposable {
		private readonly ITestOutputHelper _testOutput;
		private Task<string>? _output;
		private Task<string>? _error;

		internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pengin_handoff_" + Guid.NewGuid().ToString("N"));
		internal Process? Parent { get; private set; }

		internal ProbeFixture(ITestOutputHelper testOutput) {
			_testOutput = testOutput;
			Directory.CreateDirectory(Root);
		}

		internal void Start(string scenario) {
			Parent = Process.Start(TestChildProcess.StartInfo("restart-probe", "parent-" + scenario, Root))
				?? throw new InvalidOperationException("Restart parent did not start");
			_output = Parent.StandardOutput.ReadToEndAsync();
			_error = Parent.StandardError.ReadToEndAsync();
		}

		public async ValueTask DisposeAsync() {
			foreach (var name in new[] { "exit-parent", "release-child", "release-bystander", "release-contender" }) WriteMarker(Root, name);
			if (Parent != null) await StopAsync(Parent);
			foreach (var file in Directory.GetFiles(Root, "*.pid")) {
				using var process = OpenRecordedProcess(Root, Path.GetFileNameWithoutExtension(file));
				if (process != null) await StopAsync(process);
			}
			if (_output != null) _testOutput.WriteLine("Parent stdout:\n" + await _output.WaitAsync(TimeSpan.FromSeconds(5)));
			if (_error != null) _testOutput.WriteLine("Parent stderr:\n" + await _error.WaitAsync(TimeSpan.FromSeconds(5)));
			Parent?.Dispose();
			Directory.Delete(Root, true);
		}

		private static async Task StopAsync(Process process) {
			if (process.HasExited) return;
			try {
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
			} catch (TimeoutException) {
				if (!process.HasExited) process.Kill();
				await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			}
		}
	}
}
