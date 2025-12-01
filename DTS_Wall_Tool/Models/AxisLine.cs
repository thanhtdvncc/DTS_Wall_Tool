using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Models.Base;
using System;

namespace DTS_Wall_Tool.Models
{
    /// <summary>
    /// Đại diện cho trục kết cấu (grid line). 
    /// Kế thừa từ LineGeometryBase để tái sử dụng code geometry.
    /// </summary>
    public class AxisLine : LineGeometryBase
    {
        #region Axis-Specific Properties

        /// <summary>
        /// Tên trục (VD: "A", "1", "B'")
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Loại trục: "H" (Horizontal), "V" (Vertical), "D" (Diagonal)
        /// </summary>
        public string AxisType
        {
            get
            {
                if (IsHorizontal) return "H";
                if (IsVertical) return "V";
                return "D";
            }
        }

        /// <summary>
        /// Kiểm tra trục gần như nằm ngang (góc gần 0 hoặc 180 độ)
        /// </summary>
        public bool IsHorizontal
        {
            get
            {
                double absAngle = Math.Abs(Angle);
                return absAngle < 0.0873 || absAngle > 3.0543; // ~5 độ
            }
        }

        /// <summary>
        /// Kiểm tra trục gần như thẳng đứng (góc gần 90 độ)
        /// </summary>
        public bool IsVertical
        {
            get
            {
                return Math.Abs(Math.Abs(Angle) - GeometryConstants.HALF_PI) < 0.0873; // ~5 độ
            }
        }

        /// <summary>
        /// Vị trí chính (X nếu vertical, Y nếu horizontal)
        /// </summary>
        public double PrimaryPosition
        {
            get
            {
                if (IsVertical) return (StartPt.X + EndPt.X) / 2;
                if (IsHorizontal) return (StartPt.Y + EndPt.Y) / 2;
                return Midpoint.X; // Fallback
            }
        }

        #endregion

        #region Constructors

        public AxisLine() { }

        public AxisLine(Point2D start, Point2D end, string name = "")
        {
            StartPt = start;
            EndPt = end;
            Name = name;
            UpdateUniqueID();
        }

        public AxisLine(double x1, double y1, double x2, double y2, string name = "")
            : this(new Point2D(x1, y1), new Point2D(x2, y2), name)
        { }

        public AxisLine(LineSegment2D segment, string name = "")
            : this(segment.Start, segment.End, name)
        { }

        #endregion

        #region IIdentifiable Implementation

        public override void UpdateUniqueID()
        {
            string baseID = BuildBaseUniqueID(0);
            UniqueID = $"Axis_{Name}_{baseID}";
        }

        #endregion

        #region Methods

        /// <summary>
        /// Mở rộng trục về cả hai hướng (vô hạn hóa cho tính toán)
        /// </summary>
        public AxisLine ExtendToInfinity(double extensionLength = 1000000)
        {
            var dir = Direction;
            return new AxisLine(
                StartPt - dir * extensionLength,
                EndPt + dir * extensionLength,
                Name
            );
        }

        /// <summary>
        /// Kiểm tra điểm có nằm trên trục không (với tolerance)
        /// </summary>
        public bool ContainsPoint(Point2D point, double tolerance = 10)
        {
            double dist = Core.Algorithms.DistanceAlgorithms.PointToInfiniteLine(point, AsSegment);
            return dist <= tolerance;
        }

        /// <summary>
        /// Clone AxisLine
        /// </summary>
        public AxisLine Clone()
        {
            return new AxisLine
            {
                Handle = Handle,
                StartPt = StartPt,
                EndPt = EndPt,
                Name = Name,
                IsActive = IsActive
            };
        }

        public override string ToString()
        {
            string status = IsActive ? "" : "[X]";
            return $"{status}Axis[{Name}]: {StartPt}->{EndPt} ({AxisType})";
        }

        #endregion

        #region Static Factory

        /// <summary>
        /// Tạo trục ngang tại Y
        /// </summary>
        public static AxisLine HorizontalAt(double y, double xMin, double xMax, string name = "")
        {
            return new AxisLine(new Point2D(xMin, y), new Point2D(xMax, y), name);
        }

        /// <summary>
        /// Tạo trục đứng tại X
        /// </summary>
        public static AxisLine VerticalAt(double x, double yMin, double yMax, string name = "")
        {
            return new AxisLine(new Point2D(x, yMin), new Point2D(x, yMax), name);
        }

        #endregion
    }
}