using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ThoseIdles.Views;

/// <summary>
/// Dispõe os quatro painéis em grade 2x2 ou em "foco" (uma conta grande + três miniaturas), com
/// divisórias arrastáveis:
///  - grade: uma divisória horizontal entre as linhas e uma vertical em CADA linha (independentes),
///    então cada conta pode ter tamanho próprio;
///  - foco: uma divisória vertical entre a conta em foco e a coluna de miniaturas.
/// Duplo clique numa divisória volta ao meio.
///
/// Os painéis NUNCA saem da árvore visual nem ficam ocultos: mudar de modo ou arrastar só muda
/// retângulos. Ocultar/reparentar um WebView2 faria o Chromium tratar a página como escondida
/// (requestAnimationFrame para, timers ficam limitados) ou recriaria a janela nativa.
/// As divisórias ficam nos vãos entre os painéis (o WebView2 é um HWND: nada do WPF é desenhado
/// por cima dele).
/// </summary>
public sealed class SessionLayoutPanel : Panel
{
    private const double Gap = 6;
    private const double MinPanel = 160;

    private enum SplitKind { Row, Top, Bottom, Focus }

    private readonly Dictionary<SplitKind, Thumb> _splitters = new();

    public SessionLayoutPanel()
    {
        foreach (var kind in Enum.GetValues<SplitKind>())
        {
            var thumb = CreateSplitter(kind);
            _splitters[kind] = thumb;
            InternalChildren.Add(thumb);
        }
    }

    // ------------------------------------------------------------------ propriedades

    public static readonly DependencyProperty IsFocusModeProperty = DependencyProperty.Register(
        nameof(IsFocusMode), typeof(bool), typeof(SessionLayoutPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty FocusedIndexProperty = DependencyProperty.Register(
        nameof(FocusedIndex), typeof(int), typeof(SessionLayoutPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Quantas contas aparecem (as primeiras N); as demais ficam com tamanho zero.</summary>
    public static readonly DependencyProperty VisibleCountProperty = DependencyProperty.Register(
        nameof(VisibleCount), typeof(int), typeof(SessionLayoutPanel),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int VisibleCount
    {
        get => (int)GetValue(VisibleCountProperty);
        set => SetValue(VisibleCountProperty, value);
    }

    public static readonly DependencyProperty RowSplitProperty = SplitProperty(nameof(RowSplit), 0.5);
    public static readonly DependencyProperty TopSplitProperty = SplitProperty(nameof(TopSplit), 0.5);
    public static readonly DependencyProperty BottomSplitProperty = SplitProperty(nameof(BottomSplit), 0.5);
    public static readonly DependencyProperty FocusSplitProperty = SplitProperty(nameof(FocusSplit), 0.78);

    /// <summary>Disparado ao soltar uma divisória (para salvar as configurações uma vez só).</summary>
    public event EventHandler? SplitCommitted;

    private static DependencyProperty SplitProperty(string name, double initial) => DependencyProperty.Register(
        name, typeof(double), typeof(SessionLayoutPanel),
        new FrameworkPropertyMetadata(initial,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault)
        {
            DefaultUpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });

    public bool IsFocusMode
    {
        get => (bool)GetValue(IsFocusModeProperty);
        set => SetValue(IsFocusModeProperty, value);
    }

    public int FocusedIndex
    {
        get => (int)GetValue(FocusedIndexProperty);
        set => SetValue(FocusedIndexProperty, value);
    }

    public double RowSplit { get => (double)GetValue(RowSplitProperty); set => SetValue(RowSplitProperty, value); }
    public double TopSplit { get => (double)GetValue(TopSplitProperty); set => SetValue(TopSplitProperty, value); }
    public double BottomSplit { get => (double)GetValue(BottomSplitProperty); set => SetValue(BottomSplitProperty, value); }
    public double FocusSplit { get => (double)GetValue(FocusSplitProperty); set => SetValue(FocusSplitProperty, value); }

    // ------------------------------------------------------------------ layout

    private IEnumerable<UIElement> Accounts => InternalChildren.Cast<UIElement>().Where(c => c is not Thumb t || !_splitters.ContainsValue(t));

    protected override Size MeasureOverride(Size available)
    {
        var size = new Size(
            double.IsInfinity(available.Width) ? 1280 : available.Width,
            double.IsInfinity(available.Height) ? 800 : available.Height);
        var l = Compute(size);
        var i = 0;
        foreach (var child in Accounts) child.Measure(i < l.Panels.Length ? l.Panels[i++].Size : default);
        foreach (var (kind, thumb) in _splitters) thumb.Measure(l.Splitters.TryGetValue(kind, out var r) ? r.Size : default);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var l = Compute(finalSize);
        var i = 0;
        foreach (var child in Accounts) child.Arrange(i < l.Panels.Length ? l.Panels[i++] : default);
        foreach (var (kind, thumb) in _splitters)
        {
            var visible = l.Splitters.TryGetValue(kind, out var r);
            thumb.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            thumb.Arrange(visible ? r : default);
        }
        return finalSize;
    }

    private (Rect[] Panels, Dictionary<SplitKind, Rect> Splitters) Compute(Size s)
    {
        // Painéis além de VisibleCount ficam com retângulo vazio (a conta deles está fechada).
        var panels = new Rect[Accounts.Count()];
        var n = Math.Clamp(VisibleCount, 0, panels.Length);
        var splitters = new Dictionary<SplitKind, Rect>();
        if (n == 0) return (panels, splitters);
        var w = Math.Max(0, s.Width);
        var h = Math.Max(0, s.Height);

        if (n == 1)
        {
            panels[0] = new Rect(0, 0, w, h);
            return (panels, splitters);
        }

        if (!IsFocusMode && n == 2)
        {
            // Lado a lado, com a divisória da linha de cima.
            var leftW = Fraction(TopSplit, w - Gap);
            panels[0] = new Rect(0, 0, leftW, h);
            panels[1] = new Rect(leftW + Gap, 0, Math.Max(0, w - leftW - Gap), h);
            splitters[SplitKind.Top] = new Rect(leftW, 0, Gap, h);
            return (panels, splitters);
        }

        if (!IsFocusMode)
        {
            // Linhas: a de cima recebe RowSplit da altura útil.
            var topH = Fraction(RowSplit, h - Gap);
            var bottomY = topH + Gap;
            var bottomH = Math.Max(0, h - bottomY);
            splitters[SplitKind.Row] = new Rect(0, topH, w, Gap);

            // Cada linha tem sua própria divisória vertical.
            var topW = Fraction(TopSplit, w - Gap);
            var botW = Fraction(BottomSplit, w - Gap);
            Place(0, new Rect(0, 0, topW, topH));
            Place(1, new Rect(topW + Gap, 0, Math.Max(0, w - topW - Gap), topH));
            splitters[SplitKind.Top] = new Rect(topW, 0, Gap, topH);
            if (n == 3)
            {
                // Terceira conta ocupa a linha de baixo inteira.
                Place(2, new Rect(0, bottomY, w, bottomH));
                return (panels, splitters);
            }
            Place(2, new Rect(0, bottomY, botW, bottomH));
            Place(3, new Rect(botW + Gap, bottomY, Math.Max(0, w - botW - Gap), bottomH));
            splitters[SplitKind.Bottom] = new Rect(botW, bottomY, Gap, bottomH);
            return (panels, splitters);
        }

        var focused = Math.Clamp(FocusedIndex, 0, n - 1);
        var mainW = Fraction(FocusSplit, w - Gap);
        var thumbW = Math.Max(0, w - mainW - Gap);
        panels[focused] = new Rect(0, 0, mainW, h);
        splitters[SplitKind.Focus] = new Rect(mainW, 0, Gap, h);

        var others = n - 1;
        var thumbH = others > 0 ? Math.Max(0, (h - Gap * (others - 1)) / others) : 0;
        var k = 0;
        for (var i = 0; i < n; i++)
        {
            if (i == focused) continue;
            panels[i] = new Rect(mainW + Gap, k * (thumbH + Gap), thumbW, thumbH);
            k++;
        }
        return (panels, splitters);

        void Place(int index, Rect r)
        {
            if (index < panels.Length) panels[index] = r;
        }
    }

    /// <summary>Retângulos calculados para um tamanho (o autoteste confere a disposição).</summary>
    public Rect[] PanelRects(Size s) => Compute(s).Panels;

    /// <summary>Fração do espaço útil, respeitando o tamanho mínimo dos dois lados.</summary>
    private static double Fraction(double f, double total)
    {
        total = Math.Max(0, total);
        if (total < MinPanel * 2) return total * 0.5;
        return Math.Clamp(total * f, MinPanel, total - MinPanel);
    }

    // ------------------------------------------------------------------ divisórias

    private Thumb CreateSplitter(SplitKind kind)
    {
        var vertical = kind != SplitKind.Row; // barra vertical = arrasta na horizontal
        var thumb = new Thumb
        {
            Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS,
            Focusable = false,
            ToolTip = "Arraste para redimensionar · duplo clique volta ao meio",
            Template = SplitterTemplate(),
        };
        thumb.DragDelta += (_, e) => OnDrag(kind, vertical ? e.HorizontalChange : e.VerticalChange);
        thumb.DragCompleted += (_, _) => SplitCommitted?.Invoke(this, EventArgs.Empty);
        thumb.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            SetSplit(kind, kind == SplitKind.Focus ? 0.78 : 0.5);
            SplitCommitted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
        return thumb;
    }

    private void OnDrag(SplitKind kind, double delta)
    {
        var total = kind == SplitKind.Row ? ActualHeight - Gap : ActualWidth - Gap;
        if (total <= MinPanel * 2) return;
        var current = GetSplit(kind);
        var min = MinPanel / total;
        var max = 1 - min;
        if (kind == SplitKind.Focus) max = Math.Min(max, 0.95);
        SetSplit(kind, Math.Clamp(current + delta / total, min, max));
    }

    private double GetSplit(SplitKind kind) => kind switch
    {
        SplitKind.Row => RowSplit,
        SplitKind.Top => TopSplit,
        SplitKind.Bottom => BottomSplit,
        _ => FocusSplit,
    };

    private void SetSplit(SplitKind kind, double value)
    {
        switch (kind)
        {
            case SplitKind.Row: RowSplit = value; break;
            case SplitKind.Top: TopSplit = value; break;
            case SplitKind.Bottom: BottomSplit = value; break;
            default: FocusSplit = value; break;
        }
    }

    private static ControlTemplate SplitterTemplate()
    {
        // Faixa transparente que acende no hover/arraste, com um traço central discreto.
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        var line = new FrameworkElementFactory(typeof(Border), "line");
        line.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x35)));
        line.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        line.SetValue(Border.CornerRadiusProperty, new CornerRadius(1));
        border.AppendChild(line);

        var template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x55, 0x8B, 0x6C, 0xFF)), "bd"));
        var drag = new Trigger { Property = Thumb.IsDraggingProperty, Value = true };
        drag.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x8B, 0x6C, 0xFF)), "bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(drag);
        return template;
    }
}
