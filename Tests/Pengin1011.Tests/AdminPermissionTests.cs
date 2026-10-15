using System.Reflection;
using System.Text.Json;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.AIPresetAdmin;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class AdminPermissionTests : IDisposable {
	private static readonly IServiceProvider _services = new EmptyServices();
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pengin1011_admin_{Guid.NewGuid():N}");

	public AdminPermissionTests() {
		Directory.CreateDirectory(_directory);
		Configure();
	}

	public void Dispose() {
		Configure();
		Directory.Delete(_directory, true);
	}

	[Fact]
	public async Task ConfiguredRole_AllowsNonOwnerThroughActualPrecondition() {
		Configure("111", "222");
		var context = CreateContext(100, [333, 222]);

		var result = await new RequireBotAdminAttribute().CheckRequirementsAsync(context, null!, _services);

		Assert.True(result.IsSuccess);
		var owner = await new RequireOwnerAttribute().CheckRequirementsAsync(context, null!, _services);
		Assert.False(owner.IsSuccess);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task BotOwner_IsAllowedWithoutConfiguredRole(bool inGuild) {
		var context = CreateContext(9000, [], inGuild);

		var owner = await new RequireOwnerAttribute().CheckRequirementsAsync(context, null!, _services);
		var result = await new RequireBotAdminAttribute().CheckRequirementsAsync(context, null!, _services);

		Assert.True(owner.IsSuccess);
		Assert.True(result.IsSuccess);
	}

	[Fact]
	public async Task EmptyRoleConfiguration_RejectsNonOwner() {
		var context = CreateContext(100, [111]);

		var result = await new RequireBotAdminAttribute().CheckRequirementsAsync(context, null!, _services);

		Assert.False(result.IsSuccess);
		Assert.Equal(L.Get("AdminPermissionRequired"), result.ErrorReason);
	}

	[Fact]
	public async Task UnconfiguredRole_RejectsNonOwner() {
		Configure("111");
		var context = CreateContext(100, [222, 333]);

		var result = await new RequireBotAdminAttribute().CheckRequirementsAsync(context, null!, _services);

		Assert.False(result.IsSuccess);
	}

	[Fact]
	public async Task ServerAdministrator_HasNoImplicitBypass() {
		Configure("111");
		var context = CreateContext(100, [222], administrator: true);
		Assert.True(((IGuildUser)context.User).GuildPermissions.Administrator);

		var result = await new RequireBotAdminAttribute().CheckRequirementsAsync(context, null!, _services);

		Assert.False(result.IsSuccess);
	}

	[Fact]
	public async Task DirectMessage_CannotUseGuildRoleToAuthorizeNonOwner() {
		Configure("111");
		var context = CreateContext(100, [111], inGuild: false);

		var result = await new RequireBotAdminAttribute().CheckRequirementsAsync(context, null!, _services);

		Assert.False(result.IsSuccess);
	}

	[Fact]
	public async Task ManagementModules_RegisterOneSharedAdminPrecondition() {
		using var client = new DiscordSocketClient();
		using var service = new InteractionService(client.Rest, new InteractionServiceConfig { AutoServiceScopes = false });
		var moduleTypes = new[] { typeof(Pengin1011.Modules.AIPresetAdmin.AIPresetAdmin), typeof(DatabaseAdmin) };
		foreach (var moduleType in moduleTypes) {
			var module = await service.AddModuleAsync(moduleType, _services);
			Assert.Single(module.Preconditions.OfType<RequireBotAdminAttribute>());
			Assert.Single(module.Preconditions.OfType<ModuleAvailabilityAttribute>());
			Assert.Empty(module.Preconditions.OfType<RequireOwnerAttribute>());
			Assert.NotEmpty(module.SlashCommands);
			Assert.All(module.SlashCommands, command => Assert.Same(module, command.Module));
		}
	}

	[Fact]
	public void Config_AdminRoleIds_ParseStringsAndRemoveDuplicates() {
		var path = WriteConfig([" 111 ", "222", "111", ulong.MaxValue.ToString()]);

		Assert.True(AppConfig.TryLoad(path, out var errors));

		Assert.Empty(errors);
		Assert.Equal(new ulong[] { 111, 222, ulong.MaxValue }, AppConfig.Discord.AdminRoleIds);
	}

	[Theory]
	[InlineData("0")]
	[InlineData("-1")]
	[InlineData("invalid")]
	[InlineData("18446744073709551616")]
	[InlineData("")]
	public void Config_InvalidAdminRoleId_RejectsAndKeepsPreviousRoles(string roleId) {
		Configure("111");

		Assert.False(AppConfig.TryLoad(WriteConfig([roleId]), out var errors));

		Assert.Contains(errors, error => error.Contains(L.Prefix("AdminRoleInvalid")));
		Assert.Equal(new ulong[] { 111 }, AppConfig.Discord.AdminRoleIds);
	}

	[Fact]
	public void Config_DataDirectorySurvivesMissingDiscordToken() {
		const string dataDirectory = "data-for-maintenance";

		Assert.False(AppConfig.TryLoad(WriteConfig(["111"], token: "", dataDirectory: dataDirectory), out var errors));

		Assert.Contains(errors, error => error.Contains("Discord.Token"));
		Assert.True(AppConfig.PathConfigurationValid);
		Assert.Equal(dataDirectory, AppConfig.Databases.DataDirectory);
	}

	private void Configure(params string[] roles) {
		Assert.True(AppConfig.TryLoad(WriteConfig(roles), out var errors));
		Assert.Empty(errors);
	}

	private string WriteConfig(string[] roles, string token = "fictional-token", string? dataDirectory = null) {
		var path = Path.Combine(_directory, "config.json");
		File.WriteAllText(path, JsonSerializer.Serialize(new {
			Discord = new { Token = token, AdminRoleIds = roles },
			Databases = new { DataDirectory = dataDirectory },
		}));
		return path;
	}

	private static IInteractionContext CreateContext(ulong userId, IReadOnlyCollection<ulong> roles, bool inGuild = true, bool administrator = false) {
		var user = Proxy<IGuildUser>(method => method.Name switch {
			"get_Id" => userId,
			"get_RoleIds" => roles,
			"get_GuildPermissions" => new GuildPermissions(administrator ? (ulong)GuildPermission.Administrator : 0),
			_ => throw new InvalidOperationException(method.Name),
		});
		var owner = Proxy<IUser>(method => method.Name == "get_Id" ? 9000UL : throw new InvalidOperationException(method.Name));
		var application = Proxy<IApplication>(method => method.Name switch {
			"get_Owner" => owner,
			"get_Team" => null,
			_ => throw new InvalidOperationException(method.Name),
		});
		var client = Proxy<IDiscordClient>(method => method.Name switch {
			"get_TokenType" => TokenType.Bot,
			"GetApplicationInfoAsync" => Task.FromResult(application),
			_ => throw new InvalidOperationException(method.Name),
		});
		var guild = inGuild ? Proxy<IGuild>(method => throw new InvalidOperationException(method.Name)) : null;
		return Proxy<IInteractionContext>(method => method.Name switch {
			"get_User" => user,
			"get_Client" => client,
			"get_Guild" => guild,
			_ => throw new InvalidOperationException(method.Name),
		});
	}

	private static T Proxy<T>(Func<MethodInfo, object?> invoke) where T : class {
		var proxy = DispatchProxy.Create<T, PermissionProxy>();
		((PermissionProxy)(object)proxy).InvokeHandler = invoke;
		return proxy;
	}

	public class PermissionProxy : DispatchProxy {
		public Func<MethodInfo, object?> InvokeHandler { get; set; } = null!;

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
			return InvokeHandler(targetMethod!);
		}
	}

	private sealed class EmptyServices : IServiceProvider {
		public object? GetService(Type serviceType) => null;
	}
}
