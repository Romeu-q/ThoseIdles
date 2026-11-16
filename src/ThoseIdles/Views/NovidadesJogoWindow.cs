using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThoseIdles.Services;

namespace ThoseIdles.Views;

/// <summary>A janela "Novidades do PokeIdle": o changelog da versão nova e o que pode ter afetado o ThoseIdles.</summary>
public static class NovidadesJogoWindow
{
    public static Window Mostrar(GameUpdateWatcher.Relatorio r, Window? dono)
    {
        var app = Application.Current;
        Brush B(string k) => (Brush)app.FindResource(k);

        var corpo = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        void Titulo(string t, string cor = "Text") =>
            corpo.Children.Add(new TextBlock { Text = t, FontSize = 16, Foreground = B(cor), Margin = new Thickness(0, 10, 0, 6), TextWrapping = TextWrapping.Wrap });
        void Item(string t, string cor = "Text") =>
            corpo.Children.Add(new TextBlock { Text = "▶ " + t, FontSize = 13, Foreground = B(cor), Margin = new Thickness(6, 2, 0, 4), TextWrapping = TextWrapping.Wrap });

        if (r.PodeAfetar.Count > 0 || r.Conferencias.Count > 0)
        {
            Titulo("O que pode ter afetado o ThoseIdles", "Accent");
            foreach (var c in r.Conferencias) Item(c, "Danger");
            foreach (var p in r.PodeAfetar) Item(p);
            corpo.Children.Add(new TextBlock
            {
                Text = "Para adaptar: clique em \"Copiar para o Claude\" e cole na conversa com ele.",
                FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(6, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            Titulo("Nada do ThoseIdles parece afetado", "Good");
        }

        foreach (var n in r.Novidades)
        {
            Titulo($"Novidades da v{n.Versao} ({n.Data})");
            if (n.Itens.Count == 0) Item("(o texto desta novidade não veio)", "Muted");
            foreach (var i in n.Itens) Item(i);
        }

        var copiar = new Button { Content = "Copiar para o Claude", Style = (Style)app.FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        copiar.Click += (_, _) =>
        {
            try { Clipboard.SetText(GameUpdateWatcher.TextoParaClaude(r)); copiar.Content = "✓ Copiado"; } catch { }
        };
        var fechar = new Button { Content = "Fechar", Style = (Style)app.FindResource("BarButton") };
        var rodape = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 8, 14, 12) };
        rodape.Children.Add(copiar);
        rodape.Children.Add(fechar);

        var raiz = new DockPanel();
        DockPanel.SetDock(rodape, Dock.Bottom);
        raiz.Children.Add(rodape);
        raiz.Children.Add(new ScrollViewer { Content = corpo, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var w = new Window
        {
            Title = r.MaisNova is { } m ? $"Novidades do PokeIdle — v{m.Versao}" : "Novidades do PokeIdle",
            Width = 720, Height = 640, Content = raiz,
            Background = B("WindowBg"), Foreground = B("Text"),
            FontFamily = (FontFamily)app.FindResource("PixelFont"),
            Owner = dono, WindowStartupLocation = dono is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Ideal);
        fechar.Click += (_, _) => w.Close();
        w.Show();
        return w;
    }
}
