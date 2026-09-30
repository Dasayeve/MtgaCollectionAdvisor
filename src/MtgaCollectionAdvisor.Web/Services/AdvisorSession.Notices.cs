using MtgaCollectionAdvisor.Core.Notices;

namespace MtgaCollectionAdvisor.Web.Services;

// Maintainer notices (#96): notices.json read within NoticeSchedule, the notice to show chosen by
// NoticeSelector, dismissals stored for good. Its own event, not Changed: the deck list resets to
// page 1 on every Changed (#85).
public sealed partial class AdvisorSession
{
    private IReadOnlyList<Notice> _notices = [];
    private int _noticesLoading;

    /// <summary>The notice to show now; null when none is due, and always during the first-run setup.</summary>
    public Notice? CurrentNotice { get; private set; }

    public event Action? NoticeChanged;

    /// <summary>Dismisses a notice for good and shows the next one still active, if any.</summary>
    public async Task DismissNoticeAsync(string id)
    {
        try
        {
            await services.NoticeStore.DismissAsync(id, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Storing a dismissed notice failed");
        }
        await SelectNoticeAsync();
    }

    /// <summary>
    /// At start and on the 30-minute tick. The network is used only when NoticeSchedule allows it
    /// (every 6 hours at most); otherwise this re-reads the stored copy, so a notice whose time
    /// comes while the app is open appears within the tick.
    /// </summary>
    private async Task LoadNoticesAsync()
    {
        if (Interlocked.Exchange(ref _noticesLoading, 1) == 1) return;
        try
        {
            var load = await services.NoticeService.LoadAsync(DateTimeOffset.UtcNow);
            if (load.ReadFailure is { } reason) log.LogWarning("Notices not read: {Reason}", reason);
            _notices = load.Notices;
            await SelectNoticeAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Loading notices failed");
        }
        finally
        {
            Volatile.Write(ref _noticesLoading, 0);
        }
    }

    /// <summary>Chooses again: after a read, a card import (a set's cards may have arrived), a dismissal, or the setup's end.</summary>
    private async Task SelectNoticeAsync()
    {
        Notice? next = null;
        try
        {
            if (Setup is null && _notices.Count > 0)
            {
                var dismissed = await services.NoticeStore.LoadDismissedAsync();
                var sets = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var set in _notices.Select(n => n.RequiresSet).OfType<string>().Distinct())
                {
                    sets[set] = await services.CardDatabaseStore.HasSetAsync(set);
                }

                next = NoticeSelector.Next(_notices,
                    new NoticeContext(DateTimeOffset.UtcNow, set => sets.GetValueOrDefault(set), dismissed));
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Choosing a notice failed");
        }

        if (next == CurrentNotice) return;
        CurrentNotice = next;
        NoticeChanged?.Invoke();
    }
}
