// Educational and personal use only.

using TermReader.Application.Interfaces;

namespace TermReader.Persistence;

/// <summary>
/// In-memory singleton that holds collection preferences across DI scopes.
/// </summary>
public class InMemoryCollectionPreferences : ICollectionPreferences
{
    public Guid? LastUsedCollectionId { get; set; }
}
