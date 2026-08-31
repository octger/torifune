using System.Globalization;
using System.Text.Json;
using Torifune.Core.Models;

namespace Torifune.Core.Services.Ytdlp;

/// <summary>yt-dlp の JSON 出力・進捗行のパースを担う。</summary>
public static class YtdlpOutputParser
{
    /// <summary>--dump-single-json の出力を MediaInfo にパースする。</summary>
    public static MediaInfo ParseMediaInfo(string json, string requestUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var type = GetString(root, "_type");
        if (type == "playlist")
        {
            var entries = new List<PlaylistEntry>();
            if (root.TryGetProperty("entries", out var entriesElem) &&
                entriesElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in entriesElem.EnumerateArray())
                {
                    var entryUrl = GetString(e, "url") ?? GetString(e, "webpage_url");
                    if (entryUrl is null)
                    {
                        continue;
                    }
                    entries.Add(new PlaylistEntry(
                        GetString(e, "id"),
                        entryUrl,
                        GetString(e, "title"),
                        GetDurationSeconds(e)));
                }
            }

            return new MediaInfo
            {
                Url = requestUrl,
                Id = GetString(root, "id"),
                Title = GetString(root, "title"),
                Uploader = GetString(root, "uploader") ?? GetString(root, "channel"),
                Extractor = GetString(root, "extractor_key") ?? GetString(root, "extractor"),
                IsPlaylist = true,
                Entries = entries,
            };
        }

        return new MediaInfo
        {
            Url = GetString(root, "webpage_url") ?? requestUrl,
            Id = GetString(root, "id"),
            Title = GetString(root, "title"),
            Duration = GetDurationSeconds(root),
            ThumbnailUrl = GetString(root, "thumbnail"),
            Uploader = GetString(root, "uploader") ?? GetString(root, "channel"),
            Extractor = GetString(root, "extractor_key") ?? GetString(root, "extractor"),
            IsPlaylist = false,
            Formats = ParseFormats(root),
            Subtitles = ParseSubtitles(root),
        };
    }

    /// <summary>
    /// 進捗行([TORIFUNE];status;downloaded;total;total_estimate;speed;eta)をパースする。
    /// 進捗行でない場合は null。
    /// </summary>
    public static DownloadProgress? TryParseProgressLine(string line)
    {
        if (!line.StartsWith(YtdlpArgumentBuilder.ProgressPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var fields = line[YtdlpArgumentBuilder.ProgressPrefix.Length..].Split(';');
        if (fields.Length < 6)
        {
            return null;
        }

        var state = fields[0] switch
        {
            "downloading" => DownloadState.Downloading,
            "finished" => DownloadState.PostProcessing, // 1ファイル完了 → マージ等の後処理へ
            _ => DownloadState.Preparing,
        };

        var downloaded = ParseLong(fields[1]);
        var total = ParseLong(fields[2]) ?? ParseLong(fields[3]); // total → estimate フォールバック
        var speed = ParseDouble(fields[4]);
        var etaSec = ParseLong(fields[5]);

        double? percent = downloaded is not null && total is > 0
            ? downloaded.Value * 100.0 / total.Value
            : null;

        return new DownloadProgress(
            state,
            percent,
            downloaded,
            total,
            speed,
            etaSec is not null ? TimeSpan.FromSeconds(etaSec.Value) : null,
            null);
    }

    /// <summary>完了ファイルパス行([TORIFUNE:FILE]...)をパースする。該当しない場合は null。</summary>
    public static string? TryParseFilePathLine(string line) =>
        line.StartsWith(YtdlpArgumentBuilder.FilePathPrefix, StringComparison.Ordinal)
            ? line[YtdlpArgumentBuilder.FilePathPrefix.Length..].Trim()
            : null;

    /// <summary>
    /// yt-dlp 標準ログの出力先行からファイルパスを抽出する。
    /// 例: [download] Destination: ... / [Merger] Merging formats into "..."
    /// </summary>
    public static string? TryParseFallbackFilePathLine(string line)
    {
        const string destinationPrefix = "[download] Destination: ";
        if (line.StartsWith(destinationPrefix, StringComparison.Ordinal))
        {
            return line[destinationPrefix.Length..].Trim();
        }

        const string mergerPrefix = "[Merger] Merging formats into \"";
        if (line.StartsWith(mergerPrefix, StringComparison.Ordinal) && line.EndsWith('"'))
        {
            return line[mergerPrefix.Length..^1].Trim();
        }

        const string extractAudioPrefix = "[ExtractAudio] Destination: ";
        if (line.StartsWith(extractAudioPrefix, StringComparison.Ordinal))
        {
            return line[extractAudioPrefix.Length..].Trim();
        }

        return null;
    }

    // ---- private helpers ----

    private static IReadOnlyList<FormatInfo> ParseFormats(JsonElement root)
    {
        if (!root.TryGetProperty("formats", out var formatsElem) ||
            formatsElem.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<FormatInfo>();
        foreach (var f in formatsElem.EnumerateArray())
        {
            var formatId = GetString(f, "format_id");
            if (formatId is null)
            {
                continue;
            }
            list.Add(new FormatInfo(
                formatId,
                GetString(f, "ext"),
                GetString(f, "vcodec"),
                GetString(f, "acodec"),
                GetInt(f, "width"),
                GetInt(f, "height"),
                GetDouble(f, "fps"),
                GetLong(f, "filesize"),
                GetLong(f, "filesize_approx"),
                GetDouble(f, "tbr"),
                GetDouble(f, "abr"),
                GetString(f, "format_note"),
                GetString(f, "language"),
                GetString(f, "protocol")));
        }
        return list;
    }

    private static IReadOnlyList<SubtitleInfo> ParseSubtitles(JsonElement root)
    {
        var list = new List<SubtitleInfo>();
        AddSubtitles(root, "subtitles", isAuto: false, list);
        AddSubtitles(root, "automatic_captions", isAuto: true, list);
        return list;
    }

    private static void AddSubtitles(JsonElement root, string prop, bool isAuto, List<SubtitleInfo> list)
    {
        if (!root.TryGetProperty(prop, out var subsElem) ||
            subsElem.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var lang in subsElem.EnumerateObject())
        {
            string? name = null;
            if (lang.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in lang.Value.EnumerateArray())
                {
                    name = GetString(t, "name");
                    if (name is not null)
                    {
                        break;
                    }
                }
            }
            list.Add(new SubtitleInfo(lang.Name, name, isAuto));
        }
    }

    private static TimeSpan? GetDurationSeconds(JsonElement elem)
    {
        var seconds = GetDouble(elem, "duration");
        return seconds is not null ? TimeSpan.FromSeconds(seconds.Value) : null;
    }

    private static string? GetString(JsonElement elem, string prop) =>
        elem.ValueKind == JsonValueKind.Object &&
        elem.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? GetInt(JsonElement elem, string prop) =>
        elem.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static long? GetLong(JsonElement elem, string prop) =>
        elem.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? (long)v.GetDouble()
            : null;

    private static double? GetDouble(JsonElement elem, string prop) =>
        elem.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static long? ParseLong(string s) =>
        long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v
        : double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? (long)d
        : null;

    private static double? ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
}
