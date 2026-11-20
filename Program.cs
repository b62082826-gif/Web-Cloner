
using System;
using System.Windows.Forms;

namespace WebGrabber.Pro;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

