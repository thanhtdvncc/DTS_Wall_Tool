using DTS_Wall_Tool.Core.Primitives;
using System;

namespace DTS_Wall_Tool.Core.Algorithms
{
    /// <summary>
    /// Các thuật toán liên quan đến góc
    /// </summary>
    public static class AngleAlgorithms
    {
        /// <summary>
        /// Tính góc từ điểm p1 đến p2 (radian)
        /// </summary>
        public static double Angle2D(Point2D p1, Point2D p2)
        {
            return Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
        }

        /// <summary>
        /// Chuẩn hóa góc về khoảng [0, 2*PI)
        /// </summary>
        public static double Normalize0To2PI(double angleRad)
        {
            while (angleRad < 0) angleRad += GeometryConstants.TWO_PI;
            while (angleRad >= GeometryConstants.TWO_PI) angleRad -= GeometryConstants.TWO_PI;
            return angleRad;
        }

        /// <summary>
        /// Chuẩn hóa góc về khoảng [-PI, PI)
        /// </summary>
        public static double NormalizeMinusPIToPI(double angleRad)
        {
            while (angleRad < -GeometryConstants.PI) angleRad += GeometryConstants.TWO_PI;
            while (angleRad >= GeometryConstants.PI) angleRad -= GeometryConstants.TWO_PI;
            return angleRad;
        }

        /// <summary>
        /// Chuẩn hóa góc về khoảng [0, PI) - bỏ qua hướng
        /// </summary>
        public static double Normalize0ToPI(double angleRad)
        {
            while (angleRad < 0) angleRad += GeometryConstants.PI;
            while (angleRad >= GeometryConstants.PI) angleRad -= GeometryConstants.PI;
            return angleRad;
        }

        /// <summary>
        /// Kiểm tra hai góc có song song không (bỏ qua hướng ngược)
        /// </summary>
        public static bool IsParallel(double angle1, double angle2, double toleranceRad = GeometryConstants.DEFAULT_ANGLE_TOLERANCE)
        {
            double diff = Math.Abs(angle1 - angle2);
            while (diff > GeometryConstants.PI) diff -= GeometryConstants.PI;
            return diff <= toleranceRad || (GeometryConstants.PI - diff) <= toleranceRad;
        }

        /// <summary>
        /// Kiểm tra hai góc có vuông góc không
        /// </summary>
        public static bool IsPerpendicular(double angle1, double angle2, double toleranceRad = GeometryConstants.DEFAULT_ANGLE_TOLERANCE)
        {
            double diff = Math.Abs(angle1 - angle2);
            while (diff > GeometryConstants.PI) diff -= GeometryConstants.PI;
            return Math.Abs(diff - GeometryConstants.HALF_PI) <= toleranceRad;
        }

        /// <summary>
        /// Snap góc về các hướng chính (0, 90, 180, 270 độ)
        /// </summary>
        public static double SnapToCardinal(double angleRad, double toleranceRad = GeometryConstants.DEFAULT_ANGLE_TOLERANCE)
        {
            angleRad = Normalize0To2PI(angleRad);

            double[] cardinals = { 0, GeometryConstants. HALF_PI, GeometryConstants.PI,
                                   3 * GeometryConstants.HALF_PI, GeometryConstants.TWO_PI };

            foreach (var c in cardinals)
            {
                if (Math.Abs(angleRad - c) <= toleranceRad)
                    return c == GeometryConstants.TWO_PI ? 0 : c;
            }

            return angleRad;
        }

        /// <summary>
        /// Tính góc giữa hai vector
        /// </summary>
        public static double AngleBetween(Point2D v1, Point2D v2)
        {
            double dot = v1.Dot(v2);
            double len1 = v1.Length;
            double len2 = v2.Length;

            if (len1 < GeometryConstants.EPSILON || len2 < GeometryConstants.EPSILON)
                return 0;

            double cos = dot / (len1 * len2);
            cos = Math.Max(-1, Math.Min(1, cos)); // Clamp để tránh lỗi Acos

            return Math.Acos(cos);
        }

        /// <summary>
        /// Tính góc có hướng từ v1 đến v2 (+ ngược chiều kim đồng hồ)
        /// </summary>
        public static double SignedAngleBetween(Point2D v1, Point2D v2)
        {
            double angle = AngleBetween(v1, v2);
            double cross = v1.Cross(v2);
            return cross >= 0 ? angle : -angle;
        }
    }
}