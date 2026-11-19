using System.Text.Json;
using LiveClipper.Core;

namespace LiveClipper.Tests;

/// <summary>P1.6-b：导出格式矩阵与调色参数。只测纯函数，不启动 FFmpeg。</summary>
public sealed class EditorExportMatrixTests
{
    [Theory]
    [InlineData("h264", "libx264")]
    [InlineData("hevc", "libx265")]
    [InlineData("prores", "prores_ks")]
    [InlineData("dnxhr", "dnxhd")]
    [InlineData(null, "libx264")]
    [InlineData(" HEVC ", "libx265")]
    public void VideoCodecArgs_MapFormatToEncoder(string? format, string encoder)
    {
        var args = EditorRenderService.VideoCodecArgs(format, 0);
        Assert.Equal("-c:v", args[0]);
        Assert.Equal(encoder, args[1]);
    }

    [Fact]
    public void VideoCodecArgs_RejectUnknownFormat()
    {
        var error = Assert.Throws<ArgumentException>(() => EditorRenderService.VideoCodecArgs("av1", 0));
        Assert.Contains("h264|hevc|prores|dnxhr", error.Message);
    }

    [Fact]
    public void VideoCodecArgs_HonorBitrateAndKeepQualityDefaults()
    {
        Assert.Contains("-crf", EditorRenderService.VideoCodecArgs("h264", 0));
        var rate = EditorRenderService.VideoCodecArgs("h264", 8000);
        var at = Array.IndexOf(rate, "-b:v");
        Assert.True(at >= 0);
        Assert.Equal("8000k", rate[at + 1]);
        Assert.DoesNotContain("-crf", rate);
    }

    [Theory]
    [InlineData("h264", ".mp4")]
    [InlineData("hevc", ".mp4")]
    [InlineData("prores", ".mov")]
    [InlineData("dnxhr", ".mov")]
    [InlineData(null, ".mp4")]
    public void OutputExtension_FollowsCodec(string? format, string extension)
        => Assert.Equal(extension, EditorRenderService.OutputExtension(format));

    [Fact]
    public void GradeFilter_OmittedAtDefaults()
    {
        Assert.Null(EditorRenderService.GradeFilter(null));
        Assert.Null(EditorRenderService.GradeFilter(new ColorGrade()));
    }

    [Fact]
    public void GradeFilter_ComposesEqAndTemperature()
    {
        var eq = EditorRenderService.GradeFilter(new ColorGrade(Brightness: .2, Contrast: 1.2, Saturation: .8));
        Assert.Equal("eq=brightness=0.2:contrast=1.2:saturation=0.8", eq);
        var warm = EditorRenderService.GradeFilter(new ColorGrade(Temperature: 1));
        Assert.Equal("colorbalance=rs=0.12:bs=-0.12", warm);
    }

    [Fact]
    public void GradeFilter_ClampsOutOfRangeValues()
    {
        var filter = EditorRenderService.GradeFilter(new ColorGrade(Brightness: 5, Contrast: 9, Saturation: -3, Temperature: -8));
        Assert.Contains("brightness=1", filter);
        Assert.Contains("contrast=3", filter);
        Assert.Contains("saturation=0", filter);
        Assert.Contains("rs=-0.12", filter);
    }

    [Fact]
    public void TimelineItem_GradeRoundTripsAndLegacyJsonStaysNull()
    {
        var original = new TimelineItem("a", "source", 0, 2, Grade: new ColorGrade(.1, 1.1, 1.2, -.3));
        var restored = JsonSerializer.Deserialize<TimelineItem>(JsonSerializer.Serialize(original))!;
        Assert.Equal(original.Grade, restored.Grade);

        var legacy = JsonSerializer.Deserialize<TimelineItem>("{\"Id\":\"a\",\"ClipId\":\"source\",\"Start\":0,\"End\":2}")!;
        Assert.Null(legacy.Grade);
    }

    [Fact]
    public void ProjectFormat_IsOmittedWhenUnset()
    {
        Assert.DoesNotContain("\"Format\"", JsonSerializer.Serialize(new EditorProject()));
        var withFormat = JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(new EditorProject { Format = "prores" }))!;
        Assert.Equal("prores", withFormat.Format);
    }
}
