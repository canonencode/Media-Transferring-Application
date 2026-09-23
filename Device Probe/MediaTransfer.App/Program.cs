using System.Windows.Forms;

namespace MediaTransfer.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        // STA because WebView2 requires it. The scanner's COM work is not here
        // at all - it runs in its own process - so nothing in this app has to
        // care about apartment rules beyond this line.
        ApplicationConfiguration.Initialize();
        Application.Run(new MainWindow());
    }
}
