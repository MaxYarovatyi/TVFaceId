using System.Drawing;

namespace Core.Models
{
    public record DetectedFace
    (
        RectangleF BoundingBox,
        PointF[] Landmarks
    );
}
