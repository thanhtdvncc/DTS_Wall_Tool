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

        public double CoveredLength => Mappings.Sum(m => m.CoveredLength);
        public double CoveragePercent => WallLength > 0 ? (CoveredLength / WallLength) * 100 : 0;
        public bool IsFullyCovered => CoveragePercent >= 99.0;
        public bool HasMapping => Mappings.Count > 0 && Mappings[0].TargetFrame != "New";

        public string GetLabelText(string wallType, string loadPattern, double loadValue)
        {
            string loadStr = $"{wallType} {loadPattern}={loadValue:0.00}";

            if (Mappings.Count == 0 || !HasMapping)
                return loadStr + " -> New";

            if (Mappings.Count == 1)
                return loadStr + " -> " + Mappings[0].TargetFrame;

            var frameNames = Mappings.Select(m => m.TargetFrame).Distinct();
            return loadStr + " -> " + string.Join(", ", frameNames);
        }
    }

    /// <summary>
    /// Engine mapping tường lên dầm SAP2000
    /// </summary>
    public static class MappingEngine
    {
        #region Configuration

        public static double TOLERANCE_Z = 200.0;
        public static double TOLERANCE_DIST = 300.0;
        public static double MIN_OVERLAP = 100.0;
        public static double TOLERANCE_ANGLE = 5 * GeometryConstants.DEG_TO_RAD;

        #endregion

        #region Main Mapping

        /// <summary>
        /// Tìm tất cả dầm đỡ một tường
        /// </summary>
        public static MappingResult FindMappings(
            Point2D wallStart,
            Point2D wallEnd,
            double wallZ,
            IEnumerable<SapFrame> frames,
            Point2D insertionOffset = default)
        {
            var result = new MappingResult
            {
                WallLength = wallStart.DistanceTo(wallEnd)
            };

            if (result.WallLength < GeometryConstants.EPSILON)
                return result;

            // Áp dụng offset
            var wStart = new Point2D(wallStart.X - insertionOffset.X, wallStart.Y - insertionOffset.Y);
            var wEnd = new Point2D(wallEnd.X - insertionOffset.X, wallEnd.Y - insertionOffset.Y);
            var wallSeg = new LineSegment2D(wStart, wEnd);

            // Bước 1: Lọc theo cao độ
            var zFiltered = frames.Where(f => IsElevationMatch(f, wallZ)).ToList();

            if (zFiltered.Count == 0)
            {
                result.Mappings.Add(CreateNewMapping(result.WallLength));
                return result;
            }

            // Bước 2: Lọc theo song song và khoảng cách
            var candidates = new List<FrameCandidate>();

            foreach (var frame in zFiltered)
            {
                if (frame.IsVertical) continue;

                var frameSeg = new LineSegment2D(frame.StartPt, frame.EndPt);

                if (!AngleAlgorithms.IsParallel(wallSeg.Angle, frameSeg.Angle, TOLERANCE_ANGLE))
                    continue;

                double perpDist = DistanceAlgorithms.BetweenParallelSegments(wallSeg, frameSeg);
                if (perpDist > TOLERANCE_DIST)
                    continue;

                var overlap = OverlapAlgorithms.CalculateOverlap(wallSeg, frameSeg);
                if (!overlap.HasOverlap || overlap.OverlapLength < MIN_OVERLAP)
                    continue;

                candidates.Add(new FrameCandidate
                {
                    Frame = frame,
                    OverlapLength = overlap.OverlapLength,
                    PerpDist = perpDist
                });
            }

            if (candidates.Count == 0)
            {
                result.Mappings.Add(CreateNewMapping(result.WallLength));
                return result;
            }

            // Bước 3: Sắp xếp theo khoảng cách
            candidates = candidates.OrderBy(c => c.PerpDist)
                                   .ThenByDescending(c => c.OverlapLength)
                                   .ToList();

            // Bước 4: Tính chi tiết mapping
            foreach (var candidate in candidates)
            {
                var mapping = CalculateMappingDetails(wallSeg, candidate.Frame, candidate.OverlapLength);
                if (mapping != null)
                {
                    result.Mappings.Add(mapping);
                }
            }

            // Bước 5: Tối ưu hóa
            result.Mappings = OptimizeMappings(result.Mappings, result.WallLength);

            return result;
        }

        /// <summary>
        /// Tìm mapping cho LineSegment2D
        /// </summary>
        public static MappingResult FindMappings(LineSegment2D wallSegment, double wallZ,
            IEnumerable<SapFrame> frames, Point2D insertionOffset = default)
        {
            return FindMappings(wallSegment.Start, wallSegment.End, wallZ, frames, insertionOffset);
        }

        #endregion

        #region Helper Methods

        private static bool IsElevationMatch(SapFrame frame, double wallZ)
        {
            double frameZ = Math.Min(frame.Z1, frame.Z2);
            return Math.Abs(frameZ - wallZ) <= TOLERANCE_Z;
        }

        private static MappingRecord CreateNewMapping(double length)
        {
            return new MappingRecord
            {
                TargetFrame = "New",
                MatchType = "NEW",
                CoveredLength = length
            };
        }

        private static MappingRecord CalculateMappingDetails(LineSegment2D wallSeg, SapFrame frame, double overlapLength)
        {
            double wallAngle = wallSeg.Angle;
            double cosA = Math.Cos(wallAngle);
            double sinA = Math.Sin(wallAngle);

            var basePoint = wallSeg.Start;

            double wStartProj = 0;
            double wEndProj = (wallSeg.End.X - basePoint.X) * cosA + (wallSeg.End.Y - basePoint.Y) * sinA;

            double fStartProj = (frame.StartPt.X - basePoint.X) * cosA + (frame.StartPt.Y - basePoint.Y) * sinA;
            double fEndProj = (frame.EndPt.X - basePoint.X) * cosA + (frame.EndPt.Y - basePoint.Y) * sinA;

            // Sắp xếp
            if (wStartProj > wEndProj)
                (wStartProj, wEndProj) = (wEndProj, wStartProj);

            if (fStartProj > fEndProj)
                (fStartProj, fEndProj) = (fEndProj, fStartProj);

            double distI = Math.Max(0, fStartProj - wStartProj);
            double distJ = Math.Max(0, wEndProj - fEndProj);

            string mapType = "PARTIAL";
            if (distI < 1 && distJ < 1)
                mapType = "FULL";
            else if (overlapLength >= wallSeg.Length * 0.95)
                mapType = "FULL";

            return new MappingRecord
            {
                TargetFrame = frame.Name,
                MatchType = mapType,
                DistI = distI,
                DistJ = distJ,
                FrameLength = frame.Length2D,
                CoveredLength = overlapLength
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

        #region Helper Types

        private class FrameCandidate
        {
            public SapFrame Frame { get; set; }
            public double OverlapLength { get; set; }
            public double PerpDist { get; set; }
        }

        #endregion
    }
}