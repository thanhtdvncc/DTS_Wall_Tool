using System;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Lightweight geometry data for beam detection algorithms.
    /// ISO 25010: Reusability - Centralized DTO for geometric operations.
    /// NOTE: For XData serialization, use BeamData (ElementData-based) instead.
    /// </summary>
    public class BeamGeometry
    {
        /// <summary>CAD Entity Handle</summary>
        public string Handle { get; set; }

        /// <summary>SAP/User assigned name</summary>
        public string Name { get; set; }

        /// <summary>
        /// Dữ liệu kết quả từ XData (A_req, Top/Bot areas).
        /// Dùng để truyền vào thuật toán tính toán/grouping.
        /// </summary>
        public BeamResultData ResultData { get; set; }

        /// <summary>Start point X (mm)</summary>
        public double StartX { get; set; }

        /// <summary>Start point Y (mm)</summary>
        public double StartY { get; set; }

        /// <summary>Start point Z (mm) - for story matching</summary>
        public double StartZ { get; set; }

        /// <summary>End point X (mm)</summary>
        public double EndX { get; set; }

        /// <summary>End point Y (mm)</summary>
        public double EndY { get; set; }

        /// <summary>End point Z (mm) - for story matching</summary>
        public double EndZ { get; set; }

        /// <summary>
        /// Base Z from XData (if available). Prioritized over geometric Z for 2D drawings.
        /// This value comes from BeamData.BaseZ (e.g., 11700mm = Story 4).
        /// </summary>
        public double? BaseZ { get; set; }

        /// <summary>Section width (mm)</summary>
        public double Width { get; set; }

        /// <summary>Section height/depth (mm)</summary>
        public double Height { get; set; }

        /// <summary>Computed length in XY plane (mm)</summary>
        public double Length => Math.Sqrt(Math.Pow(EndX - StartX, 2) + Math.Pow(EndY - StartY, 2));

        /// <summary>Center point X</summary>
        public double CenterX => (StartX + EndX) / 2;

        /// <summary>Center point Y</summary>
        public double CenterY => (StartY + EndY) / 2;

        /// <summary>Direction: "X" if mostly horizontal, "Y" if mostly vertical</summary>
        public string Direction => Math.Abs(EndX - StartX) > Math.Abs(EndY - StartY) ? "X" : "Y";

        /// <summary>Support at Joint I (Start): 1 = có cột/tường, 0 = FreeEnd</summary>
        public int SupportI { get; set; } = 1;

        /// <summary>Support at Joint J (End): 1 = có cột/tường, 0 = FreeEnd</summary>
        public int SupportJ { get; set; } = 1;

        /// <summary>AxisName from SAP grid (e.g., "A", "B", "1", "2")</summary>
        public string AxisName { get; set; }

        /// <summary>
        /// True if beam runs in X direction (horizontal in plan)
        /// </summary>
        public bool IsXDirection => Math.Abs(EndX - StartX) > Math.Abs(EndY - StartY);

        /// <summary>
        /// Girder = Beam with both ends on columns OR (one column end + on grid axis)
        /// Beam = All others (typically secondary beams resting on other beams)
        /// </summary>
        public bool IsGirder => (SupportI == 1 && SupportJ == 1)
                             || ((SupportI == 1 || SupportJ == 1) && !string.IsNullOrEmpty(AxisName));
    }
}
