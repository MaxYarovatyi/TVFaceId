using System.Drawing;

namespace Core
{
    public record DetectedFace
    (
        RectangleF BoundingBox,
        float Score,
        PointF[] Landmarks
    );
}