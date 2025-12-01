using DTS_Wall_Tool.Core.Algorithms;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Models.Base;
using System;
using System.Collections.Generic;

namespace DTS_Wall_Tool.Models
{
    /// <summary>
    /// Đại diện cho đường tim tường đã xử lý. 
    /// Kế thừa từ LineGeometryBase để tái sử dụng code geometry.
    /// </summary>
    public class CenterLine : LineGeometryBase
    {
        #region Wall Properties

        /// <summary>
        /// Độ dày tường (mm)
        /// </summary>
        public double Thickness { get; set; } = 0;

        /// <summary>
        /// Loại tường
        /// </summary>
        public string WallType { get; set; } = "";

        /// <summary>
        /// Cao độ tầng
        /// </summary>
        public double StoryZ { get; set; } = 0;

        /// <summary>
        /// Load pattern
        /// </summary>
        public string LoadPattern { get; set; } = "DL";

        /// <summary>
        /// Giá trị tải (kN/m)
        /// </summary>
        public double LoadValue { get; set; } = 0;

        #endregion

        #region Processing State

        /// <summary>
        /// ID cặp nguồn (-1 nếu từ đường đơn)
        /// </summary>
        public int SourcePairID { get; set; } = -1;

        /// <summary>
        /// ID nhóm vector
        /// </summary>
        public int VectorID { get; set; } = -1;

        /// <summary>
        /// Danh sách Handle nguồn đã đóng góp vào centerline này
        /// </summary>
        public List<string> SourceHandles { get; set; } = new List<string>();

        #endregion

        #region Constructors

        public CenterLine() { }

        public CenterLine(Point2D start, Point2D end, double thickness = 0)
        {
            StartPt = start;
            EndPt = end;
            Thickness = thickness;
            UpdateUniqueID();
        }

        public CenterLine(LineSegment2D segment, double thickness = 0)
            : this(segment.Start, segment.End, thickness)
        { }

        #endregion

        #region IIdentifiable Implementation

        public override void UpdateUniqueID()
        {
            string baseID = BuildBaseUniqueID(2);
            string th = Thickness.ToString("0");
            UniqueID = $"{baseID}_T{th}_A{Angle:0.000}";
        }

        #endregion

        #region Methods

        /// <summary>
        /// Gộp với CenterLine khác
        /// </summary>
        public void MergeWith(CenterLine other)
        {
            // Gộp geometry
            var merged = MergeAlgorithms.MergeCollinear(AsSegment, other.AsSegment);
            SetFromSegment(merged);

            // Lấy độ dày lớn hơn
            if (other.Thickness > Thickness)
            {
                Thickness = other.Thickness;
                WallType = other.WallType;
            }

            // Gộp source handles
            SourceHandles.AddRange(other.SourceHandles);

            // Vô hiệu hóa centerline kia
            other.IsActive = false;

            UpdateUniqueID();
        }

        /// <summary>
        /// Kiểm tra có thể gộp với CenterLine khác không
        /// </summary>
        public bool CanMergeWith(CenterLine other, double angleTolerance, double distTolerance)
        {
            if (!IsActive || !other.IsActive)
                return false;

            // Kiểm tra độ dày tương tự (trong 20%)
            if (Thickness > 0 && other.Thickness > 0)
            {
                double thickDiff = Math.Abs(Thickness - other.Thickness);
                if (thickDiff > Thickness * 0.2)
                    return false;
            }

            // Kiểm tra đồng tuyến
            return OverlapAlgorithms.AreCollinear(AsSegment, other.AsSegment, angleTolerance, distTolerance);
        }

        /// <summary>
        /// Clone CenterLine
        /// </summary>
        public CenterLine Clone()
        {
            var clone = new CenterLine
            {
                Handle = Handle,
                StartPt = StartPt,
                EndPt = EndPt,
                Thickness = Thickness,
                WallType = WallType,
                StoryZ = StoryZ,
                LoadPattern = LoadPattern,
                LoadValue = LoadValue,
                SourcePairID = SourcePairID,
                VectorID = VectorID,
                IsActive = IsActive
            };
            clone.SourceHandles.AddRange(SourceHandles);
            clone.UpdateUniqueID();
            return clone;
        }

        /// <summary>
        /// Tự động tạo WallType từ Thickness
        /// </summary>
        public void EnsureWallType()
        {
            if (string.IsNullOrEmpty(WallType) && Thickness > 0)
            {
                WallType = "W" + ((int)Thickness).ToString();
            }
        }

        public override string ToString()
        {
            string status = IsActive ? "" : "[X]";
            return $"{status}CL: {StartPt}->{EndPt}, L={Length:0.0}, T={Thickness}, {WallType}";
        }

        #endregion

        #region Static Factory

        /// <summary>
        /// Tạo CenterLine từ cặp WallSegment
        /// </summary>
        public static CenterLine FromWallPair(WallSegment seg1, WallSegment seg2)
        {
            var merged = MergeAlgorithms.CreateCenterline(seg1.AsSegment, seg2.AsSegment, out double thickness);

            var centerline = new CenterLine
            {
                StartPt = merged.Start,
                EndPt = merged.End,
                Thickness = thickness > 0 ? thickness : Math.Max(seg1.Thickness, seg2.Thickness),
                StoryZ = seg1.StoryZ,
                SourcePairID = seg1.Index,
                IsActive = true
            };

            if (!string.IsNullOrEmpty(seg1.Handle))
                centerline.SourceHandles.Add(seg1.Handle);
            if (!string.IsNullOrEmpty(seg2.Handle))
                centerline.SourceHandles.Add(seg2.Handle);

            centerline.EnsureWallType();
            centerline.UpdateUniqueID();

            return centerline;
        }

        /// <summary>
        /// Tạo CenterLine từ WallSegment đơn
        /// </summary>
        public static CenterLine FromSingleSegment(WallSegment segment)
        {
            var centerline = new CenterLine
            {
                StartPt = segment.StartPt,
                EndPt = segment.EndPt,
                Thickness = segment.Thickness > 0 ? segment.Thickness : 100,
                WallType = segment.WallType,
                StoryZ = segment.StoryZ,
                LoadPattern = segment.LoadPattern,
                LoadValue = segment.LoadValue,
                SourcePairID = -1,
                IsActive = true
            };

            if (!string.IsNullOrEmpty(segment.Handle))
                centerline.SourceHandles.Add(segment.Handle);

            centerline.EnsureWallType();
            centerline.UpdateUniqueID();

            return centerline;
        }

        #endregion
    }
}