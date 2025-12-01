using DTS_Wall_Tool.Core.Primitives;
using System;

namespace DTS_Wall_Tool.Core.Algorithms
{
    /// <summary>
    /// Các thuật toán chiếu (projection)
    /// </summary>
    public static class ProjectionAlgorithms
    {
        /// <summary>
        /// Chiếu điểm P lên đoạn thẳng
        /// </summary>
        public static PointProjectionResult PointOnSegment(Point2D P, LineSegment2D segment)
        {
            var AB = segment.End - segment.Start;

            double len2 = AB.LengthSquared;
            if (len2 < GeometryConstants.EPSILON)
            {
                return new PointProjectionResult
                {
                    ProjectedPoint = segment.Start,
                    T = 0,
                    Distance = P.DistanceTo(segment.Start)
                };
            }

            double t = (P - segment.Start).Dot(AB) / len2;
            var projection = new Point2D(
                segment.Start.X + t * AB.X,
                segment.Start.Y + t * AB.Y
            );

            return new PointProjectionResult
            {
                ProjectedPoint = projection,
                T = t,
                Distance = P.DistanceTo(projection)
            };
        }

        /// <summary>
        /// Chiếu điểm P lên đoạn thẳng (kẹp trong đoạn)
        /// </summary>
        public static PointProjectionResult PointOnSegmentClamped(Point2D P, LineSegment2D segment)
        {
            var result = PointOnSegment(P, segment);

            if (result.T < 0)
            {
                result.T = 0;
                result.ProjectedPoint = segment.Start;
                result.Distance = P.DistanceTo(segment.Start);
            }
            else if (result.T > 1)
            {
                result.T = 1;
                result.ProjectedPoint = segment.End;
                result.Distance = P.DistanceTo(segment.End);
            }

            return result;
        }

        /// <summary>
        /// Chiếu đoạn thẳng lên trục định bởi điểm và góc
        /// </summary>
        public static GeometryResults SegmentOnVector(LineSegment2D segment, Point2D refPoint, double refAngle)
        {
            double cosA = Math.Cos(refAngle);
            double sinA = Math.Sin(refAngle);

            double dx1 = segment.Start.X - refPoint.X;
            double dy1 = segment.Start.Y - refPoint.Y;
            double startProj = dx1 * cosA + dy1 * sinA;

            double dx2 = segment.End.X - refPoint.X;
            double dy2 = segment.End.Y - refPoint.Y;
            double endProj = dx2 * cosA + dy2 * sinA;

            return new GeometryResults(startProj, endProj);
        }

        /// <summary>
        /// Chiếu đoạn thẳng lên đoạn thẳng khác (dùng đoạn tham chiếu làm trục)
        /// </summary>
        public static GeometryResults SegmentOnSegment(LineSegment2D segment, LineSegment2D reference)
        {
            return SegmentOnVector(segment, reference.Start, reference.Angle);
        }
    }
}