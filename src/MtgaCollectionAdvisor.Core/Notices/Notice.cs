namespace MtgaCollectionAdvisor.Core.Notices;

/// <summary>
/// A line the maintainer wants players to read (#96), from notices.json at the repository root.
/// Plain text only: nothing in it is rendered as HTML or Markdown.
/// </summary>
/// <param name="ShowFrom">When it starts; every other rule counts from it.</param>
/// <param name="ShowUntil">When it ends, when given: this replaces the 30 days and the end a newer notice brings.</param>
/// <param name="RequiresSet">A set code: the notice waits until this copy has that set's cards.</param>
public sealed record Notice(
    string Id,
    string Title,
    string Text,
    DateTimeOffset ShowFrom,
    DateTimeOffset? ShowUntil = null,
    string? RequiresSet = null);
