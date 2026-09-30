namespace MtgaCollectionAdvisor.Core.Notices;

/// <summary>What this copy knows when choosing a notice: the time, its card sets, and what the player dismissed.</summary>
public sealed record NoticeContext(DateTimeOffset Now, Func<string, bool> HasSet, IReadOnlySet<string> Dismissed);

/// <summary>
/// Which notice to show (#96). A notice starts at <see cref="Notice.ShowFrom"/>, once its set's
/// cards are in this copy when it names one (they come with the player's MTG Arena update, #101).
/// It ends 30 days later, or earlier when a newer notice starts, so notices don't pile up; an
/// explicit <see cref="Notice.ShowUntil"/> replaces both, which is how the maintainer keeps one
/// alive or ends it early. The active ones show one at a time, oldest first, until dismissed.
/// </summary>
public static class NoticeSelector
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);

    /// <summary>The first notice to show, oldest first; null when none is due.</summary>
    public static Notice? Next(IReadOnlyList<Notice> notices, NoticeContext context) =>
        notices
            .Select((notice, order) => (notice, order))
            .OrderBy(n => n.notice.ShowFrom)
            .ThenBy(n => n.order)
            .Select(n => n.notice)
            .FirstOrDefault(n => IsDue(n, notices, context));

    /// <summary>Its time has come and its set, if any, is in this copy. Dismissal is not considered.</summary>
    public static bool HasStarted(Notice notice, NoticeContext context) =>
        context.Now >= notice.ShowFrom
        && (notice.RequiresSet is not { } set || context.HasSet(set));

    /// <summary>Started, not ended, and not dismissed.</summary>
    public static bool IsDue(Notice notice, IReadOnlyList<Notice> all, NoticeContext context) =>
        HasStarted(notice, context)
        && !HasEnded(notice, all, context)
        && !context.Dismissed.Contains(notice.Id);

    // A newer notice ends this one even once it is dismissed itself: the older one never comes back.
    private static bool HasEnded(Notice notice, IReadOnlyList<Notice> all, NoticeContext context) =>
        notice.ShowUntil is { } until
            ? context.Now >= until
            : context.Now >= notice.ShowFrom + DefaultLifetime
              || all.Any(other => other.ShowFrom > notice.ShowFrom && HasStarted(other, context));
}
