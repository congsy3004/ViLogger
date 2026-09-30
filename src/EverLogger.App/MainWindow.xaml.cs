using System.Windows;
using System.Windows.Controls;
using EverLogger.App.ViewModels;

namespace EverLogger.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    /// <summary>
    /// Auto-scroll handler checks the per-port AutoScroll setting.
    /// </summary>
    private void MonitorListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0 && sender is ListBox listBox && listBox.Items.Count > 0)
        {
            // Get the per-port AutoScroll from the ListBox's DataContext (PortTabViewModel)
            if (listBox.DataContext is PortTabViewModel portVm && portVm.AutoScroll)
            {
                listBox.ScrollIntoView(listBox.Items[listBox.Items.Count - 1]);
            }
        }
    }
}
