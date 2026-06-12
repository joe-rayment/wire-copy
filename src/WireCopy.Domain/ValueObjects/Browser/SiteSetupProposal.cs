// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

/// <summary>
/// workspace-5oe9.7 — the kind of clarifying question the AI setup wizard asks.
/// Determines how the TUI renders the answer affordance.
/// </summary>
public enum SetupQuestionKind
{
    /// <summary>"Which link is the main story?" — pick one option / point at a link.</summary>
    PickMain,

    /// <summary>"Hide these N links that look like ads?" — yes/review.</summary>
    ConfirmExclude,

    /// <summary>"Is this the right section order?" — confirm / reorder.</summary>
    ConfirmOrder,

    /// <summary>"Group stories into these sections?" — confirm grouping.</summary>
    GroupBy,
}

/// <summary>
/// A candidate the model proposes for a question, carrying the DURABLE
/// identifier (selector / url-pattern) it would persist if chosen — this is the
/// "it has THIS identifier" the user sees at confirmation.
/// </summary>
public record SetupOption
{
    public required string Label { get; init; }

    /// <summary>Durable CSS parent-selector fragment (empty when none).</summary>
    public string ParentSelector { get; init; } = string.Empty;

    /// <summary>Durable URL path pattern, e.g. "/opinion/" (empty when none).</summary>
    public string UrlPattern { get; init; } = string.Empty;

    /// <summary>Indices (into the supplied link list) this option refers to.</summary>
    public List<int> ExampleLinkIndices { get; init; } = new();
}

/// <summary>One bounded clarifying question with the model's best-guess default.</summary>
public record SetupQuestion
{
    public required string Id { get; init; }

    public required string Prompt { get; init; }

    public required SetupQuestionKind Kind { get; init; }

    /// <summary>The model's best guess — Enter accepts it (zero typing).</summary>
    public string DefaultAnswer { get; init; } = string.Empty;

    public List<SetupOption> Options { get; init; } = new();
}

/// <summary>The model's initial reading of the page before any questions.</summary>
public record ProposedPattern
{
    public SetupOption? TopStory { get; init; }

    public List<SetupOption> Tiers { get; init; } = new();

    public List<SetupOption> Exclude { get; init; } = new();
}

/// <summary>
/// Round-1 output of the AI setup contract: a proposed pattern plus a bounded
/// set of clarifying questions for the wizard (workspace-5oe9.7 / B8a).
/// </summary>
public record SiteSetupProposal
{
    public required ProposedPattern ProposedPattern { get; init; }

    public List<SetupQuestion> Questions { get; init; } = new();
}

/// <summary>The user's answer to one <see cref="SetupQuestion"/>.</summary>
public record SetupAnswer
{
    public required string QuestionId { get; init; }

    public required string Answer { get; init; }
}

/// <summary>
/// workspace-romy.8 — round-2 output of the AI setup contract: the durable
/// config plus the model's self-assessed confidence and AT MOST one targeted
/// confirm question ("is X the lead?"). The wizard renders the question as
/// ignorable rows on the preview card — Enter on a section row still saves
/// immediately, so a confident first proposal stays one keystroke.
/// </summary>
public record InferredPattern
{
    public required SiteHierarchyConfig Config { get; init; }

    /// <summary>Model's 0..1 confidence in the pattern (1.0 when not reported).</summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// One targeted confirm question, or null when the model is confident.
    /// Only shown when discriminating (see the wizard's IsDiscriminating).
    /// </summary>
    public SetupQuestion? ConfirmQuestion { get; init; }
}
