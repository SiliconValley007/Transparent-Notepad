using Xunit;

namespace TransparentNotepad.Tests;

/// <summary>
/// xUnit entry point. Every case in <see cref="CoreTests"/> (ASR clients, LLM failover, prompt building, audio
/// abstractions: ring buffer / noise gate / device-recovery backoff / meeting session, exporter, logger) becomes one test.
/// </summary>
public class CoreSuite
{
    public static IEnumerable<object[]> Names() => CoreTests.All().Select(t => new object[] { t.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public Task Run(string name) => CoreTests.RunAsync(name);
}
