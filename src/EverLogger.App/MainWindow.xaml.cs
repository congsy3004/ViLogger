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
        if (e.Key != Key.Enter) return;
        if (sender is not TextBox tb || tb.DataContext is not PortTabViewModel portVm) return;

        // Check whether the current modifier matches the port's chosen send macro
        bool isSendMacro = portVm.SendMacro switch
        {
            "Ctrl+Enter"  => (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            "Alt+Enter"   => (Keyboard.Modifiers & ModifierKeys.Alt)     != 0,
            "Shift+Enter" => (Keyboard.Modifiers & ModifierKeys.Shift)   != 0,
            _             => false
        };

        if (isSendMacro)
        {
            // Send macro pressed — transmit the TX content
            if (portVm.SendCommand.CanExecute(null))
            {
                portVm.SendCommand.Execute(null);
            }
        }
        else
        {
            // Plain Enter (no macro modifier): insert CR then LF on next Enter
            string current = portVm.TxInput;
            if (portVm.SendAsHex)
            {
                // HEX mode: insert 0D (CR byte), then 0A (LF byte) if last byte was 0D
                string rawHex = new string(
                    current.Where(c => "0123456789ABCDEFabcdef".Contains(c)).ToArray())
                    .ToUpperInvariant();
                bool lastWasCR = rawHex.Length >= 2 && rawHex.Substring(rawHex.Length - 2) == "0D";
                portVm.TxInput = lastWasCR ? current + "0A" : current + "0D";
            }
            else
            {
                // ASCII mode: insert \r, then \n if the last char is already \r
                if (current.Length > 0 && current[current.Length - 1] == '\r')
                    portVm.TxInput = current + "\n";
                else
                    portVm.TxInput = current + "\r";
            }
            tb.CaretIndex = portVm.TxInput.Length;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Ensure all logs are flushed and serial connections closed when the application closes.
    /// </summary>
    private HelpWindow? _helpWindow;

    /// <summary>
    /// Opens the help window (or brings it to front if already open).
    /// </summary>
    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (_helpWindow == null || !_helpWindow.IsLoaded)
        {
            _helpWindow = new HelpWindow { Owner = this };
            _helpWindow.Show();
        }
        else
        {
            _helpWindow.Activate();
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
