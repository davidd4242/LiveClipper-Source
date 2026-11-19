using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LiveClipper.Domain;

namespace LiveClipper.Core;

/// <summary>
/// 统一产物命名：主播-直播日期-高光时刻[序号][切片|剪映草稿]-导出日期。
/// 直播日期与导出日期使用 dd-MM-yyyy；高光时刻是固定的成片标签，同日多条高光从第二条起追加 1、2、3…。
/// </summary>
public static class OutputNaming
{
    public const string HighlightLabel="高光时刻";
    public const string ClipSuffix="切片";
    public const string DraftSuffix="剪映草稿";
    private static readonly object Gate=new();
    private static readonly HashSet<string> Reserved=new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex HighlightFolderPattern=new(@"^.+-\d{2}-\d{2}-\d{4}-高光时刻\d*$",RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex LegacyHighlightFolderPattern=new(@"^(?<who>.+)-(?<room>\d+)-(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})-高光(?<index>\d*)$",RegexOptions.Compiled|RegexOptions.CultureInvariant);

    /// <summary>直播/导出日期：dd-MM-yyyy（本机时区）。</summary>
    public static string Date(DateTimeOffset value)=>value.ToLocalTime().ToString("dd-MM-yyyy",CultureInfo.InvariantCulture);

    /// <summary>自动高光基名：主播-直播日期-高光时刻。</summary>
    public static string HighlightPrefix(string streamer,DateTimeOffset liveStartedAt)=>$"{streamer}-{Date(liveStartedAt)}-{HighlightLabel}";

    /// <summary>
    /// 取高光素材的导出基名：新目录名本身就是基名（含同日序号）；旧版“主播-房号-日期-高光N”目录会转换成新格式；
    /// 导入视频等非高光素材返回 null，由调用方沿用原有随机名。
    /// </summary>
    public static string? HighlightBase(Clip clip)
    {
        var folder=Path.GetFileName(Path.GetDirectoryName(clip.Path));
        if(string.IsNullOrEmpty(folder))return null;
        if(HighlightFolderPattern.IsMatch(folder))return folder;
        var legacy=LegacyHighlightFolderPattern.Match(folder);
        if(!legacy.Success)return null;
        return $"{legacy.Groups["who"].Value}-{legacy.Groups["day"].Value}-{legacy.Groups["month"].Value}-{legacy.Groups["year"].Value}-{HighlightLabel}{legacy.Groups["index"].Value}";
    }

    /// <summary>同一目录下分配不重名的文件路径；已存在或本次已预留时追加 -2、-3…。</summary>
    public static string UniquePath(string directory,string name,string extension,IEnumerable<string>? existing=null)
    {
        var known=existing is null?null:new HashSet<string>(existing,StringComparer.OrdinalIgnoreCase);
        lock(Gate)
        {
            var candidate=Path.Combine(directory,name+extension);var index=2;
            while(File.Exists(candidate)||Reserved.Contains(candidate)||(known?.Contains(candidate)??false))candidate=Path.Combine(directory,$"{name}-{index++}{extension}");
            Reserved.Add(candidate);return candidate;
        }
    }

    /// <summary>把主播名清理成可安全用作文件名的文本；无效字符与空白折叠成连字符。</summary>
    public static string? SafeName(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return null;
        var builder=new StringBuilder(value.Length);
        foreach(var c in value.Trim())
        {
            if(char.IsWhiteSpace(c)||Path.GetInvalidFileNameChars().Contains(c))builder.Append('-');
            else builder.Append(c);
        }
        var clean=builder.ToString();
        while(clean.Contains("--",StringComparison.Ordinal))clean=clean.Replace("--","-");
        clean=clean.Trim('-','.',' ');
        return clean.Length==0?null:clean[..Math.Min(clean.Length,40)];
    }
}
