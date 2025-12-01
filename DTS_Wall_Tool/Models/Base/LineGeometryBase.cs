using DTS_Wall_Tool.Core.Interfaces;
using DTS_Wall_Tool.Core.Primitives;
using System;

namespace DTS_Wall_Tool.Models.Base
{
    /// <summary>
    /// Base class cho các đối tượng có hình học đoạn thẳng. 
    /// Loại bỏ code trùng lặp giữa WallSegment, CenterLine, AxisLine.
    /// </summary>
    public abstract class LineGeometryBase : ILineGeometry, IIdentifiable, IActivatable
    {
        #region ILineGeometry Implementation

        private Point2D _startPt;
        private Point2D _endPt;

        public Point2D StartPt
        {
            get => _startPt;
            set
            {
                _startPt = value;
                InvalidateCache();
            }
        }

        public Point2D EndPt
        {
            get => _endPt;
            set
            {
                _endPt = value;
                InvalidateCache();
            }
        }

        public double Length => _startPt.DistanceTo(_endPt);

        public Point2D Midpoint => _startPt.MidpointTo(_endPt);

        public double Angle => Math.Atan2(_endPt.Y - _startPt.Y, _endPt.X - _startPt.X);

        /// <summary>
        /// Góc chuẩn hóa [0, PI) - bỏ qua hướng
        /// </summary>
        public double NormalizedAngle
        {
            get
            {
                double a = Angle;
                while (a < 0) a += Math.PI;
                while (a >= Math.PI) a -= Math.PI;
                return a;
            }
        }

        public LineSegment2D AsSegment => new LineSegment2D(_startPt, _endPt);

        #endregion

        #region IIdentifiable Implementation

        public string Handle { get; set; }

        public string UniqueID { get; protected set; }

        public abstract void UpdateUniqueID();

        #endregion

        #region IActivatable Implementation

        public bool IsActive { get; set; } = true;

        #endregion

        #region Additional Properties

        /// <summary>
        /// Vector đơn vị hướng từ Start đến End
        /// </summary>
        public Point2D Direction => (EndPt - StartPt).Normalized;

        /// <summary>
        /// BoundingBox của đoạn thẳng
        /// </summary>
        public BoundingBox BoundingBox => new BoundingBox(AsSegment);

        #endregion

        #region Protected Methods

        /// <summary>
        /// Đánh dấu cache không hợp lệ khi geometry thay đổi
        /// </summary>
        protected virtual void InvalidateCache()
        {
            // Override trong subclass nếu cần
        }

        /// <summary>
        /// Tạo chuỗi ID cơ bản từ geometry
        /// </summary>
        protected string BuildBaseUniqueID(int precision = 1)
        {
            string format = precision == 0 ? "0" : "0." + new string('0', precision);
            string sx = StartPt.X.ToString(format);
            string sy = StartPt.Y.ToString(format);
            string ex = EndPt.X.ToString(format);
            string ey = EndPt.Y.ToString(format);
            return $"{sx}_{sy}_{ex}_{ey}";
        }

        #endregion

        #region Geometry Operations

        /// <summary>
        /// Đảo ngược hướng đoạn thẳng
        /// </summary>
        public void Reverse()
        {
            var temp = _startPt;
            _startPt = _endPt;
            _endPt = temp;
            InvalidateCache();
        }

        /// <summary>
        /// Dịch chuyển theo vector
        /// </summary>
        public void Translate(Point2D offset)
        {
            _startPt = _startPt + offset;
            _endPt = _endPt + offset;
            InvalidateCache();
        }

        /// <summary>
        /// Mở rộng đoạn thẳng theo cả hai hướng
        /// </summary>
        public void Extend(double amount)
        {
            var dir = Direction;
            _startPt = _startPt - dir * amount;
            _endPt = _endPt + dir * amount;
            InvalidateCache();
        }

        /// <summary>
        /// Cập nhật geometry từ LineSegment2D
        /// </summary>
        public void SetFromSegment(LineSegment2D segment)
        {
            _startPt = segment.Start;
            _endPt = segment.End;
            InvalidateCache();
        }

        #endregion

        #region Validation

        /// <summary>
        /// Kiểm tra đoạn thẳng có hợp lệ không (có chiều dài > 0)
        /// </summary>
        public bool IsValid => Length > GeometryConstants.EPSILON;

        #endregion
    }
}