using Discord;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;

namespace Pengin1011.Services.Discord;

public static class Threads {
	private const int RestoreArchiveBudgetMs = 5000;

	public static async Task<bool> JoinAsync(ulong threadId) {
		if (await Channels.ResolveAsync(threadId) is not IThreadChannel thread) return false;
		try {
			await thread.JoinAsync();
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(Threads), e, Localizer.Format("ThreadJoinFailed", threadId));
			return false;
		}
	}

	public static async Task<bool> SetArchivedAsync(ulong threadId, bool archived) {
		if (await Channels.ResolveAsync(threadId) is not IThreadChannel thread) return false;
		try {
			await thread.ModifyAsync(properties => properties.Archived = archived);
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(Threads), e, Localizer.Format("ThreadArchiveSetFailed", threadId, archived));
			return false;
		}
	}

	public static async Task<bool> SetAppliedTagsAsync(ulong threadId, IReadOnlyCollection<ulong> tagIds, CancellationToken ct = default) {
		if (await Channels.ResolveAsync(threadId) is not IThreadChannel thread) return false;
		return await SetAppliedTagsAsync(thread, tagIds, ct);
	}

	internal static async Task<bool> SetAppliedTagsAsync(IThreadChannel thread, IReadOnlyCollection<ulong> tagIds, CancellationToken ct = default) {
		var wasArchived = thread.IsArchived;
		var unarchived = false;
		var success = false;
		try {
			if (wasArchived) {
				await thread.ModifyAsync(properties => properties.Archived = false, MakeOptions(ct));
				unarchived = true;
			}
			Action<ThreadChannelProperties> applyTags = properties => properties.AppliedTags = new Optional<IEnumerable<ulong>>(tagIds);
			await thread.ModifyAsync(applyTags, MakeOptions(ct));
			success = true;
		} catch (Exception e) {
			Logger.Error(typeof(Threads), e, Localizer.Format("ThreadTagsSetFailed", thread.Id));
		}
		if (unarchived) {
			try {
				using var budget = new CancellationTokenSource(RestoreArchiveBudgetMs);
				await thread.ModifyAsync(properties => properties.Archived = true, MakeOptions(budget.Token));
			} catch (Exception e) {
				Logger.Error(typeof(Threads), e, Localizer.Format("ThreadArchiveRestoreFailed", thread.Id));
				success = false;
			}
		}
		return success;
	}

	private static RequestOptions? MakeOptions(CancellationToken ct) {
		return ct.CanBeCanceled ? new RequestOptions { CancelToken = ct } : null;
	}

	public static async Task<IReadOnlyList<IThreadChannel>> GetThreadsAsync(ulong forumChannelId, bool includeArchived = false, int archivedLimit = 50) {
		if (await Channels.ResolveAsync(forumChannelId) is not IThreadContainerChannel forum) return [];

		var threads = new List<IThreadChannel>();
		try {
			threads.AddRange(await forum.GetActiveThreadsAsync());
		} catch (Exception e) {
			Logger.Error(typeof(Threads), e, Localizer.Format("ActiveThreadsEnumFailed", forumChannelId));
		}
		if (includeArchived) {
			try {
				var archived = await forum.GetPublicArchivedThreadsAsync(archivedLimit);
				threads.AddRange(archived);
			} catch (Exception e) {
				Logger.Error(typeof(Threads), e, Localizer.Format("ArchivedThreadsEnumFailed", forumChannelId));
			}
		}
		return threads;
	}

	public static IReadOnlyList<ForumTag> GetAvailableTags(ulong forumChannelId) {
		return Channels.Resolve(forumChannelId) is IForumChannel forum ? forum.Tags.ToList() : [];
	}
}
