using Torifune.Core.Models;

namespace Torifune.Core.Services.Ytdlp;

/// <summary>
/// DownloadOptions から yt-dlp の引数リストを構築する。
/// 引数は常に配列で渡し、シェル文字列連結は行わない(インジェクション対策)。
/// </summary>
public static class YtdlpArgumentBuilder
{
    /// <summary>進捗行のプレフィックス。</summary>
    public const string ProgressPrefix = "[TORIFUNE];";

    /// <summary>完了ファイルパス行のプレフィックス。</summary>
    public const string FilePathPrefix = "[TORIFUNE:FILE]";

    private const string ProgressTemplate =
        ProgressPrefix +
        "%(progress.status)s;%(progress.downloaded_bytes)s;%(progress.total_bytes)s;" +
        "%(progress.total_bytes_estimate)s;%(progress.speed)s;%(progress.eta)s";

    /// <summary>全実行に共通する出力安定化フラグ。</summary>
    public static IReadOnlyList<string> BaseArgs { get; } =
    [
        "--ignore-config",
        "--no-warnings",
        "--color", "never",
        "--encoding", "utf-8",
    ];

    /// <summary>メタデータ取得用の引数を構築する。</summary>
    public static List<string> BuildFetchInfoArgs(string url)
    {
        var args = new List<string>(BaseArgs)
        {
            "--skip-download",
            "--flat-playlist",   // プレイリストは軽量取得(単一動画には影響しない)
            "--dump-single-json",
            "--",
            url,
        };
        return args;
    }

    /// <summary>ダウンロード実行用の引数を構築する。</summary>
    public static List<string> BuildDownloadArgs(DownloadOptions options, string ffmpegDirectory)
    {
        var args = new List<string>(BaseArgs)
        {
            "--no-playlist",     // P2: 単一動画のみ(プレイリストは P4)
            "--newline",
            "--progress",        // --print が implies する quiet 下でも進捗を出す
            "--progress-delta", "0.5",
            "--progress-template", ProgressTemplate,
            "--print", "after_move:" + FilePathPrefix + "%(filepath)s",
            "--no-simulate",
            "--ffmpeg-location", ffmpegDirectory,
            "-P", options.OutputDirectory,
            "-o", options.OutputTemplate,
        };

        if (options.AudioOnly)
        {
            args.Add("-x");
            if (!string.IsNullOrWhiteSpace(options.AudioFormat))
            {
                args.Add("--audio-format");
                args.Add(options.AudioFormat);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.FormatString))
        {
            args.Add("-f");
            args.Add(options.FormatString);
        }

        if (!string.IsNullOrWhiteSpace(options.FormatSort))
        {
            args.Add("-S");
            args.Add(options.FormatSort);
        }

        if (options.StartTimeSeconds is not null || options.EndTimeSeconds is not null)
        {
            var start = options.StartTimeSeconds is not null
                ? options.StartTimeSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "0";
            var end = options.EndTimeSeconds is not null
                ? options.EndTimeSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";

            args.Add("--download-sections");
            args.Add(end.Length > 0 ? $"*{start}-{end}" : $"*{start}-inf");
        }

        if (!string.IsNullOrWhiteSpace(options.RemuxTo))
        {
            args.Add("--remux-video");
            args.Add(options.RemuxTo);
        }

        if (!string.IsNullOrWhiteSpace(options.MergeOutputFormat))
        {
            args.Add("--merge-output-format");
            args.Add(options.MergeOutputFormat);
        }

        args.Add("--");
        args.Add(options.Url);
        return args;
    }
}
