// Licensed under the MIT License. See LICENSE in the repository root.

using Xunit;

namespace WireCopy.Tests;

/// <summary>
/// workspace-i8r7: xUnit collection definition that serializes tests which mutate or depend on
/// PROCESS-GLOBAL state — specifically the current working directory. <c>ViewCommandHandlerTests</c>
/// calls <see cref="System.IO.Directory.SetCurrentDirectory"/> (a process-wide setting) to exercise the
/// relative <c>fixtures/</c> dump path, then deletes that temp directory in its finally block. The runner
/// allows unbounded parallelism (<c>maxParallelThreads: -1</c>, <c>parallelizeTestCollections: true</c>),
/// so a concurrently-running test that resolves paths from the current directory — e.g.
/// <c>SchedulerShutdownContractTests.Program_ConfiguresSchedulerShutdownTimeout</c>, which builds a host
/// via <c>Host.CreateDefaultBuilder()</c> (content root = <c>Directory.GetCurrentDirectory()</c>) — could
/// observe the swapped (and about-to-be-deleted) directory and fail intermittently. Marking this
/// collection <c>DisableParallelization</c> ensures the CWD-mutation window never overlaps any other test.
/// Apply <c>[Collection(ProcessGlobalStateCollection.Name)]</c> to any class that reads or mutates the
/// process-global current directory.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessGlobalStateCollection
{
    public const string Name = "ProcessGlobalState";
}
