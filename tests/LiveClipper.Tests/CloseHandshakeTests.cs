using System.IO;
using LiveClipper.Desktop;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace LiveClipper.Tests;

/// <summary>
/// 原生关窗决策使用与 Desktop 相同的源码；另核对 WPF 挂接和页面的协议常量。
/// </summary>
public sealed class CloseHandshakeTests
{
    [Theory]
    [InlineData("0", CloseDecision.Close)]
    [InlineData("null", CloseDecision.ConfirmUnresponsive)]
    [InlineData("\"0\"", CloseDecision.ConfirmUnresponsive)]
    [InlineData("-1", CloseDecision.ConfirmUnresponsive)]
    [InlineData("broken", CloseDecision.ConfirmUnresponsive)]
    public async Task Probe_OnlyExplicitZeroSkipsEditorConfirmation(string response,CloseDecision expected)
    {
        var requests=0;
        var actual=await CloseHandshake.DecideAsync(()=>Task.FromResult(response),()=>
        {
            requests++;
            return Task.FromResult(new CloseAck("id",false,true));
        });
        Assert.Equal(expected,actual);
        Assert.Equal(0,requests);
    }

    [Fact]
    public async Task Probe_FaultOrTimeoutRequiresConfirmation()
    {
        var ackCount=0;
        Task<CloseAck> Ack(){ackCount++;return Task.FromResult(new CloseAck("id",false,true));}
        Assert.Equal(CloseDecision.ConfirmUnresponsive,await CloseHandshake.DecideAsync(
            ()=>Task.FromException<string>(new InvalidOperationException("probe failed")),Ack));
        Assert.Equal(CloseDecision.ConfirmUnresponsive,await CloseHandshake.DecideAsync(
            ()=>new TaskCompletionSource<string>().Task,Ack));
        Assert.Equal(0,ackCount);
    }

    [Fact]
    public async Task Editor_AckTransportFailureOrTimeoutRequiresConfirmation()
    {
        Task<string> Editor()=>Task.FromResult("1");
        Assert.Equal(CloseDecision.ConfirmUnresponsive,await CloseHandshake.DecideAsync(
            Editor,()=>throw new InvalidOperationException("post failed")));
        Assert.Equal(CloseDecision.ConfirmTimeout,await CloseHandshake.DecideAsync(
            Editor,()=>new TaskCompletionSource<CloseAck>().Task));
    }

    [Theory]
    [InlineData(false,true,CloseDecision.Close)]
    [InlineData(true,true,CloseDecision.ConfirmDirty)]
    [InlineData(false,false,CloseDecision.ConfirmUnflushed)]
    public async Task Editor_ReturnsDecisionForItsActualAck(bool dirty,bool flushed,CloseDecision expected)
    {
        var handshake=new CloseHandshake();
        Assert.True(handshake.Begin());
        try
        {
            var actual=await CloseHandshake.DecideAsync(()=>Task.FromResult("1"),()=>
            {
                var (_,ack)=handshake.CreateRequest();
                Assert.True(handshake.HandleMessage(System.Text.Json.JsonSerializer.Serialize(
                    new {type=CloseHandshake.AckType,requestId="close-1",dirty,flushed})));
                return ack;
            });
            Assert.Equal(expected,actual);
        }
        finally{handshake.End();}
    }

    private const char Quote = '"';

    private static string Source(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LiveClipper.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. segments]));
    }

    [Fact]
    public void DesktopAndPage_ShareTheProtocolConstants()
    {
        var desktop = Source("src", "LiveClipper.Desktop", "CloseHandshake.cs");
        var page = Source("web", "src", "features", "editor", "closeHandshake.ts");
        Assert.Contains($"RequestType={Quote}lc-close-request{Quote};", desktop, StringComparison.Ordinal);
        Assert.Contains($"AckType={Quote}lc-close-ack{Quote};", desktop, StringComparison.Ordinal);
        Assert.Contains($"MarkerName={Quote}__lcCloseHandshake{Quote};", desktop, StringComparison.Ordinal);
        Assert.Contains("export const CLOSE_REQUEST_TYPE = 'lc-close-request';", page, StringComparison.Ordinal);
        Assert.Contains("export const CLOSE_ACK_TYPE = 'lc-close-ack';", page, StringComparison.Ordinal);
        Assert.Contains("export const CLOSE_HANDSHAKE_MARKER = '__lcCloseHandshake';", page, StringComparison.Ordinal);
        // 桌面壳等 ack（3s）必须比页面 flush 上限（2500ms）长。
        Assert.Contains("AckTimeout=TimeSpan.FromSeconds(3);", desktop, StringComparison.Ordinal);
        Assert.Contains("export const CLOSE_FLUSH_TIMEOUT_MS = 2500;", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_OnlyClosesDirectlyWhenCleanAndFlushed()
    {
        var desktop = Source("src", "LiveClipper.Desktop", "CloseHandshake.cs");
        Assert.Contains("if(ack is null)return CloseDecision.ConfirmTimeout;", desktop, StringComparison.Ordinal);
        Assert.Contains("if(ack.Dirty)return CloseDecision.ConfirmDirty;", desktop, StringComparison.Ordinal);
        Assert.Contains("if(!ack.Flushed)return CloseDecision.ConfirmUnflushed;", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreWebView2", desktop, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_AsksTheEditorBeforeDisposingTheHost()
    {
        var window = Source("src", "LiveClipper.Desktop", "MainWindow.xaml.cs");
        var handle = window.IndexOf("if(_closeHandshake.HandleMessage(args.WebMessageAsJson))return;", StringComparison.Ordinal);
        var generic = window.IndexOf($"var requestId=root.GetProperty({Quote}requestId{Quote})", StringComparison.Ordinal);
        Assert.True(handle > 0 && generic > handle, "lc-close-ack 必须在通用 requestId 解析之前被截走");
        var closing = window.IndexOf("private async void OnClosing(", StringComparison.Ordinal);
        Assert.True(closing > 0, "OnClosing not found");
        var confirm = window.IndexOf("await ConfirmEditorCloseAsync()", closing, StringComparison.Ordinal);
        var dispose = window.IndexOf("_webHost?.Dispose();_webHost=null;", closing, StringComparison.Ordinal);
        Assert.True(confirm > closing && dispose > confirm, "必须先问完编辑器再释放 WebHost");
        Assert.Contains("CoreWebView2ProcessFailedKind.RenderProcessExited", window, StringComparison.Ordinal);
        Assert.Contains("return UnsavedChangesDialog.Confirm(this,decision);", window, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", window, StringComparison.Ordinal);
    }

    private static string Handler(string source, string signature)
    {
        var match = Regex.Match(source, Regex.Escape(signature) + @"[^{]*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(match.Success, signature + " not found");
        return match.Groups["body"].Value;
    }

    [Fact]
    public void UnsavedChangesDialog_OnlyLeaveButtonReturnsTrue_AndStayIsDefaultAndCancel()
    {
        var code = Source("src", "LiveClipper.Desktop", "UnsavedChangesDialog.cs");
        Assert.Contains("private bool _leave;", code, StringComparison.Ordinal);

        // Confirm：新建 owner 对话框 → ShowDialog → 返回 _leave。
        var confirm = Handler(code, "public static bool Confirm(");
        Assert.Matches(@"new UnsavedChangesDialog\(reason\)\s*\{\s*Owner\s*=\s*owner\s*\}", confirm);
        Assert.True(confirm.IndexOf("ShowDialog()", StringComparison.Ordinal) > confirm.IndexOf("new UnsavedChangesDialog", StringComparison.Ordinal));
        Assert.Matches(@"return\s+dialog\._leave;\s*$", confirm);

        // 整个文件里唯一的 _leave=true 必须在 OnLeave 内并随后 Close。
        Assert.Single(Regex.Matches(code, @"_leave\s*=\s*true\s*;"));
        var leave = Handler(code, "private void OnLeave(");
        Assert.Matches(@"_leave\s*=\s*true\s*;\s*Close\(\)\s*;", leave);

        var stay = Handler(code, "private void OnStay(");
        Assert.Matches(@"_leave\s*=\s*false\s*;\s*Close\(\)\s*;", stay);
        Assert.DoesNotContain("true", stay, StringComparison.Ordinal);

        var keyDown = Handler(code, "protected override void OnKeyDown(");
        Assert.Matches(@"e\.Key\s*==\s*Key\.Escape\s*\)\s*\{\s*_leave\s*=\s*false\s*;\s*Close\(\)\s*;", keyDown);
        Assert.DoesNotContain("true", keyDown.Replace("e.Handled=true", ""), StringComparison.Ordinal);

        // XAML：实际解析按钮属性。
        var xaml = XDocument.Parse(Source("src", "LiveClipper.Desktop", "UnsavedChangesDialog.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var buttons = xaml.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
        XElement Button(string name) => Assert.Single(buttons, b => (string?)b.Attribute(x + "Name") == name);
        var close = Button("CloseButton");
        var leaveBtn = Button("LeaveButton");
        var stayBtn = Button("StayButton");
        Assert.Equal("OnStay", (string?)close.Attribute("Click"));
        Assert.Equal("OnLeave", (string?)leaveBtn.Attribute("Click"));
        Assert.Equal("OnStay", (string?)stayBtn.Attribute("Click"));
        Assert.Null(leaveBtn.Attribute("IsDefault"));
        Assert.Null(leaveBtn.Attribute("IsCancel"));
        Assert.Equal("True", (string?)stayBtn.Attribute("IsDefault"));
        Assert.Equal("True", (string?)stayBtn.Attribute("IsCancel"));
        Assert.Single(buttons, b => (string?)b.Attribute("IsDefault") == "True");
        Assert.Single(buttons, b => (string?)b.Attribute("IsCancel") == "True");
        Assert.Equal("{Binding ElementName=StayButton}", (string?)xaml.Root!.Attribute("FocusManager.FocusedElement"));
        // 唯一绑定 OnLeave 的控件就是 LeaveButton。
        Assert.Single(xaml.Descendants().Attributes("Click"), a => a.Value == "OnLeave");
    }
}
