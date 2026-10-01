using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Web.Services;

// Suggested packs (#84): which packs would get a deck the most of what it is missing. The rule is
// PackSuggester's, in Core; this only passes the deck and the session's format through.
public sealed partial class AdvisorSession
{
    /// <summary>
    /// Whether the card database has set names yet. A database from before migration 11 gets them
    /// from its one-time re-import; until then the button stays hidden rather than show bare codes.
    /// </summary>
    public bool PacksAvailable { get; private set; }

    /// <param name="shown">The deck as the detail shows it: without non-basic lands when excluded.</param>
    /// <returns>Null when the card database couldn't be read; the reason is logged.</returns>
    public async Task<PackSuggestion?> SuggestPacksAsync(DeckAnalysisResult shown)
    {
        try
        {
            return await services.PackSuggestionService.SuggestAsync(shown, Format);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Suggesting packs failed");
            return null;
        }
    }

    private async Task RefreshPacksAvailableAsync()
    {
        try
        {
            PacksAvailable = (await services.CardDatabaseStore.CountCardDataAsync()).WithSetName > 0;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Counting set names failed");
            PacksAvailable = false;
        }
    }
}
