global using Xunit;
global using TestContext = V12UltraController.Tests.TestContextShim;

namespace V12UltraController.Tests;

internal static class TestContextShim
{
    internal static TestContextState Current { get; } = new();

    internal sealed class TestContextState
    {
        internal CancellationToken CancellationToken => CancellationToken.None;
    }
}
