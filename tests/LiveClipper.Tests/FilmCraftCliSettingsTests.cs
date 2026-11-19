using System.Globalization;
using System.Text.Json;
using LiveClipper.App;

namespace LiveClipper.Tests;

public sealed class FilmCraftCliSettingsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitBurnCaptions_IsOneJsonArgument(bool burnCaptions)
    {
        var request = new FilmCraftExportRequest(
            Path.Combine(Path.GetTempPath(), "project folder", "quoted 项目.fcproj"),
            Path.Combine(Path.GetTempPath(), "output folder", "render.mp4"),
            Start: 1.25, End: 4.5, Format: "H264", BurnCaptions: burnCaptions);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var psi = FilmCraftCliRunner.CreateExportStartInfo("filmcraft-cli.exe", request);
            var args = psi.ArgumentList.ToArray();
            Assert.False(psi.UseShellExecute);
            Assert.True(psi.RedirectStandardOutput);
            Assert.True(psi.RedirectStandardError);
            Assert.True(psi.CreateNoWindow);
            Assert.Equal("", psi.Arguments);
            Assert.Equal(new[]
            {
                "--project", Path.GetFullPath(request.ProjectPath),
                "export", Path.GetFullPath(request.OutputPath),
                "--format", "h264", "--range", "custom", "--start", "1.25", "--end", "4.5",
                "--allow-offline", "--settings", JsonSerializer.Serialize(new { burnCaptions }),
            }, args);
            using var json = JsonDocument.Parse(args[^1]);
            Assert.Single(json.RootElement.EnumerateObject());
            Assert.Equal(burnCaptions, json.RootElement.GetProperty("burnCaptions").GetBoolean());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void OmittedBurnCaptions_KeepsLegacyFalseDefault()
    {
        var request = new FilmCraftExportRequest("project.fcproj", "output.mp4");
        Assert.False(request.BurnCaptions);
        var args = FilmCraftCliRunner.CreateExportStartInfo("filmcraft-cli.exe", request).ArgumentList.ToArray();
        Assert.Equal("--settings", args[^2]);
        using var json = JsonDocument.Parse(args[^1]);
        Assert.False(json.RootElement.GetProperty("burnCaptions").GetBoolean());
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(",\"burnCaptions\":true", true)]
    [InlineData(",\"burnCaptions\":false", false)]
    public void WebJsonBinding_ReachesProductionArgumentList(string setting, bool expected)
    {
        var body = "{\"projectPath\":\"project.fcproj\",\"outputPath\":\"output.mp4\"" + setting + "}";
        var request = JsonSerializer.Deserialize<FilmCraftExportRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(request);
        Assert.Equal(expected, request.BurnCaptions);
        var args = FilmCraftCliRunner.CreateExportStartInfo("filmcraft-cli.exe", request).ArgumentList.ToArray();
        using var json = JsonDocument.Parse(args[^1]);
        Assert.Equal(expected, json.RootElement.GetProperty("burnCaptions").GetBoolean());
    }
}
