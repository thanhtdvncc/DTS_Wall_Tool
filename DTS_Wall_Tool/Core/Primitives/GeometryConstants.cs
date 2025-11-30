using System;

namespace DTS_Wall_Tool.Core.Primitives
{
    /// <summary>
    /// Các hằng số dùng trong tính toán hình học
    /// </summary>
    public static class GeometryConstants
    {
        /// <summary>
        /// Sai số cho phép khi so sánh số thực
        /// </summary>
        public const double EPSILON = 1e-6;

        /// <summary>
        /// Số PI
        /// </summary>
        public const double PI = Math.PI;

        /// <summary>
        /// Nửa PI (90 độ)
        /// </summary>
        public const double HALF_PI = Math.PI / 2.0;

        /// <summary>
        /// 2 PI (360 độ)
        /// </summary>
        public const double TWO_PI = Math.PI * 2.0;

        /// <summary>
        /// Hệ số chuyển đổi độ sang radian
        /// </summary>
        public const double DEG_TO_RAD = Math.PI / 180.0;

        /// <summary>
        /// Hệ số chuyển đổi radian sang độ
        /// </summary>
        public const double RAD_TO_DEG = 180.0 / Math.PI;

        /// <summary>
        /// Góc dung sai mặc định cho kiểm tra song song (5 độ)
        /// </summary>
        public const double DEFAULT_ANGLE_TOLERANCE = 5.0 * DEG_TO_RAD;

        /// <summary>
        /// Khoảng cách dung sai mặc định (mm)
        /// </summary>
        public const double DEFAULT_DISTANCE_TOLERANCE = 10.0;
    }
}