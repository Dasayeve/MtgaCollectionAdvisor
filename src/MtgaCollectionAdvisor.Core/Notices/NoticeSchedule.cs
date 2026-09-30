namespace MtgaCollectionAdvisor.Core.Notices;

/// <summary>
/// How often notices.json may be read (#96): at most once every 6 hours per install, held across
/// restarts by the stored time of the last attempt. A failed read waits for the next window too.
/// </summary>
public static class NoticeSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public static bool IsDue(DateTimeOffset? lastAttempt, DateTimeOffset now) =>
        lastAttempt is not { } last || now - last >= Interval;
}
