namespace MtgaCollectionAdvisor.Web.Services;

/// <summary>
/// How the status bar shows a message. A warning is one the player can do something about
/// (open MTG Arena, fix a pasted list) or a failed operation; everything else stays quiet.
/// </summary>
public enum StatusLevel
{
    Info,
    Warning,
}

/// <summary>What an operation says in the status bar while it runs and when it ends.</summary>
public delegate void StatusReport(string message, StatusLevel level = StatusLevel.Info);
