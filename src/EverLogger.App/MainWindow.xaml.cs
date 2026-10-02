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
    /// Handles Enter key in Tx input:
    /// - Plain Enter: sends the TX content immediately (same as clicking the Send button).
    /// - Ctrl+Enter: inserts CR (&lt;CR&gt; / 0D) into the input. If the last character was
    ///   already CR, inserts LF instead (&lt;LF&gt; / 0A). LF is only appended immediately
    ///   after CR (the smart rule).
    /// </summary>
    private void TxTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not TextBox tb || tb.DataContext is not PortTabViewModel portVm) return;

        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (!ctrlHeld)
        {
            // Plain Enter → send
            if (portVm.SendCommand.CanExecute(null))
            {
                portVm.SendCommand.Execute(null);
            }
        }
        else
        {
            // Ctrl+Enter → insert CR, then LF if last was CR (smart rule)
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
                // ASCII mode: insert <CR> token, then <LF> token if last token was <CR>
                if (current.EndsWith("<CR>"))
                    portVm.TxInput = current + "<LF>";
                else
                    portVm.TxInput = current + "<CR>";
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
