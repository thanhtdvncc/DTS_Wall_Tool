using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Wall_Tool.Core.Interfaces;
using DTS_Wall_Tool.Core.Primitives;

namespace DTS_Wall_Tool.Core.Data
{
    /// <summary>
    /// Represents an area (polygon) imported from SAP2000 or used for mapping.
    /// Contains boundary points, optional joint names and Z values and utility methods.
    /// </summary>
    public class SapArea : IAreaGeometry
    {
        /// <summary>
        /// Area name in SAP2000
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Boundary points in plan (ordered)
        /// </summary>
        public List<Point2D> BoundaryPoints { get; set; } = new List<Point2D>();

        /// <summary>
        /// Optional joint names (SAP joint ids) associated with boundary points
        /// </summary>
        public List<string> JointNames { get; set; } = new List<string>();

        /// <summary>
        /// Optional Z values for each boundary vertex
        /// </summary>
        public List<double> ZValues { get; set; } = new List<double>();

        /// <summary>
        /// Section name (if any)
        /// </summary>
        public string Section { get; set; } = string.Empty;

        /// <summary>
        /// Story name (if any)
        /// </summary>
        public string Story { get; set; } = string.Empty;

        /// <summary>
        /// Area type (default "Shell")
        /// </summary>
        public string AreaType { get; set; } = "Shell";

        /// <summary>
        /// Average Z of provided Z values (0 if none)
        /// </summary>
        public double AverageZ => (ZValues.Count > 0) ? ZValues.Average() : 0.0;

        /// <summary>
        /// Alias for AverageZ
        /// </summary>
        public double Z => AverageZ;

        /// <summary>
        /// Centroid of the polygon (average of vertex coordinates). Returns Point2D.Origin for empty polygons.
        /// </summary>
        public Point2D Centroid
        {
            get
            {
                if (BoundaryPoints == null || BoundaryPoints.Count == 0)
                    return Point2D.Origin;

                double cx = BoundaryPoints.Average(p => p.X);
                double cy = BoundaryPoints.Average(p => p.Y);
                return new Point2D(cx, cy);
            }
        }

        /// <summary>
        /// Alias for Centroid
        /// </summary>
        public Point2D Center => Centroid;

        /// <summary>
        /// Axis-aligned bounding box of the polygon.
        /// </summary>
        public BoundingBox BoundingBox
        {
            get
            {
                if (BoundaryPoints == null || BoundaryPoints.Count == 0)
                    return BoundingBox.Empty;

                return BoundingBox.FromPoints(BoundaryPoints.ToArray());
            }
        }

        /// <summary>
        /// Area of polygon (absolute value). Returns 0 for degenerate polygons.
        /// </summary>
        public double Area
        {
            get
            {
                if (BoundaryPoints == null || BoundaryPoints.Count < 3)
                    return 0.0;

                return Math.Abs(CalculatePolygonArea(BoundaryPoints));
            }
        }

        /// <summary>
        /// Number of boundary points
        /// </summary>
        public int PointCount => BoundaryPoints?.Count ?? 0;

        /// <summary>
        /// True if the polygon has exactly 4 points and they form right angles (approx).
        /// </summary>
        public bool IsRectangle
        {
            get
            {
                if (PointCount != 4) return false;

                const double tol = 1e-4;
                for (int i = 0; i < 4; i++)
                {
                    Point2D p0 = BoundaryPoints[i];
                    Point2D p1 = BoundaryPoints[(i + 1) % 4];
                    Point2D p2 = BoundaryPoints[(i + 2) % 4];

                    Point2D v1 = p1 - p0;
                    Point2D v2 = p2 - p1;
                    double dot = v1.Dot(v2);
                    if (Math.Abs(dot) > tol) return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Returns true if point is inside polygon (ray-casting algorithm)
        /// </summary>
        public bool ContainsPoint(Point2D point)
        {
            if (BoundaryPoints == null || BoundaryPoints.Count < 3) return false;
            return IsPointInPolygon(point, BoundaryPoints);
        }

        /// <summary>
        /// Calculate polygon signed area using shoelace formula.
        /// </summary>
        public static double CalculatePolygonArea(List<Point2D> pts)
        {
            if (pts == null || pts.Count < 3) return 0.0;

            double area = 0.0;
            int j = pts.Count - 1;
            for (int i = 0; i < pts.Count; i++)
            {
                area += (pts[j].X + pts[i].X) * (pts[j].Y - pts[i].Y);
                j = i;
            }
            return area / 2.0;
        }

        /// <summary>
        /// Point-in-polygon test using ray casting.
        /// </summary>
        public static bool IsPointInPolygon(Point2D p, List<Point2D> poly)
        {
            if (poly == null || poly.Count < 3) return false;

            bool inside = false;
            int j = poly.Count - 1;
            for (int i = 0; i < poly.Count; i++)
            {
                if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                    p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                {
                    inside = !inside;
                }
                j = i;
            }
            return inside;
        }

        /// <summary>
        /// Calculate centroid as average of vertices. Returns Point2D.Origin for empty list.
        /// </summary>
        public static Point2D CalculateCentroid(List<Point2D> pts)
        {
            if (pts == null || pts.Count == 0) return Point2D.Origin;
            double cx = pts.Average(p => p.X);
            double cy = pts.Average(p => p.Y);
            return new Point2D(cx, cy);
        }

        public override string ToString()
        {
            return string.Format("SapArea[{0}]: {1} pts, Area={2:0.00}m², Z={3:0}",
                Name, PointCount, Area / 1000000.0, Z);
        }

        /// <summary>
        /// Clone the SapArea (deep copy of lists)
        /// </summary>
        public SapArea Clone()
        {
            var clone = new SapArea();
            clone.Name = this.Name;
            clone.BoundaryPoints = (this.BoundaryPoints != null) ? this.BoundaryPoints.ToList() : new List<Point2D>();
            clone.JointNames = (this.JointNames != null) ? this.JointNames.ToList() : new List<string>();
            clone.ZValues = (this.ZValues != null) ? this.ZValues.ToList() : new List<double>();
            clone.Section = this.Section;
            clone.Story = this.Story;
            clone.AreaType = this.AreaType;
            return clone;
        }
    }
}
