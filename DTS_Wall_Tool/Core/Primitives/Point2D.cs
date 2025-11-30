using System;

namespace DTS_Wall_Tool.Core.Primitives
{
    /// <summary>
    /// Cấu trúc điểm 2D với các phép toán vector cơ bản. 
    /// Sử dụng struct để tối ưu bộ nhớ và tốc độ. 
    /// </summary>
    public struct Point2D
    {
        public double X;
        public double Y;

        #region Constructors

        /// <summary>
        /// Tạo điểm mới tại tọa độ (x, y)
        /// </summary>
        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        #endregion

        #region Distance Methods

        /// <summary>
        /// Tính khoảng cách Euclid đến điểm khác
        /// </summary>
        public double DistanceTo(Point2D other)
        {
            double dx = other.X - X;
            double dy = other.Y - Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Tính khoảng cách bình phương (nhanh hơn khi chỉ cần so sánh)
        /// </summary>
        public double DistanceSquaredTo(Point2D other)
        {
            double dx = other.X - X;
            double dy = other.Y - Y;
            return dx * dx + dy * dy;
        }

        #endregion

        #region Operators

        public static Point2D operator +(Point2D a, Point2D b) => new Point2D(a.X + b.X, a.Y + b.Y);
        public static Point2D operator -(Point2D a, Point2D b) => new Point2D(a.X - b.X, a.Y - b.Y);
        public static Point2D operator *(Point2D a, double s) => new Point2D(a.X * s, a.Y * s);
        public static Point2D operator *(double s, Point2D a) => new Point2D(a.X * s, a.Y * s);
        public static Point2D operator /(Point2D a, double s) => new Point2D(a.X / s, a.Y / s);
        public static Point2D operator -(Point2D a) => new Point2D(-a.X, -a.Y);

        public static bool operator ==(Point2D a, Point2D b) => a.Equals(b);
        public static bool operator !=(Point2D a, Point2D b) => !a.Equals(b);

        #endregion

        #region Vector Operations

        /// <summary>
        /// Tính trung điểm với điểm khác
        /// </summary>
        public Point2D MidpointTo(Point2D other) => new Point2D((X + other.X) / 2.0, (Y + other.Y) / 2.0);

        /// <summary>
        /// Tích vô hướng (Dot product): a. Dot(b) = |a|*|b|*cos(θ)
        /// </summary>
        public double Dot(Point2D other) => X * other.X + Y * other.Y;

        /// <summary>
        /// Tích có hướng 2D (Cross product): a. Cross(b) = |a|*|b|*sin(θ)
        /// Dấu cho biết hướng quay (+ ngược chiều kim đồng hồ)
        /// </summary>
        public double Cross(Point2D other) => X * other.Y - Y * other.X;

        /// <summary>
        /// Độ dài vector (khoảng cách từ gốc tọa độ)
        /// </summary>
        public double Length => Math.Sqrt(X * X + Y * Y);

        /// <summary>
        /// Bình phương độ dài (nhanh hơn khi chỉ cần so sánh)
        /// </summary>
        public double LengthSquared => X * X + Y * Y;

        /// <summary>
        /// Vector đơn vị cùng hướng
        /// </summary>
        public Point2D Normalized
        {
            get
            {
                double len = Length;
                return len > GeometryConstants.EPSILON ? this / len : new Point2D(0, 0);
            }
        }

        /// <summary>
        /// Vector vuông góc (quay 90 độ ngược chiều kim đồng hồ)
        /// </summary>
        public Point2D Perpendicular => new Point2D(-Y, X);

        /// <summary>
        /// Vector vuông góc đơn vị
        /// </summary>
        public Point2D PerpendicularNormalized => Perpendicular.Normalized;

        #endregion

        #region Transformation

        /// <summary>
        /// Xoay điểm quanh gốc tọa độ
        /// </summary>
        public Point2D Rotate(double angleRad)
        {
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            return new Point2D(X * cos - Y * sin, X * sin + Y * cos);
        }

        /// <summary>
        /// Xoay điểm quanh tâm cho trước
        /// </summary>
        public Point2D RotateAround(Point2D center, double angleRad)
        {
            Point2D translated = this - center;
            Point2D rotated = translated.Rotate(angleRad);
            return rotated + center;
        }

        #endregion

        #region Comparison

        public override bool Equals(object obj)
        {
            if (!(obj is Point2D)) return false;
            var other = (Point2D)obj;
            return Math.Abs(X - other.X) < GeometryConstants.EPSILON &&
                   Math.Abs(Y - other.Y) < GeometryConstants.EPSILON;
        }

        /// <summary>
        /// So sánh với sai số tùy chỉnh
        /// </summary>
        public bool Equals(Point2D other, double tolerance)
        {
            return Math.Abs(X - other.X) < tolerance &&
                   Math.Abs(Y - other.Y) < tolerance;
        }

        public override int GetHashCode()
        {
            // Làm tròn đến 1mm để ổn định hash
            int hx = (int)Math.Round(X);
            int hy = (int)Math.Round(Y);
            return hx.GetHashCode() ^ (hy.GetHashCode() << 16);
        }

        #endregion

        #region String

        public override string ToString() => $"({X:0.00}, {Y:0.00})";

        public string ToString(string format) => $"({X.ToString(format)}, {Y.ToString(format)})";

        #endregion

        #region Static Factory

        /// <summary>
        /// Điểm gốc tọa độ (0, 0)
        /// </summary>
        public static Point2D Origin => new Point2D(0, 0);

        /// <summary>
        /// Vector đơn vị theo trục X
        /// </summary>
        public static Point2D UnitX => new Point2D(1, 0);

        /// <summary>
        /// Vector đơn vị theo trục Y
        /// </summary>
        public static Point2D UnitY => new Point2D(0, 1);

        #endregion
    }
}