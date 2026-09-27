using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QingQiu1011.Core;
using QingQiu1011.Core.Modules;
using QingQiu1011.Services.Discord;

namespace QingQiu1011.Modules.FakeModule;

[ModuleAvailability(typeof(FakeModuleRuntime))]
public class FakeModule : InteractionModuleBase<FakeInteractionContext> {
	public static int CommandCount;
	public static Exception? CommandFailure;
	public static TaskCompletionSource? CommandGate;

	public static void ResetCommands() {
		CommandCount = 0;
		CommandFailure = null;
		CommandGate = null;
	}

	[SlashCommand("fakeping", "测试命令")]
	public async Task FakePing() {
		CommandCount++;
		if (CommandFailure != null) throw CommandFailure;
		if (CommandGate != null) await CommandGate.Task;
	}
}

public sealed class FakeModuleRuntime : IModuleRuntime {
	public static int InitCount;
	public static int StopCount;
	public static int BackgroundEventCount;
	public static bool EnableBackgroundWork;
	public static bool InitIgnoreCancel;
	public static bool StopEntryTokenCancelled;
	public static Func<Exception>? OnInitFailure;
	public static Func<Exception>? OnStopFailure;
	public static TaskCompletionSource? InitGate;
	public static TaskCompletionSource? StopGate;
	public static TaskCompletionSource? StopSyncGate;
	public static TaskCompletionSource? InitEntered;
	public static TaskCompletionSource? StopEntered;
	public static List<string> Events = [];
	public static List<object> CreatedResources { get; } = [];

	private IDisposable? _backgroundSubscription;

	public static void Reset() {
		InitCount = 0;
		StopCount = 0;
		BackgroundEventCount = 0;
		EnableBackgroundWork = false;
		InitIgnoreCancel = false;
		StopEntryTokenCancelled = false;
		OnInitFailure = null;
		OnStopFailure = null;
		InitGate = null;
		StopGate = null;
		StopSyncGate = null;
		InitEntered = null;
		StopEntered = null;
		Events.Clear();
		CreatedResources.Clear();
	}

	private static TaskCompletionSource NewGate() {
		return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	public async Task InitializeAsync(CancellationToken ct) {
		InitCount++;
		Events.Add("init-entered");
		(InitEntered ??= NewGate()).TrySetResult();
		try {
			if (EnableBackgroundWork) {
				var run = ModuleRegistry.RunOf(GetType().Assembly);
				if (run != null) {
					CreatedResources.Add(new object());
					_backgroundSubscription = DiscordGateway.SubscribeMessage(run, OnBackgroundMessage);
				}
			}
			if (InitGate != null) {
				if (InitIgnoreCancel) await InitGate.Task;
				else await InitGate.Task.WaitAsync(ct);
			}
			if (OnInitFailure != null) throw OnInitFailure();
		} finally {
			Events.Add("init-finished");
		}
	}

	public async Task StopAsync(CancellationToken ct) {
		StopCount++;
		StopEntryTokenCancelled = ct.IsCancellationRequested;
		Events.Add("stop-entered");
		(StopEntered ??= NewGate()).TrySetResult();
		try {
			if (StopSyncGate != null) StopSyncGate.Task.Wait();
			_backgroundSubscription?.Dispose();
			_backgroundSubscription = null;
			if (StopGate != null) await StopGate.Task.WaitAsync(ct);
			if (OnStopFailure != null) throw OnStopFailure();
		} finally {
			Events.Add("stop-finished");
		}
	}

	private Task OnBackgroundMessage(SocketMessage message, CancellationToken ct) {
		BackgroundEventCount++;
		return Task.CompletedTask;
	}
}

public sealed class FakeItem {
	public long Id { get; set; }

	public string Name { get; set; } = "";
}

public class FakeModuleDbContext : DbContext {
	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		Databases.Configure(GetType(), options);
	}

	public DbSet<FakeItem> Items => Set<FakeItem>();
}

[DbContext(typeof(FakeModuleDbContext))]
[Migration("20260925000001_Init")]
public sealed class FakeModuleInitMigration : Migration {
	protected override void Up(MigrationBuilder migrationBuilder) {
		migrationBuilder.CreateTable(
			name: "Items",
			columns: table => new {
				Id = table.Column<long>(type: "INTEGER", nullable: false)
					.Annotation("Sqlite:Autoincrement", true),
				Name = table.Column<string>(type: "TEXT", nullable: false)
			},
			constraints: table => {
				table.PrimaryKey("PK_Items", x => x.Id);
			});
	}

	protected override void Down(MigrationBuilder migrationBuilder) {
		migrationBuilder.DropTable(name: "Items");
	}
}
