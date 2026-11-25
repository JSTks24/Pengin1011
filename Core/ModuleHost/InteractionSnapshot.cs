using Discord.Interactions;

namespace Pengin1011.Core;

public sealed record InteractionSnapshot(InteractionService Service, IReadOnlyList<LoadedModule> Modules);
