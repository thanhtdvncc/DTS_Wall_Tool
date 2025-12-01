using DTS_Wall_Tool.Core.Algorithms;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Engines
{
    /// <summary>
    /// Xử lý wall segments để tạo centerlines
    /// </summary>
    public class WallSegmentProcessor
    {
        #region Configuration

        public List<double> WallThicknesses { get; set; } = new List<double>();
        public List<double> DoorWidths { get; set; } = new List<double>();
        public List<double> ColumnWidths { get; set; } = new List<double>();
        public double AngleTolerance { get; set; } = 5.0;
        public double DistanceTolerance { get; set; } = 10.0;
        public double AxisSnapDistance { get; set; } = 50.0;
        public double AutoJoinGapDistance { get; set; } = 300.0;
        public bool EnableAutoExtend { get; set; } = true;
        public bool BreakAtGridIntersections { get; set; } = false;
        public bool ExtendToGridIntersections { get; set; } = false;

        #endregion

        #region Working Data

        private List<WallSegment> _segments = new List<WallSegment>();
        private List<CenterLine> _centerlines = new List<CenterLine>();
        private List<AxisLine> _axes = new List<AxisLine>();
        private Dictionary<int, List<int>> _angleGroups = new Dictionary<int, List<int>>();
        private HashSet<string> _processedPairs = new HashSet<string>();

        #endregion

        #region Statistics

        public int MergedSegmentsCount { get; private set; }
        public int DetectedPairsCount { get; private set; }
        public int RecoveredGapsCount { get; private set; }
        public int SnappedToAxesCount { get; private set; }

        #endregion

        #region Main Processing

        public List<CenterLine> Process(List<WallSegment> segments, List<AxisLine> axes = null)
        {
            Initialize(segments, axes);

            if (_segments.Count == 0)
                return _centerlines;

            // Pipeline xử lý
            NormalizeAngles();
            BuildAngleGroups();
            MergedSegmentsCount = MergeOverlappingSegments();
            DetectedPairsCount = DetectWallPairs();
            GenerateCenterlinesFromPairs();
            AddSingleLinesToCenterlines();
            RecoveredGapsCount = RecoverGaps();
            MergeOverlappingCenterlines();

            if (_axes.Count > 0 && AxisSnapDistance > 0)
                SnappedToAxesCount = SnapToAxes();

            if (EnableAutoExtend)
                AutoExtendCenterlines();

            if (BreakAtGridIntersections && _axes.Count > 0)
                BreakAtGrid();

            if (ExtendToGridIntersections && _axes.Count > 0)
                ExtendToGrid();

            CleanupCenterlines();

            return _centerlines;
        }

        private void Initialize(List<WallSegment> segments, List<AxisLine> axes)
        {
            _segments = segments ?? new List<WallSegment>();
            _axes = axes ?? new List<AxisLine>();
            _centerlines = new List<CenterLine>();
            _processedPairs = new HashSet<string>();
            _angleGroups = new Dictionary<int, List<int>>();

            MergedSegmentsCount = 0;
            DetectedPairsCount = 0;
            RecoveredGapsCount = 0;
            SnappedToAxesCount = 0;

            for (int i = 0; i < _segments.Count; i++)
            {
                _segments[i].Index = i;
                _segments[i].IsActive = true;
                _segments[i].IsProcessed = false;
                _segments[i].PairSegmentID = -1;
            }
        }

        #endregion

        #region Step 1: Normalize Angles

        private void NormalizeAngles()
        {
            double snapTol = AngleTolerance * GeometryConstants.DEG_TO_RAD;

            foreach (var seg in _segments.Where(s => s.IsActive))
            {
                double snappedAngle = AngleAlgorithms.SnapToCardinal(seg.Angle, snapTol);

                if (Math.Abs(snappedAngle - seg.Angle) > GeometryConstants.EPSILON)
                {
                    double length = seg.Length;
                    double cosA = Math.Cos(snappedAngle);
                    double sinA = Math.Sin(snappedAngle);

                    seg.EndPt = new Point2D(
                        seg.StartPt.X + length * cosA,
                        seg.StartPt.Y + length * sinA
                    );
                }
            }
        }

        #endregion

        #region Step 2: Build Angle Groups

        private void BuildAngleGroups()
        {
            _angleGroups.Clear();

            foreach (var seg in _segments.Where(s => s.IsActive))
            {
                int angleKey = (int)Math.Round(seg.NormalizedAngle * GeometryConstants.RAD_TO_DEG);

                if (!_angleGroups.ContainsKey(angleKey))
                    _angleGroups[angleKey] = new List<int>();

                _angleGroups[angleKey].Add(seg.Index);
            }
        }

        #endregion

        #region Step 3: Merge Overlapping Segments

        private int MergeOverlappingSegments()
        {
            int totalMerged = 0;
            int maxIterations = 5;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                int mergedThisPass = 0;

                foreach (var angleGroup in _angleGroups.Values)
                {
                    var activeInGroup = angleGroup.Where(i => _segments[i].IsActive).ToList();

                    for (int i = 0; i < activeInGroup.Count; i++)
                    {
                        var seg1 = _segments[activeInGroup[i]];
                        if (!seg1.IsActive) continue;

                        for (int j = i + 1; j < activeInGroup.Count; j++)
                        {
                            var seg2 = _segments[activeInGroup[j]];
                            if (!seg2.IsActive) continue;

                            if (!OverlapAlgorithms.AreCollinear(seg1.AsSegment, seg2.AsSegment,
                                AngleTolerance * GeometryConstants.DEG_TO_RAD, DistanceTolerance))
                                continue;

                            var overlap = OverlapAlgorithms.CalculateOverlap(seg1.AsSegment, seg2.AsSegment);
                            double gap = OverlapAlgorithms.CalculateGapDistance(seg1.AsSegment, seg2.AsSegment);

                            if (overlap.HasOverlap || gap <= DistanceTolerance)
                            {
                                var merged = MergeAlgorithms.MergeCollinear(seg1.AsSegment, seg2.AsSegment);
                                seg1.SetFromSegment(merged);

                                if (seg2.Thickness > seg1.Thickness)
                                {
                                    seg1.Thickness = seg2.Thickness;
                                    seg1.WallType = seg2.WallType;
                                }

                                seg2.IsActive = false;
                                seg2.MergedIntoID = seg1.Index;
                                mergedThisPass++;
                            }
                        }
                    }
                }

                totalMerged += mergedThisPass;
                if (mergedThisPass == 0) break;
            }

            return totalMerged;
        }

        #endregion

        #region Step 4: Detect Wall Pairs

        private int DetectWallPairs()
        {
            int pairCount = 0;
            _processedPairs.Clear();

            foreach (var thickness in WallThicknesses.OrderByDescending(t => t))
            {
                double minDist = thickness * 0.8;
                double maxDist = thickness * 1.2;

                foreach (var angleGroup in _angleGroups.Values)
                {
                    var activeInGroup = angleGroup.Where(i =>
                        _segments[i].IsActive && _segments[i].PairSegmentID == -1).ToList();

                    for (int i = 0; i < activeInGroup.Count; i++)
                    {
                        var seg1 = _segments[activeInGroup[i]];
                        if (!seg1.IsActive || seg1.PairSegmentID != -1) continue;

                        double bestScore = double.MaxValue;
                        int bestPairIndex = -1;
                        double bestThickness = 0;

                        for (int j = i + 1; j < activeInGroup.Count; j++)
                        {
                            var seg2 = _segments[activeInGroup[j]];
                            if (!seg2.IsActive || seg2.PairSegmentID != -1) continue;

                            if (!AngleAlgorithms.IsParallel(seg1.Angle, seg2.Angle,
                                AngleTolerance * GeometryConstants.DEG_TO_RAD))
                                continue;

                            double perpDist = DistanceAlgorithms.BetweenParallelSegments(
                                seg1.AsSegment, seg2.AsSegment);

                            if (perpDist < minDist || perpDist > maxDist)
                                continue;

                            var overlap = OverlapAlgorithms.CalculateOverlap(seg1.AsSegment, seg2.AsSegment);
                            if (!overlap.HasOverlap || overlap.OverlapPercent < 0.5)
                                continue;

                            double distScore = Math.Abs(perpDist - thickness);
                            double overlapScore = 1.0 - overlap.OverlapPercent;
                            double totalScore = distScore + overlapScore * thickness;

                            if (totalScore < bestScore)
                            {
                                bestScore = totalScore;
                                bestPairIndex = activeInGroup[j];
                                bestThickness = perpDist;
                            }
                        }

                        if (bestPairIndex >= 0)
                        {
                            string pairKey = $"{Math.Min(seg1.Index, bestPairIndex)}_{Math.Max(seg1.Index, bestPairIndex)}";
                            if (!_processedPairs.Contains(pairKey))
                            {
                                seg1.SetPairedWith(_segments[bestPairIndex], bestThickness);
                                _processedPairs.Add(pairKey);
                                pairCount++;
                            }
                        }
                    }
                }
            }

            return pairCount;
        }

        #endregion

        #region Step 5: Generate Centerlines from Pairs

        private void GenerateCenterlinesFromPairs()
        {
            var processedPairs = new HashSet<string>();

            foreach (var seg in _segments.Where(s => s.IsActive && s.PairSegmentID != -1))
            {
                string pairKey = $"{Math.Min(seg.Index, seg.PairSegmentID)}_{Math.Max(seg.Index, seg.PairSegmentID)}";
                if (processedPairs.Contains(pairKey)) continue;

                var pairSeg = _segments[seg.PairSegmentID];
                if (!pairSeg.IsActive) continue;

                var centerline = CenterLine.FromWallPair(seg, pairSeg);
                if (centerline != null)
                {
                    _centerlines.Add(centerline);
                    processedPairs.Add(pairKey);

                    seg.IsProcessed = true;
                    pairSeg.IsProcessed = true;
                }
            }
        }

        #endregion

        #region Step 6: Add Single Lines

        private void AddSingleLinesToCenterlines()
        {
            foreach (var seg in _segments.Where(s => s.IsActive && !s.IsProcessed && s.PairSegmentID == -1))
            {
                if (!seg.IsSingleLine && seg.Thickness <= 0)
                    continue;

                var centerline = CenterLine.FromSingleSegment(seg);
                _centerlines.Add(centerline);
                seg.IsProcessed = true;
            }
        }

        #endregion

        #region Step 7: Recover Gaps

        private int RecoverGaps()
        {
            int recoveredCount = 0;

            var validGaps = new List<double>();
            validGaps.AddRange(DoorWidths);
            validGaps.AddRange(ColumnWidths);
            validGaps.Add(AutoJoinGapDistance);
            validGaps = validGaps.Where(g => g > 0).Distinct().OrderBy(g => g).ToList();

            if (validGaps.Count == 0)
                return 0;

            double maxGap = validGaps.Max() * 1.1;

            var clGroups = _centerlines
                .Where(cl => cl.IsActive)
                .Select((cl, idx) => new { CL = cl, Idx = idx })
                .GroupBy(x => (int)Math.Round(x.CL.Angle * GeometryConstants.RAD_TO_DEG / 5) * 5)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var group in clGroups.Values)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    var cl1 = group[i].CL;
                    if (!cl1.IsActive) continue;

                    for (int j = i + 1; j < group.Count; j++)
                    {
                        var cl2 = group[j].CL;
                        if (!cl2.IsActive) continue;

                        if (!cl1.CanMergeWith(cl2, AngleTolerance * GeometryConstants.DEG_TO_RAD, DistanceTolerance * 2))
                            continue;

                        double gap = OverlapAlgorithms.CalculateGapDistance(cl1.AsSegment, cl2.AsSegment);

                        bool isValidGap = gap <= maxGap && validGaps.Any(vg => Math.Abs(gap - vg) <= vg * 0.15);

                        if (isValidGap || gap <= AutoJoinGapDistance)
                        {
                            cl1.MergeWith(cl2);
                            recoveredCount++;
                        }
                    }
                }
            }

            return recoveredCount;
        }

        #endregion

        #region Step 8: Merge Overlapping Centerlines

        private int MergeOverlappingCenterlines()
        {
            int mergedCount = 0;
            int maxIterations = 3;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                int mergedThisPass = 0;
                var activeList = _centerlines.Where(cl => cl.IsActive).ToList();

                for (int i = 0; i < activeList.Count; i++)
                {
                    var cl1 = activeList[i];
                    if (!cl1.IsActive) continue;

                    for (int j = i + 1; j < activeList.Count; j++)
                    {
                        var cl2 = activeList[j];
                        if (!cl2.IsActive) continue;

                        if (!cl1.CanMergeWith(cl2, AngleTolerance * GeometryConstants.DEG_TO_RAD, DistanceTolerance))
                            continue;

                        var overlap = OverlapAlgorithms.CalculateOverlap(cl1.AsSegment, cl2.AsSegment);
                        if (overlap.HasOverlap && overlap.OverlapLength > 0)
                        {
                            cl1.MergeWith(cl2);
                            mergedThisPass++;
                        }
                    }
                }

                mergedCount += mergedThisPass;
                if (mergedThisPass == 0) break;
            }

            return mergedCount;
        }

        #endregion

        #region Step 9: Snap to Axes

        private int SnapToAxes()
        {
            int snappedCount = 0;

            foreach (var cl in _centerlines.Where(c => c.IsActive))
            {
                foreach (var axis in _axes)
                {
                    if (!AngleAlgorithms.IsParallel(cl.Angle, axis.Angle, AngleTolerance * GeometryConstants.DEG_TO_RAD))
                        continue;

                    double perpDist = DistanceAlgorithms.PointToInfiniteLine(cl.Midpoint, axis.AsSegment);

                    if (perpDist <= AxisSnapDistance)
                    {
                        SnapCenterlineToAxis(cl, axis);
                        snappedCount++;
                        break;
                    }
                }
            }

            return snappedCount;
        }

        private void SnapCenterlineToAxis(CenterLine cl, AxisLine axis)
        {
            double perpDist = DistanceAlgorithms.PointToInfiniteLine(cl.Midpoint, axis.AsSegment);
            double cosA = Math.Cos(cl.Angle);
            double sinA = Math.Sin(cl.Angle);

            double perpX = -sinA;
            double perpY = cosA;

            double testDist = DistanceAlgorithms.PointToInfiniteLine(
                new Point2D(cl.Midpoint.X + perpX, cl.Midpoint.Y + perpY),
                axis.AsSegment);

            double sign = testDist < perpDist ? 1 : -1;

            cl.StartPt = new Point2D(
                cl.StartPt.X + sign * perpX * perpDist,
                cl.StartPt.Y + sign * perpY * perpDist);
            cl.EndPt = new Point2D(
                cl.EndPt.X + sign * perpX * perpDist,
                cl.EndPt.Y + sign * perpY * perpDist);

            cl.UpdateUniqueID();
        }

        #endregion

        #region Step 10: Auto Extend

        private int AutoExtendCenterlines()
        {
            int extendedCount = 0;
            double extendTolerance = 100;

            var activeList = _centerlines.Where(cl => cl.IsActive).ToList();

            foreach (var cl in activeList)
            {
                foreach (var other in activeList.Where(o => o != cl && o.IsActive))
                {
                    if (!AngleAlgorithms.IsPerpendicular(cl.Angle, other.Angle, AngleTolerance * GeometryConstants.DEG_TO_RAD))
                        continue;

                    double distStart = DistanceAlgorithms.PointToSegment(cl.StartPt, other.AsSegment);
                    double distEnd = DistanceAlgorithms.PointToSegment(cl.EndPt, other.AsSegment);

                    if (distStart > GeometryConstants.EPSILON && distStart <= extendTolerance)
                    {
                        var result = IntersectionAlgorithms.LineLine(cl.AsSegment, other.AsSegment);
                        if (result.HasIntersection && cl.StartPt.DistanceTo(result.Point) <= extendTolerance)
                        {
                            cl.StartPt = result.Point;
                            extendedCount++;
                        }
                    }

                    if (distEnd > GeometryConstants.EPSILON && distEnd <= extendTolerance)
                    {
                        var result = IntersectionAlgorithms.LineLine(cl.AsSegment, other.AsSegment);
                        if (result.HasIntersection && cl.EndPt.DistanceTo(result.Point) <= extendTolerance)
                        {
                            cl.EndPt = result.Point;
                            extendedCount++;
                        }
                    }
                }
            }

            return extendedCount;
        }

        #endregion

        #region Step 11: Break at Grid

        private int BreakAtGrid()
        {
            int brokenCount = 0;
            var newCenterlines = new List<CenterLine>();

            foreach (var cl in _centerlines.Where(c => c.IsActive).ToList())
            {
                var breakPoints = new List<double>();

                foreach (var axis in _axes)
                {
                    if (!AngleAlgorithms.IsPerpendicular(cl.Angle, axis.Angle, AngleTolerance * GeometryConstants.DEG_TO_RAD))
                        continue;

                    var result = IntersectionAlgorithms.SegmentSegment(cl.AsSegment, axis.AsSegment, out bool isParallel, 10);
                    if (result.HasIntersection)
                    {
                        double t = result.T1;
                        if (t > 0.05 && t < 0.95)
                        {
                            breakPoints.Add(t);
                        }
                    }
                }

                if (breakPoints.Count > 0)
                {
                    breakPoints = breakPoints.OrderBy(t => t).Distinct().ToList();

                    double prevT = 0;
                    double cosA = Math.Cos(cl.Angle);
                    double sinA = Math.Sin(cl.Angle);
                    double length = cl.Length;

                    foreach (var t in breakPoints)
                    {
                        var segStart = new Point2D(
                            cl.StartPt.X + prevT * length * cosA,
                            cl.StartPt.Y + prevT * length * sinA);
                        var segEnd = new Point2D(
                            cl.StartPt.X + t * length * cosA,
                            cl.StartPt.Y + t * length * sinA);

                        var newCL = cl.Clone();
                        newCL.StartPt = segStart;
                        newCL.EndPt = segEnd;
                        newCL.UpdateUniqueID();
                        newCenterlines.Add(newCL);

                        prevT = t;
                        brokenCount++;
                    }

                    var lastStart = new Point2D(
                        cl.StartPt.X + prevT * length * cosA,
                        cl.StartPt.Y + prevT * length * sinA);
                    var lastCL = cl.Clone();
                    lastCL.StartPt = lastStart;
                    lastCL.EndPt = cl.EndPt;
                    lastCL.UpdateUniqueID();
                    newCenterlines.Add(lastCL);

                    cl.IsActive = false;
                }
            }

            _centerlines.AddRange(newCenterlines);
            return brokenCount;
        }

        #endregion

        #region Step 12: Extend to Grid

        private int ExtendToGrid()
        {
            int extendedCount = 0;
            double maxExtend = 500;

            foreach (var cl in _centerlines.Where(c => c.IsActive))
            {
                foreach (var axis in _axes)
                {
                    if (!AngleAlgorithms.IsPerpendicular(cl.Angle, axis.Angle, AngleTolerance * GeometryConstants.DEG_TO_RAD))
                        continue;

                    var result = IntersectionAlgorithms.LineLine(cl.AsSegment, axis.AsSegment);
                    if (result.HasIntersection)
                    {
                        double distToStart = cl.StartPt.DistanceTo(result.Point);
                        double distToEnd = cl.EndPt.DistanceTo(result.Point);

                        if (distToStart < maxExtend && distToStart > cl.Length * 0.1 && result.T1 < 0)
                        {
                            cl.StartPt = result.Point;
                            extendedCount++;
                        }

                        if (distToEnd < maxExtend && distToEnd > cl.Length * 0.1 && result.T1 > 1)
                        {
                            cl.EndPt = result.Point;
                            extendedCount++;
                        }
                    }
                }

                cl.UpdateUniqueID();
            }

            return extendedCount;
        }

        #endregion

        #region Step 13: Final Cleanup

        private void CleanupCenterlines()
        {
            _centerlines = _centerlines.Where(cl => cl.IsActive).ToList();

            var uniqueDict = new Dictionary<string, CenterLine>();
            foreach (var cl in _centerlines)
            {
                cl.UpdateUniqueID();
                if (!uniqueDict.ContainsKey(cl.UniqueID))
                {
                    uniqueDict[cl.UniqueID] = cl;
                }
                else
                {
                    uniqueDict[cl.UniqueID].SourceHandles.AddRange(cl.SourceHandles);
                }
            }

            _centerlines = uniqueDict.Values.ToList();
            _centerlines = _centerlines.Where(cl => cl.Length >= 50).ToList();
        }

        #endregion
    }
}