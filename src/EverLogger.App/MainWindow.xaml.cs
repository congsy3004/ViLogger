using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EverLogger.App.ViewModels;

namespace EverLogger.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    /// <summary>
    /// Handles auto-scroll when the MonitorListBox items change.
    /// Called from the TabControl's content to scroll to the bottom.
    /// </summary>
    private void MonitorListBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel.AutoScroll && e.ExtentHeightChange > 0)
        {
            if (sender is ListBox listBox && listBox.Items.Count > 0)
            {
                listBox.ScrollIntoView(listBox.Items[listBox.Items.Count - 1]);
            }
        }
    }
}
