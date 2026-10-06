using Discord.WebSocket;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class GatewayStartTests {
	private static void Reset() {
		DiscordGateway.DetachForTest();
		DiscordGateway.ResetClientOverridesForTest();
	}

	[Fact]
	public async Task StartAsync_CompletedLoginAndStart_ReturnsTrue() {
		DiscordGateway.Init("test-token");
		DiscordGateway.ClientLoginForTest = static _ => Task.CompletedTask;
		DiscordGateway.ClientStartForTest = static _ => Task.CompletedTask;
		try {
			Assert.True(await DiscordGateway.StartAsync());
		} finally {
			Reset();
		}
	}

	[Fact]
	public async Task StartAsync_LoginFails_ReturnsFalse() {
		DiscordGateway.Init("test-token");
		DiscordGateway.ClientLoginForTest = static _ => throw new InvalidOperationException("登录失败");
		DiscordGateway.ClientStartForTest = static _ => Task.CompletedTask;
		try {
			Assert.False(await DiscordGateway.StartAsync());
		} finally {
			Reset();
		}
	}

	[Fact]
	public async Task StartAsync_WithoutToken_ReturnsFalse() {
		DiscordGateway.Init(new DiscordSocketClient());
		try {
			Assert.False(await DiscordGateway.StartAsync());
		} finally {
			Reset();
		}
	}

	[Fact]
	public async Task StartAsync_LoginHangs_CancelledToken_ThrowsWithoutWaitingForLogin() {
		DiscordGateway.Init("test-token");
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		DiscordGateway.ClientLoginForTest = _ => gate.Task;
		DiscordGateway.ClientStartForTest = static _ => Task.CompletedTask;
		using var cts = new CancellationTokenSource();
		cts.CancelAfter(50);
		try {
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiscordGateway.StartAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
		} finally {
			gate.SetResult();
			Reset();
		}
	}

	[Fact]
	public async Task StartAsync_LoginHangs_UncancelledToken_KeepsWaitingUntilLoginEnds() {
		DiscordGateway.Init("test-token");
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		DiscordGateway.ClientLoginForTest = _ => gate.Task;
		DiscordGateway.ClientStartForTest = static _ => Task.CompletedTask;
		try {
			var start = DiscordGateway.StartAsync(CancellationToken.None);
			Assert.False(start.IsCompleted);

			gate.SetResult();

			Assert.True(await start.WaitAsync(TimeSpan.FromSeconds(5)));
		} finally {
			Reset();
		}
	}
}
