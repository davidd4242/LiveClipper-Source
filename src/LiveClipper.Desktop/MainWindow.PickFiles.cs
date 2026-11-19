using System;
using System.IO;

namespace LiveClipper.Desktop;

// pick-files 快捷位置（媒体浏览器六个入口，web 侧 desktopPickFiles.ts / EditorNextPage）。
// 只映射受限枚举到 Windows 已知文件夹作为选择器起始目录；不做目录枚举、不接受网页任意路径。
public partial class MainWindow
{
    /// <summary>受限位置枚举 → 起始目录；未知枚举或目录不存在返回 null（选择器落到系统默认位置）。</summary>
    private static string? TryGetQuickLocationDirectory(string? location) => location switch
    {
        "videos" => KnownFolder(Environment.SpecialFolder.MyVideos),
        "music" => KnownFolder(Environment.SpecialFolder.MyMusic),
        "pictures" => KnownFolder(Environment.SpecialFolder.MyPictures),
        "documents" => KnownFolder(Environment.SpecialFolder.MyDocuments),
        // Downloads 不在 Environment.SpecialFolder 里；按约定用「用户目录\Downloads」兜底，不存在回 null。
        "downloads" => Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads")),
        // computer、未知枚举、缺失字段：就开系统默认位置，不做任何定位。
        _ => null,
    };

    private static string? KnownFolder(Environment.SpecialFolder folder)=>Existing(Environment.GetFolderPath(folder));
    private static string? Existing(string path)=>Directory.Exists(path)?path:null;
}
