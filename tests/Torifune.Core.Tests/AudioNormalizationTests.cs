using Torifune.Core.Services.Normalization;

namespace Torifune.Core.Tests;

public class AudioNormalizationTests
{
    [Fact]
    public void LoudnormMeasurement_FFmpegログ末尾のJSONをパースできる()
    {
        const string output = """
            [Parsed_loudnorm_0 @ 000001] some ffmpeg output
            {
                "input_i" : "-23.47",
                "input_tp" : "-3.12",
                "input_lra" : "6.80",
                "input_thresh" : "-33.91",
                "output_i" : "-16.02",
                "output_tp" : "-1.50",
                "output_lra" : "6.70",
                "output_thresh" : "-26.48",
                "normalization_type" : "linear",
                "target_offset" : "0.02"
            }
            """;

        var measurement = LoudnormMeasurement.Parse(output);

        Assert.Equal(-23.47, measurement.InputIntegratedLoudness);
        Assert.Equal(-3.12, measurement.InputTruePeak);
        Assert.Equal(6.80, measurement.InputLoudnessRange);
        Assert.Equal(-33.91, measurement.InputThreshold);
        Assert.Equal(0.02, measurement.TargetOffset);
    }

    [Fact]
    public void BuildAnalysisFilter_既定目標をInvariant形式で生成する()
    {
        var options = new AudioNormalizationOptions(-16, 11, -1.5);

        var filter = AudioNormalizationService.BuildAnalysisFilter(options);

        Assert.Equal("loudnorm=I=-16:LRA=11:TP=-1.5:print_format=json", filter);
    }

    [Fact]
    public void BuildApplyFilter_測定値を2パス目へ渡す()
    {
        var options = new AudioNormalizationOptions(-16, 11, -1.5);
        var measurement = new LoudnormMeasurement(-23.47, -3.12, 6.8, -33.91, 0.02);

        var filter = AudioNormalizationService.BuildApplyFilter(options, measurement);

        Assert.Equal(
            "loudnorm=I=-16:LRA=11:TP=-1.5" +
            ":measured_I=-23.47:measured_LRA=6.8:measured_TP=-3.12" +
            ":measured_thresh=-33.91:offset=0.02:linear=true:print_format=summary",
            filter);
    }

    [Fact]
    public void LoudnormMeasurement_JSONがなければ失敗する()
    {
        Assert.Throws<FormatException>(() => LoudnormMeasurement.Parse("ffmpeg error only"));
    }
}
