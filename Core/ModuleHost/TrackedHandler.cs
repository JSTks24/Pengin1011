namespace Pengin1011.Core;

public sealed record TrackedHandler(LoadedModule? Owner, Func<CancellationToken, Task> Handler);
