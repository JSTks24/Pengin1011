using Discord;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

public sealed class ThreadsTests {
	[Fact]
	public async Task SetTags_NotArchived_AppliesTagsWithoutArchiveOperations() {
		var thread = new FakeThreadChannel { IsArchived = false };

		var result = await Threads.SetAppliedTagsAsync(thread, [10, 20]);

		Assert.True(result);
		Assert.False(thread.IsArchived);
		var modification = Assert.Single(thread.Modifications);
		Assert.Null(modification.Archived);
		Assert.Equal([10, 20], modification.AppliedTags);
	}

	[Fact]
	public async Task SetTags_WasArchived_UnappliesTagsThenRestoresArchive() {
		var thread = new FakeThreadChannel { IsArchived = true };

		var result = await Threads.SetAppliedTagsAsync(thread, [30]);

		Assert.True(result);
		Assert.True(thread.IsArchived);
		Assert.Equal(3, thread.Modifications.Count);
		Assert.Equal(false, thread.Modifications[0].Archived);
		Assert.Null(thread.Modifications[1].Archived);
		Assert.Equal([30], thread.Modifications[1].AppliedTags);
		Assert.Equal(true, thread.Modifications[2].Archived);
	}

	[Fact]
	public async Task SetTags_EmptyCollection_ClearsTags() {
		var thread = new FakeThreadChannel { IsArchived = false };

		var result = await Threads.SetAppliedTagsAsync(thread, []);

		Assert.True(result);
		var modification = Assert.Single(thread.Modifications);
		Assert.Empty(modification.AppliedTags);
	}

	[Fact]
	public async Task SetTags_UnarchiveFails_TagsNotApplied_NoRestoreAttempt() {
		var thread = new FakeThreadChannel {
			IsArchived = true,
			OnModify = properties => properties.Archived.IsSpecified && properties.Archived.Value == false ? new InvalidOperationException("解档失败") : null,
		};

		var result = await Threads.SetAppliedTagsAsync(thread, [40]);

		Assert.False(result);
		Assert.True(thread.IsArchived);
		Assert.Empty(thread.Modifications);
	}

	[Fact]
	public async Task SetTags_TagChangeFails_ArchiveStillRestored() {
		var thread = new FakeThreadChannel {
			IsArchived = true,
			OnModify = properties => properties.AppliedTags.IsSpecified ? new InvalidOperationException("改标签失败") : null,
		};

		var result = await Threads.SetAppliedTagsAsync(thread, [50]);

		Assert.False(result);
		Assert.True(thread.IsArchived);
		Assert.Equal(2, thread.Modifications.Count);
		Assert.Equal(false, thread.Modifications[0].Archived);
		Assert.Empty(thread.Modifications[0].AppliedTags);
		Assert.Equal(true, thread.Modifications[1].Archived);
	}

	[Fact]
	public async Task SetTags_RestoreFails_ReturnsFalse() {
		var thread = new FakeThreadChannel {
			IsArchived = true,
			OnModify = properties => properties.Archived.IsSpecified && properties.Archived.Value == true ? new InvalidOperationException("恢复归档失败") : null,
		};

		var result = await Threads.SetAppliedTagsAsync(thread, [60]);

		Assert.False(result);
		Assert.False(thread.IsArchived);
		Assert.Equal(2, thread.Modifications.Count);
		Assert.Equal([60], thread.Modifications[1].AppliedTags);
	}

	[Fact]
	public async Task SetTags_CancelledToken_TagsModificationStillBounded() {
		var thread = new FakeThreadChannel { IsArchived = false };
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var result = await Threads.SetAppliedTagsAsync(thread, [70], cts.Token);

		Assert.False(result);
		Assert.Empty(thread.Modifications);
	}
}
