using System;
using System.Collections.Generic;
using DTS_Wall_Tool.Core.Primitives;

namespace DTS_Wall_Tool.Core.Interfaces
{
    /// <summary>
    /// Represents a 2D area geometry (polygon) with additional metadata.
    /// Implementations provide boundary points, centroid, bounding box, area and average Z.
    /// </summary>
    public interface IAreaGeometry
    {
        /// <summary>
        /// Ordered list of boundary points in 2D (plan view).
        /// </summary>
        List<Point2D> BoundaryPoints { get; set; }

        /// <summary>
        /// Centroid of the polygon (plan coordinates).
        /// </summary>
        Point2D Centroid { get; }

        /// <summary>
        /// Axis-aligned bounding box of the polygon.
        /// </summary>
        BoundingBox BoundingBox { get; }

        /// <summary>
        /// Area of the polygon (in the same length units squared as the points).
        /// </summary>
        double Area { get; }

        /// <summary>
        /// Average Z value of the polygon vertices (if available), otherwise 0.
        /// </summary>
        double AverageZ { get; }

        /// <summary>
        /// Returns true if the given 2D point is inside the polygon.
        /// </summary>
        bool ContainsPoint(Point2D point);
    }
}
