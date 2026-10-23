using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoConverterUI.Services;

public static class ClipHelper
{
    public static readonly DependencyProperty CornerRadiusClipProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadiusClip",
            typeof(bool),
            typeof(ClipHelper),
            new PropertyMetadata(false, OnCornerRadiusClipChanged));

    public static bool GetCornerRadiusClip(DependencyObject obj)
    {
        return (bool)obj.GetValue(CornerRadiusClipProperty);
    }

    public static void SetCornerRadiusClip(DependencyObject obj, bool value)
    {
        obj.SetValue(CornerRadiusClipProperty, value);
    }

    private static void OnCornerRadiusClipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Border border)
        {
            if ((bool)e.NewValue)
            {
                border.SizeChanged += Border_SizeChanged;
                border.Loaded += Border_Loaded;

                var dpd = DependencyPropertyDescriptor.FromProperty(Border.CornerRadiusProperty, typeof(Border));
                dpd?.AddValueChanged(border, Border_CornerRadiusChanged);

                UpdateClip(border);
            }
            else
            {
                border.SizeChanged -= Border_SizeChanged;
                border.Loaded -= Border_Loaded;

                var dpd = DependencyPropertyDescriptor.FromProperty(Border.CornerRadiusProperty, typeof(Border));
                dpd?.RemoveValueChanged(border, Border_CornerRadiusChanged);

                if (border.Child != null)
                {
                    border.Child.Clip = null;
                }
            }
        }
    }

    private static void Border_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            UpdateClip(border);
        }
    }

    private static void Border_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border border)
        {
            UpdateClip(border);
        }
    }

    private static void Border_CornerRadiusChanged(object? sender, EventArgs e)
    {
        if (sender is Border border)
        {
            UpdateClip(border);
        }
    }

    public static void UpdateClip(Border border)
    {
        if (border.Child == null || border.ActualWidth <= 0 || border.ActualHeight <= 0)
            return;

        CornerRadius cr = border.CornerRadius;
        double w = border.ActualWidth;
        double h = border.ActualHeight;

        double tl = Math.Max(0, cr.TopLeft);
        double tr = Math.Max(0, cr.TopRight);
        double br = Math.Max(0, cr.BottomRight);
        double bl = Math.Max(0, cr.BottomLeft);

        if (tl == 0 && tr == 0 && br == 0 && bl == 0)
        {
            border.Child.Clip = null;
            return;
        }

        // Adjust radii if sum exceeds dimensions
        double maxH = Math.Max(tl + tr, bl + br);
        if (maxH > w && maxH > 0)
        {
            double scale = w / maxH;
            tl *= scale;
            tr *= scale;
            br *= scale;
            bl *= scale;
        }

        double maxV = Math.Max(tl + bl, tr + br);
        if (maxV > h && maxV > 0)
        {
            double scale = h / maxV;
            tl *= scale;
            tr *= scale;
            br *= scale;
            bl *= scale;
        }

        Rect rect = new Rect(0, 0, w, h);

        if (tl == tr && tr == br && br == bl)
        {
            var rectGeo = new RectangleGeometry(rect, tl, tl);
            rectGeo.Freeze();
            border.Child.Clip = rectGeo;
            return;
        }

        StreamGeometry geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(tl, 0), true, true);
            ctx.LineTo(new Point(w - tr, 0), true, false);
            if (tr > 0)
                ctx.ArcTo(new Point(w, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, true, false);
            else
                ctx.LineTo(new Point(w, 0), true, false);

            ctx.LineTo(new Point(w, h - br), true, false);
            if (br > 0)
                ctx.ArcTo(new Point(w - br, h), new Size(br, br), 0, false, SweepDirection.Clockwise, true, false);
            else
                ctx.LineTo(new Point(w, h), true, false);

            ctx.LineTo(new Point(bl, h), true, false);
            if (bl > 0)
                ctx.ArcTo(new Point(0, h - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, true, false);
            else
                ctx.LineTo(new Point(0, h), true, false);

            ctx.LineTo(new Point(0, tl), true, false);
            if (tl > 0)
                ctx.ArcTo(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, true, false);
            else
                ctx.LineTo(new Point(0, 0), true, false);
        }
        geometry.Freeze();
        border.Child.Clip = geometry;
    }
}
