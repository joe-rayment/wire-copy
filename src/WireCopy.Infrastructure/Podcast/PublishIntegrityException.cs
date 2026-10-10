// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// workspace-ianqr: a deliberate refusal to publish because existing remote
/// state (manifest.json or feed-index.json) exists but can't be read, and
/// publishing over it would destroy it (erase published episodes, or re-mint
/// feed UUIDs and break subscribers' URLs). Raised before anything is uploaded;
/// <see cref="PodcastPublisher"/> turns it into a failed
/// <c>FeedPublishResult</c> carrying <see cref="Exception.Message"/> as-is, so
/// the message is written for the user.
/// </summary>
public sealed class PublishIntegrityException : Exception
{
    public PublishIntegrityException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}
