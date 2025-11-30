using DTS_Wall_Tool.Core.Algorithms;
using DTS_Wall_Tool.Core.Primitives;

namespace DTS_Wall_Tool.Core
{
    /// <summary>
    /// Facade class giữ backward compatibility với code cũ. 
    /// Delegate tất cả calls đến các Algorithms tương ứng. 
    /// </summary>
    public static class GeoAlgo
    {
        // Constants (backward compatibility)
        public const double EPSILON = GeometryConstants.EPSILON;
        public const double PI = GeometryConstants.PI;
        public const double HALF_PI = GeometryConstants.HALF_PI;
        public const double DEG_TO_RAD = GeometryConstants.DEG_TO_RAD;
        public const double RAD_TO_DEG = GeometryConstants.RAD_TO_DEG;

        #region Distance Functions

        public static double DistPointToSegment(Point2D P, Point2D A, Point2D B)
            => DistanceAlgorithms.PointToSegment(P, A, B);

        public static double DistPointToInfiniteLine(Point2D P, Point2D A, Point2D B)
            => DistanceAlgorithms.PointToInfiniteLine(P, A, B);

        public static double DistBetweenParallelSegments(LineSegment2D seg1, LineSegment2D seg2)
            => DistanceAlgorithms.BetweenParallelSegments(seg1, seg2);

        #endregion

        #region Projection Functions

        public static double ProjectPointOnSegment(Point2D P, LineSegment2D segment, out Point2D projection)
        {
            var result = ProjectionAlgorithms.PointOnSegment(P, segment);
            projection = result.ProjectedPoint;
            return result.T;
        }

        public static GeometryResults ProjectSegmentOnVector(LineSegment2D segment, Point2D refPoint, double refAngle)
            => ProjectionAlgorithms.SegmentOnVector(segment, refPoint, refAngle);

        #endregion

        #region Angle Functions

        public static bool IsParallel(double angle1, double angle2, double toleranceRad = GeometryConstants.DEFAULT_ANGLE_TOLERANCE)
            => AngleAlgorithms.IsParallel(angle1, angle2, toleranceRad);

        public static bool IsPerpendicular(double angle1, double angle2, double toleranceRad = GeometryConstants.DEFAULT_ANGLE_TOLERANCE)
            => AngleAlgorithms.IsPerpendicular(angle1, angle2, toleranceRad);

        public static double SnapToCardinalAngle(double angleRad, double toleranceRad = GeometryConstants.DEFAULT_ANGLE_TOLERANCE)
            => AngleAlgorithms.SnapToCardinal(angleRad, toleranceRad);

        public static double NormalizeAngle(double angleRad)
            => AngleAlgorithms.Normalize0ToPI(angleRad);

        public static double Angle2D(Point2D p1, Point2D p2)
            => AngleAlgorithms.Angle2D(p1, p2);

        #endregion

        #region Intersection Functions

        public static bool GetLineIntersection(Point2D A1, Point2D A2, Point2D B1, Point2D B2, out Point2D intersection)
        {
            var result = IntersectionAlgorithms.LineLine(A1, A2, B1, B2);
            intersection = result.Point;
            return result.HasIntersection;
        }

        public static bool GetSegmentIntersection(LineSegment2D seg1, LineSegment2D seg2, out Point2D intersection, double tolerance = 0)
        {
            var result = IntersectionAlgorithms.SegmentSegment(seg1, seg2, out _, tolerance);
            intersection = result.Point;
            return result.HasIntersection;
        }

        #endregion

        #region Overlap Functions

        public static OverlapResult CalculateOverlap(LineSegment2D seg1, LineSegment2D seg2)
            => OverlapAlgorithms.CalculateOverlap(seg1, seg2);

        public static double CalculateGapDistance(LineSegment2D seg1, LineSegment2D seg2)
            => OverlapAlgorithms.CalculateGapDistance(seg1, seg2);

        public static bool AreCollinear(LineSegment2D seg1, LineSegment2D seg2,
            double angleTolerance = GeometryConstants.DEFAULT_ANGLE_TOLERANCE,
            double distTolerance = GeometryConstants.DEFAULT_DISTANCE_TOLERANCE)
            => OverlapAlgorithms.AreCollinear(seg1, seg2, angleTolerance, distTolerance);

        #endregion

        #region Merge Functions

        public static LineSegment2D MergeCollinearSegments(LineSegment2D seg1, LineSegment2D seg2)
            => MergeAlgorithms.MergeCollinear(seg1, seg2);

        #endregion

        #region Bounding Box

        public static BoundingBox BuildBoundingBox(LineSegment2D seg)
            => new BoundingBox(seg);

        #endregion
    }
}