// Licensed under the MIT License. See LICENSE in the repository root.

using Xunit;

namespace WireCopy.Tests;

/// <summary>
/// xUnit collection definition that serializes tests which mutate
/// <see cref="Console.Out"/>. Without this, parallel test execution races
/// on the shared singleton and the test host crashes with
/// "Test host process crashed" — observed as a 53-failure flake during
/// workspace-n49i development. Apply <c>[Collection(ConsoleSerialCollection.Name)]</c>
/// to any test class that calls <c>Console.SetOut</c>.
///
/// <para>
/// Distinct from the older <c>[Trait("Collection", "ConsoleOutput")]</c>
/// pattern in some test files, which is just a filter tag (does NOT
/// control parallelism) — preserved on existing files to avoid a
/// session-wide rename, but new code should use this real collection.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleSerialCollection
{
    public const string Name = "ConsoleSerial";
}
