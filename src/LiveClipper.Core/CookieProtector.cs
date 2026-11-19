using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LiveClipper.Core;

/// <summary>
/// 直播 Cookie 的本机加密存储：Windows 下用 DPAPI（当前用户作用域）加密后写入设置文件，
/// 文件内容不再出现明文 SESSDATA。非 Windows 或解密失败时退回原样，旧明文配置仍然可用。
/// </summary>
public static class CookieProtector
{
    private const string Prefix="dpapi:";
    private const string Description="LiveClipper cookie";

    /// <summary>把粘贴的 Cookie 头或 cURL 命令归一成 `a=1; b=2` 的键值对形式。</summary>
    public static string Normalize(string input)
    {
        var text=(input??"").Trim().Trim('"','\'').Trim();
        if(text.Length==0)return "";
        // cURL：-H "Cookie: ..." 或 --cookie "..." 或 -b "..."。
        var quoted=Regex.Match(text,@"(?i)(?:cookie\s*:|--cookie(?:\s*=)?|-b\s+)\s*(?:'([^']+)'|""([^""]+)"")");
        if(quoted.Success)return Pairs(quoted.Groups[1].Success?quoted.Groups[1].Value:quoted.Groups[2].Value);
        // 纯文本里带 Cookie: 前缀的部分。
        var header=Regex.Match(text,@"(?i)cookie\s*:\s*(.+)$");
        if(header.Success)return Pairs(header.Groups[1].Value);
        return Pairs(text);
    }
    /// <summary>
    /// 按分号拆出 key=value；值里的逗号、星号、百分号原样保留（SESSDATA 里可能出现这些字符），
    /// 只过滤掉 cURL 参数之类的噪声片段。
    /// </summary>
    private static string Pairs(string text)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs=new List<string>();
        foreach(var segment in text.Split(';'))
        {
            var item=segment.Trim();
            var index=item.IndexOf('=');
            if(index<=0)continue;
            var key=item[..index].Trim();
            if(!Regex.IsMatch(key,@"^[A-Za-z0-9_.\-]+$"))continue;
            var value=item[(index+1)..].Trim().Trim('\'','"');
            if(value.Length==0||!seen.Add(key))continue;
            pairs.Add($"{key}={value}");
        }
        return string.Join("; ",pairs);
    }

    /// <summary>加密后存储；已是密文或空值原样返回，非 Windows 保持明文。</summary>
    public static string Protect(string plain)
    {
        var text=(plain??"").Trim();
        if(text.Length==0||text.StartsWith(Prefix,StringComparison.Ordinal))return text;
        if(!OperatingSystem.IsWindows())return text;
        var bytes=System.Text.Encoding.UTF8.GetBytes(text);
        var input=new DataBlob(bytes.Length,Marshal.AllocHGlobal(bytes.Length));
        Marshal.Copy(bytes,0,input.pbData,bytes.Length);
        try
        {
            if(!CryptProtectData(ref input,Description,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0,out var output))
                return text; // 加密不可用时宁可存明文也不要让设置保存失败。
            try
            {
                var protectedBytes=new byte[output.cbData];
                Marshal.Copy(output.pbData,protectedBytes,0,output.cbData);
                return Prefix+Convert.ToBase64String(protectedBytes);
            }
            finally{LocalFree(output.pbData);}
        }
        finally{Marshal.FreeHGlobal(input.pbData);}
    }

    /// <summary>解密 dpapi: 前缀的值；其余（旧明文/空值）原样返回。</summary>
    public static string Unprotect(string stored)
    {
        var text=(stored??"").Trim();
        if(!text.StartsWith(Prefix,StringComparison.Ordinal))return text;
        if(!OperatingSystem.IsWindows())return "";
        byte[] bytes;
        try{bytes=Convert.FromBase64String(text[Prefix.Length..]);}
        catch(FormatException){return "";}
        var input=new DataBlob(bytes.Length,Marshal.AllocHGlobal(bytes.Length));
        Marshal.Copy(bytes,0,input.pbData,bytes.Length);
        try
        {
            if(!CryptUnprotectData(ref input,out var descr,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0,out var output))return "";
            try
            {
                var plain=new byte[output.cbData];
                Marshal.Copy(output.pbData,plain,0,output.cbData);
                return System.Text.Encoding.UTF8.GetString(plain);
            }
            finally{LocalFree(output.pbData);}
        }
        finally{Marshal.FreeHGlobal(input.pbData);}
    }

    /// <summary>解密原始 DPAPI 密文（浏览器 Cookie 库里的旧版数据就是这种字节，没有 dpapi: 前缀）。</summary>
    public static byte[] UnprotectBytes(byte[] data)
    {
        if(data is null||data.Length==0||!OperatingSystem.IsWindows())return [];
        var input=new DataBlob(data.Length,Marshal.AllocHGlobal(data.Length));
        Marshal.Copy(data,0,input.pbData,data.Length);
        try
        {
            if(!CryptUnprotectData(ref input,out var descr,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0,out var output))return [];
            try
            {
                var plain=new byte[output.cbData];
                Marshal.Copy(output.pbData,plain,0,output.cbData);
                return plain;
            }
            finally{LocalFree(output.pbData);}
        }
        finally{Marshal.FreeHGlobal(input.pbData);}
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob(int cbData,IntPtr pbData){public int cbData=cbData;public IntPtr pbData=pbData;}
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn,string szDataDescr,IntPtr pOptionalEntropy,IntPtr pvReserved,IntPtr pPromptStruct,int dwFlags,out DataBlob pDataOut);
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn,out string ppszDataDescr,IntPtr pOptionalEntropy,IntPtr pvReserved,IntPtr pPromptStruct,int dwFlags,out DataBlob pDataOut);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
