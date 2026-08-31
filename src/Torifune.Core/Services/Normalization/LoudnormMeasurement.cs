using System.Globalization;
using System.Text.Json;

namespace Torifune.Core.Services.Normalization;

/// <summary>FFmpeg loudnorm 1パス目が出力する測定値。</summary>
public sealed record LoudnormMeasurement(
    double InputIntegratedLoudness,
    double InputTruePeak,
    double InputLoudnessRange,
    double InputThreshold,
    double TargetOffset)
{
    /// <summary>FFmpeg のログに含まれる末尾の loudnorm JSON ブロックを読み取る。</summary>
    public static LoudnormMeasurement Parse(string ffmpegOutput)
    {
        var start = ffmpegOutput.LastIndexOf('{');
        var end = ffmpegOutput.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new FormatException("FFmpeg loudnorm の測定結果(JSON)が見つかりませんでした。");
        }

        using var document = JsonDocument.Parse(ffmpegOutput[start..(end + 1)]);
        var root = document.RootElement;
        return new LoudnormMeasurement(
            ReadNumber(root, "input_i"),
            ReadNumber(root, "input_tp"),
            ReadNumber(root, "input_lra"),
            ReadNumber(root, "input_thresh"),
            ReadNumber(root, "target_offset"));
    }

    private static double ReadNumber(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw new FormatException($"loudnorm 測定結果に {propertyName} がありません。");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw new FormatException($"loudnorm 測定値 {propertyName} を数値として解釈できません。");
    }
}
