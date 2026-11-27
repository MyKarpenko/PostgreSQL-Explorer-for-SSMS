using System;
using System.Windows.Forms;

namespace SsmsPgExplorer.UI
{
    /// <summary>
    /// ToolStrip that acts on the first click. Hosted in WPF (VS tool windows), a plain ToolStrip
    /// treats the first click as "activate the window" and drops it, so buttons look dead.
    /// </summary>
    internal sealed class ClickThroughToolStrip : ToolStrip
    {
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_ACTIVATEANDEAT = 2;
        private const int MA_ACTIVATE = 1;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_MOUSEACTIVATE && m.Result == (IntPtr)MA_ACTIVATEANDEAT)
                m.Result = (IntPtr)MA_ACTIVATE;
        }
    }
}
