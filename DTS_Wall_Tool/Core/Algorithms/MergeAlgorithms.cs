using DTS_Wall_Tool.Core.Primitives;
using System;

namespace DTS_Wall_Tool.Core.Algorithms
{
    /// <summary>
    /// Các thuật toán gộp/nối đoạn thẳng
    /// </summary>
    public static class MergeAlgorithms
    {
        /// <summary>
        /// Gộp hai đoạn thẳng đồng tuyến thành một
        /// </summary>
        public static LineSegment2D MergeCollinear(LineSegment2D seg1, LineSegment2D seg2)
        {
            // Chọn đoạn dài hơn làm tham chiếu
            var dominant = seg1.Length >= seg2.Length ? seg1 : seg2;
            double refAngle = AngleAlgorithms.SnapToCardinal(dominant.Angle);
            var refPoint = dominant.Start;

            double cosA = Math.Cos(refAngle);
            double sinA = Math.Sin(refAngle);

            // Chiếu tất cả 4 điểm
            double[] projections = new double[4];
            Point2D[] points = { seg1.Start, seg1.End, seg2.Start, seg2.End };

            double minProj = double.MaxValue;
            double maxProj = double.MinValue;

            for (int i = 0; i < 4; i++)
            {
                double dx = points[i].X - refPoint.X;
                double dy = points[i].Y - refPoint.Y;
                double proj = dx * cosA + dy * sinA;

                if (proj < minProj) minProj = proj;
                if (proj > maxProj) maxProj = proj;
            }

            return new LineSegment2D(
                new Point2D(refPoint.X + minProj * cosA, refPoint.Y + minProj * sinA),
                new Point2D(refPoint.X + maxProj * cosA, refPoint.Y + maxProj * sinA)
            );
        }

        /// <summary>
        /// Tạo đường tim từ hai đoạn song song
        /// </summary>
        public static LineSegment2D CreateCenterline(LineSegment2D seg1, LineSegment2D seg2, out double thickness)
        {
            // Tính độ dày (khoảng cách giữa hai đoạn)
            thickness = DistanceAlgorithms.BetweenParallelSegments(seg1, seg2);

            // Chọn đoạn dài hơn làm tham chiếu
            var dominant = seg1.Length >= seg2.Length ? seg1 : seg2;
            double refAngle = AngleAlgorithms.SnapToCardinal(dominant.Angle);

            double cosA = Math.Cos(refAngle);
            double sinA = Math.Sin(refAngle);

            // Tính tâm giữa hai đoạn
            var mid1 = seg1.Midpoint;
            var mid2 = seg2.Midpoint;
            var centerMid = mid1.MidpointTo(mid2);

            // Chiếu tất cả 4 điểm
            double[] projections = new double[4];
            Point2D[] points = { seg1.Start, seg1.End, seg2.Start, seg2.End };

            double minProj = double.MaxValue;
            double maxProj = double.MinValue;

            foreach (var pt in points)
            {
                double dx = pt.X - centerMid.X;
                double dy = pt.Y - centerMid.Y;
                double proj = dx * cosA + dy * sinA;

                if (proj < minProj) minProj = proj;
                if (proj > maxProj) maxProj = proj;
            }

            return new LineSegment2D(
                new Point2D(centerMid.X + minProj * cosA, centerMid.Y + minProj * sinA),
                new Point2D(centerMid.X + maxProj * cosA, centerMid.Y + maxProj * sinA)
            );
        }

        /// <summary>
        /// Nối hai đoạn tại điểm gần nhất
        /// </summary>
        public static LineSegment2D ConnectAtClosestPoints(LineSegment2D seg1, LineSegment2D seg2, out double gap)
        {
            // Tìm cặp điểm gần nhất
            double d1 = seg1.End.DistanceTo(seg2.Start);
            double d2 = seg1.End.DistanceTo(seg2.End);
            double d3 = seg1.Start.DistanceTo(seg2.Start);
            double d4 = seg1.Start.DistanceTo(seg2.End);

            double minDist = Math.Min(Math.Min(d1, d2), Math.Min(d3, d4));
            gap = minDist;

            if (minDist == d1)
            {
                // seg1. End gần seg2.Start
                return new LineSegment2D(seg1.Start, seg2.End);
            }
            else if (minDist == d2)
            {
                // seg1. End gần seg2.End
                return new LineSegment2D(seg1.Start, seg2.Start);
            }
            else if (minDist == d3)
            {
                // seg1.Start gần seg2.Start
                return new LineSegment2D(seg1.End, seg2.End);
            }
            else
            {
                // seg1.Start gần seg2.End
                return new LineSegment2D(seg1.End, seg2.Start);
            }
        }
    }
}