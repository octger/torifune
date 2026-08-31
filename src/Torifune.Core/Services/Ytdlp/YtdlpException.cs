namespace Torifune.Core.Services.Ytdlp;

/// <summary>yt-dlp の実行失敗を表す例外。</summary>
public sealed class YtdlpException : Exception
{
    public int ExitCode { get; }

    /// <summary>stderr から収集した ERROR 行。</summary>
    public IReadOnlyList<string> ErrorLines { get; }

    public YtdlpException(string message, int exitCode, IReadOnlyList<string> errorLines)
        : base(message)
    {
        ExitCode = exitCode;
        ErrorLines = errorLines;
    }
}
