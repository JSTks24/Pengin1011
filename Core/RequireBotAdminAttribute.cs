using Discord;
using Discord.Interactions;

namespace Pengin1011.Core;

public sealed class RequireBotAdminAttribute : PreconditionAttribute {
	public override async Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services) {
		if (context.Guild != null && context.User is IGuildUser member && member.RoleIds.Any(AppConfig.Discord.AdminRoleIds.Contains)) return PreconditionResult.FromSuccess();
		var owner = await new RequireOwnerAttribute().CheckRequirementsAsync(context, commandInfo, services);
		return owner.IsSuccess ? owner : PreconditionResult.FromError(Loc.Get("AdminPermissionRequired"));
	}
}
