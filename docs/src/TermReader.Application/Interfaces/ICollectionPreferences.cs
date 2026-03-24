// Educational and personal use only.

namespace TermReader.Application.Interfaces;

/// <summary>
/// Stores user preferences for collections that persist across DI scopes within a session.
/// </summary>
public interface ICollectionPreferences
{
    Guid? LastUsedCollectionId { get; set; }
}
