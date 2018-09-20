using System.Windows;
using VSInspector.Models;

namespace VSInspector
{
    public partial class App
    {
        private void MainWindow_Exit(object sender, ExitEventArgs e)
        {
            InspectorModel.Instance.Dispose();
        }
    }
}
