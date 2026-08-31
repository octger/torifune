namespace Torifune.Core.Services.Normalization;

/// <summary>FFmpeg による音声正規化の失敗。</summary>
public sealed class AudioNormalizationException : Exception
{
    public AudioNormalizationException(string message) : base(message)
    {
    }
}
