using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Wall_Tool.Core;
using DTS_Wall_Tool.Models;

namespace DTS_Wall_Tool.Engines
{
    /// <summary>
    /// Processes wall segments to generate centerlines
    /// Replaces VBA module n01_ACAD_Wall_To_Diagram
    /// </summary>
    public class WallSegmentProcessor
    {
        #region Configuration

        /// <summary>
        /// List of wall thicknesses to detect (in mm)
        /// </summary>
        public List<double> WallThicknesses { get; set; } = new List<double>();

        /// <summary>
        /// Break widths for gap recovery (doors, windows)
        /// </summary>
        public List<double> DoorWidths { get; set; } = new List<double>();

        /// <summary>
        /// Column widths for gap recovery
        /// </summary>
        public List<double> ColumnWidths { get; set; } = new List<double>();

        /// <summary>
        /// Angle tolerance in degrees
        /// </summary>
        public double AngleTolerance { get; set; } = 5.0;

        /// <summary>
        /// Distance tolerance in mm
        /// </summary>
        public double DistanceTolerance { get; set; } = 10.0;

        /// <summary>
        /// Snap distance to axes in mm
        /// </summary>
        public double AxisSnapDistance { get; set; } = 50.0;

        /// <summary>
        /// Auto-join gap distance in mm
        /// </summary>
        public double AutoJoinGapDistance { get; set; } = 300.0;

        /// <summary>
        /// Enable auto-extend to perpendicular lines
        /// </summary>
        public bool EnableAutoExtend { get; set; } = true;

        /// <summary>
        /// Enable breaking at grid intersections
        /// </summary>
        public bool BreakAtGridIntersections { get; set; } = false;

        /// <summary>
        /// Enable extending to grid intersections
        /// </summary>
        public bool ExtendToGridIntersections { get; set; } = false;

        #endregion

        #region Working Data

        private List<WallSegment> _segments = new List<WallSegment>();
        private List<CenterLine> _centerlines = new List<CenterLine>();
        private List<AxisLine> _axes = new List<AxisLine>();

        // Spatial index for fast lookup
        private Dictionary<int, List<int>> _angleGroups = new Dictionary<int, List<int>>();

        // Track processed combinations to avoid duplicates
        private HashSet<string> _processedPairs = new HashSet<string>();

        #endregion

        #region Main Processing Pipeline

        /// <summary>
        /// Process wall segments and generate centerlines
        /// </summary>
        /// <param name="segments">Input wall segments</param>
        /// <param name="axes">Structural axes (optional)</param>
        /// <returns>Generated centerlines</returns>
        public List<CenterLine> Process(List<WallSegment> segments, List<AxisLine> axes = null)
        {
            _segments = segments ?? new List<WallSegment>();
            _axes = axes ?? new List<AxisLine>();
            _centerlines = new List<CenterLine>();
            _processedPairs = new HashSet<string>();
            _angleGroups = new Dictionary<int, List<int>>();

            if (_segments.Count == 0)
                return _centerlines;

            // Initialize indices
            for (int i = 0; i < _segments.Count; i++)
            {
                _segments[i].Index = i;
                _segments[i].IsActive = true;
            }

            // STEP 1: Normalize angles
            NormalizeAngles();

            // STEP 2: Build angle-based groups for O(n) lookup
            BuildAngleGroups();

            // STEP 3: Merge overlapping segments
            int mergedCount = MergeOverlappingSegments();

            // STEP 4: Detect wall pairs (double lines)
            int pairCount = DetectWallPairs();

            // STEP 5: Generate centerlines from pairs
            GenerateCenterlinesFromPairs();

            // STEP 6: Add single lines as centerlines
            AddSingleLinesToCenterlines();

            // STEP 7: Recover gaps (doors, columns)
            int recoveredGaps = RecoverGaps();

            // STEP 8: Merge overlapping centerlines
            int mergedCenterlines = MergeOverlappingCenterlines();

            // STEP 9: Snap to axes
            if (_axes.Count > 0 && AxisSnapDistance > 0)
            {
                int snapped = SnapToAxes();
            }

            // STEP 10: Auto-extend
            if (EnableAutoExtend)
            {
                int extended = AutoExtendCenterlines();
            }

            // STEP 11: Break at grid (optional)
            if (BreakAtGridIntersections && _axes.Count > 0)
            {
                int broken = BreakAtGrid();
            }

            // STEP 12: Extend to grid (optional)
            if (ExtendToGridIntersections && _axes.Count > 0)
            {
                int extended = ExtendToGrid();
            }

            // STEP 13: Final cleanup - remove inactive and duplicates
            CleanupCenterlines();

            return _centerlines;
        }

        #endregion

        #region Step 1: Normalize Angles

        private void NormalizeAngles()
        {
            double snapTol = 5 * GeoAlgo.DEG_TO_RAD;

            foreach (var seg in _segments.Where(s => s.IsActive))
            {
                double snappedAngle = GeoAlgo.SnapToCardinalAngle(seg.Angle, snapTol);

                if (Math.Abs(snappedAngle - seg.Angle) > GeoAlgo.EPSILON)
                {
                    // Recalculate endpoints based on snapped angle
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

            // Round angle to integer degrees for grouping
            foreach (var seg in _segments.Where(s => s.IsActive))
            {
                int angleKey = (int)Math.Round(seg.NormalizedAngle * GeoAlgo.RAD_TO_DEG);

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

                            // Check if collinear
                            if (!GeoAlgo.AreCollinear(seg1.AsSegment, seg2.AsSegment,
                                AngleTolerance * GeoAlgo.DEG_TO_RAD, DistanceTolerance))
                                continue;

                            // Check overlap or touching
                            var overlap = GeoAlgo.CalculateOverlap(seg1.AsSegment, seg2.AsSegment);
                            double gap = GeoAlgo.CalculateGapDistance(seg1.AsSegment, seg2.AsSegment);

                            if (overlap.HasOverlap || gap <= DistanceTolerance)
                            {
                                // Merge seg2 into seg1
                                var merged = GeoAlgo.MergeCollinearSegments(seg1.AsSegment, seg2.AsSegment);
                                seg1.StartPt = merged.Start;
                                seg1.EndPt = merged.End;

                                // Take larger thickness
                                if (seg2.Thickness > seg1.Thickness)
                                {
                                    seg1.Thickness = seg2.Thickness;
                                    seg1.WallType = seg2.WallType;
                                }

                                // Mark seg2 as inactive
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
                    var activeInGroup = angleGroup.Where(i => _segments[i].IsActive &&
                        _segments[i].PairSegmentID == -1).ToList();

                    for (int i = 0; i < activeInGroup.Count; i++)
                    {
                        var seg1 = _segments[activeInGroup[i]];
                        if (!seg1.IsActive || seg1.PairSegmentID != -1) continue;

                        CandidatePair bestPair = null;
                        double bestScore = double.MaxValue;

                        for (int j = i + 1; j < activeInGroup.Count; j++)
                        {
                            var seg2 = _segments[activeInGroup[j]];
                            if (!seg2.IsActive || seg2.PairSegmentID != -1) continue;

                            // Check parallel
                            if (!GeoAlgo.IsParallel(seg1.Angle, seg2.Angle, AngleTolerance * GeoAlgo.DEG_TO_RAD))
                                continue;

                            // Calculate perpendicular distance
                            double perpDist = GeoAlgo.DistBetweenParallelSegments(seg1.AsSegment, seg2.AsSegment);

                            if (perpDist < minDist || perpDist > maxDist)
                                continue;

                            // Check overlap
                            var overlap = GeoAlgo.CalculateOverlap(seg1.AsSegment, seg2.AsSegment);
                            if (!overlap.HasOverlap || overlap.OverlapPercent < 0.5)
                                continue;

                            // Score: prefer closer to target thickness, higher overlap
                            double distScore = Math.Abs(perpDist - thickness);
                            double overlapScore = 1.0 - overlap.OverlapPercent;
                            double totalScore = distScore + overlapScore * thickness;

                            if (totalScore < bestScore)
                            {
                                bestScore = totalScore;
                                bestPair = new CandidatePair
                                {
                                    Seg1Index = seg1.Index,
                                    Seg2Index = seg2.Index,
                                    Thickness = perpDist,
                                    OverlapPercent = overlap.OverlapPercent
                                };
                            }
                        }

                        // Apply best pair
                        if (bestPair != null)
                        {
                            string pairKey = $"{Math.Min(bestPair.Seg1Index, bestPair.Seg2Index)}_{Math.Max(bestPair.Seg1Index, bestPair.Seg2Index)}";
                            if (!_processedPairs.Contains(pairKey))
                            {
                                _segments[bestPair.Seg1Index].PairSegmentID = bestPair.Seg2Index;
                                _segments[bestPair.Seg2Index].PairSegmentID = bestPair.Seg1Index;
                                _segments[bestPair.Seg1Index].Thickness = bestPair.Thickness;
                                _segments[bestPair.Seg2Index].Thickness = bestPair.Thickness;

                                _processedPairs.Add(pairKey);
                                pairCount++;
                            }
                        }
                    }
                }
            }

            return pairCount;
        }

        private class CandidatePair
        {
            public int Seg1Index;
            public int Seg2Index;
            public double Thickness;
            public double OverlapPercent;
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

                // Calculate centerline
                var centerline = CalculateCenterline(seg, pairSeg);
                if (centerline != null)
                {
                    _centerlines.Add(centerline);
                    processedPairs.Add(pairKey);

                    // Mark as processed
                    seg.IsProcessed = true;
                    pairSeg.IsProcessed = true;
                }
            }
        }

        private CenterLine CalculateCenterline(WallSegment seg1, WallSegment seg2)
        {
            // Use the longer segment's direction as reference
            var dominant = seg1.Length >= seg2.Length ? seg1 : seg2;
            double refAngle = GeoAlgo.SnapToCardinalAngle(dominant.Angle);

            double cosA = Math.Cos(refAngle);
            double sinA = Math.Sin(refAngle);

            // Perpendicular direction
            double perpCos = -sinA;
            double perpSin = cosA;

            // Calculate midpoint between the two lines
            var mid1 = seg1.Midpoint;
            var mid2 = seg2.Midpoint;
            var centerMid = mid1.MidpointTo(mid2);

            // Project all 4 endpoints onto reference direction
            double proj1S = (seg1.StartPt.X - centerMid.X) * cosA + (seg1.StartPt.Y - centerMid.Y) * sinA;
            double proj1E = (seg1.EndPt.X - centerMid.X) * cosA + (seg1.EndPt.Y - centerMid.Y) * sinA;
            double proj2S = (seg2.StartPt.X - centerMid.X) * cosA + (seg2.StartPt.Y - centerMid.Y) * sinA;
            double proj2E = (seg2.EndPt.X - centerMid.X) * cosA + (seg2.EndPt.Y - centerMid.Y) * sinA;

            double minProj = Math.Min(Math.Min(proj1S, proj1E), Math.Min(proj2S, proj2E));
            double maxProj = Math.Max(Math.Max(proj1S, proj1E), Math.Max(proj2S, proj2E));

            var startPt = new Point2D(centerMid.X + minProj * cosA, centerMid.Y + minProj * sinA);
            var endPt = new Point2D(centerMid.X + maxProj * cosA, centerMid.Y + maxProj * sinA);

            var centerline = new CenterLine
            {
                StartPt = startPt,
                EndPt = endPt,
                Thickness = seg1.Thickness > 0 ? seg1.Thickness : seg2.Thickness,
                StoryZ = seg1.StoryZ,
                SourcePairID = seg1.Index,
                IsActive = true
            };

            centerline.SourceHandles.Add(seg1.Handle);
            centerline.SourceHandles.Add(seg2.Handle);
            centerline.WallType = "W" + ((int)centerline.Thickness).ToString();
            centerline.UpdateUniqueID();

            return centerline;
        }

        #endregion

        #region Step 6: Add Single Lines

        private void AddSingleLinesToCenterlines()
        {
            foreach (var seg in _segments.Where(s => s.IsActive && !s.IsProcessed && s.PairSegmentID == -1))
            {
                // Only add if marked as single line or has thickness
                if (!seg.IsSingleLine && seg.Thickness <= 0)
                    continue;

                var centerline = new CenterLine
                {
                    StartPt = seg.StartPt,
                    EndPt = seg.EndPt,
                    Thickness = seg.Thickness > 0 ? seg.Thickness : 100, // Default thickness
                    StoryZ = seg.StoryZ,
                    SourcePairID = -1,
                    IsActive = true
                };

                centerline.SourceHandles.Add(seg.Handle);
                centerline.WallType = seg.WallType;
                centerline.UpdateUniqueID();

                _centerlines.Add(centerline);
                seg.IsProcessed = true;
            }
        }

        #endregion

        #region Step 7: Recover Gaps

        private int RecoverGaps()
        {
            int recoveredCount = 0;

            // Build list of all valid gap widths
            var validGaps = new List<double>();
            validGaps.AddRange(DoorWidths);
            validGaps.AddRange(ColumnWidths);
            validGaps.Add(AutoJoinGapDistance);
            validGaps = validGaps.Where(g => g > 0).Distinct().OrderBy(g => g).ToList();

            if (validGaps.Count == 0)
                return 0;

            double maxGap = validGaps.Max() * 1.1; // Allow 10% tolerance

            // Group centerlines by angle
            var clGroups = _centerlines
                .Where(cl => cl.IsActive)
                .Select((cl, idx) => new { CL = cl, Idx = idx })
                .GroupBy(x => (int)Math.Round(x.CL.Angle * GeoAlgo.RAD_TO_DEG / 5) * 5) // Group by 5-degree increments
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

                        // Check collinear
                        if (!GeoAlgo.AreCollinear(cl1.AsSegment, cl2.AsSegment,
                            AngleTolerance * GeoAlgo.DEG_TO_RAD, DistanceTolerance * 2))
                            continue;

                        // Calculate gap
                        double gap = GeoAlgo.CalculateGapDistance(cl1.AsSegment, cl2.AsSegment);

                        // Check if gap matches any valid width
                        bool validGap = gap <= maxGap && validGaps.Any(vg => Math.Abs(gap - vg) <= vg * 0.15);

                        if (validGap || gap <= AutoJoinGapDistance)
                        {
                            // Merge centerlines
                            cl1.MergeWith(cl2);
                            cl2.IsActive = false;
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

                        // Check thickness match (within 20%)
                        if (Math.Abs(cl1.Thickness - cl2.Thickness) > cl1.Thickness * 0.2)
                            continue;

                        // Check collinear
                        if (!GeoAlgo.AreCollinear(cl1.AsSegment, cl2.AsSegment,
                            AngleTolerance * GeoAlgo.DEG_TO_RAD, DistanceTolerance))
                            continue;

                        // Check overlap
                        var overlap = GeoAlgo.CalculateOverlap(cl1.AsSegment, cl2.AsSegment);
                        if (overlap.HasOverlap && overlap.OverlapLength > 0)
                        {
                            cl1.MergeWith(cl2);
                            cl2.IsActive = false;
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
                bool snapped = false;

                foreach (var axis in _axes)
                {
                    // Check if parallel
                    if (!GeoAlgo.IsParallel(cl.Angle, axis.Angle, AngleTolerance * GeoAlgo.DEG_TO_RAD))
                        continue;

                    // Check perpendicular distance
                    double perpDist = GeoAlgo.DistPointToInfiniteLine(cl.Midpoint, axis.StartPt, axis.EndPt);

                    if (perpDist <= AxisSnapDistance)
                    {
                        // Snap centerline to axis
                        SnapCenterlineToAxis(cl, axis);
                        snapped = true;
                        snappedCount++;
                        break;
                    }
                }
            }

            return snappedCount;
        }

        private void SnapCenterlineToAxis(CenterLine cl, AxisLine axis)
        {
            // Project centerline endpoints onto axis
            Point2D projStart, projEnd;
            GeoAlgo.ProjectPointOnSegment(cl.StartPt, axis.AsSegment, out projStart);
            GeoAlgo.ProjectPointOnSegment(cl.EndPt, axis.AsSegment, out projEnd);

            // Move centerline to axis position (perpendicular shift)
            var axisMid = axis.StartPt.MidpointTo(axis.EndPt);
            var clMid = cl.Midpoint;

            double shiftX = projStart.X - cl.StartPt.X;
            double shiftY = projStart.Y - cl.StartPt.Y;

            // Only shift perpendicular to centerline direction
            double perpDist = GeoAlgo.DistPointToInfiniteLine(clMid, axis.StartPt, axis.EndPt);
            double cosA = Math.Cos(cl.Angle);
            double sinA = Math.Sin(cl.Angle);

            // Perpendicular direction
            double perpX = -sinA;
            double perpY = cosA;

            // Determine sign of shift
            double testDist = GeoAlgo.DistPointToInfiniteLine(
                new Point2D(clMid.X + perpX, clMid.Y + perpY),
                axis.StartPt, axis.EndPt);

            double sign = testDist < perpDist ? 1 : -1;

            cl.StartPt = new Point2D(cl.StartPt.X + sign * perpX * perpDist, cl.StartPt.Y + sign * perpY * perpDist);
            cl.EndPt = new Point2D(cl.EndPt.X + sign * perpX * perpDist, cl.EndPt.Y + sign * perpY * perpDist);

            cl.UpdateUniqueID();
        }

        #endregion

        #region Step 10: Auto Extend

        private int AutoExtendCenterlines()
        {
            int extendedCount = 0;
            double extendTolerance = 100; // Max extension distance

            var activeList = _centerlines.Where(cl => cl.IsActive).ToList();

            foreach (var cl in activeList)
            {
                // Find perpendicular centerlines near endpoints
                foreach (var other in activeList.Where(o => o != cl && o.IsActive))
                {
                    // Check perpendicular
                    if (!GeoAlgo.IsPerpendicular(cl.Angle, other.Angle, AngleTolerance * GeoAlgo.DEG_TO_RAD))
                        continue;

                    // Check if cl endpoint is close to other's line
                    double distStart = GeoAlgo.DistPointToSegment(cl.StartPt, other.StartPt, other.EndPt);
                    double distEnd = GeoAlgo.DistPointToSegment(cl.EndPt, other.StartPt, other.EndPt);

                    // Extend start point
                    if (distStart > GeoAlgo.EPSILON && distStart <= extendTolerance)
                    {
                        Point2D intersection;
                        if (GeoAlgo.GetLineIntersection(cl.StartPt, cl.EndPt, other.StartPt, other.EndPt, out intersection))
                        {
                            // Verify intersection is in valid direction
                            double toCl = cl.StartPt.DistanceTo(intersection);
                            if (toCl <= extendTolerance)
                            {
                                cl.StartPt = intersection;
                                extendedCount++;
                            }
                        }
                    }

                    // Extend end point
                    if (distEnd > GeoAlgo.EPSILON && distEnd <= extendTolerance)
                    {
                        Point2D intersection;
                        if (GeoAlgo.GetLineIntersection(cl.StartPt, cl.EndPt, other.StartPt, other.EndPt, out intersection))
                        {
                            double toCl = cl.EndPt.DistanceTo(intersection);
                            if (toCl <= extendTolerance)
                            {
                                cl.EndPt = intersection;
                                extendedCount++;
                            }
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

            foreach (var cl in _centerlines. Where(c => c.IsActive). ToList())
            {
                var breakPoints = new List<double>();

                foreach (var axis in _axes)
                {
                    // Check if perpendicular
                    if (! GeoAlgo. IsPerpendicular(cl.Angle, axis.Angle, AngleTolerance * GeoAlgo.DEG_TO_RAD))
                        continue;

                    // Find intersection
                    Point2D intersection;
                    if (GeoAlgo. GetSegmentIntersection(cl.AsSegment, axis.AsSegment, out intersection, 10))
                    {
                        // Calculate t parameter (position along centerline)
                        double t = GeoAlgo.ProjectPointOnSegment(intersection, cl.AsSegment, out _);
                        if (t > 0.05 && t < 0.95) // Only break if not at endpoints
                        {
                            breakPoints.Add(t);
                        }
                    }
                }

                if (breakPoints.Count > 0)
                {
                    // Sort break points
                    breakPoints = breakPoints.OrderBy(t => t). Distinct().ToList();

                    // Create segments
                    double prevT = 0;
                    double cosA = Math. Cos(cl. Angle);
                    double sinA = Math.Sin(cl. Angle);
                    double length = cl.Length;

                    foreach (var t in breakPoints)
                    {
                        var segStart = new Point2D(
                            cl.StartPt.X + prevT * length * cosA,
                            cl.StartPt.Y + prevT * length * sinA);
                        var segEnd = new Point2D(
                            cl.StartPt.X + t * length * cosA,
                            cl.StartPt.Y + t * length * sinA);

                        var newCL = new CenterLine
                        {
                            StartPt = segStart,
                            EndPt = segEnd,
                            Thickness = cl.Thickness,
                            WallType = cl. WallType,
                            StoryZ = cl.StoryZ,
                            IsActive = true
                        };
                        newCL. SourceHandles. AddRange(cl. SourceHandles);
                        newCL.UpdateUniqueID();
                        newCenterlines.Add(newCL);

                        prevT = t;
                        brokenCount++;
                    }

                    // Last segment
                    var lastStart = new Point2D(
                        cl.StartPt. X + prevT * length * cosA,
                        cl. StartPt.Y + prevT * length * sinA);
                    var lastCL = new CenterLine
                    {
                        StartPt = lastStart,
                        EndPt = cl.EndPt,
                        Thickness = cl.Thickness,
                        WallType = cl.WallType,
                        StoryZ = cl.StoryZ,
                        IsActive = true
                    };
                    lastCL.SourceHandles.AddRange(cl.SourceHandles);
                    lastCL. UpdateUniqueID();
                    newCenterlines.Add(lastCL);

                    // Deactivate original
                    cl.IsActive = false;
                }
            }

            _centerlines. AddRange(newCenterlines);
            return brokenCount;
        }

        #endregion

        #region Step 12: Extend to Grid

        private int ExtendToGrid()
        {
            int extendedCount = 0;
            double maxExtend = 500; // Maximum extension distance

            foreach (var cl in _centerlines.Where(c => c.IsActive))
            {
                foreach (var axis in _axes)
                {
                    // Check if perpendicular
                    if (!GeoAlgo.IsPerpendicular(cl.Angle, axis.Angle, AngleTolerance * GeoAlgo.DEG_TO_RAD))
                        continue;

                    // Find intersection with infinite line
                    Point2D intersection;
                    if (GeoAlgo.GetLineIntersection(cl.StartPt, cl.EndPt, axis.StartPt, axis.EndPt, out intersection))
                    {
                        // Check if intersection is near start or end
                        double distToStart = cl.StartPt.DistanceTo(intersection);
                        double distToEnd = cl.EndPt.DistanceTo(intersection);

                        // Extend start if close enough
                        if (distToStart < maxExtend && distToStart > cl.Length * 0.1)
                        {
                            // Verify direction (intersection should be beyond start)
                            double projT = GeoAlgo.ProjectPointOnSegment(intersection, cl.AsSegment, out _);
                            if (projT < 0)
                            {
                                cl.StartPt = intersection;
                                extendedCount++;
                            }
                        }

                        // Extend end if close enough
                        if (distToEnd < maxExtend && distToEnd > cl.Length * 0.1)
                        {
                            double projT = GeoAlgo.ProjectPointOnSegment(intersection, cl.AsSegment, out _);
                            if (projT > 1)
                            {
                                cl.EndPt = intersection;
                                extendedCount++;
                            }
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
            // Remove inactive
            _centerlines = _centerlines.Where(cl => cl.IsActive).ToList();

            // Remove duplicates by UniqueID
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
                    // Merge source handles
                    uniqueDict[cl.UniqueID].SourceHandles.AddRange(cl.SourceHandles);
                }
            }

            _centerlines = uniqueDict.Values.ToList();

            // Remove very short centerlines (< 50mm)
            _centerlines = _centerlines.Where(cl => cl.Length >= 50).ToList();
        }

        #endregion
    }
}