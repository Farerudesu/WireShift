using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetBinder.UI.ViewModels;

namespace NetBinder.UI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Dispose();
        }
        base.OnClosed(e);
    }

    /// <summary>
    /// Event handler for copying the SOCKS5 proxy endpoint to the clipboard.
    /// </summary>
    private void CopyProxy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string proxyAddress)
        {
            try
            {
                Clipboard.SetText(proxyAddress);
                if (DataContext is MainViewModel vm)
                {
                    vm.ShowToast($"Copied proxy address: {proxyAddress}");
                }
            }
            catch (Exception ex)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.ShowToast($"Failed to copy: {ex.Message}", true);
                }
            }
        }
    }

    /// <summary>
    /// Event handler to commit inline metric edits when the Enter key is pressed.
    /// </summary>
    private void MetricTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            // Force binding source update
            var bindingExpr = textBox.GetBindingExpression(TextBox.TextProperty);
            bindingExpr?.UpdateSource();

            if (DataContext is MainViewModel vm && vm.ApplyMetricCommand.CanExecute(null))
            {
                // Lose focus to look clean
                Keyboard.ClearFocus();
                vm.ApplyMetricCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Event handler to commit inline metric edits when focus is lost (blur).
    /// </summary>
    private void MetricTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            var bindingExpr = textBox.GetBindingExpression(TextBox.TextProperty);
            bindingExpr?.UpdateSource();

            if (DataContext is MainViewModel vm && vm.ApplyMetricCommand.CanExecute(null))
            {
                vm.ApplyMetricCommand.Execute(null);
            }
        }
    }

    /// <summary>
    /// Quick double-click action to bind a process to the selected network interface.
    /// </summary>
    private void ProcessList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.BindCommand.CanExecute(null))
        {
            vm.BindCommand.Execute(null);
        }
    }
}
