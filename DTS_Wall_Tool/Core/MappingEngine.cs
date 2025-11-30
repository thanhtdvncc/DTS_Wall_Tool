using System;
using System.Collections.Generic;
using System.Linq;
using static DTS_Wall_Tool.Core.Geometry;

namespace DTS_Wall_Tool.Core
{
    /// <summary>
    /// Class containing mapping results for a single wall
    /// </summary>
    public class MappingResult
    {
        public string WallHandle { get; set; }
        public double WallLength { get; set; }
        public List<MappingRecord> Mappings { get; set; } = new List<MappingRecord>();

        /// <summary>
        /// Total covered length by all mappings
        /// </summary>
        public double CoveredLength => Mappings.Sum(m => m.CoveredLength);

        /// <summary>
        /// Coverage percentage (0-100)
        /// </summary>
        public double CoveragePercent => WallLength > 0 ? (CoveredLength / WallLength) * 100 : 0;

        /// <summary>
        /// True if wall is fully covered
        /// </summary>
        public bool IsFullyCovered => CoveragePercent >= 99. 0;

        /// <summary>
        /// Generate composite label text
        /// </summary>
        public string GetLabelText(string wallType, string loadPattern, double loadValue)
        {
            string loadStr = $"{wallType} {loadPattern}={loadValue:0.00}";

            if (Mappings.Count == 0)
                return loadStr + " to New";

            if (Mappings.Count == 1)
                return loadStr + " to " + Mappings[0].TargetFrame;

            // Multiple mappings
            var frameNames = Mappings.Select(m => m.TargetFrame).Distinct();
            return loadStr + " to " + string.Join(",", frameNames);
        }
    }

    /// <summary>
    /// Record of a single wall-to-frame mapping
    /// </summary>
    public class MappingRecord
    {
        public string TargetFrame { get; set; }
        public string MapType { get; set; } = "PARTIAL"; // FULL, PARTIAL, NEW
        public double DistI { get; set; } = 0; // Distance from wall start to frame start
        public double DistJ { get; set; } = 0; // Distance from wall end to frame end
        public double FrameLength { get; set; } = 0;
        public double CoveredLength { get; set; } = 0; // Length of wall covered by this frame

        public override string ToString()
        {
            return $"{TargetFrame}({MapType}, I={DistI:0}, J={DistJ:0})";
        }
    }

    /// <summary>
    /// Core mapping engine - finds SAP2000 frames that support a wall
    /// </summary>
    public static class MappingEngine
    {
        #region Constants (Tunable)

        /// <summary>
        /// Maximum Z elevation difference (mm)
        /// </summary>
        public static double TOLERANCE_Z = 200.0;

        /// <summary>
        /// Maximum perpendicular distance from wall centerline to frame (mm)
        /// </summary>
        public static double TOLERANCE_DIST = 300.0;

        /// <summary>
        /// Minimum overlap length to consider mapping (mm)
        /// </summary>
        public static double MIN_OVERLAP = 100.0;

        /// <summary>
        /// Angle tolerance for parallelism (radians)
        /// </summary>
        public static double TOLERANCE_ANGLE = 5 * GeoAlgo.DEG_TO_RAD;

        #endregion

        #region Main Mapping Function

        /// <summary>
        /// Find all frames that support a wall segment
        /// </summary>
        /// <param name="wallStart">Wall start point (2D)</param>
        /// <param name="wallEnd">Wall end point (2D)</param>
        /// <param name="wallZ">Wall elevation</param>
        /// <param name="frames">List of SAP2000 frames to search</param>
        /// <param name="insertionOffset">Offset from CAD origin to SAP2000 origin</param>
        /// <returns>Mapping result with all matched frames</returns>
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

            if (result.WallLength < GeoAlgo.EPSILON)
                return result;

            // Apply offset if provided
            var wStart = new Point2D(wallStart.X - insertionOffset.X, wallStart.Y - insertionOffset.Y);
            var wEnd = new Point2D(wallEnd.X - insertionOffset.X, wallEnd.Y - insertionOffset.Y);
            var wallSeg = new LineSegment2D(wStart, wEnd);

            // Step 1: Filter candidates by Z elevation
            var zFiltered = frames.Where(f => IsElevationMatch(f, wallZ)).ToList();

            if (zFiltered.Count == 0)
            {
                // No frames at this elevation - mark as NEW
                result.Mappings.Add(new MappingRecord
                {
                    TargetFrame = "New",
                    MapType = "NEW",
                    CoveredLength = result.WallLength
                });
                return result;
            }

            // Step 2: Filter by parallelism and proximity
            var candidates = new List<FrameCandidate>();

            foreach (var frame in zFiltered)
            {
                // Skip columns (vertical elements)
                if (frame.IsVertical)
                    continue;

                var frameSeg = new LineSegment2D(frame.StartPt, frame.EndPt);

                // Check parallel
                if (!GeoAlgo.IsParallel(wallSeg.Angle, frameSeg.Angle, TOLERANCE_ANGLE))
                    continue;

                // Check proximity (perpendicular distance)
                double perpDist = GeoAlgo.DistBetweenParallelSegments(wallSeg, frameSeg);
                if (perpDist > TOLERANCE_DIST)
                    continue;

                // Calculate overlap
                var overlap = GeoAlgo.CalculateOverlap(wallSeg, frameSeg);
                if (!overlap.HasOverlap || overlap.OverlapLength < MIN_OVERLAP)
                    continue;

                // This is a valid candidate
                candidates.Add(new FrameCandidate
                {
                    Frame = frame,
                    OverlapLength = overlap.OverlapLength,
                    PerpDist = perpDist
                });
            }

            if (candidates.Count == 0)
            {
                // No matching frames - mark as NEW
                result.Mappings.Add(new MappingRecord
                {
                    TargetFrame = "New",
                    MapType = "NEW",
                    CoveredLength = result.WallLength
                });
                return result;
            }

            // Step 3: Sort candidates by proximity (closer = better)
            candidates = candidates.OrderBy(c => c.PerpDist).ThenByDescending(c => c.OverlapLength).ToList();

            // Step 4: Calculate mapping details for each candidate
            foreach (var candidate in candidates)
            {
                var mapping = CalculateMappingDetails(wallSeg, candidate.Frame, candidate.OverlapLength);
                if (mapping != null)
                {
                    result.Mappings.Add(mapping);
                }
            }

            // Step 5: Remove duplicates and optimize
            result.Mappings = OptimizeMappings(result.Mappings, result.WallLength);

            return result;
        }

        #endregion

        #region Helper Functions

        /// <summary>
        /// Check if frame elevation matches wall elevation
        /// </summary>
        private static bool IsElevationMatch(SapFrame frame, double wallZ)
        {
            // Frame's Z should be at or below wall Z (beam supports wall from below)
            double frameZ = Math.Min(frame.Z1, frame.Z2);
            return Math.Abs(frameZ - wallZ) <= TOLERANCE_Z;
        }

        /// <summary>
        /// Calculate detailed mapping information
        /// </summary>
        private static MappingRecord CalculateMappingDetails(LineSegment2D wallSeg, SapFrame frame, double overlapLength)
        {
            var frameSeg = new LineSegment2D(frame.StartPt, frame.EndPt);

            // Project wall endpoints onto frame direction
            double wallAngle = wallSeg.Angle;
            double cosA = Math.Cos(wallAngle);
            double sinA = Math.Sin(wallAngle);

            // Project all 4 points onto wall direction
            var basePoint = wallSeg.Start;

            double wStartProj = 0;
            double wEndProj = (wallSeg.End.X - basePoint.X) * cosA + (wallSeg.End.Y - basePoint.Y) * sinA;

            double fStartProj = (frame.StartPt.X - basePoint.X) * cosA + (frame.StartPt.Y - basePoint.Y) * sinA;
            double fEndProj = (frame.EndPt.X - basePoint.X) * cosA + (frame.EndPt.Y - basePoint.Y) * sinA;

            // Normalize projections
            if (wStartProj > wEndProj)
            {
                var temp = wStartProj;
                wStartProj = wEndProj;
                wEndProj = temp;
            }

            if (fStartProj > fEndProj)
            {
                var temp = fStartProj;
                fStartProj = fEndProj;
                fEndProj = temp;
            }

            // Calculate DistI and DistJ
            // DistI = distance from wall start to where frame starts (on wall)
            // DistJ = distance from where frame ends to wall end
            double distI = Math.Max(0, fStartProj - wStartProj);
            double distJ = Math.Max(0, wEndProj - fEndProj);

            // Determine map type
            string mapType = "PARTIAL";
            if (distI < 1 && distJ < 1)
                mapType = "FULL";
            else if (overlapLength >= wallSeg.Length * 0.95)
                mapType = "FULL";

            return new MappingRecord
            {
                TargetFrame = frame.Name,
                MapType = mapType,
                DistI = distI,
                DistJ = distJ,
                FrameLength = frame.Length2D,
                CoveredLength = overlapLength
            };
        }

        /// <summary>
        /// Optimize mapping list (remove duplicates, merge overlaps)
        /// </summary>
        private static List<MappingRecord> OptimizeMappings(List<MappingRecord> mappings, double wallLength)
        {
            if (mappings.Count <= 1)
                return mappings;

            // Remove exact duplicates
            var unique = mappings
                .GroupBy(m => m.TargetFrame)
                .Select(g => g.First())
                .ToList();

            // Check if we have full coverage with one frame
            var fullCoverage = unique.FirstOrDefault(m => m.MapType == "FULL");
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