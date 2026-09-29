using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace PCBoost.App.Controls;

/// <summary>
/// Graphique linéaire léger (Path, pas de bibliothèque tierce) : valeurs normalisées 0–100, NaN = absence de mesure
/// (interruption de la courbe, jamais d'interpolation inventée). Redessiné uniquement quand les valeurs ou la taille changent.
/// </summary>
public sealed partial class PerformanceChart : UserControl
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(PerformanceChart), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(PerformanceChart), new PropertyMetadata(null, OnStrokeChanged));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(PerformanceChart), new PropertyMetadata(null, OnFillChanged));

    public static readonly DependencyProperty CapacityProperty =
        DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(PerformanceChart), new PropertyMetadata(120, OnChanged));

    public static readonly DependencyProperty AccessibleTextProperty =
        DependencyProperty.Register(nameof(AccessibleText), typeof(string), typeof(PerformanceChart), new PropertyMetadata(string.Empty, OnAccessibleChanged));

    public PerformanceChart()
    {
        // Couleurs par défaut : ressources de thème du XAML (suivent le thème effectif) ; Stroke/Fill les remplacent si fournis.
        InitializeComponent();
        Root.SizeChanged += (_, _) => Redraw();
    }

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Nombre de points représentés sur la largeur (la série s'aligne à droite, « maintenant »).</summary>
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    public string AccessibleText { get => (string)GetValue(AccessibleTextProperty); set => SetValue(AccessibleTextProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PerformanceChart)d).Redraw();

    private static void OnStrokeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is Brush brush) ((PerformanceChart)d).LinePath.Stroke = brush;
    }

    private static void OnFillChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is Brush brush) ((PerformanceChart)d).AreaPath.Fill = brush;
    }

    private static void OnAccessibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => AutomationProperties.SetName((PerformanceChart)d, (string)e.NewValue);

    private void Redraw()
    {
        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        if (width <= 1 || height <= 1) return;

        var grid = new GeometryGroup();
        for (var i = 1; i <= 3; i++)
        {
            var y = Math.Round(height * i / 4) + 0.5;
            grid.Children.Add(new LineGeometry { StartPoint = new Point(0, y), EndPoint = new Point(width, y) });
        }
        GridLines.Data = grid;

        var values = Values;
        var capacity = Math.Max(2, Capacity);
        if (values is null || values.Count == 0)
        {
            LinePath.Data = null;
            AreaPath.Data = null;
            return;
        }

        var step = width / (capacity - 1);
        var offset = capacity - Math.Min(values.Count, capacity);
        var start = Math.Max(0, values.Count - capacity);

        var line = new PathGeometry();
        var area = new PathGeometry();
        PathFigure? lineFigure = null;
        PathFigure? areaFigure = null;
        Point last = default;

        for (var i = start; i < values.Count; i++)
        {
            var v = values[i];
            var x = (offset + (i - start)) * step;
            if (double.IsNaN(v))
            {
                CloseArea(areaFigure, last, height);
                lineFigure = null;
                areaFigure = null;
                continue;
            }
            var y = height - Math.Clamp(v, 0, 100) / 100d * (height - 2) - 1;
            var point = new Point(x, y);
            if (lineFigure is null)
            {
                lineFigure = new PathFigure { StartPoint = point, IsClosed = false, IsFilled = false };
                line.Figures.Add(lineFigure);
                areaFigure = new PathFigure { StartPoint = new Point(x, height), IsClosed = true, IsFilled = true };
                areaFigure.Segments.Add(new LineSegment { Point = point });
                area.Figures.Add(areaFigure);
            }
            else
            {
                lineFigure.Segments.Add(new LineSegment { Point = point });
                areaFigure!.Segments.Add(new LineSegment { Point = point });
            }
            last = point;
        }
        CloseArea(areaFigure, last, height);

        LinePath.Data = line;
        AreaPath.Data = area;
    }

    private static void CloseArea(PathFigure? figure, Point last, double height)
    {
        figure?.Segments.Add(new LineSegment { Point = new Point(last.X, height) });
    }
}
