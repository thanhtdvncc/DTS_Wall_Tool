using System;
using DTS_Wall_Tool.Core;
using static DTS_Wall_Tool.Core.Geometry;

namespace DTS_Wall_Tool.Models
{
    /// <summary>
    /// Represents a structural axis line (grid line)
    /// </summary>
    public class AxisLine
    {
        public Point2D StartPt { get; set; }
        public Point2D EndPt { get; set; }

        public double Length => StartPt.DistanceTo(EndPt);
        public double Angle => Math.Atan2(EndPt.Y - StartPt.Y, EndPt.X - StartPt.X);

        public LineSegment2D AsSegment => new LineSegment2D(StartPt, EndPt);

        /// <summary>
        /// True if axis is approximately horizontal
        /// </summary>
        public bool IsHorizontal
        {
            get
            {
                double absAngle = Math.Abs(Angle);
                return absAngle < 0.0873 || absAngle > 3.0543; // ~5 degrees
            }
        }

        /// <summary>
        /// True if axis is approximately vertical
        /// </summary>
        public bool IsVertical
        {
            get
            {
                return Math.Abs(Math.Abs(Angle) - GeoAlgo.HALF_PI) < 0.0873;
            }
        }

        /// <summary>
        /// Name/label of the axis (e.g., "A", "1", etc.)
        /// </summary>
        public string Name { get; set; } = "";

        public override string ToString()
        {
            string dir = IsHorizontal ? "H" : (IsVertical ? "V" : "D");
            return $"Axis[{Name}]: {StartPt}->{EndPt} ({dir})";
        }
    }
}