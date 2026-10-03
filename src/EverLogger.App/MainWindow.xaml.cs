using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    /// Subscribes to the per-port ScrollToEndRequested event when the terminal ListBox is loaded.
    /// </summary>
    private void MonitorListBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        if (listBox.DataContext is not PortTabViewModel portVm) return;

        // Cache the inner ScrollViewer so we can call ScrollToBottom() directly.
        // ScrollToBottom() is unconditional and works regardless of item render state,
        // unlike ScrollIntoView() which silently fails when variable-height items
        // (from TextWrapping=Wrap) haven't been measured yet.
        ScrollViewer? scrollViewer = null;
        listBox.Loaded += (_, __) => scrollViewer = GetScrollViewer(listBox);
        scrollViewer = GetScrollViewer(listBox);

        portVm.ScrollToEndRequested -= OnScrollToEndRequested;
        portVm.ScrollToEndRequested += OnScrollToEndRequested;

        void OnScrollToEndRequested(object? _, EventArgs __)
        {
            // Defer to Background priority so new items are measured before we scroll.
            listBox.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                () =>
                {
                    // Re-acquire if template was just applied
                    scrollViewer ??= GetScrollViewer(listBox);
                    scrollViewer?.ScrollToBottom();
                });
        }

        // Store the unsubscribe action so Unloaded can detach it
        listBox.Tag = (Action)(() => portVm.ScrollToEndRequested -= OnScrollToEndRequested);
    }

    /// <summary>
    /// Unsubscribes from the per-port ScrollToEndRequested event when the terminal ListBox is unloaded.
    /// </summary>
    private void MonitorListBox_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.Tag is Action unsubscribe)
        {
            unsubscribe();
            listBox.Tag = null;
        }
    }

    /// <summary>
    /// Walks the visual tree to find the first <see cref="ScrollViewer"/> child of a ListBox.
    /// </summary>
    private static ScrollViewer? GetScrollViewer(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is ScrollViewer sv) return sv;
            var found = GetScrollViewer(child);
            if (found != null) return found;
        }
        return null;
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
