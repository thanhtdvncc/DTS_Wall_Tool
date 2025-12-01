using DTS_Wall_Tool.Core.Primitives;
using System;

namespace DTS_Wall_Tool.Core.Algorithms
{
    /// <summary>
    /// Các thuật toán tìm giao điểm
    /// </summary>
    public static class IntersectionAlgorithms
    {
        /// <summary>
        /// Tìm giao điểm của hai đường thẳng vô hạn
        /// </summary>
        public static IntersectionResult LineLine(Point2D A1, Point2D A2, Point2D B1, Point2D B2)
        {
            var result = new IntersectionResult { HasIntersection = false };

            double dx1 = A2.X - A1.X;
            double dy1 = A2.Y - A1.Y;
            double dx2 = B2.X - B1.X;
            double dy2 = B2.Y - B1.Y;

            double denom = dx1 * dy2 - dy1 * dx2;
            if (Math.Abs(denom) < GeometryConstants.EPSILON)
                return result; // Song song hoặc trùng

            double t1 = ((B1.X - A1.X) * dy2 - (B1.Y - A1.Y) * dx2) / denom;
            double t2 = ((B1.X - A1.X) * dy1 - (B1.Y - A1.Y) * dx1) / denom;

            result.HasIntersection = true;
            result.Point = new Point2D(A1.X + t1 * dx1, A1.Y + t1 * dy1);
            result.T1 = t1;
            result.T2 = t2;

            return result;
        }

        /// <summary>
        /// Tìm giao điểm của hai đường thẳng vô hạn
        /// </summary>
        public static IntersectionResult LineLine(LineSegment2D line1, LineSegment2D line2)
        {
            return LineLine(line1.Start, line1.End, line2.Start, line2.End);
        }

        /// <summary>
        /// Tìm giao điểm của hai đoạn thẳng hữu hạn
        /// </summary>
        public static IntersectionResult SegmentSegment(LineSegment2D seg1, LineSegment2D seg2, out bool isParallel, double tolerance = 0)
        {
            var result = LineLine(seg1, seg2);
            isParallel = !result.HasIntersection;

            if (!result.HasIntersection)
                return result;

            // Tính tolerance tương đối
            double tol = tolerance > 0 ? tolerance / Math.Max(seg1.Length, seg2.Length) : 0;

            // Kiểm tra nằm trong cả hai đoạn
            if (result.T1 >= -tol && result.T1 <= 1 + tol &&
                result.T2 >= -tol && result.T2 <= 1 + tol)
            {
                return result;
            }

            result.HasIntersection = false;
            return result;
        }

        /// <summary>
        /// Tìm giao điểm của đoạn thẳng với đường thẳng vô hạn
        /// </summary>
        public static IntersectionResult SegmentLine(LineSegment2D segment, LineSegment2D infiniteLine, double tolerance = 0)
        {
            var result = LineLine(segment, infiniteLine);

            if (!result.HasIntersection)
                return result;

            double tol = tolerance > 0 ? tolerance / segment.Length : 0;

            if (result.T1 >= -tol && result.T1 <= 1 + tol)
                return result;

            result.HasIntersection = false;
            return result;
        }
    }
}