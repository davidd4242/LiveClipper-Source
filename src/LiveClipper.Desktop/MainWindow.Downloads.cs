using System.IO;
using System.Text.Json;
using System.Windows;
using LiveClipper.Core;
using Microsoft.Web.WebView2.Core;

namespace LiveClipper.Desktop;

public partial class MainWindow
{
    private void AttachExportDownloads(CoreWebView2 browser)
    {
        browser.DownloadStarting+=async (_,args)=>
        {
            if(!Uri.TryCreate(args.DownloadOperation.Uri,UriKind.Absolute,out var source)||!IsAppOrigin(source))return;
            using var deferral=args.GetDeferral();
            try
            {
                using var response=await Api.GetAsync("/api/settings");response.EnsureSuccessStatusCode();
                await using var stream=await response.Content.ReadAsStreamAsync();using var json=await JsonDocument.ParseAsync(stream);
                var directory=json.RootElement.TryGetProperty("exportOutputDirectory",out var configured)?configured.GetString():null;
                if(string.IsNullOrWhiteSpace(directory))return;
                var root=RenderStoragePaths.NormalizeDirectory(directory);
                Directory.CreateDirectory(root);
                var name=Path.GetFileName(args.ResultFilePath);
                var destination=Path.Combine(root,name);
                if(File.Exists(destination))destination=Path.Combine(root,Path.GetFileNameWithoutExtension(name)+"-"+Guid.NewGuid().ToString("N")[..8]+Path.GetExtension(name));
                args.ResultFilePath=destination;
            }
            catch(Exception error)
            {
                args.Cancel=true;
                MessageBox.Show(this,"下载未开始，未改用其他目录："+error.Message,"导出下载",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        };
    }
}
