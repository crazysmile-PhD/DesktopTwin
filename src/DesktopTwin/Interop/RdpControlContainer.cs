using System.Drawing;
using System.Windows.Forms;

namespace DesktopTwin.Interop;

/// <summary>
/// 由 WPF 安排外层容器，再由 WinForms 布局保持 ActiveX 的原点和尺寸。
/// 避免控件自行改变尺寸后，WindowsFormsHost 在相同约束下保留过期的布局。
/// </summary>
internal sealed class RdpControlContainer : Panel
{
    internal RdpControlContainer(Control child)
    {
        BackColor = Color.Black;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        TabStop = false;
        Controls.Add(child);
        child.Dock = DockStyle.Fill;
    }
}
