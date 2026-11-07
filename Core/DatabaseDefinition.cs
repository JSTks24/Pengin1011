using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Core;

internal sealed record DatabaseDefinition(string Id, Type ContextType, Func<string, DbContext> Create);
