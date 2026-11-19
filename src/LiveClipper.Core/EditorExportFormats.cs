namespace LiveClipper.Core;

/// <summary>R81：新增 FFmpeg 原生导出格式的产物形态。</summary>
public enum ExportKind
{
    /// <summary>单文件视频容器（mov/webm/gif 之外）。</summary>
    VideoFile,
    /// <summary>单文件动画 GIF（palettegen/paletteuse 两步滤镜，无音轨）。</summary>
    AnimatedGif,
    /// <summary>图像序列：编码到临时目录 frame%06d，再打包为 zip 作为成片。</summary>
    ImageSequence,
    /// <summary>纯音频文件：复用现有混音图，-map [aout_master] -vn。</summary>
    AudioOnly,
}

/// <summary>
/// 一个新增导出格式的完整参数。VideoArgs/AudioArgs/MuxArgs 与
/// docs/merge/_work/r81/FMT/format-table.json 逐字节一致（顺序也一致），
/// 并由 docs/merge/_work/r81/FMT/smoke.json 的真实编码结果验证。
/// </summary>
public sealed record ExportFormatDescriptor(
    string Id,
    string Label,
    ExportKind Kind,
    string Extension,
    string[] VideoArgs,
    string[] AudioArgs,
    string[] MuxArgs,
    bool HighBitDepth,
    string FrameExtension = "")
{
    /// <summary>该格式是否带音轨（GIF 与图像序列没有音轨）。</summary>
    public bool HasAudio => Kind is ExportKind.VideoFile or ExportKind.AudioOnly;
}

/// <summary>
/// R81：后端 FFmpeg 能从复刻版导出面板里真正接管的格式。
/// 只收「FFmpeg 有可靠编码器 + 产物能被第三方读取」的条目；AV1/APV/MXF/AAF/OMF 刻意不在表内。
/// 现有 h264/hevc/prores(422 HQ)/dnxhr(SQ) 仍由 <see cref="EditorRenderService"/> 的旧分支处理，行为不变。
/// </summary>
public static class EditorExportFormats
{
    public static readonly ExportFormatDescriptor[] Table =
    [
        // ProRes 4 档：prores_ks profile 0/1/2/3。prores(=HQ, profile 3) 走旧分支。
        new("prores-proxy","Apple ProRes 422 Proxy",ExportKind.VideoFile,".mov",
            ["-c:v","prores_ks","-profile:v","0","-vendor","apl0","-pix_fmt","yuv422p10le"],["-c:a","pcm_s16le"],["-movflags","+faststart"],true),
        new("prores-lt","Apple ProRes 422 LT",ExportKind.VideoFile,".mov",
            ["-c:v","prores_ks","-profile:v","1","-vendor","apl0","-pix_fmt","yuv422p10le"],["-c:a","pcm_s16le"],["-movflags","+faststart"],true),
        new("prores-422","Apple ProRes 422",ExportKind.VideoFile,".mov",
            ["-c:v","prores_ks","-profile:v","2","-vendor","apl0","-pix_fmt","yuv422p10le"],["-c:a","pcm_s16le"],["-movflags","+faststart"],true),
        // DNxHR 4 档：dnxhr_lb/sq/hq 8-bit 4:2:2，hqx 10-bit。dnxhr(=SQ) 走旧分支。
        new("dnxhr-lb","Avid DNxHR LB (8-bit)",ExportKind.VideoFile,".mov",
            ["-c:v","dnxhd","-profile:v","dnxhr_lb","-pix_fmt","yuv422p"],["-c:a","pcm_s16le"],["-movflags","+faststart"],false),
        new("dnxhr-sq","Avid DNxHR SQ (8-bit)",ExportKind.VideoFile,".mov",
            ["-c:v","dnxhd","-profile:v","dnxhr_sq","-pix_fmt","yuv422p"],["-c:a","pcm_s16le"],["-movflags","+faststart"],false),
        new("dnxhr-hq","Avid DNxHR HQ (8-bit)",ExportKind.VideoFile,".mov",
            ["-c:v","dnxhd","-profile:v","dnxhr_hq","-pix_fmt","yuv422p"],["-c:a","pcm_s16le"],["-movflags","+faststart"],false),
        new("dnxhr-hqx","Avid DNxHR HQX (10-bit)",ExportKind.VideoFile,".mov",
            ["-c:v","dnxhd","-profile:v","dnxhr_hqx","-pix_fmt","yuv422p10le"],["-c:a","pcm_s16le"],["-movflags","+faststart"],true),
        // 其他视频容器
        new("mjpeg","QuickTime Motion JPEG",ExportKind.VideoFile,".mov",
            ["-c:v","mjpeg","-q:v","3","-pix_fmt","yuvj422p"],["-c:a","pcm_s16le"],["-movflags","+faststart"],false),
        new("webm","WebM (VP9 + Opus)",ExportKind.VideoFile,".webm",
            ["-c:v","libvpx-vp9","-crf","32","-b:v","0","-pix_fmt","yuv420p","-row-mt","1"],["-c:a","libopus","-b:a","128k"],[],false),
        // 动画 GIF：滤镜链在 FinishAsync 里拼（palettegen/paletteuse），无音轨。
        new("gif","动画 GIF",ExportKind.AnimatedGif,".gif",[],[],[],false),
        // 图像序列：image2 编码到临时目录后打包 zip。
        new("png","PNG 序列",ExportKind.ImageSequence,".zip",["-c:v","png","-pix_fmt","rgb24"],[],[],false,".png"),
        new("tiff","TIFF 序列",ExportKind.ImageSequence,".zip",["-c:v","tiff","-pix_fmt","rgb24"],[],[],false,".tiff"),
        new("bmp","BMP 序列",ExportKind.ImageSequence,".zip",["-c:v","bmp","-pix_fmt","bgr24"],[],[],false,".bmp"),
        new("jpg","JPEG 序列",ExportKind.ImageSequence,".zip",["-c:v","mjpeg","-q:v","2","-pix_fmt","yuvj420p"],[],[],false,".jpg"),
        new("tga","Targa 序列",ExportKind.ImageSequence,".zip",["-c:v","targa","-pix_fmt","bgr24"],[],[],false,".tga"),
        new("dpx","DPX 序列",ExportKind.ImageSequence,".zip",["-c:v","dpx","-pix_fmt","rgb24"],[],[],false,".dpx"),
        new("exr","OpenEXR 序列",ExportKind.ImageSequence,".zip",["-c:v","exr","-pix_fmt","gbrpf32le","-compression","none"],[],[],false,".exr"),
        // 纯音频
        new("wav","Waveform Audio (16-bit)",ExportKind.AudioOnly,".wav",[],["-c:a","pcm_s16le"],["-rf64","auto"],false),
        new("wavlpcm","Waveform Audio (32-bit float)",ExportKind.AudioOnly,".wav",[],["-c:a","pcm_f32le"],["-rf64","auto"],false),
        new("wav51","Waveform Audio (5.1)",ExportKind.AudioOnly,".wav",[],["-c:a","pcm_s16le","-ac","6"],["-rf64","auto"],false),
        new("wavbwf","广播 WAV (BWF / bext)",ExportKind.AudioOnly,".wav",[],["-c:a","pcm_s24le"],["-write_bext","1","-rf64","auto"],false),
        new("aiff","AIFF",ExportKind.AudioOnly,".aiff",[],["-c:a","pcm_s16be"],[],false),
        new("aac","AAC 音频",ExportKind.AudioOnly,".m4a",[],["-c:a","aac","-b:a","192k"],["-movflags","+faststart"],false),
        new("flac","FLAC",ExportKind.AudioOnly,".flac",[],["-c:a","flac","-compression_level","5"],[],false),
    ];

    private static readonly Dictionary<string,ExportFormatDescriptor> ById =
        Table.ToDictionary(x=>x.Id,StringComparer.OrdinalIgnoreCase);

    /// <summary>新增格式 id 列表（不含现有 h264/hevc/prores/dnxhr）。</summary>
    public static readonly string[] Ids = Table.Select(x=>x.Id).ToArray();

    /// <summary>按 id 查表；未知返回 null（调用方保留旧行为/旧报错）。</summary>
    public static ExportFormatDescriptor? Find(string? format)
    {
        if(string.IsNullOrWhiteSpace(format))return null;
        return ById.GetValueOrDefault(format.Trim().ToLowerInvariant());
    }

    /// <summary>未知格式的报错文案，保留旧契约子串 h264|hevc|prores|dnxhr。</summary>
    public static string UnsupportedMessage =>
        "导出格式只支持 h264|hevc|prores|dnxhr 与 FFmpeg 原生格式："+string.Join('|',Ids);

    /// <summary>DNxHR（dnxhd 编码器）能接受的最小画面宽度。</summary>
    public const int DnxhrMinimumWidth = 256;
    /// <summary>DNxHR（dnxhd 编码器）能接受的最小画面高度。</summary>
    public const int DnxhrMinimumHeight = 120;

    /// <summary>
    /// 纯函数：dnxhr* (=dnxhr 与 dnxhr-lb/sq/hq/hqx) 的画布下限守卫。
    /// dnxhd 编码器对更小的画面直接失败（ffmpeg 报 "Input dimensions too small, input must be at least 256x120"，
    /// 退出码 -22），看不到原因的编码器报错对用户没有意义，所以在编码前提前拒绝。
    /// </summary>
    public static void RequireDnxhrCanvas(string? format,int width,int height)
    {
        var id=format?.Trim().ToLowerInvariant();
        if(string.IsNullOrEmpty(id)||!id.StartsWith("dnxhr",StringComparison.Ordinal))return;
        if(width>=DnxhrMinimumWidth&&height>=DnxhrMinimumHeight)return;
        throw new ArgumentException(
            $"invalid_dnxhr_canvas：DNxHR 需要画面至少 {DnxhrMinimumWidth}x{DnxhrMinimumHeight}，当前画布为 {width}x{height}；请提高输出分辨率或改用其他格式");
    }

    /// <summary>纯函数：新格式的编码参数；未知 id 抛 ArgumentException（与旧分支一致）。GIF 由封装决定编码器。</summary>
    public static string[] VideoArgsOrThrow(string? format)
    {
        var descriptor=Find(format)??throw new ArgumentException(UnsupportedMessage);
        if(descriptor.Kind==ExportKind.AnimatedGif)return ["-c:v","gif"];
        return descriptor.VideoArgs.Length>0?descriptor.VideoArgs:throw new ArgumentException(UnsupportedMessage);
    }
}
