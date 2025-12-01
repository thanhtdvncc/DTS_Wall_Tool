using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Models.Base;

namespace DTS_Wall_Tool.Models
{
    /// <summary>
    /// Đại diện cho một đoạn tường trong quá trình xử lý. 
    /// Kế thừa từ LineGeometryBase để tái sử dụng code geometry.
    /// </summary>
    public class WallSegment : LineGeometryBase
    {
        #region Wall-Specific Properties

        /// <summary>
        /// Độ dày tường (mm)
        /// </summary>
        public double Thickness { get; set; } = 0;

        /// <summary>
        /// Loại tường (VD: "W220", "W110")
        /// </summary>
        public string WallType { get; set; } = "";

        /// <summary>
        /// Load pattern trong SAP2000
        /// </summary>
        public string LoadPattern { get; set; } = "DL";

        /// <summary>
        /// Giá trị tải (kN/m)
        /// </summary>
        public double LoadValue { get; set; } = 0;

        /// <summary>
        /// Cao độ tầng (mm)
        /// </summary>
        public double StoryZ { get; set; } = 0;

        /// <summary>
        /// Tên tầng
        /// </summary>
        public string StoryName { get; set; } = "";

        /// <summary>
        /// Tên layer trong AutoCAD
        /// </summary>
        public string Layer { get; set; } = "";

        #endregion

        #region Processing State

        /// <summary>
        /// Index trong mảng xử lý
        /// </summary>
        public int Index { get; set; } = -1;

        /// <summary>
        /// True nếu là đường đơn (không ghép cặp)
        /// </summary>
        public bool IsSingleLine { get; set; } = false;

        /// <summary>
        /// True nếu đã được xử lý
        /// </summary>
        public bool IsProcessed { get; set; } = false;

        /// <summary>
        /// ID của đoạn ghép cặp (-1 nếu không có)
        /// </summary>
        public int PairSegmentID { get; set; } = -1;

        /// <summary>
        /// ID nhóm vector (theo góc)
        /// </summary>
        public int VectorID { get; set; } = -1;

        /// <summary>
        /// ID đoạn đã gộp vào (-1 nếu chưa gộp)
        /// </summary>
        public int MergedIntoID { get; set; } = -1;

        #endregion

        #region Constructors

        public WallSegment() { }

        public WallSegment(Point2D start, Point2D end)
        {
            StartPt = start;
            EndPt = end;
            UpdateUniqueID();
        }

        public WallSegment(double x1, double y1, double x2, double y2)
            : this(new Point2D(x1, y1), new Point2D(x2, y2))
        { }

        #endregion

        #region IIdentifiable Implementation

        public override void UpdateUniqueID()
        {
            string baseID = BuildBaseUniqueID(1);
            string th = Thickness.ToString("0");
            UniqueID = $"{baseID}_T{th}";
        }

        #endregion

        #region Methods

        /// <summary>
        /// Tự động tạo WallType từ Thickness nếu chưa có
        /// </summary>
        public void EnsureWallType()
        {
            if (string.IsNullOrEmpty(WallType) && Thickness > 0)
            {
                WallType = "W" + ((int)Thickness).ToString();
            }
        }

        /// <summary>
        /// Clone với geometry mới
        /// </summary>
        public WallSegment CloneWithGeometry(Point2D newStart, Point2D newEnd)
        {
            return new WallSegment
            {
                Handle = Handle,
                StartPt = newStart,
                EndPt = newEnd,
                Thickness = Thickness,
                WallType = WallType,
                LoadPattern = LoadPattern,
                LoadValue = LoadValue,
                StoryZ = StoryZ,
                StoryName = StoryName,
                Layer = Layer,
                IsSingleLine = IsSingleLine,
                IsActive = IsActive
            };
        }

        /// <summary>
        /// Clone đầy đủ
        /// </summary>
        public WallSegment Clone()
        {
            return CloneWithGeometry(StartPt, EndPt);
        }

        /// <summary>
        /// Kiểm tra có thể ghép cặp với đoạn khác không
        /// </summary>
        public bool CanPairWith(WallSegment other)
        {
            return IsActive && other.IsActive &&
                   PairSegmentID == -1 && other.PairSegmentID == -1 &&
                   !IsProcessed && !other.IsProcessed;
        }

        /// <summary>
        /// Đánh dấu đã ghép cặp
        /// </summary>
        public void SetPairedWith(WallSegment other, double thickness)
        {
            PairSegmentID = other.Index;
            other.PairSegmentID = Index;
            Thickness = thickness;
            other.Thickness = thickness;
        }

        public override string ToString()
        {
            string status = IsActive ? "" : "[X]";
            string paired = PairSegmentID >= 0 ? $"(P:{PairSegmentID})" : "";
            return $"{status}Wall[{Handle}]: {StartPt}->{EndPt}, L={Length:0.0}, T={Thickness}, {WallType}{paired}";
        }

        #endregion
    }
}