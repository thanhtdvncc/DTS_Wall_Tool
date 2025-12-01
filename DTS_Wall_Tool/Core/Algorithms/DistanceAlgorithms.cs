using DTS_Wall_Tool.Core.Primitives;
using System;

namespace DTS_Wall_Tool.Core.Algorithms
{
    /// <summary>
    /// Các thuật toán tính khoảng cách
    /// </summary>
    public static class DistanceAlgorithms
    {
        /// <summary>
        /// Tính khoảng cách từ điểm P đến đoạn thẳng AB (có giới hạn)
        /// </summary>
        public static double PointToSegment(Point2D P, Point2D A, Point2D B)
        {
            var AB = B - A;
            var AP = P - A;

            double len2 = AB.LengthSquared;
            if (len2 < GeometryConstants.EPSILON)
                return P.DistanceTo(A);

            double t = AP.Dot(AB) / len2;
            t = Math.Max(0.0, Math.Min(1.0, t));

            var proj = new Point2D(A.X + t * AB.X, A.Y + t * AB.Y);
            return P.DistanceTo(proj);
        }

        /// <summary>
        /// Tính khoảng cách từ điểm P đến đoạn thẳng
        /// </summary>
        public static double PointToSegment(Point2D P, LineSegment2D segment)
        {
            return PointToSegment(P, segment.Start, segment.End);
        }

        /// <summary>
        /// Tính khoảng cách vuông góc từ điểm P đến đường thẳng vô hạn qua AB
        /// </summary>
        public static double PointToInfiniteLine(Point2D P, Point2D A, Point2D B)
        {
            var AB = B - A;
            double len = AB.Length;
            if (len < GeometryConstants.EPSILON)
                return P.DistanceTo(A);

            // |AB × AP| / |AB|
            return Math.Abs(AB.Cross(P - A)) / len;
        }

        /// <summary>
        /// Tính khoảng cách vuông góc từ điểm P đến đường thẳng
        /// </summary>
        public static double PointToInfiniteLine(Point2D P, LineSegment2D line)
        {
            return PointToInfiniteLine(P, line.Start, line.End);
        }

        /// <summary>
        /// Ước tính khoảng cách vuông góc giữa hai đoạn thẳng song song
        /// </summary>
        public static double BetweenParallelSegments(LineSegment2D seg1, LineSegment2D seg2)
        {
            double d1 = PointToInfiniteLine(seg2.Start, seg1);
            double d2 = PointToInfiniteLine(seg2.End, seg1);
            return (d1 + d2) / 2.0;
        }

        /// <summary>
        /// Tính khoảng cách gần nhất giữa hai đoạn thẳng
        /// </summary>
        public static double BetweenSegments(LineSegment2D seg1, LineSegment2D seg2)
        {
            // Kiểm tra giao điểm trước
            if (IntersectionAlgorithms.SegmentSegment(seg1, seg2, out _).HasIntersection)
                return 0;

            // Tính khoảng cách từ các đầu mút
            double d1 = PointToSegment(seg1.Start, seg2);
            double d2 = PointToSegment(seg1.End, seg2);
            double d3 = PointToSegment(seg2.Start, seg1);
            double d4 = PointToSegment(seg2.End, seg1);

            return Math.Min(Math.Min(d1, d2), Math.Min(d3, d4));
        }
    }
}