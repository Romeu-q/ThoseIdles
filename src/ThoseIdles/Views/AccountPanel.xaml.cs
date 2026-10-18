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

    private void OnLiveSizeChanged(object sender, SizeChangedEventArgs e) => Vm?.OnLiveResized(e.NewSize.Width);

    /// <summary>Abre o menu "⋯" logo abaixo do botão (clique esquerdo).</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } b) return;
        menu.PlacementTarget = b;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

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
