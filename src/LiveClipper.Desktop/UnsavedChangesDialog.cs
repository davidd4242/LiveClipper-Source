using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LiveClipper.Desktop;

/// <summary>关窗时的未保存确认。只有点「仍然退出」才返回 true；Esc、右上关闭、Alt+F4 都等于留在编辑器。</summary>
public partial class UnsavedChangesDialog : Window
{
    private bool _leave;

    private UnsavedChangesDialog(string reason)
    {
        InitializeComponent();
        ReasonText.Text=reason;
        if(SystemParameters.HighContrast)ApplyHighContrast();
    }

    public static bool Confirm(Window owner,CloseDecision decision)
    {
        // CloseHandshake.ConfirmMessage 首段是原因，后面的“是/否”说明由按钮本身取代。
        var message=CloseHandshake.ConfirmMessage(decision);
        var split=message.IndexOf(Environment.NewLine+Environment.NewLine,StringComparison.Ordinal);
        var reason=split>0?message[..split]:message;
        var dialog=new UnsavedChangesDialog(reason){Owner=owner};
        dialog.ShowDialog();
        return dialog._leave;
    }

    private void OnLeave(object sender,RoutedEventArgs e)
    {
        _leave=true;
        Close();
    }

    private void OnStay(object sender,RoutedEventArgs e)
    {
        _leave=false;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if(e.Key==Key.Escape){_leave=false;Close();e.Handled=true;}
    }

    private void ApplyHighContrast()
    {
        Frame.Background=SystemColors.WindowBrush;
        Frame.BorderBrush=SystemColors.WindowTextBrush;
        Accent.Background=SystemColors.HighlightBrush;
        TitleText.Foreground=SystemColors.WindowTextBrush;
        ReasonText.Foreground=SystemColors.WindowTextBrush;
        WarnText.Foreground=SystemColors.WindowTextBrush;
        foreach(var b in new[]{CloseButton,LeaveButton,StayButton})
        {
            b.Background=SystemColors.ControlBrush;
            b.Foreground=SystemColors.ControlTextBrush;
            b.BorderBrush=SystemColors.WindowTextBrush;
        }
    }
}
