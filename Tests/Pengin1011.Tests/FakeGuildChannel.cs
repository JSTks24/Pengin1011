using Discord;

namespace Pengin1011.Tests;

public sealed class FakeGuildChannel : FakeMessageChannel, IGuildChannel {
	public IGuild Guild => throw new NotSupportedException();
	public ulong GuildId => 0;
	public int Position => 0;
	public ChannelFlags Flags => ChannelFlags.None;
	public IReadOnlyCollection<Overwrite> PermissionOverwrites => [];

	public Task DeleteAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task ModifyAsync(Action<GuildChannelProperties> func, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public OverwritePermissions? GetPermissionOverwrite(IUser user) {
		throw new NotSupportedException();
	}

	public OverwritePermissions? GetPermissionOverwrite(IRole role) {
		throw new NotSupportedException();
	}

	public Task AddPermissionOverwriteAsync(IUser user, OverwritePermissions permissions, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task AddPermissionOverwriteAsync(IRole role, OverwritePermissions permissions, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task RemovePermissionOverwriteAsync(IUser user, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	public Task RemovePermissionOverwriteAsync(IRole role, RequestOptions? options = null) {
		throw new NotSupportedException();
	}

	IAsyncEnumerable<IReadOnlyCollection<IGuildUser>> IGuildChannel.GetUsersAsync(CacheMode mode, RequestOptions? options) {
		throw new NotSupportedException();
	}

	Task<IGuildUser> IGuildChannel.GetUserAsync(ulong id, CacheMode mode, RequestOptions? options) {
		throw new NotSupportedException();
	}
}
