using Torifune.Desktop.Diagnostics;

namespace Torifune.Core.Tests;

public sealed class DebugConsoleLogStoreTests
{
    [Theory]
    [InlineData(
        "GET https://example.com/watch?v=secret&list=private",
        "GET https://example.com/watch")]
    [InlineData(
        "GET https://example.com/video#private-section",
        "GET https://example.com/video")]
    [InlineData(
        @"Saved to C:\Users\alice\Videos\sample.mp4",
        @"Saved to C:\Users\<user>\Videos\sample.mp4")]
    [InlineData(
        "Saved to /home/alice/Videos/sample.mp4",
        "Saved to /home/<user>/Videos/sample.mp4")]
    public void Sanitize_RemovesSensitiveUrlAndUserPathParts(
        string input,
        string expected)
    {
        Assert.Equal(expected, DebugConsoleLogStore.Sanitize(input));
    }
}
