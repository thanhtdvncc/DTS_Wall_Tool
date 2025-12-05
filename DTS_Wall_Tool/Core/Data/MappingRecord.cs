namespace DTS_Wall_Tool.Core.Data
{
    /// <summary>
    /// Bản ghi mapping giữa phần tử CAD và đối tượng SAP2000 (Frame/Area)
    /// </summary>
    public class MappingRecord
    {
        /// <summary>
        /// Tên đối tượng đích trong SAP2000 (Frame name hoặc Area name)
        /// </summary>
        public string TargetFrame { get; set; }

        /// <summary>
        /// Loại đối tượng đích: "Frame" hoặc "Area"
        /// </summary>
        public string TargetType { get; set; } = "Frame";

        /// <summary>
        /// Loại mapping: FULL, PARTIAL, NEW
        /// </summary>
        public string MatchType { get; set; } = "PARTIAL";

        /// <summary>
        /// Khoảng cách từ đầu tường đến điểm bắt đầu dầm (mm)
        /// </summary>
        public double DistI { get; set; } = 0;

        /// <summary>
        /// Khoảng cách từ điểm kết thúc dầm đến cuối tường (mm)
        /// </summary>
        public double DistJ { get; set; } = 0;

        /// <summary>
        /// Chiều dài phần tường được dầm này đỡ (mm)
        /// </summary>
        public double CoveredLength { get; set; } = 0;

        /// <summary>
        /// Chiều dài dầm (mm)
        /// </summary>
        public double FrameLength { get; set; } = 0;

        /// <summary>
        /// Tỷ lệ phủ (0-1)
        /// </summary>
        public double CoverageRatio => FrameLength > 0 ? CoveredLength / FrameLength : 0;

        public override string ToString()
        {
            return $"{TargetFrame}({MatchType}, I={DistI:0}, J={DistJ:0}, Cover={CoveredLength:0}, Type={TargetType})";
        }

        /// <summary>
        /// Clone bản ghi
        /// </summary>
        public MappingRecord Clone()
        {
            return new MappingRecord
            {
                TargetFrame = TargetFrame,
                TargetType = TargetType,
                MatchType = MatchType,
                DistI = DistI,
                DistJ = DistJ,
                CoveredLength = CoveredLength,
                FrameLength = FrameLength
            };
        }
    }
}