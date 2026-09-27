using Discord;
using Discord.Interactions;

namespace QingQiu1011.Core.Modules;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ModuleAvailabilityAttribute : PreconditionAttribute {
	private readonly Type _runtimeType;

	public ModuleAvailabilityAttribute(Type runtimeType) {
		_runtimeType = runtimeType ?? throw new ArgumentNullException(nameof(runtimeType));
	}

	public Type RuntimeType => _runtimeType;

	public override Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services) {
		var run = ModuleRegistry.RunOf(_runtimeType.Assembly);
		if (run == null || run.State != ModuleState.Ready) {
			return Task.FromResult(PreconditionResult.FromError("模块暂不可用（未就绪或已停用）"));
		}
		return Task.FromResult(PreconditionResult.FromSuccess());
	}
}
