using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ThoseIdles.ViewModels;

namespace ThoseIdles.Views;

public partial class AccountPanel : UserControl
{
    public AccountPanel() => InitializeComponent();

    private AccountViewModel? Vm => DataContext as AccountViewModel;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Vm?.OnPanelResized(e.NewSize.Width);

    private void OnNameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (NameBox.IsVisible)
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }
    }

    private void OnNameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm is { IsEditingName: true } vm) vm.CommitRenameCommand.Execute(null);
    }
}
