using System.Runtime.InteropServices;
using System.Text.Json;

namespace LiveClipper.Desktop;

/// <summary>
/// 把原生标题栏（caption）跟 Web 应用主题对齐，只走 DWM 属性，不改窗口样式，
/// 所以系统最小化/最大化/关闭、拖动、双击最大化、Snap、键盘行为都保持原样。
/// 主题信号来源：页面 web/src/design/theme.ts 的 applyTheme 在 &lt;html data-theme&gt; 变化时 Post
/// {type:"lc-theme",theme:"dark"|"light"}；本类只接受这两个值。
/// </summary>
public static class WindowCaptionTheme
{
    public const string MessageType="lc-theme";

    private const int DwmaUseImmersiveDarkModeBefore20H1=19;
    private const int DwmaUseImmersiveDarkMode=20;
    private const int DwmaBorderColor=34;   // Windows 11 22000+
    private const int DwmaCaptionColor=35;  // Windows 11 22000+
    private const int DwmaTextColor=36;     // Windows 11 22000+

    // COLORREF = 0x00BBGGRR。深色取 --surface #151516 / --text #f1f1ee；浅色取 #f5f5f5 / #1a1a1a。
    private const int DarkCaption=0x161515, DarkText=0xEEF1F1;
    private const int LightCaption=0xF5F5F5, LightText=0x1A1A1A;

    internal static void SetStartupCloak(IntPtr hwnd,bool hidden)
    {
        const int DwmwaCloak=13;
        var value=hidden?1:0;
        DwmSetWindowAttribute(hwnd,DwmwaCloak,ref value,sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);

    /// <summary>解析 Web 消息；只有 type=lc-theme 且 theme 为 dark/light 才返回主题，其余返回 null。</summary>
    public static bool TryParse(JsonElement root,out string? theme)
    {
        theme=null;
        if(root.ValueKind!=JsonValueKind.Object)return false;
        if(!root.TryGetProperty("type",out var type)||type.ValueKind!=JsonValueKind.String||type.GetString()!=MessageType)return false;
        if(root.TryGetProperty("theme",out var value)&&value.ValueKind==JsonValueKind.String)
        {
            var text=value.GetString();
            if(text=="dark"||text=="light")theme=text;
        }
        return true;   // 是 lc-theme 消息（值不合法时 theme=null，调用方忽略但不落到通用分支）
    }

    /// <summary>应用主题到窗口标题栏；返回 immersive dark mode 是否设置成功。失败只写 stderr，不影响窗口功能。</summary>
    public static bool Apply(IntPtr hwnd,string theme)
    {
        if(hwnd==IntPtr.Zero)return false;
        var dark=theme=="dark";
        try
        {
            var flag=dark?1:0;
            var hr=DwmSetWindowAttribute(hwnd,DwmaUseImmersiveDarkMode,ref flag,sizeof(int));
            if(hr!=0)hr=DwmSetWindowAttribute(hwnd,DwmaUseImmersiveDarkModeBefore20H1,ref flag,sizeof(int));
            var dwmOk=hr==0;
            if(!dwmOk)Console.Error.WriteLine($"[desktop] DWM immersive dark mode 设置失败 hr=0x{hr:X8}");
            if(Environment.OSVersion.Version.Build>=22000)
            {
                var caption=dark?DarkCaption:LightCaption;
                var text=dark?DarkText:LightText;
                var captionHr=DwmSetWindowAttribute(hwnd,DwmaCaptionColor,ref caption,sizeof(int));
                var textHr=DwmSetWindowAttribute(hwnd,DwmaTextColor,ref text,sizeof(int));
                var border=caption;
                DwmSetWindowAttribute(hwnd,DwmaBorderColor,ref border,sizeof(int));
                if(captionHr!=0||textHr!=0)Console.Error.WriteLine($"[desktop] DWM caption/text 颜色设置失败 caption=0x{captionHr:X8} text=0x{textHr:X8}");
            }
            return dwmOk;
        }
        catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine("[desktop] DWM 不可用："+ex.Message);
            return false;
        }
    }
}
