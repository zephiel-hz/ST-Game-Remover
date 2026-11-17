using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SteamPluginManager.Helpers
{
    public static class ThumbnailClipHelper
    {
        public static void ApplyTopRoundedClip(Border border, double width, double height, double cornerRadius)
        {
            if (width <= 0 || height <= 0)
                return;

            var radius = Math.Min(cornerRadius, Math.Min(width / 2, height));
            var geometry = new StreamGeometry();

            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(0, radius), true, true);
                context.ArcTo(new Point(radius, 0), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(width - radius, 0), true, false);
                context.ArcTo(new Point(width, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(width, height), true, false);
                context.LineTo(new Point(0, height), true, false);
            }

            geometry.Freeze();
            border.Clip = geometry;
        }
    }
}
