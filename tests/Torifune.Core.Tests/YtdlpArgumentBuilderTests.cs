using Torifune.Core.Models;
using Torifune.Core.Services.Ytdlp;

namespace Torifune.Core.Tests;

public class YtdlpArgumentBuilderTests
{
    private static DownloadOptions DefaultOptions => new()
    {
        Url = "https://example.com/watch?v=abc",
        OutputDirectory = @"C:\Downloads",
    };

    [Fact]
    public void BuildDownloadArgs_既定でAVC1080pとAAC高品質優先のソートが指定される()
    {
        var args = YtdlpArgumentBuilder.BuildDownloadArgs(DefaultOptions, @"C:\tools\ffmpeg");

        var sortIndex = args.IndexOf("-S");
        Assert.True(sortIndex >= 0, "-S が指定されていること");
        Assert.Equal("vcodec:h264,res:1080,fps,acodec:aac,abr,lang,quality,hdr:12", args[sortIndex + 1]);
    }

    [Fact]
    public void BuildDownloadArgs_既定でmp4コンテナが指定される()
    {
        var args = YtdlpArgumentBuilder.BuildDownloadArgs(DefaultOptions, @"C:\tools\ffmpeg");

        var remuxIndex = args.IndexOf("--remux-video");
        Assert.True(remuxIndex >= 0);
        Assert.Equal("mp4", args[remuxIndex + 1]);

        var mergeIndex = args.IndexOf("--merge-output-format");
        Assert.True(mergeIndex >= 0);
        Assert.Equal("mp4", args[mergeIndex + 1]);
    }

    [Fact]
    public void BuildDownloadArgs_URLは末尾のセパレータ後に置かれる()
    {
        var args = YtdlpArgumentBuilder.BuildDownloadArgs(DefaultOptions, @"C:\tools\ffmpeg");

        Assert.Equal("--", args[^2]);
        Assert.Equal(DefaultOptions.Url, args[^1]);
    }

    [Fact]
    public void BuildDownloadArgs_開始終了時間を指定するとdownload_sectionsが追加される()
    {
        var options = DefaultOptions with
        {
            StartTimeSeconds = 15,
            EndTimeSeconds = 45,
        };

        var args = YtdlpArgumentBuilder.BuildDownloadArgs(options, @"C:\tools\ffmpeg");

        var sectionsIndex = args.IndexOf("--download-sections");
        Assert.True(sectionsIndex >= 0);
        Assert.Equal("*15-45", args[sectionsIndex + 1]);
    }
}
