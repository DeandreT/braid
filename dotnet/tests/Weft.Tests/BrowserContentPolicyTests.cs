using Weft.Server;

namespace Weft.Tests;

public sealed class BrowserContentPolicyTests
{
    [Fact]
    public void ShellDoesNotPermitWasmOrInlineScriptsAndWorkerIsPathConfined()
    {
        Assert.DoesNotContain("wasm-unsafe-eval", BrowserContentPolicy.Shell);
        Assert.Contains("script-src 'self';", BrowserContentPolicy.Shell);
        Assert.Contains("worker-src 'self';", BrowserContentPolicy.Shell);
        var worker = BrowserContentPolicy.Editor("https://weft.example");
        Assert.Contains("script-src https://weft.example/edit/ 'wasm-unsafe-eval';", worker);
        Assert.Contains("worker-src 'none';", worker);
        Assert.DoesNotContain("connect-src", worker); // default-src 'none' blocks every network API.
    }

    [Theory]
    [InlineData("https://weft.example/path")]
    [InlineData("https://weft.example/")]
    [InlineData("https://weft.example/?script-src=*")]
    [InlineData("https://someone@weft.example")]
    [InlineData("data:text/plain,unsafe")]
    public void EditorRefusesNonOriginPolicyInput(string origin) => Assert.Throws<ArgumentException>(() => BrowserContentPolicy.Editor(origin));
}
