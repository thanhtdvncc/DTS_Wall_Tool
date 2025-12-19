using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms
{
    /// <summary>
    /// Đại diện một đoạn thép sau khi cắt
    /// </summary>
    public class BarSegment
    {
        /// <summary>Vị trí bắt đầu (mm từ đầu dầm)</summary>
        public double StartPos { get; set; }

        /// <summary>Vị trí kết thúc (mm từ đầu dầm)</summary>
        public double EndPos { get; set; }

        /// <summary>Chiều dài đoạn (mm)</summary>
        public double Length => EndPos - StartPos;

        /// <summary>Có mối nối tại đầu không</summary>
        public bool SpliceAtStart { get; set; }

        /// <summary>Có mối nối tại cuối không</summary>
        public bool SpliceAtEnd { get; set; }

        /// <summary>Vị trí mối nối (mm) - sau khi stagger</summary>
        public double SplicePosition { get; set; }

        /// <summary>Index của thanh (để phân nhóm stagger)</summary>
        public int BarIndex { get; set; }

        /// <summary>Có hook ở đầu start không</summary>
        public bool HookAtStart { get; set; }

        /// <summary>Có hook ở đầu end không</summary>
        public bool HookAtEnd { get; set; }

        /// <summary>Góc hook (90, 135, 180)</summary>
        public int HookAngle { get; set; } = 90;

        /// <summary>Chiều dài hook (mm)</summary>
        public double HookLength { get; set; }

        /// <summary>Là thanh thuộc nhóm stagger (so le)</summary>
        public bool IsStaggered { get; set; }
    }

    /// <summary>
    /// Kết quả tính toán cắt thép cho một layer
    /// </summary>
    public class CuttingResult
    {
        public List<BarSegment> Segments { get; set; } = new List<BarSegment>();
        public int TotalBars => Segments.Count;
        public double TotalLength => Segments.Sum(s => s.Length);
        public int SpliceCount => Segments.Count(s => s.SpliceAtEnd);
        public bool HasHooks => Segments.Any(s => s.HookAtStart || s.HookAtEnd);
    }

    /// <summary>
    /// Thuật toán cắt thép tự động - Xử lý dầm siêu dài, so le mối nối, neo gối biên
    /// </summary>
    public class RebarCuttingAlgorithm
    {
        private readonly DetailingConfig _detailing;
        private readonly AnchorageConfig _anchorage;

        public RebarCuttingAlgorithm(DtsSettings settings)
        {
            _detailing = settings?.Detailing ?? new DetailingConfig();
            _anchorage = settings?.Anchorage ?? new AnchorageConfig();
        }

        #region Algorithm 1: Auto-Cutting

        /// <summary>
        /// Thuật toán cắt thép tự động cho dầm siêu dài
        /// </summary>
        /// <param name="totalLength">Tổng chiều dài dầm (mm)</param>
        /// <param name="spans">Danh sách các nhịp với vùng nối cho phép</param>
        /// <param name="isTopBar">True = thép trên, False = thép dưới</param>
        /// <param name="groupType">Loại dầm: "BEAM" hoặc "GIRDER"</param>
        /// <returns>Danh sách các đoạn thép</returns>
        public CuttingResult AutoCutBars(
            double totalLength,
            List<SpanInfo> spans,
            bool isTopBar,
            string groupType = "BEAM")
        {
            var result = new CuttingResult();
            double maxLength = _detailing.MaxBarLength;

            // Case 1: Dầm ngắn - không cần cắt
            if (totalLength <= maxLength)
            {
                result.Segments.Add(new BarSegment
                {
                    StartPos = 0,
                    EndPos = totalLength,
                    BarIndex = 0,
                    SpliceAtStart = false,
                    SpliceAtEnd = false
                });
                return result;
            }

            // Case 2: Dầm dài - cần cắt
            var rule = _detailing.GetRule(groupType);
            var spliceZone = isTopBar ? rule.TopSpliceZone : rule.BotSpliceZone;

            // Tính số thanh cần thiết
            int numBars = (int)Math.Ceiling(totalLength / maxLength);
            double idealLength = totalLength / numBars;

            // Xây dựng danh sách vùng cho phép nối
            var allowedZones = BuildAllowedZones(spans, spliceZone, rule.SupportZoneRatio);

            double currentPos = 0;
            for (int i = 0; i < numBars; i++)
            {
                double targetEnd = Math.Min(currentPos + idealLength, totalLength);

                // Nếu đây là thanh cuối cùng
                if (i == numBars - 1)
                {
                    targetEnd = totalLength;
                }

                // Tìm điểm nối hợp lệ
                double actualEnd = targetEnd;
                bool hasSplice = (i < numBars - 1); // Thanh cuối không có splice

                if (hasSplice)
                {
                    actualEnd = FindValidSplicePoint(targetEnd, allowedZones, maxLength * 0.1);
                }

                result.Segments.Add(new BarSegment
                {
                    StartPos = currentPos,
                    EndPos = actualEnd,
                    BarIndex = i,
                    SpliceAtStart = (i > 0),
                    SpliceAtEnd = hasSplice,
                    SplicePosition = actualEnd
                });

                currentPos = actualEnd;
            }

            return result;
        }

        /// <summary>
        /// Xây dựng danh sách vùng cho phép nối dựa trên SpliceZone
        /// </summary>
        private List<(double Start, double End)> BuildAllowedZones(
            List<SpanInfo> spans,
            SpliceZone zone,
            double supportRatio)
        {
            var zones = new List<(double Start, double End)>();

            double cumLength = 0;
            foreach (var span in spans)
            {
                double spanStart = cumLength;
                double spanEnd = cumLength + span.Length;
                double spanLen = span.Length;

                switch (zone)
                {
                    case SpliceZone.Support:
                        // Vùng gối: đầu và cuối nhịp (L/4 mỗi bên)
                        zones.Add((spanStart, spanStart + spanLen * supportRatio));
                        zones.Add((spanEnd - spanLen * supportRatio, spanEnd));
                        break;

                    case SpliceZone.QuarterSpan:
                        // Vùng L/4: từ 0.25L đến 0.75L
                        zones.Add((spanStart + spanLen * supportRatio, spanEnd - spanLen * supportRatio));
                        break;

                    case SpliceZone.MidSpan:
                        // Vùng giữa nhịp: từ 0.35L đến 0.65L
                        zones.Add((spanStart + spanLen * 0.35, spanStart + spanLen * 0.65));
                        break;
                }

                cumLength = spanEnd;
            }

            return zones;
        }

        /// <summary>
        /// Tìm điểm nối hợp lệ gần nhất với vị trí mục tiêu
        /// </summary>
        private double FindValidSplicePoint(
            double targetPos,
            List<(double Start, double End)> allowedZones,
            double searchRange)
        {
            // Kiểm tra targetPos có nằm trong vùng cho phép không
            foreach (var zone in allowedZones)
            {
                if (targetPos >= zone.Start && targetPos <= zone.End)
                {
                    return targetPos; // OK, nằm trong vùng hợp lệ
                }
            }

            // Tìm vùng gần nhất
            double bestPos = targetPos;
            double minDist = double.MaxValue;

            foreach (var zone in allowedZones)
            {
                // Khoảng cách đến đầu vùng
                double distToStart = Math.Abs(targetPos - zone.Start);
                if (distToStart < minDist && distToStart <= searchRange)
                {
                    minDist = distToStart;
                    bestPos = zone.Start + 50; // Lùi vào trong vùng 50mm
                }

                // Khoảng cách đến cuối vùng
                double distToEnd = Math.Abs(targetPos - zone.End);
                if (distToEnd < minDist && distToEnd <= searchRange)
                {
                    minDist = distToEnd;
                    bestPos = zone.End - 50; // Lùi vào trong vùng 50mm
                }
            }

            return bestPos;
        }

        #endregion

        #region Algorithm 2: Staggering

        /// <summary>
        /// Áp dụng so le mối nối (Stagger) để 50% nối tại vị trí A, 50% nối tại vị trí B
        /// </summary>
        /// <param name="result">Kết quả từ AutoCutBars</param>
        /// <param name="barDiameter">Đường kính thép (mm)</param>
        /// <param name="barsPerLayer">Số thanh mỗi lớp</param>
        public void ApplyStaggering(
            CuttingResult result,
            int barDiameter,
            int barsPerLayer = 2)
        {
            ApplyStaggering(result, barDiameter, concreteGrade: null, steelGrade: null, barsPerLayer: barsPerLayer);
        }

        /// <summary>
        /// Áp dụng so le mối nối (Stagger) với mác vật liệu thực (không hardcode).
        /// </summary>
        public void ApplyStaggering(
            CuttingResult result,
            int barDiameter,
            string concreteGrade,
            string steelGrade,
            int barsPerLayer = 2)
        {
            if (result.Segments.Count < 2 || barsPerLayer < 2) return;

            // Tính khoảng cách so le theo bảng neo/nối
            if (string.IsNullOrWhiteSpace(concreteGrade))
                concreteGrade = _anchorage?.ConcreteGrades?.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(steelGrade))
                steelGrade = _anchorage?.SteelGrades?.FirstOrDefault() ?? string.Empty;

            double spliceLength = _anchorage.GetSpliceLength(barDiameter, concreteGrade, steelGrade);
            double staggerDist = Math.Max(
                _detailing.MinStaggerDistance,
                spliceLength * _detailing.StaggerFactorLd
            );

            // Phân chia thanh thành 2 nhóm
            foreach (var segment in result.Segments)
            {
                if (!segment.SpliceAtEnd) continue;

                // Nhóm lẻ: dịch điểm nối
                if (segment.BarIndex % 2 == 1)
                {
                    segment.SplicePosition += staggerDist;
                    segment.IsStaggered = true; // Mark as staggered

                    // Đảm bảo không vượt quá chiều dài thanh tiếp theo
                    int nextIndex = segment.BarIndex + 1;
                    if (nextIndex < result.Segments.Count)
                    {
                        double nextEnd = result.Segments[nextIndex].EndPos;
                        if (segment.SplicePosition > nextEnd - 200)
                        {
                            segment.SplicePosition = nextEnd - 200;
                        }
                    }
                }
            }
        }

        #endregion

        #region Algorithm 3: End Anchorage

        /// <summary>
        /// Xác định và áp dụng neo gối biên (Hooks) cho thanh thép
        /// </summary>
        /// <param name="result">Kết quả cutting</param>
        /// <param name="startSupportType">Loại gối đầu (Column, Wall, FreeEnd...)</param>
        /// <param name="endSupportType">Loại gối cuối</param>
        /// <param name="barDiameter">Đường kính thép (mm)</param>
        public void ApplyEndAnchorage(
            CuttingResult result,
            string startSupportType,
            string endSupportType,
            int barDiameter)
        {
            if (result.Segments.Count == 0) return;

            // Thanh đầu tiên - kiểm tra gối đầu
            var firstBar = result.Segments.First();
            if (RequiresHook(startSupportType))
            {
                firstBar.HookAtStart = true;
                firstBar.HookAngle = 90; // Mặc định móc 90°
                firstBar.HookLength = Math.Max(
                    _anchorage.Hook90Factor * barDiameter,
                    _anchorage.MinHookLength
                );
            }

            // Thanh cuối cùng - kiểm tra gối cuối
            var lastBar = result.Segments.Last();
            if (RequiresHook(endSupportType))
            {
                lastBar.HookAtEnd = true;
                lastBar.HookAngle = 90;
                lastBar.HookLength = Math.Max(
                    _anchorage.Hook90Factor * barDiameter,
                    _anchorage.MinHookLength
                );
            }
        }

        /// <summary>
        /// Kiểm tra loại gối có cần hook không
        /// </summary>
        private bool RequiresHook(string supportType)
        {
            if (string.IsNullOrEmpty(supportType)) return false;

            string type = supportType.ToUpperInvariant();

            // Cần hook khi gối là Cột hoặc Vách
            return type == "COLUMN" || type == "WALL" || type.Contains("COL") || type.Contains("WALL");
        }

        #endregion

        #region Combined Processing

        /// <summary>
        /// Xử lý hoàn chỉnh: Cắt + So le + Neo
        /// </summary>
        public CuttingResult ProcessComplete(
            double totalLength,
            List<SpanInfo> spans,
            bool isTopBar,
            string groupType,
            string startSupportType,
            string endSupportType,
            int barDiameter,
            int barsPerLayer = 2,
            string concreteGrade = null,
            string steelGrade = null)
        {
            // Step 1: Auto-cut
            var result = AutoCutBars(totalLength, spans, isTopBar, groupType);

            // Step 2: Apply staggering
            ApplyStaggering(result, barDiameter, concreteGrade, steelGrade, barsPerLayer);

            // Step 3: Apply end anchorage
            ApplyEndAnchorage(result, startSupportType, endSupportType, barDiameter);

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Thông tin nhịp để tính vùng nối
    /// </summary>
    public class SpanInfo
    {
        public string SpanId { get; set; }
        public double Length { get; set; } // mm
        public double StartPos { get; set; } // Vị trí bắt đầu từ đầu dầm (mm)
    }
}
