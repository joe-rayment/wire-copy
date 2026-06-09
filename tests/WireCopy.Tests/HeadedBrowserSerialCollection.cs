// Licensed under the MIT License. See LICENSE in the repository root.

using Xunit;

namespace WireCopy.Tests;

/// <summary>
/// xUnit collection definition that serializes integration tests which launch a REAL
/// headed Chromium under an X display (Xvfb). The runner allows unbounded parallelism
/// (<c>maxParallelThreads: -1</c>), and several headed browser windows launching and
/// positioning at once under a single display contend for resources — observed as a flaky
/// <c>BrowserWindowDockIntegrationTests.SummonAndDock</c> failure when the dock and spotlight
/// integration suites ran together. Apply
/// <c>[Collection(HeadedBrowserSerialCollection.Name)]</c> to any class that drives a headed
/// browser window so only one launches at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadedBrowserSerialCollection
{
    public const string Name = "HeadedBrowserSerial";
}
