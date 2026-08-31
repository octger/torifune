using Torifune.Core.Models;
using Torifune.Core.Services.Ytdlp;

namespace Torifune.Core.Tests;

public class YtdlpOutputParserTests
{
    // ---- 進捗行 ----

    [Fact]
    public void TryParseProgressLine_ダウンロード中の行をパースできる()
    {
        var line = "[TORIFUNE];downloading;52428800;104857600;NA;1048576.5;42";

        var p = YtdlpOutputParser.TryParseProgressLine(line);

        Assert.NotNull(p);
        Assert.Equal(DownloadState.Downloading, p.State);
        Assert.Equal(52428800, p.DownloadedBytes);
        Assert.Equal(104857600, p.TotalBytes);
        Assert.Equal(50.0, p.Percent!.Value, precision: 1);
        Assert.Equal(1048576.5, p.SpeedBytesPerSec);
        Assert.Equal(TimeSpan.FromSeconds(42), p.Eta);
    }

    [Fact]
    public void TryParseProgressLine_total_bytesがNAならestimateへフォールバック()
    {
        var line = "[TORIFUNE];downloading;1000;NA;2000;NA;NA";

        var p = YtdlpOutputParser.TryParseProgressLine(line);

        Assert.NotNull(p);
        Assert.Equal(2000, p.TotalBytes);
        Assert.Equal(50.0, p.Percent!.Value, precision: 1);
        Assert.Null(p.SpeedBytesPerSec);
        Assert.Null(p.Eta);
    }

    [Fact]
    public void TryParseProgressLine_finishedは後処理状態になる()
    {
        var line = "[TORIFUNE];finished;1000;1000;NA;NA;0";

        var p = YtdlpOutputParser.TryParseProgressLine(line);

        Assert.NotNull(p);
        Assert.Equal(DownloadState.PostProcessing, p.State);
    }

    [Fact]
    public void TryParseProgressLine_進捗行以外はnull()
    {
        Assert.Null(YtdlpOutputParser.TryParseProgressLine("[download] Destination: video.mp4"));
        Assert.Null(YtdlpOutputParser.TryParseProgressLine(""));
    }

    // ---- ファイルパス行 ----

    [Fact]
    public void TryParseFilePathLine_パスを抽出できる()
    {
        var path = YtdlpOutputParser.TryParseFilePathLine(@"[TORIFUNE:FILE]C:\Users\test\Downloads\video [abc123].mp4");

        Assert.Equal(@"C:\Users\test\Downloads\video [abc123].mp4", path);
    }

    [Fact]
    public void TryParseFilePathLine_該当しない行はnull()
    {
        Assert.Null(YtdlpOutputParser.TryParseFilePathLine("[TORIFUNE];downloading;1;2;NA;NA;NA"));
    }

    [Fact]
    public void TryParseFallbackFilePathLine_Destination行から抽出できる()
    {
        var path = YtdlpOutputParser.TryParseFallbackFilePathLine(
            @"[download] Destination: C:\Users\test\AppData\Local\Temp\preview\preview.webm");

        Assert.Equal(@"C:\Users\test\AppData\Local\Temp\preview\preview.webm", path);
    }

    [Fact]
    public void TryParseFallbackFilePathLine_Merger行から抽出できる()
    {
        var path = YtdlpOutputParser.TryParseFallbackFilePathLine(
            @"[Merger] Merging formats into ""C:\Users\test\AppData\Local\Temp\preview\preview.mp4""");

        Assert.Equal(@"C:\Users\test\AppData\Local\Temp\preview\preview.mp4", path);
    }

    [Fact]
    public void TryParseFallbackFilePathLine_該当しない行はnull()
    {
        Assert.Null(YtdlpOutputParser.TryParseFallbackFilePathLine("[debug] something"));
    }

    // ---- メタデータ JSON ----

    [Fact]
    public void ParseMediaInfo_単一動画をパースできる()
    {
        const string json = """
        {
            "id": "abc123",
            "title": "テスト動画",
            "webpage_url": "https://example.com/watch?v=abc123",
            "duration": 125.5,
            "thumbnail": "https://example.com/thumb.jpg",
            "uploader": "テストチャンネル",
            "extractor_key": "Youtube",
            "formats": [
                {
                    "format_id": "137",
                    "ext": "mp4",
                    "vcodec": "avc1.640028",
                    "acodec": "none",
                    "width": 1920,
                    "height": 1080,
                    "fps": 30,
                    "filesize": 100000000,
                    "tbr": 4500.5
                },
                {
                    "format_id": "140",
                    "ext": "m4a",
                    "vcodec": "none",
                    "acodec": "mp4a.40.2",
                    "abr": 128.0
                }
            ],
            "subtitles": {
                "ja": [{ "ext": "vtt", "name": "Japanese" }]
            },
            "automatic_captions": {
                "en": [{ "ext": "vtt", "name": "English (auto)" }]
            }
        }
        """;

        var info = YtdlpOutputParser.ParseMediaInfo(json, "https://example.com/watch?v=abc123");

        Assert.False(info.IsPlaylist);
        Assert.Equal("abc123", info.Id);
        Assert.Equal("テスト動画", info.Title);
        Assert.Equal(TimeSpan.FromSeconds(125.5), info.Duration);
        Assert.Equal(2, info.Formats.Count);

        var video = info.Formats[0];
        Assert.True(video.HasVideo);
        Assert.False(video.HasAudio);
        Assert.Equal(1080, video.Height);

        var audio = info.Formats[1];
        Assert.False(audio.HasVideo);
        Assert.True(audio.HasAudio);

        Assert.Equal(2, info.Subtitles.Count);
        Assert.Contains(info.Subtitles, s => s is { Language: "ja", IsAutoGenerated: false });
        Assert.Contains(info.Subtitles, s => s is { Language: "en", IsAutoGenerated: true });
    }

    [Fact]
    public void ParseMediaInfo_プレイリストをパースできる()
    {
        const string json = """
        {
            "_type": "playlist",
            "id": "PL123",
            "title": "テストリスト",
            "entries": [
                { "id": "v1", "url": "https://example.com/watch?v=v1", "title": "動画1", "duration": 60 },
                { "id": "v2", "url": "https://example.com/watch?v=v2", "title": "動画2" }
            ]
        }
        """;

        var info = YtdlpOutputParser.ParseMediaInfo(json, "https://example.com/playlist?list=PL123");

        Assert.True(info.IsPlaylist);
        Assert.Equal(2, info.Entries.Count);
        Assert.Equal("動画1", info.Entries[0].Title);
        Assert.Equal(TimeSpan.FromMinutes(1), info.Entries[0].Duration);
        Assert.Null(info.Entries[1].Duration);
    }
}
