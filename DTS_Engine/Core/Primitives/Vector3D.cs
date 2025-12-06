using System;

namespace DTS_Engine.Core.Primitives
{
    /// <summary>
    /// Vector 3D v?i các phép toán c? b?n cho tính toán l?c và tr?c ??a ph??ng.
    /// Thi?t k? nh?, ??c l?p v?i AutoCAD API ?? Core layer hoàn toàn portable.
    /// </summary>
    public struct Vector3D
    {
        public double X;
        public double Y;
        public double Z;

        #region Constructors

        public Vector3D(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        #endregion

        #region Properties

        /// <summary>
        /// ?? dài (magnitude) c?a vector
        /// </summary>
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        /// <summary>
        /// Bình ph??ng ?? dài (nhanh h?n khi ch? c?n so sánh)
        /// </summary>
        public double LengthSquared => X * X + Y * Y + Z * Z;

        /// <summary>
        /// Vector ??n v? cùng h??ng
        /// </summary>
        public Vector3D Normalized
        {
            get
            {
                double len = Length;
                return len > GeometryConstants.EPSILON 
                    ? new Vector3D(X / len, Y / len, Z / len) 
                    : Zero;
            }
        }

        /// <summary>
        /// Ki?m tra vector có g?n b?ng zero không
        /// </summary>
        public bool IsZero => LengthSquared < GeometryConstants.EPSILON;

        #endregion

        #region Operators

        public static Vector3D operator +(Vector3D a, Vector3D b) 
            => new Vector3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vector3D operator -(Vector3D a, Vector3D b) 
            => new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vector3D operator *(Vector3D v, double s) 
            => new Vector3D(v.X * s, v.Y * s, v.Z * s);

        public static Vector3D operator *(double s, Vector3D v) 
            => new Vector3D(v.X * s, v.Y * s, v.Z * s);

        public static Vector3D operator /(Vector3D v, double s) 
            => new Vector3D(v.X / s, v.Y / s, v.Z / s);

        public static Vector3D operator -(Vector3D v) 
            => new Vector3D(-v.X, -v.Y, -v.Z);

        public static bool operator ==(Vector3D a, Vector3D b) => a.Equals(b);
        public static bool operator !=(Vector3D a, Vector3D b) => !a.Equals(b);

        #endregion

        #region Vector Operations

        /// <summary>
        /// Tích vô h??ng (Dot product): a·b = |a||b|cos(?)
        /// </summary>
        public double Dot(Vector3D other)
        {
            return X * other.X + Y * other.Y + Z * other.Z;
        }

        /// <summary>
        /// Tích có h??ng (Cross product): a × b
        /// K?t qu? là vector vuông góc v?i c? a và b
        /// </summary>
        public Vector3D Cross(Vector3D other)
        {
            return new Vector3D(
                Y * other.Z - Z * other.Y,
                Z * other.X - X * other.Z,
                X * other.Y - Y * other.X
            );
        }

        /// <summary>
        /// Hình chi?u c?a vector này lên vector khác
        /// proj_b(a) = (a·b / |b|²) * b
        /// </summary>
        public Vector3D ProjectOnto(Vector3D other)
        {
            double lenSq = other.LengthSquared;
            if (lenSq < GeometryConstants.EPSILON)
                return Zero;
            
            double scale = Dot(other) / lenSq;
            return other * scale;
        }

        /// <summary>
        /// Góc gi?a hai vector (radians, 0 ??n ?)
        /// </summary>
        public double AngleTo(Vector3D other)
        {
            double dot = Dot(other);
            double len1 = Length;
            double len2 = other.Length;

            if (len1 < GeometryConstants.EPSILON || len2 < GeometryConstants.EPSILON)
                return 0;

            double cos = dot / (len1 * len2);
            cos = Math.Max(-1, Math.Min(1, cos)); // Clamp ?? tránh l?i Acos
            return Math.Acos(cos);
        }

        #endregion

        #region Transformation

        /// <summary>
        /// Bi?n ??i vector b?ng ma tr?n 3x3 (row-major: [r0c0, r0c1, r0c2, r1c0, ...])
        /// K?t qu?: M * v
        /// </summary>
        public Vector3D Transform(double[] matrix)
        {
            if (matrix == null || matrix.Length < 9)
                return this;

            return new Vector3D(
                matrix[0] * X + matrix[1] * Y + matrix[2] * Z,
                matrix[3] * X + matrix[4] * Y + matrix[5] * Z,
                matrix[6] * X + matrix[7] * Y + matrix[8] * Z
            );
        }

        /// <summary>
        /// Bi?n ??i t? h? tr?c ??a ph??ng sang Global
        /// localToGlobal: Ma tr?n có 3 c?t là 3 vector tr?c ??a ph??ng (L1, L2, L3)
        /// </summary>
        public Vector3D TransformLocalToGlobal(double[] localToGlobal)
        {
            return Transform(localToGlobal);
        }

        #endregion

        #region Comparison

        public override bool Equals(object obj)
        {
            if (!(obj is Vector3D)) return false;
            var other = (Vector3D)obj;
            return Math.Abs(X - other.X) < GeometryConstants.EPSILON &&
                   Math.Abs(Y - other.Y) < GeometryConstants.EPSILON &&
                   Math.Abs(Z - other.Z) < GeometryConstants.EPSILON;
        }

        public bool Equals(Vector3D other, double tolerance)
        {
            return Math.Abs(X - other.X) < tolerance &&
                   Math.Abs(Y - other.Y) < tolerance &&
                   Math.Abs(Z - other.Z) < tolerance;
        }

        public override int GetHashCode()
        {
            int hx = (int)Math.Round(X * 1000);
            int hy = (int)Math.Round(Y * 1000);
            int hz = (int)Math.Round(Z * 1000);
            return hx ^ (hy << 10) ^ (hz << 20);
        }

        #endregion

        #region String

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";

        public string ToString(string format) 
            => $"({X.ToString(format)}, {Y.ToString(format)}, {Z.ToString(format)})";

        #endregion

        #region Static Factory & Constants

        /// <summary>
        /// Vector zero (0, 0, 0)
        /// </summary>
        public static Vector3D Zero => new Vector3D(0, 0, 0);

        /// <summary>
        /// Vector ??n v? tr?c X (1, 0, 0)
        /// </summary>
        public static Vector3D UnitX => new Vector3D(1, 0, 0);

        /// <summary>
        /// Vector ??n v? tr?c Y (0, 1, 0)
        /// </summary>
        public static Vector3D UnitY => new Vector3D(0, 1, 0);

        /// <summary>
        /// Vector ??n v? tr?c Z (0, 0, 1)
        /// </summary>
        public static Vector3D UnitZ => new Vector3D(0, 0, 1);

        /// <summary>
        /// Vector tr?ng l?c (0, 0, -1) - h??ng xu?ng
        /// </summary>
        public static Vector3D Gravity => new Vector3D(0, 0, -1);

        /// <summary>
        /// T?o vector t? 3 giá tr? trong m?ng
        /// </summary>
        public static Vector3D FromArray(double[] values, int startIndex = 0)
        {
            if (values == null || values.Length < startIndex + 3)
                return Zero;
            
            return new Vector3D(
                values[startIndex],
                values[startIndex + 1],
                values[startIndex + 2]
            );
        }

        /// <summary>
        /// T?o vector t? Point2D v?i Z = 0
        /// </summary>
        public static Vector3D FromPoint2D(Point2D pt, double z = 0)
        {
            return new Vector3D(pt.X, pt.Y, z);
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Ki?m tra vector có song song v?i tr?c Global nào không
        /// Tr? v?: "X", "Y", "Z" ho?c null n?u không song song v?i tr?c nào
        /// </summary>
        public string GetDominantAxis(double tolerance = 0.1)
        {
            double absX = Math.Abs(X);
            double absY = Math.Abs(Y);
            double absZ = Math.Abs(Z);

            double total = absX + absY + absZ;
            if (total < GeometryConstants.EPSILON)
                return null;

            double ratioX = absX / total;
            double ratioY = absY / total;
            double ratioZ = absZ / total;

            if (ratioX > 1.0 - tolerance) return "X";
            if (ratioY > 1.0 - tolerance) return "Y";
            if (ratioZ > 1.0 - tolerance) return "Z";

            return null;
        }

        /// <summary>
        /// L?y tr?c ch? ??o (có component l?n nh?t)
        /// </summary>
        public string GetPrimaryAxis()
        {
            double absX = Math.Abs(X);
            double absY = Math.Abs(Y);
            double absZ = Math.Abs(Z);

            if (absX >= absY && absX >= absZ) return "X";
            if (absY >= absX && absY >= absZ) return "Y";
            return "Z";
        }

        /// <summary>
        /// Ki?m tra có ph?i t?i ngang (lateral) không
        /// Tiêu chí: max(|X|, |Y|) > 0.5 * |Z|
        /// </summary>
        public bool IsLateral
        {
            get
            {
                double absX = Math.Abs(X);
                double absY = Math.Abs(Y);
                double absZ = Math.Abs(Z);
                double lateralMag = Math.Max(absX, absY);
                return lateralMag > absZ * 0.5;
            }
        }

        #endregion
    }
}
