using System.Windows;
using System.Windows.Input;

namespace Hanairo;

public partial class IconEditor : Window
{
    private static readonly Point Center = new(0.5, 0.5);
    private bool syncingSlider;
    private Point? panStart;
    private IconComposition? panOrigin;

    internal IconComposition Composition { get; private set; }

    internal IconEditor(IconComposition composition, string fileName)
    {
        Composition = composition;
        syncingSlider = true;
        InitializeComponent();
        ImageFileName.Text = fileName;
        ImageFileName.ToolTip = fileName;
        ZoomSlider.Minimum = Math.Log2(IconComposition.MinZoom);
        ZoomSlider.Maximum = Math.Log2(composition.MaxZoom);
        UpdateComposition(composition);
    }

    private void UpdateComposition(IconComposition composition)
    {
        Composition = composition;
        SourceImage.Source = IconImage.Render(composition, (int)Viewport.Width);

        syncingSlider = true;
        ZoomSlider.Value = Math.Log2(composition.Zoom);
        CornerSlider.Value = composition.CornerRadius * 100;
        PaddingSlider.Value = composition.Padding * 100;
        syncingSlider = false;
        ZoomValue.Text = $"{composition.Zoom:P0}";
        CornerValue.Text = $"{composition.CornerRadius:P0}";
        PaddingValue.Text = $"{composition.Padding:P0}";

        LargePreview.Source = IconImage.Render(composition, 96);
        Preview16.Source = IconImage.Render(composition, 16);
        Preview32.Source = IconImage.Render(composition, 32);
        Preview48.Source = IconImage.Render(composition, 48);
    }

    private void ZoomSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!syncingSlider)
            UpdateComposition(Composition.WithZoom(Math.Pow(2, e.NewValue), Center));
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        UpdateComposition(Composition.WithZoom(Composition.Zoom / 1.25, Center));

    private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        UpdateComposition(Composition.WithZoom(Composition.Zoom * 1.25, Center));

    private void Fit_Click(object sender, RoutedEventArgs e) =>
        UpdateComposition(Composition with { Zoom = 1, X = 0, Y = 0 });

    private void Fill_Click(object sender, RoutedEventArgs e) =>
        UpdateComposition(Composition with { Zoom = Composition.FillZoom, X = 0, Y = 0 });

    private void EdgeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!syncingSlider)
            UpdateComposition(Composition with
            {
                CornerRadius = CornerSlider.Value / 100,
                Padding = PaddingSlider.Value / 100
            });
    }

    private void ResetEdges_Click(object sender, RoutedEventArgs e) =>
        UpdateComposition(Composition with { CornerRadius = 0, Padding = 0 });

    private void ZoomAtPointer(object sender, MouseWheelEventArgs e)
    {
        Point cursor = e.GetPosition(Viewport);
        double inset = Viewport.Width * Composition.Padding;
        double innerSize = Viewport.Width - 2 * inset;
        var anchor = new Point((cursor.X - inset) / innerSize, (cursor.Y - inset) / innerSize);
        UpdateComposition(Composition.WithZoom(Composition.Zoom * Math.Pow(1.15, e.Delta / 120.0), anchor));
        e.Handled = true;
    }

    private void BeginPan(object sender, MouseButtonEventArgs e)
    {
        Viewport.Focus();
        if (!Viewport.CaptureMouse())
            return;

        panStart = e.GetPosition(Viewport);
        panOrigin = Composition;
        e.Handled = true;
    }

    private void Pan(object sender, MouseEventArgs e)
    {
        if (panStart is not Point start || panOrigin is not IconComposition origin)
            return;

        Vector delta = e.GetPosition(Viewport) - start;
        double innerSize = Viewport.Width * (1 - 2 * origin.Padding);
        var next = origin.Move(delta.X / innerSize, delta.Y / innerSize);
        if (next != Composition)
            UpdateComposition(next);
    }

    private void EndPan(object sender, MouseButtonEventArgs e) => Viewport.ReleaseMouseCapture();

    private void LostPan(object sender, MouseEventArgs e)
    {
        panStart = null;
        panOrigin = null;
    }

    private void Viewport_KeyDown(object sender, KeyEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.08 : 0.02;
        var delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => default
        };
        if (delta.LengthSquared == 0)
            return;

        UpdateComposition(Composition.Move(delta.X, delta.Y));
        e.Handled = true;
    }

    private void UseIcon_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
