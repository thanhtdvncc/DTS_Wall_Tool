using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Wall_Tool.Core.Algorithms;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Primitives;

namespace DTS_Wall_Tool.Core.Engines
{
    /// <summary>
    /// Kết quả mapping cho một tường
    /// </summary>
    public class MappingResult
    {
        public string WallHandle { get; set; }
        public double WallLength { get; set; }
        public List<MappingRecord> Mappings { get; set; } = new List<MappingRecord>();

        public double CoveredLength => Mappings.Where(m => m.TargetFrame != "New").Sum(m => m.CoveredLength);

        // CoveragePercent > 95% coi như Full
        public double CoveragePercent => WallLength > 0 ? (CoveredLength / WallLength) * 100 : 0;
        public bool IsFullyCovered => CoveragePercent >= 95.0;

        public bool HasMapping => Mappings.Count > 0 && Mappings.Any(m => m.TargetFrame != "New");

        /// <summary>
        /// Xác định màu hiển thị cho tường:
        /// - Xanh (3): Full coverage (>=95%)
        /// - Vàng (2): Partial coverage
        /// - Đỏ (1): No match -> NEW
        /// </summary>
        public int GetColorIndex()
        {
            if (!HasMapping) return 1; // Red - New
            if (IsFullyCovered) return 3; // Green - Full
            return 2; // Yellow - Partial
        }
    

        /// <summary>
        /// Tạo label text cho dòng trên (thông tin mapping)
        /// </summary>
        public string GetTopLabelText()
        {
            if (Mappings.Count == 0 || !HasMapping)
                return "{\\C1;-> NEW}";

            var mapping = Mappings.First();
            int color = GetColorIndex();

            if (mapping.MatchType == "FULL")
                return $"{{\\C{color};-> {mapping.TargetFrame} (full)}}";
            else
                return $"{{\\C{color};-> {mapping.TargetFrame}}}";
        }

        /// <summary>
        /// Tạo label text cho dòng dưới (thông tin load)
        /// </summary>
        public string GetBottomLabelText(string wallType, string loadPattern, double loadValue)
        {
            int color = GetColorIndex();
            string loadStr = $"{wallType} {loadPattern}={loadValue:0.00}";
            return $"{{\\C{color};{loadStr}}}";
        }

        /// <summary>
        /// Tạo label text đầy đủ (backward compatible)
        /// </summary>
        public string GetLabelText(string wallType, string loadPattern, double loadValue)
        {
            string loadStr = $"{wallType} {loadPattern}={loadValue:0.00}";

            if (Mappings.Count == 0 || !HasMapping)
                return loadStr + " -> New";

            if (Mappings.Count == 1)
            {
                var m = Mappings[0];
                if (m.MatchType == "FULL")
                    return loadStr + $" -> {m.TargetFrame} (full {m.FrameLength / 1000:0.0}m)";
                else
                    return loadStr + $" -> {m.TargetFrame} I={m.DistI / 1000:0.0}to{m.DistJ / 1000:0.0}";
            }

            var frameNames = Mappings.Select(m => m.TargetFrame).Distinct();
            return loadStr + " -> " + string.Join(", ", frameNames);
        }
    }

    /// <summary>
    /// Candidate frame sau khi lọc sơ bộ
    /// </summary>
    internal class FrameCandidate
    {
        public SapFrame Frame { get; set; }
        public double OverlapLength { get; set; }
        public double PerpDist { get; set; }
        public double Score { get; set; }

        // Projection results (local coordinate on frame axis)
        public double WallProjStart { get; set; }
        public double WallProjEnd { get; set; }
        public double OverlapStart { get; set; }
        public double OverlapEnd { get; set; }
    }

    /// <summary>
    /// Engine mapping tường lên dầm SAP2000
    /// Chiến thuật: "Hình chiếu & Chồng lấn" (Projection & Overlap)
    /// 
    /// Quy trình:
    /// 1. Sàng lọc thô: Loại cột, lọc theo Z, lọc theo góc, lọc theo khoảng cách
    /// 2. Hệ trục địa phương: Coi dầm là trục số [0, L], chiếu tường lên trục
    /// 3.  Tính toán chồng lấn: Tìm giao [Wall] ∩ [0, L]
    /// 4.  Phân loại: FULL / PARTIAL / NEW
    /// </summary>
    public static class MappingEngine
    {
        // Cấu hình dung sai
        #region Configuration (Tunable Parameters)

        /// <summary>Dung sai cao độ Z (mm)</summary>
        public static double TOLERANCE_Z = 200.0;

        /// <summary>Dung sai khoảng cách vuông góc mặc định (mm)</summary>
        public static double TOLERANCE_DIST = 300.0;

        /// <summary>Chiều dài overlap tối thiểu để chấp nhận (mm)</summary>
        public static double MIN_OVERLAP = 100.0;

        /// <summary>Khoảng cách khe hở cho phép để snap đầu dầm (mm)</summary>
        public static double TOLERANCE_GAP = 300.0; // Khe hở cho phép snap đầu dầm

        /// <summary>Dung sai góc song song (rad) ~ 5 độ</summary>
        public static double TOLERANCE_ANGLE = 5 * GeometryConstants.DEG_TO_RAD;

        /// <summary>Tỷ lệ overlap tối thiểu để xem là match hợp lệ (15%)</summary>
        public static double MIN_OVERLAP_RATIO = 0.15;

        /// <summary>Gap tối đa cho phép để thực hiện Gap Match (mm)</summary>
        public static double MAX_GAP_DISTANCE = 2000.0;

        #endregion

        #region Main Mapping API

        /// <summary>
        /// Tìm tất cả dầm đỡ một tường (Main Entry Point)
        /// </summary>
        public static MappingResult FindMappings(
                   Point2D wallStart,
                   Point2D wallEnd,
                   double wallZ,
                   IEnumerable<SapFrame> frames,
                   Point2D insertionOffset = default,
                   double wallThickness = 200.0)
        {
            var result = new MappingResult
            {
                WallLength = wallStart.DistanceTo(wallEnd)
            };

            if (result.WallLength < 1.0) return result;

            var wStart = new Point2D(wallStart.X - insertionOffset.X, wallStart.Y - insertionOffset.Y);
            var wEnd = new Point2D(wallEnd.X - insertionOffset.X, wallEnd.Y - insertionOffset.Y);
            var wallSeg = new LineSegment2D(wStart, wEnd);

            double searchDistTol = Math.Max(wallThickness * 2.0, 250.0);
            var validMappings = new List<MappingRecord>();

            foreach (var frame in frames)
            {
                // Lọc sơ bộ
                if (frame.IsVertical) continue;
                if (Math.Abs(frame.AverageZ - wallZ) > TOLERANCE_Z) continue;

                var frameSeg = new LineSegment2D(frame.StartPt, frame.EndPt);
                if (!AngleAlgorithms.IsParallel(wallSeg.Angle, frameSeg.Angle, TOLERANCE_ANGLE)) continue;
                if (DistanceAlgorithms.BetweenParallelSegments(wallSeg, frameSeg) > searchDistTol) continue;

                // Tính giao cắt
                double frameLen = frame.Length2D;
                if (frameLen < 1.0) continue;

                Point2D vecFrame = (frame.EndPt - frame.StartPt).Normalized;
                double t_Start = (wStart - frame.StartPt).Dot(vecFrame);
                double t_End = (wEnd - frame.StartPt).Dot(vecFrame);

                double tMin = Math.Min(t_Start, t_End);
                double tMax = Math.Max(t_Start, t_End);

                double overlapStart = Math.Max(0, tMin);
                double overlapEnd = Math.Min(frameLen, tMax);
                double overlapLen = overlapEnd - overlapStart;

                // Snap thông minh
                bool isMatch = false;
                if (overlapLen > 50.0) isMatch = true;
                else // Logic GAP
                {
                    if (tMax < 0 && tMax > -TOLERANCE_GAP) { overlapStart = 0; overlapEnd = 0; isMatch = true; }
                    else if (tMin > frameLen && tMin < frameLen + TOLERANCE_GAP) { overlapStart = frameLen; overlapEnd = frameLen; isMatch = true; }
                }

                if (isMatch)
                {
                    // Snap vào đầu mút
                    if (overlapStart < TOLERANCE_GAP) overlapStart = 0;
                    if (frameLen - overlapEnd < TOLERANCE_GAP) overlapEnd = frameLen;

                    double effectiveCover = overlapEnd - overlapStart;

                    // --- [QUAN TRỌNG] LỌC BỎ ĐOẠN QUÁ NGẮN (RÁC 0-0) ---
                    // Chỉ chấp nhận nếu đoạn phủ > 10mm (1cm)
                    if (effectiveCover < 10.0) continue;

                    string type = "PARTIAL";
                    if (effectiveCover >= frameLen * 0.98) type = "FULL";

                    validMappings.Add(new MappingRecord
                    {
                        TargetFrame = frame.Name,
                        MatchType = type,
                        DistI = overlapStart,
                        DistJ = overlapEnd,
                        FrameLength = frameLen,
                        CoveredLength = effectiveCover
                    });
                }
            }

            // Tổng hợp kết quả
            if (validMappings.Count == 0)
            {
                result.Mappings.Add(new MappingRecord { TargetFrame = "New", MatchType = "NEW" });
            }
            else
            {
                // Sắp xếp theo vị trí I
                result.Mappings = validMappings.OrderBy(m => m.DistI).ToList();
            }

            return result;
        }



        /// <summary>
        /// Overload for LineSegment2D input
        /// </summary>
        public static MappingResult FindMappings(LineSegment2D wallSegment, double wallZ,
            IEnumerable<SapFrame> frames, Point2D insertionOffset = default, double wallThickness = 200.0)
        {
            return FindMappings(wallSegment.Start, wallSegment.End, wallZ, frames, insertionOffset, wallThickness);
        }

        #endregion

        #region Core Algorithms

        private static bool IsElevationMatch(SapFrame frame, double wallZ)
        {
            double frameZ = Math.Min(frame.Z1, frame.Z2);
            return Math.Abs(frameZ - wallZ) <= TOLERANCE_Z;
        }

        private static bool IsParallelOrOpposite(double angle1, double angle2)
        {
            double diff = Math.Abs(angle1 - angle2);
            if (diff > Math.PI) diff = 2 * Math.PI - diff;

            if (diff <= TOLERANCE_ANGLE) return true;
            if (Math.Abs(diff - Math.PI) <= TOLERANCE_ANGLE) return true;

            return false;
        }

        private static double CalculateDynamicDistanceTolerance(double wallThickness)
        {
            double tol = wallThickness * 5.0;
            if (tol < 250) tol = 250;
            if (tol > 1500) tol = 1500;
            return tol;
        }

        private static (double WallProjStart, double WallProjEnd) ProjectWallOntoFrame(LineSegment2D wallSeg, SapFrame frame)
        {
            double frameLen = frame.Length2D;
            if (frameLen < GeometryConstants.EPSILON)
                return (0, 0);

            double ux = (frame.EndPt.X - frame.StartPt.X) / frameLen;
            double uy = (frame.EndPt.Y - frame.StartPt.Y) / frameLen;

            double t1 = (wallSeg.Start.X - frame.StartPt.X) * ux + (wallSeg.Start.Y - frame.StartPt.Y) * uy;
            double t2 = (wallSeg.End.X - frame.StartPt.X) * ux + (wallSeg.End.Y - frame.StartPt.Y) * uy;

            return (t1, t2);
        }

        private static MappingRecord CreateNewMapping(double length)
        {
            return new MappingRecord
            {
                TargetFrame = "New",
                MatchType = "NEW",
                CoveredLength = length,
                DistI = 0,
                DistJ = length,
                FrameLength = length
            };
        }

        private static MappingRecord CreateMappingFromCandidate(FrameCandidate candidate, LineSegment2D wallSeg, double wallLength)
        {
            double frameLen = candidate.Frame.Length2D;

            double distI = Math.Round(candidate.OverlapStart, 0);
            double distJ = Math.Round(candidate.OverlapEnd, 0);

            if (distI < 0) distI = 0;
            if (distJ > frameLen) distJ = frameLen;

            string matchType = "PARTIAL";
            if (distI < 1 && Math.Abs(distJ - frameLen) < 1)
                matchType = "FULL";
            else if (candidate.OverlapLength >= wallLength * 0.95)
                matchType = "FULL";

            return new MappingRecord
            {
                TargetFrame = candidate.Frame.Name,
                MatchType = matchType,
                DistI = distI,
                DistJ = distJ,
                FrameLength = frameLen,
                CoveredLength = candidate.OverlapLength
            };
        }

        private static List<MappingRecord> OptimizeMappings(List<MappingRecord> mappings, double wallLength)
        {
            if (mappings.Count <= 1)
                return mappings;

            var unique = mappings
                .GroupBy(m => m.TargetFrame)
                .Select(g => g.First())
                .ToList();

            var fullCoverage = unique.FirstOrDefault(m => m.MatchType == "FULL");
            if (fullCoverage != null && fullCoverage.CoveredLength >= wallLength * 0.95)
            {
                return new List<MappingRecord> { fullCoverage };
            }

            return unique;
        }

        #endregion

        #region Debug Support

        public static string GetAnalysisReport(MappingResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== MAPPING ANALYSIS ===");
            sb.AppendLine($"Wall Length: {result.WallLength:0.0} mm");
            sb.AppendLine($"Covered: {result.CoveredLength:0. 0} mm ({result.CoveragePercent:0.0}%)");
            sb.AppendLine($"Has Mapping: {result.HasMapping}");
            sb.AppendLine($"Color Index: {result.GetColorIndex()}");
            sb.AppendLine($"Mappings ({result.Mappings.Count}):");
            foreach (var m in result.Mappings)
            {
                sb.AppendLine($"  - {m}");
            }
            return sb.ToString();
        }

        #endregion
    }


}