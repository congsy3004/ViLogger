using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>
    /// Handles Enter key in Tx input to send if 'Enter to send' is enabled.
    /// </summary>
    private void TxTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MainViewModel mainVm && mainVm.EnterToSend)
            {
                if (sender is TextBox tb && tb.DataContext is PortTabViewModel portVm)
                {
                    if (portVm.SendCommand.CanExecute(null))
                    {
                        portVm.SendCommand.Execute(null);
                        e.Handled = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Ensure all logs are flushed and serial connections closed when the application closes.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is MainViewModel mainVm)
        {
            mainVm.Shutdown();
        }
    }
}
