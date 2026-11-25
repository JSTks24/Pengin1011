using Discord;

namespace Pengin1011.Tests;

public sealed class FakeUser : IUser {
	public ulong Id { get; set; }
	public Exception? DmException { get; set; }
	public IDMChannel? DmChannel { get; set; }

	public string Username => "fake-user";
	public bool IsBot => false;
	public bool IsWebhook => false;
	public string AvatarId => throw new NotSupportedException();
	public string Discriminator => "0";
	public ushort DiscriminatorValue => 0;
	public string GlobalName => "Fake User";
	public string Mention => $"<@{Id}>";
	public UserStatus Status => UserStatus.Offline;
	public IReadOnlyCollection<ClientType> ActiveClients => [];
	public IReadOnlyCollection<IActivity> Activities => [];

	public Task<IDMChannel> CreateDMChannelAsync(RequestOptions? options = null) {
		if (DmException != null) throw DmException;
		return Task.FromResult(DmChannel ?? throw new NotSupportedException());
	}

	public string GetAvatarUrl(ImageFormat format = ImageFormat.Auto, ushort size = 128) {
		throw new NotSupportedException();
	}

	public string GetDefaultAvatarUrl() {
		throw new NotSupportedException();
	}

	public string GetDisplayAvatarUrl(ImageFormat format = ImageFormat.Auto, ushort size = 128) {
		throw new NotSupportedException();
	}

	public string GetAvatarDecorationUrl() {
		throw new NotSupportedException();
	}

	public UserProperties? PublicFlags => null;
	public string AvatarDecorationHash => throw new NotSupportedException();
	public ulong? AvatarDecorationSkuId => null;
	public PrimaryGuild? PrimaryGuild => null;
	public DateTimeOffset CreatedAt => DateTimeOffset.UnixEpoch;

	public Task UpdateAsync(RequestOptions? options = null) {
		throw new NotSupportedException();
	}
}
