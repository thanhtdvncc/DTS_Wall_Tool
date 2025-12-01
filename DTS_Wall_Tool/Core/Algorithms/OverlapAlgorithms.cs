using DTS_Wall_Tool.Core.Primitives;
using System;

namespace DTS_Wall_Tool.Core.Algorithms
{
    /// <summary>
    /// Các thuật toán tính chồng lấn và đồng tuyến
    /// </summary>
    public static class OverlapAlgorithms
    {
        /// <summary>
        /// Tính phần chồng lấn của hai đoạn thẳng (chiếu lên trục chung)
        /// </summary>
        public static OverlapResult CalculateOverlap(LineSegment2D seg1, LineSegment2D seg2)
        {
            var result = new OverlapResult { HasOverlap = false };

            double refAngle = seg1.Angle;
            var refPoint = seg1.Start;

            var proj1 = ProjectionAlgorithms.SegmentOnVector(seg1, refPoint, refAngle);
            var proj2 = ProjectionAlgorithms.SegmentOnVector(seg2, refPoint, refAngle);

            double overlapStart = Math.Max(proj1.MinProj, proj2.MinProj);
            double overlapEnd = Math.Min(proj1.MaxProj, proj2.MaxProj);

            result.OverlapStart = overlapStart;
            result.OverlapEnd = overlapEnd;
            result.OverlapLength = overlapEnd - overlapStart;
            result.HasOverlap = result.OverlapLength > -GeometryConstants.EPSILON;

            if (result.HasOverlap && result.OverlapLength > 0)
            {
                double shorterLength = Math.Min(proj1.Length, proj2.Length);
                result.OverlapPercent = shorterLength > GeometryConstants.EPSILON
                    ? result.OverlapLength / shorterLength
                    : 0;
            }

            return result;
        }

        /// <summary>
        /// Tính khoảng cách gap giữa hai đoạn đồng tuyến
        /// </summary>
        public static double CalculateGapDistance(LineSegment2D seg1, LineSegment2D seg2)
        {
            double refAngle = seg1.Angle;
            var refPoint = seg1.Start;

            var proj1 = ProjectionAlgorithms.SegmentOnVector(seg1, refPoint, refAngle);
            var proj2 = ProjectionAlgorithms.SegmentOnVector(seg2, refPoint, refAngle);

            if (proj2.MinProj >= proj1.MaxProj)
                return proj2.MinProj - proj1.MaxProj;
            else if (proj1.MinProj >= proj2.MaxProj)
                return proj1.MinProj - proj2.MaxProj;
            else
                return -1; // Chồng lấn
        }

        /// <summary>
        /// Kiểm tra hai đoạn có đồng tuyến không (nằm trên cùng đường thẳng)
        /// </summary>
        public static bool AreCollinear(LineSegment2D seg1, LineSegment2D seg2,
            double angleTolerance = GeometryConstants.DEFAULT_ANGLE_TOLERANCE,
            double distTolerance = GeometryConstants.DEFAULT_DISTANCE_TOLERANCE)
        {
            // Kiểm tra song song
            if (!AngleAlgorithms.IsParallel(seg1.Angle, seg2.Angle, angleTolerance))
                return false;

            // Kiểm tra khoảng cách vuông góc
            double d1 = DistanceAlgorithms.PointToInfiniteLine(seg2.Start, seg1);
            double d2 = DistanceAlgorithms.PointToInfiniteLine(seg2.End, seg1);
            double d3 = DistanceAlgorithms.PointToInfiniteLine(seg1.Start, seg2);
            double d4 = DistanceAlgorithms.PointToInfiniteLine(seg1.End, seg2);

            return d1 <= distTolerance && d2 <= distTolerance &&
                   d3 <= distTolerance && d4 <= distTolerance;
        }
    }
}