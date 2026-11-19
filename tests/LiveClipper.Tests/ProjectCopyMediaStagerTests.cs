using System.Security.Cryptography;
using System.Text;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class ProjectCopyMediaStagerTests
{
    [Fact]
    public async Task StageAsync_CopiesMediaAndOptionalSrt_WithActualBytesAndHashes()
    {
        var area = NewArea();
        var mediaBytes = new byte[] { 0, 1, 2, 255, 42 };
        var srtBytes = Encoding.UTF8.GetBytes("1\r\n00:00:00,000 --> 00:00:01,000\r\n字幕\r\n");
        var media = WriteFile(area, "user name.MP4", mediaBytes);
        var srt = WriteFile(area, "user captions.SRT", srtBytes);
        var root = Path.Combine(area, "destination");

        var result = await ProjectCopyMediaStager.StageAsync(root,
            [new("old-clip", media, srt), new("without-captions", media)]);

        Assert.True(Directory.Exists(result.StagingDirectory));
        Assert.Equal(root, Path.GetDirectoryName(result.StagingDirectory));
        Assert.Equal(2, result.Clips.Count);
        var first = result.Clips["old-clip"];
        var second = result.Clips["without-captions"];
        AssertNewId(first.ClipId, "old-clip");
        AssertNewId(second.ClipId, "without-captions");
        Assert.NotEqual(first.ClipId, second.ClipId);
        Assert.NotEqual(first.Media.Path, second.Media.Path);
        AssertFile(first.Media, mediaBytes, ".MP4", result.StagingDirectory);
        Assert.NotNull(first.Srt);
        AssertFile(first.Srt!, srtBytes, ".SRT", result.StagingDirectory);
        AssertFile(second.Media, mediaBytes, ".MP4", result.StagingDirectory);
        Assert.Null(second.Srt);
        Assert.Equal("media.MP4", Path.GetFileName(first.Media.Path));
        Assert.Equal("subtitles.SRT", Path.GetFileName(first.Srt!.Path));
        Assert.Equal(first.ClipId, Path.GetFileName(Path.GetDirectoryName(first.Media.Path)));
        Assert.Equal(mediaBytes, File.ReadAllBytes(media));
        Assert.Equal(srtBytes, File.ReadAllBytes(srt));
        Assert.Equal(3, Directory.GetFiles(result.StagingDirectory, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task StageAsync_SharedSourceIsCopiedIndependently_AndChangingCopiesDoesNotChangeOriginals()
    {
        var area = NewArea();
        var mediaBytes = Encoding.UTF8.GetBytes("original media");
        var srtBytes = Encoding.UTF8.GetBytes("original captions");
        var media = WriteFile(area, "source.wav", mediaBytes);
        var srt = WriteFile(area, "source.srt", srtBytes);
        var result = await ProjectCopyMediaStager.StageAsync(Path.Combine(area, "destination"),
            [new("one", media, srt), new("two", media, srt)]);
        var first = result.Clips["one"];
        var second = result.Clips["two"];

        await File.WriteAllTextAsync(first.Media.Path, "changed media");
        await File.WriteAllTextAsync(first.Srt!.Path, "changed captions");

        Assert.NotEqual(first.Media.Path, second.Media.Path);
        Assert.NotEqual(first.Srt.Path, second.Srt!.Path);
        Assert.Equal(mediaBytes, File.ReadAllBytes(media));
        Assert.Equal(srtBytes, File.ReadAllBytes(srt));
        Assert.Equal(mediaBytes, File.ReadAllBytes(second.Media.Path));
        Assert.Equal(srtBytes, File.ReadAllBytes(second.Srt.Path));
        Assert.Equal("changed media", File.ReadAllText(first.Media.Path));
        Assert.Equal("changed captions", File.ReadAllText(first.Srt.Path));
    }

    [Fact]
    public async Task StageAsync_EachInvocationUsesNewDirectory_AndDoesNotOverwriteExistingPaths()
    {
        var area = NewArea();
        var media = WriteFile(area, "source.mp4", [1, 2, 3]);
        var root = Directory.CreateDirectory(Path.Combine(area, "destination")).FullName;
        var sentinel = WriteFile(root, "media.mp4", [9, 8, 7]);
        var previous = Directory.CreateDirectory(Path.Combine(root, "project-copy-existing")).FullName;
        var previousFile = WriteFile(previous, "media.mp4", [6, 5, 4]);
        var first = await ProjectCopyMediaStager.StageAsync(root, [new("old", media)]);
        await File.WriteAllTextAsync(first.Clips["old"].Media.Path, "keep first output");

        var second = await ProjectCopyMediaStager.StageAsync(root, [new("old", media)]);

        Assert.NotEqual(first.StagingDirectory, second.StagingDirectory);
        Assert.NotEqual(first.Clips["old"].ClipId, second.Clips["old"].ClipId);
        Assert.NotEqual(first.Clips["old"].Media.Path, second.Clips["old"].Media.Path);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(sentinel));
        Assert.Equal(new byte[] { 6, 5, 4 }, File.ReadAllBytes(previousFile));
        Assert.Equal("keep first output", File.ReadAllText(first.Clips["old"].Media.Path));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(second.Clips["old"].Media.Path));
        Assert.Equal(3, Directory.GetDirectories(root).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("duplicate")]
    public async Task StageAsync_RejectsEmptyOrDuplicateSourceClipIds_BeforeAllocating(string id)
    {
        var area = NewArea();
        var media = WriteFile(area, "source.mp4", [1]);
        var root = Path.Combine(area, "destination");

        await Assert.ThrowsAsync<ArgumentException>(() => ProjectCopyMediaStager.StageAsync(root,
            [new(id, media), new(id, media)]));

        Assert.False(Directory.Exists(root));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(media));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("media")]
    [InlineData("srt")]
    public async Task StageAsync_RejectsRelativePaths_BeforeAllocating(string field)
    {
        var area = NewArea();
        var media = WriteFile(area, "source.mp4", [1]);
        var root = Path.Combine(area, "destination");
        var destination = field == "root" ? "relative-destination" : root;
        var source = new ProjectCopyMediaStager.Source("old", field == "media" ? "relative.mp4" : media,
            field == "srt" ? "relative.srt" : null);

        await Assert.ThrowsAsync<ArgumentException>(() => ProjectCopyMediaStager.StageAsync(destination, [source]));

        Assert.False(Directory.Exists(root));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(media));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("media")]
    [InlineData("srt")]
    public async Task StageAsync_RejectsExplicitTraversalSegments(string field)
    {
        var area = NewArea();
        var nested = Directory.CreateDirectory(Path.Combine(area, "nested")).FullName;
        var media = WriteFile(area, "source.mp4", [1]);
        var traversal = Path.Combine(nested, "..", "source.mp4");
        var root = Path.Combine(area, "destination");
        var destination = field == "root" ? Path.Combine(nested, "..", "destination") : root;
        var source = new ProjectCopyMediaStager.Source("old", field == "media" ? traversal : media,
            field == "srt" ? traversal : null);

        await Assert.ThrowsAsync<ArgumentException>(() => ProjectCopyMediaStager.StageAsync(destination, [source]));

        Assert.False(Directory.Exists(root));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(media));
    }

    [Fact]
    public async Task StageAsync_RejectsVolumeRoot()
    {
        var area = NewArea();
        var media = WriteFile(area, "source.mp4", [1]);
        var root = Path.GetPathRoot(area)!;

        await Assert.ThrowsAsync<ArgumentException>(() => ProjectCopyMediaStager.StageAsync(root, [new("old", media)]));

        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(media));
    }

    [Fact]
    public async Task StageAsync_MissingMediaFails_WithExactRetainedStagingDirectory()
    {
        var area = NewArea();
        var root = Path.Combine(area, "destination");
        var missing = Path.Combine(area, "missing.mp4");
        ProjectCopyMediaStager.Result? result = null;

        var error = await Assert.ThrowsAsync<ProjectCopyMediaStager.StagingException>(async () =>
            result = await ProjectCopyMediaStager.StageAsync(root, [new("old", missing)]));

        Assert.Null(result);
        Assert.IsType<FileNotFoundException>(error.InnerException);
        AssertRetainedDirectory(root, error.StagingDirectory, error.Message);
        Assert.Empty(Directory.GetFiles(error.StagingDirectory, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task StageAsync_MissingSrtFails_AfterMediaCopy_WithoutCleaningPartialStaging()
    {
        var area = NewArea();
        var root = Path.Combine(area, "destination");
        var mediaBytes = new byte[] { 1, 5, 9 };
        var media = WriteFile(area, "source.mp4", mediaBytes);
        var missingSrt = Path.Combine(area, "missing.srt");
        ProjectCopyMediaStager.Result? result = null;

        var error = await Assert.ThrowsAsync<ProjectCopyMediaStager.StagingException>(async () =>
            result = await ProjectCopyMediaStager.StageAsync(root, [new("old", media, missingSrt)]));

        Assert.Null(result);
        Assert.IsType<FileNotFoundException>(error.InnerException);
        AssertRetainedDirectory(root, error.StagingDirectory, error.Message);
        var retainedMedia = Assert.Single(Directory.GetFiles(error.StagingDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal("media.mp4", Path.GetFileName(retainedMedia));
        Assert.Equal(mediaBytes, File.ReadAllBytes(retainedMedia));
        Assert.Equal(mediaBytes, File.ReadAllBytes(media));
        Assert.False(File.Exists(missingSrt));
    }

    [Fact]
    public async Task StageAsync_PreCanceledToken_ThrowsCancellationWithRetainedDirectory_NotSuccess()
    {
        var area = NewArea();
        var root = Path.Combine(area, "destination");
        var media = WriteFile(area, "source.mp4", [1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ProjectCopyMediaStager.Result? result = null;

        var error = await Assert.ThrowsAsync<ProjectCopyMediaStager.StagingCanceledException>(async () =>
            result = await ProjectCopyMediaStager.StageAsync(root, [new("old", media)], cancellation.Token));

        Assert.Null(result);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        AssertRetainedDirectory(root, error.StagingDirectory, error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(error.StagingDirectory));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(media));
    }

    [Fact]
    public async Task StageAsync_SourceDirectoryIsRejected_AndStagingIsRetained()
    {
        var area = NewArea();
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(area, "source-directory")).FullName;
        var root = Path.Combine(area, "destination");

        var error = await Assert.ThrowsAsync<ProjectCopyMediaStager.StagingException>(() =>
            ProjectCopyMediaStager.StageAsync(root, [new("old", sourceDirectory)]));

        Assert.IsType<IOException>(error.InnerException);
        AssertRetainedDirectory(root, error.StagingDirectory, error.Message);
        Assert.True(Directory.Exists(sourceDirectory));
    }

    [Fact]
    public async Task StageAsync_WindowsSourceOpenForWriting_IsNotCopied()
    {
        if (!OperatingSystem.IsWindows()) return;
        var area = NewArea();
        var media = WriteFile(area, "source.mp4", [1, 2, 3]);
        var root = Path.Combine(area, "destination");
        using var writer = new FileStream(media, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var error = await Assert.ThrowsAsync<ProjectCopyMediaStager.StagingException>(() =>
            ProjectCopyMediaStager.StageAsync(root, [new("old", media)]));

        Assert.IsType<IOException>(error.InnerException);
        AssertRetainedDirectory(root, error.StagingDirectory, error.Message);
        Assert.Empty(Directory.GetFiles(error.StagingDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal(3L, writer.Length);
    }

    [Fact]
    public async Task StageAsync_EmptyFileHasZeroLengthAndSha256()
    {
        var area = NewArea();
        var media = WriteFile(area, "source", []);

        var result = await ProjectCopyMediaStager.StageAsync(Path.Combine(area, "destination"), [new("old", media)]);

        AssertFile(result.Clips["old"].Media, [], "", result.StagingDirectory);
    }

    private static string NewArea()
    {
        // Test fixtures are deliberately retained: this component never owns directory cleanup.
        return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "LiveClipper-ProjectCopyMediaStagerTests-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    private static string WriteFile(string directory, string name, byte[] bytes)
    {
        var path = Path.Combine(directory, name);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        return path;
    }

    private static void AssertNewId(string id, string oldId)
    {
        Assert.True(Guid.TryParseExact(id, "N", out var guid));
        Assert.NotEqual(Guid.Empty, guid);
        Assert.NotEqual(oldId, id);
    }

    private static void AssertFile(ProjectCopyMediaStager.StagedFile file, byte[] expected, string extension, string staging)
    {
        Assert.True(Path.IsPathFullyQualified(file.Path));
        var relative = Path.GetRelativePath(staging, file.Path);
        Assert.False(Path.IsPathRooted(relative));
        Assert.DoesNotContain("..", relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Assert.Equal(extension, Path.GetExtension(file.Path));
        Assert.Equal((long)expected.Length, file.Bytes);
        var actual = File.ReadAllBytes(file.Path);
        Assert.Equal(expected, actual);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), file.Sha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(actual)), file.Sha256);
    }

    private static void AssertRetainedDirectory(string root, string staging, string message)
    {
        Assert.True(Path.IsPathFullyQualified(staging));
        Assert.True(Directory.Exists(staging));
        Assert.Equal(root, Path.GetDirectoryName(staging));
        Assert.Equal(staging, Assert.Single(Directory.GetDirectories(root)));
        Assert.Contains(staging, message);
    }
}
