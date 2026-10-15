using Discord.Interactions;
using Pengin1011.Core;
using Pengin1011.Core.Modules;

namespace Pengin1011.Modules.AIPresetAdmin;

[ModuleAvailability(typeof(AIPresetAdminRuntime))]
[RequireBotAdmin]
[Group("preset", "AI 预设管理")]
public class AIPresetAdmin : InteractionModuleBase<SocketInteractionContext> {
	[SlashCommand("list", "列出全部 AI 预设")]
	public async Task List() {
		await RespondAsync(AIPresetAdminLogic.List(), ephemeral: true);
	}

	[SlashCommand("show", "查看某个预设的全部字段")]
	public async Task Show([Summary("name", "预设名")] string name) {
		await RespondAsync(AIPresetAdminLogic.Show(name), ephemeral: true);
	}

	[SlashCommand("add", "新增一个 AI 预设")]
	public async Task Add(
		[Summary("name", "预设名")] string name,
		[Summary("provider", "协议")] AIPresetProviderChoice provider,
		[Summary("apikey", "API Key")] string? apiKey = null,
		[Summary("baseurl", "BaseUrl（openai 必填）")] string? baseUrl = null,
		[Summary("project", "GCP Project（vertex 必填）")] string? project = null,
		[Summary("location", "GCP Location（vertex 必填）")] string? location = null,
		[Summary("model", "模型名")] string? model = null) {
		var fields = new AIPresetAddFields(apiKey, baseUrl, project, location, model);
		await RespondAsync(AIPresetAdminLogic.Add(name, provider.ToString(), fields), ephemeral: true);
	}

	[SlashCommand("set", "修改某个预设的单个字段")]
	public async Task Set(
		[Summary("name", "预设名")] string name,
		[Summary("field", "要修改的字段")] AIPresetFieldChoice field,
		[Summary("value", "新值（改名时填新名）")] string value) {
		await RespondAsync(AIPresetAdminLogic.Set(name, field.ToString(), value), ephemeral: true);
	}

	[SlashCommand("remove", "删除一个 AI 预设")]
	public async Task Remove([Summary("name", "预设名")] string name) {
		await RespondAsync(AIPresetAdminLogic.Remove(name), ephemeral: true);
	}
}

public enum AIPresetProviderChoice {
	openai,
	gemini,
	vertex,
}

public enum AIPresetFieldChoice {
	name,
	provider,
	apikey,
	baseurl,
	project,
	location,
	model,
	temperature,
	topp,
	maxoutputtokens,
}
