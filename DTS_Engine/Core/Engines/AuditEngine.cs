using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using DTS_Engine.Core.Utils;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DTS_Engine.Core.Engines
{

    /// <summary>
    /// Engine kiểm toán tải trọng SAP2000 (Phiên bản v2.4 - Smart Grid & Union) - enhanced with story grouping & word wrap
    /// - Sử dụng NetTopologySuite để gộp (Union) hình học, tránh cắt nát phần tử.
    /// - Định vị trục thông minh dựa trên BoundingBox (Range).
    /// - Hỗ trợ đa đơn vị hiển thị qua UnitManager.
    /// - Added: GroupLoadsByStory (sàn đỡ tường trên) và Word Wrap trong GenerateTextReport.
    /// </summary>
    public class AuditEngine
    {
        #region Constants & Fields

        private const double STORY_TOLERANCE = 500.0; // mm
        private const double GRID_SNAP_TOLERANCE = 250.0; // mm (increased slightly for better snapping)
        private const double MIN_AREA_THRESHOLD_M2 = 0.05;

        // Cache grids tách biệt X và Y để tìm kiếm nhanh
        private List<SapUtils.GridLineRecord> _xGrids;
        private List<SapUtils.GridLineRecord> _yGrids;
        private List<SapUtils.GridStoryItem> _stories;

        // Geometry Caches
        private Dictionary<string, SapFrame> _frameGeometryCache;
        private Dictionary<string, SapArea> _areaGeometryCache;

        // NTS Factory
        private GeometryFactory _geometryFactory;

        #endregion

        #region Constructor

        public AuditEngine()
        {
            _geometryFactory = new GeometryFactory();
            _frameGeometryCache = new Dictionary<string, SapFrame>();
            _areaGeometryCache = new Dictionary<string, SapArea>();

            if (SapUtils.IsConnected)
            {
                var allGrids = SapUtils.GetGridLines();
                _xGrids = allGrids.Where(g => g.Orientation == "X").OrderBy(g => g.Coordinate).ToList();
                _yGrids = allGrids.Where(g => g.Orientation == "Y").OrderBy(g => g.Coordinate).ToList();
                _stories = SapUtils.GetStories();
            }
            else
            {
                _xGrids = new List<SapUtils.GridLineRecord>();
                _yGrids = new List<SapUtils.GridLineRecord>();
                _stories = new List<SapUtils.GridStoryItem>();
            }
        }

        #endregion

        #region Main Audit Workflows

        /// <summary>
        /// Chạy kiểm toán cho danh sách Load Patterns
        /// </summary>
        public List<AuditReport> RunAudit(string loadPatterns)
        {
            var reports = new List<AuditReport>();
            if (string.IsNullOrEmpty(loadPatterns)) return reports;

            var patterns = loadPatterns.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(p => p.Trim().ToUpper())
                                       .Distinct()
                                       .ToList();

            // Cache geometry một lần dùng chung
            CacheGeometry();

            foreach (var pattern in patterns)
            {
                var report = RunSingleAudit(pattern);
                if (report != null) reports.Add(report);
            }

            return reports;
        }

        /// <summary>
        /// Chạy kiểm toán cho một Load Pattern cụ thể
        /// Uses SapDatabaseReader to get robust direction resolution and global components
        /// </summary>
        public AuditReport RunSingleAudit(string loadPattern)
        {
            // Refresh geometry cache
            CacheGeometry();

            var report = new AuditReport
            {
                LoadPattern = loadPattern,
                AuditDate = DateTime.Now,
                ModelName = SapUtils.GetModelName(),
                UnitInfo = UnitManager.Info.ToString()
            };

            var dbReader = new SapDatabaseReader(SapUtils.GetModel());
            var allLoads = new List<RawSapLoad>();

            // Read loads using new reader (provides DirectionX/Y/Z)
            allLoads.AddRange(dbReader.ReadFrameDistributedLoads(loadPattern));
            allLoads.AddRange(dbReader.ReadAreaUniformLoads(loadPattern));
            allLoads.AddRange(dbReader.ReadAreaUniformToFrameLoads(loadPattern));
            allLoads.AddRange(dbReader.ReadJointLoads(loadPattern));

            // Keep legacy frame point loads (already reliable)
            allLoads.AddRange(SapUtils.GetAllFramePointLoads(loadPattern));

            // Final strict filtering
            allLoads = allLoads.Where(l => string.Equals(l.LoadPattern, loadPattern, StringComparison.OrdinalIgnoreCase)).ToList();

            if (allLoads.Count == 0) return report;

            // Group and process by story
            var storyBuckets = GroupLoadsByStory(allLoads);
            foreach (var bucket in storyBuckets.OrderByDescending(b => b.Elevation))
            {
                var storyGroup = ProcessStory(bucket.StoryName, bucket.Elevation, bucket.Loads);
                if (storyGroup.LoadTypes.Count > 0)
                    report.Stories.Add(storyGroup);
            }

            // Smart Base Reaction: decide direction using summed global components
            if (CheckIfLateralLoad(allLoads))
            {
                double totalX = allLoads.Sum(l => Math.Abs(l.DirectionX));
                double totalY = allLoads.Sum(l => Math.Abs(l.DirectionY));
                string dominantDir = totalX > totalY ? "X" : "Y";
                report.SapBaseReaction = dbReader.ReadBaseReaction(loadPattern, dominantDir);
            }
            else
            {
                report.SapBaseReaction = dbReader.ReadBaseReaction(loadPattern, "Z");
            }

            if (Math.Abs(report.SapBaseReaction) < 0.001) report.IsAnalyzed = false;

            return report;
        }

        #endregion

        #region Grouping Loads by Story (New Logic)

        // Temp bucket class
        private class TempStoryBucket
        {
            public string StoryName { get; set; }
            public double Elevation { get; set; }
            public List<RawSapLoad> Loads { get; set; } = new List<RawSapLoad>();
        }

        /// <summary>
        /// Group loads by story using logic:
        /// Each load belongs to the highest story where load.Z >= story.Elevation - 50mm tolerance.
        /// If no story matched (below lowest), assign to lowest story.
        /// </summary>
        private List<TempStoryBucket> GroupLoadsByStory(List<RawSapLoad> loads)
        {
            var buckets = new List<TempStoryBucket>();

            // Use story Elevation information from _stories (IsElevation flag)
            var activeStories = _stories.Where(s => s.IsElevation).OrderBy(s => s.Elevation).ToList();

            // If no stories found, create a base bucket at Z=0
            if (activeStories.Count == 0)
            {
                buckets.Add(new TempStoryBucket { StoryName = "Base", Elevation = 0.0 });
            }
            else
            {
                foreach (var s in activeStories)
                    buckets.Add(new TempStoryBucket { StoryName = s.Name, Elevation = s.Elevation });
            }

            // Sort descending for assignment (highest story first)
            var sortedBuckets = buckets.OrderByDescending(b => b.Elevation).ToList();

            foreach (var load in loads)
            {
                var z = load.ElementZ;
                bool assigned = false;

                foreach (var bucket in sortedBuckets)
                {
                    // tolerance 50mm to attach loads sitting exactly on slab
                    if (z >= bucket.Elevation - 50.0)
                    {
                        bucket.Loads.Add(load);
                        assigned = true;
                        break;
                    }
                }

                // if still not assigned, add to lowest bucket
                if (!assigned && sortedBuckets.Count > 0)
                {
                    sortedBuckets.Last().Loads.Add(load);
                }
            }

            return sortedBuckets.Where(b => b.Loads.Count > 0).ToList();
        }

        #endregion

        #region Core Processing Logic

        private AuditStoryGroup ProcessStory(string storyName, double elevation, List<RawSapLoad> loads)
        {
            var storyGroup = new AuditStoryGroup
            {
                StoryName = storyName,
                Elevation = elevation
            };

            // Gom nhóm theo loại tải (Area, Frame, Point)
            var loadTypeGroups = loads.GroupBy(l => l.LoadType);

            foreach (var typeGroup in loadTypeGroups)
            {
                var typeResult = ProcessLoadType(typeGroup.Key, typeGroup.ToList());
                if (typeResult.Entries != null && typeResult.Entries.Count > 0)
                    storyGroup.LoadTypes.Add(typeResult);
            }

            return storyGroup;
        }

        private AuditLoadTypeGroup ProcessLoadType(string loadType, List<RawSapLoad> loads)
        {
            var typeGroup = new AuditLoadTypeGroup
            {
                LoadTypeName = GetLoadTypeDisplayName(loadType),
                Entries = new List<AuditEntry>(),
                ValueGroups = new List<AuditValueGroup>()
            };

            // Group internally for processing but produce a flat list of entries
            var valueGroups = loads.GroupBy(l => new { Val = Math.Round(l.Value1, 3), Dir = l.Direction });

            foreach (var group in valueGroups.OrderByDescending(g => g.Key.Val))
            {
                double val = group.Key.Val;
                string dir = group.Key.Dir;
                var subLoads = group.ToList();

                if (loadType.Contains("Area"))
                {
                    ProcessAreaLoads(subLoads, val, dir, typeGroup.Entries);
                }
                else if (loadType.Contains("Frame"))
                {
                    ProcessFrameLoads(subLoads, val, dir, typeGroup.Entries);
                }
                else
                {
                    ProcessPointLoads(subLoads, val, dir, typeGroup.Entries);
                }
            }

            // Sort entries
            typeGroup.Entries = typeGroup.Entries.OrderBy(e => e.GridLocation).ToList();

            return typeGroup;
        }

        // --- XỬ LÝ TẢI DIỆN TÍCH (AREA - SMART GEOMETRY RECOGNITION & DECOMPOSITION) ---
        private void ProcessAreaLoads(List<RawSapLoad> loads, double loadVal, string dir, List<AuditEntry> targetList)
        {
            var polygons = new List<(Polygon Poly, string Name)>();
            foreach (var load in loads)
            {
                if (_areaGeometryCache.TryGetValue(load.ElementName, out var area))
                {
                    var poly = CreateNtsPolygon(area.BoundaryPoints);
                    if (poly != null && poly.IsValid)
                        polygons.Add((poly, load.ElementName));
                }
            }
            if (polygons.Count == 0) return;

            // UNION: Gộp các tấm liền kề
            var geometries = polygons.Select(p => p.Poly).Cast<Geometry>().ToList();
            Geometry unionResult;
            try
            {
                unionResult = UnaryUnionOp.Union(geometries);
            }
            catch
            {
                unionResult = _geometryFactory.CreateGeometryCollection(geometries.ToArray());
            }

            // Duyệt và phân tích hình học
            for (int i = 0; i < unionResult.NumGeometries; i++)
            {
                var geom = unionResult.GetGeometryN(i);
                double thresholdMm2 = 0.05 * 1e6;
                if (geom.Area < thresholdMm2) continue;

                var strategy = AnalyzeShapeStrategy(geom);
                double areaM2 = geom.Area / 1.0e6;
                double force = areaM2 * loadVal;
                string location = GetGridRangeDescription(geom.EnvelopeInternal);

                targetList.Add(new AuditEntry
                {
                    GridLocation = location,
                    Explanation = strategy.Formula,
                    Quantity = areaM2,
                    QuantityUnit = "m²",
                    UnitLoad = loadVal,
                    UnitLoadString = $"{loadVal:0.00} {UnitManager.Info.ForceUnit}/m²",
                    TotalForce = force,
                    Direction = dir,
                    ElementList = new List<string>()
                });
            }
        }

        // --- SMART SHAPE ANALYSIS ---

        private struct DecompositionResult
        {
            public string Formula;
            public int ComplexityScore;
            public bool IsExact;
        }

        #region Ultimate Decomposition Engine (The Competition)

        public static class PolygonDecomposer
        {
            /// <summary>
            /// PHƯƠNG THỨC CHÍNH: Chạy đua giữa các thuật toán và chọn người chiến thắng.
            /// Tiêu chí thắng: Số lượng hình chữ nhật ít nhất (Công thức ngắn nhất).
            /// Nếu bằng nhau: Ưu tiên thuật toán tạo ra khối chính (Main Chunk) lớn nhất.
            /// </summary>
            public static List<Envelope> DecomposeOptimal(Geometry poly)
            {
                if (poly == null || poly.IsEmpty) return new List<Envelope>();

                // --- ĐỘI 1: Matrix Strategy (Tìm khối lớn nhất lặp lại) ---
                var resMatrix = RunMatrixStrategy(poly);

                // --- ĐỘI 2: Slicing Strategy (Quét trục X và Y - Dual Axis) ---
                var resSliceX = RunSlicingStrategy(poly, isVertical: false); // Cắt ngang
                var resSliceY = RunSlicingStrategy(poly, isVertical: true);  // Cắt dọc

                // --- SO SÁNH & CHỌN ---
                // Tạo danh sách các ứng viên
                var candidates = new List<List<Envelope>> { resMatrix, resSliceX, resSliceY };

                // 1. Lọc lấy những phương án có ít hình nhất (Complexity thấp nhất)
                int minCount = candidates.Min(c => c.Count);
                var bestCandidates = candidates.Where(c => c.Count == minCount).ToList();

                // 2. Nếu chỉ có 1 ứng viên tốt nhất -> Chọn luôn
                if (bestCandidates.Count == 1) return bestCandidates[0];

                // 3. Nếu hòa nhau về số lượng -> Chọn phương án có hình chữ nhật "chủ đạo" lớn nhất
                // (Tư duy kỹ sư: Thích nhìn thấy một con số to đùng cộng với mấy số lẻ)
                return bestCandidates.OrderByDescending(c => c.Max(r => r.Area)).First();
            }

            #region Strategy A: Matrix (Iterative Largest Rectangle)

            private static List<Envelope> RunMatrixStrategy(Geometry poly)
            {
                var coords = poly.Coordinates;
                var xUnique = coords.Select(c => c.X).Distinct().OrderBy(x => x).ToList();
                var yUnique = coords.Select(c => c.Y).Distinct().OrderBy(y => y).ToList();

                int cols = xUnique.Count - 1;
                int rows = yUnique.Count - 1;
                if (cols <= 0 || rows <= 0) return new List<Envelope> { poly.EnvelopeInternal };

                bool[,] matrix = new bool[rows, cols];
                var factory = poly.Factory;

                // Xây dựng ma trận
                for (int r = 0; r < rows; r++)
                {
                    double cy = (yUnique[r] + yUnique[r + 1]) / 2.0;
                    for (int c = 0; c < cols; c++)
                    {
                        double cx = (xUnique[c] + xUnique[c + 1]) / 2.0;
                        if (poly.Intersects(factory.CreatePoint(new Coordinate(cx, cy))))
                            matrix[r, c] = true;
                    }
                }

                var result = new List<Envelope>();
                // Clone ma trận để không làm hỏng dữ liệu nếu cần dùng lại (dù ở đây local var)
                var workMatrix = (bool[,])matrix.Clone();

                while (true)
                {
                    var maxRect = FindLargestRect(workMatrix, rows, cols, xUnique, yUnique);
                    if (maxRect.Area <= 0.0001) break;

                    result.Add(maxRect.Env);
                    for (int r = maxRect.RowStart; r < maxRect.RowEnd; r++)
                        for (int c = maxRect.ColStart; c < maxRect.ColEnd; c++)
                            workMatrix[r, c] = false;
                }
                return result;
            }

            private struct MatrixRect { public Envelope Env; public double Area; public int RowStart, RowEnd, ColStart, ColEnd; }

            private static MatrixRect FindLargestRect(bool[,] matrix, int rows, int cols, List<double> xCoords, List<double> yCoords)
            {
                int[] heights = new int[cols];
                double maxArea = -1;
                MatrixRect best = new MatrixRect { Area = 0 };

                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++) heights[c] = matrix[r, c] ? heights[c] + 1 : 0;

                    for (int c = 0; c < cols; c++)
                    {
                        if (heights[c] == 0) continue;
                        int h = heights[c];
                        int rStart = r - h + 1;
                        double realH = yCoords[r + 1] - yCoords[rStart];

                        int cLeft = c; while (cLeft > 0 && heights[cLeft - 1] >= h) cLeft--;
                        int cRight = c; while (cRight < cols - 1 && heights[cRight + 1] >= h) cRight++;

                        double realW = xCoords[cRight + 1] - xCoords[cLeft];
                        double area = realW * realH;

                        if (area > maxArea)
                        {
                            maxArea = area;
                            best = new MatrixRect { Area = area, Env = new Envelope(xCoords[cLeft], xCoords[cRight + 1], yCoords[rStart], yCoords[r + 1]), RowStart = rStart, RowEnd = r + 1, ColStart = cLeft, ColEnd = cRight + 1 };
                        }
                    }
                }
                return best;
            }
            #endregion

            #region Strategy B: Slicing (Dual-Axis Sweep & Merge)

            private static List<Envelope> RunSlicingStrategy(Geometry poly, bool isVertical)
            {
                var coords = poly.Coordinates;
                var splitCoords = isVertical
                    ? coords.Select(c => c.X).Distinct().OrderBy(x => x).ToList()
                    : coords.Select(c => c.Y).Distinct().OrderBy(y => y).ToList();

                var strips = new List<Envelope>();
                var factory = poly.Factory;
                var env = poly.EnvelopeInternal;

                // 1. Cắt lát (Slicing)
                for (int i = 0; i < splitCoords.Count - 1; i++)
                {
                    double c1 = splitCoords[i];
                    double c2 = splitCoords[i + 1];

                    // Tạo dải cắt vô tận
                    Envelope stripEnv = isVertical
                        ? new Envelope(c1, c2, env.MinY, env.MaxY)
                        : new Envelope(env.MinX, env.MaxX, c1, c2);

                    var intersection = poly.Intersection(factory.ToGeometry(stripEnv));
                    for (int k = 0; k < intersection.NumGeometries; k++)
                    {
                        var g = intersection.GetGeometryN(k);
                        if (g.Area > 0.001) strips.Add(g.EnvelopeInternal);
                    }
                }

                // 2. Gộp lại (Merging) theo chiều ngược lại
                // Nếu cắt dọc (Vertical Slice) -> Gộp các cột liền kề có cùng chiều cao (Y)
                return MergeStrips(strips, mergeAlongX: isVertical);
            }

            private static List<Envelope> MergeStrips(List<Envelope> inputs, bool mergeAlongX)
            {
                if (inputs.Count == 0) return new List<Envelope>();

                // Group theo chiều cao/rộng cố định
                var groups = inputs.GroupBy(e => mergeAlongX
                    ? $"{e.MinY:F3}-{e.MaxY:F3}"  // Cùng chiều cao Y -> Gộp ngang X
                    : $"{e.MinX:F3}-{e.MaxX:F3}"); // Cùng chiều rộng X -> Gộp dọc Y

                var result = new List<Envelope>();

                foreach (var grp in groups)
                {
                    var sorted = mergeAlongX ? grp.OrderBy(e => e.MinX).ToList() : grp.OrderBy(e => e.MinY).ToList();
                    Envelope current = sorted[0];

                    for (int i = 1; i < sorted.Count; i++)
                    {
                        var next = sorted[i];
                        bool isAdjacent = mergeAlongX
                            ? Math.Abs(current.MaxX - next.MinX) < 2.0
                            : Math.Abs(current.MaxY - next.MinY) < 2.0;

                        if (isAdjacent)
                        {
                            current = new Envelope(
                                Math.Min(current.MinX, next.MinX), Math.Max(current.MaxX, next.MaxX),
                                Math.Min(current.MinY, next.MinY), Math.Max(current.MaxY, next.MaxY));
                        }
                        else
                        {
                            result.Add(current);
                            current = next;
                        }
                    }
                    result.Add(current);
                }
                return result;
            }
            #endregion
        }

        #endregion

        // --- CẬP NHẬT AnalyzeShapeStrategy: THÊM TRỌNG TÀI CHO CHIẾN LƯỢC TRỪ ---

        private DecompositionResult AnalyzeShapeStrategy(Geometry geom)
        {
            // 1. Check hình cơ bản (Tuyệt đối nhanh)
            if (IsRectangle(geom))
                return new DecompositionResult { Formula = FormatRect(geom.EnvelopeInternal), ComplexityScore = 1, IsExact = true };

            if (IsTriangle(geom))
                return new DecompositionResult { Formula = $"Tam giác({FormatRect(geom.EnvelopeInternal)})", ComplexityScore = 1, IsExact = true };

            // 2. Chạy thuật toán "Đấu thầu" cho chiến lược CỘNG (Additive)
            // Đây là nơi Matrix vs Slicing đấu nhau
            var optimalRects = PolygonDecomposer.DecomposeOptimal(geom);

            // Sắp xếp kết quả: Hình to nhất lên đầu (Tư duy kỹ sư)
            optimalRects = optimalRects.OrderByDescending(r => r.Area).ToList();

            string addFormula = string.Join(" + ", optimalRects.Select(r => FormatRect(r)));
            int addScore = optimalRects.Count;

            // 3. Chạy chiến lược TRỪ (Subtractive) nếu tiềm năng
            // Tiêu chuẩn: Hình đặc chiếm > 60% hình bao
            var env = geom.EnvelopeInternal;
            if (geom.Area / env.Area > 0.6)
            {
                var subRes = EvaluateSubtractive(geom);

                // QUYẾT ĐỊNH CUỐI CÙNG:
                // Chỉ chọn TRỪ nếu nó thực sự gọn hơn CỘNG (Score nhỏ hơn hẳn)
                // Ví dụ: Cộng ra 5 hình, Trừ ra 2 hình -> Chọn Trừ.
                // Nếu Cộng ra 3 hình, Trừ ra 2 hình -> Vẫn có thể chọn Cộng vì nó tường minh hơn,
                // trừ khi Trừ là (1 BoundingBox - 1 Lỗ) rất đẹp.

                if (subRes.IsExact && subRes.ComplexityScore < addScore)
                {
                    return subRes;
                }
            }

            // Mặc định chọn phương án CỘNG tối ưu nhất
            return new DecompositionResult
            {
                Formula = addFormula,
                ComplexityScore = addScore,
                IsExact = true
            };
        }

        // Subtractive: Envelope - rectangular holes
        private DecompositionResult EvaluateSubtractive(Geometry geom)
        {
            var env = geom.EnvelopeInternal;
            var envGeom = _geometryFactory.ToGeometry(env);
            var voids = envGeom.Difference(geom);

            if (voids.IsEmpty)
                return new DecompositionResult
                {
                    Formula = FormatRect(env),
                    ComplexityScore = 1,
                    IsExact = true
                };

            var voidTerms = new List<string>();
            bool cleanVoids = true;
            double thresholdMm2 = MIN_AREA_THRESHOLD_M2 * 1e6;

            for (int i = 0; i < voids.NumGeometries; i++)
            {
                var v = voids.GetGeometryN(i);
                if (v.Area < thresholdMm2) continue;
                if (IsRectangle(v)) voidTerms.Add(FormatRect(v.EnvelopeInternal));
                else if (IsTriangle(v)) voidTerms.Add($"Tam giác({FormatRect(v.EnvelopeInternal)})");
                else { cleanVoids = false; break; }
            }

            if (cleanVoids)
            {
                string formula = FormatRect(env);
                foreach (var vTerm in voidTerms) formula += $" - {vTerm}";
                return new DecompositionResult
                {
                    Formula = formula,
                    ComplexityScore = 1 + voidTerms.Count,
                    IsExact = true
                };
            }

            return new DecompositionResult
            {
                Formula = "Complex",
                ComplexityScore = 999,
                IsExact = false
            };
        }

        // Rectangle detection: area ≈ envelope area (within 1%)
        private bool IsRectangle(Geometry g)
        {
            var env = g.EnvelopeInternal;
            return Math.Abs(g.Area - env.Area) < (g.Area * 0.01);
        }

        // Triangle detection: NTS Polygon with 4 points (A,B,C,A)
        private bool IsTriangle(Geometry g)
        {
            return g is Polygon p && p.NumPoints == 4;
        }

        // Format rectangle dimensions (m x m)
        private string FormatRect(Envelope env)
        {
            return $"{env.Width / 1000.0:0.##}x{env.Height / 1000.0:0.##}";
        }

        // --- XỬ LÝ TẢI THANH (FRAME) - SMART GROUPING THEOREM ---
        // Gom nhóm theo trục chính (Primary Grid) thay vì union hình học để tránh cắt nát
        private void ProcessFrameLoads(List<RawSapLoad> loads, double loadVal, string dir, List<AuditEntry> targetList)
        {
            var frameItems = new List<FrameAuditItem>();

            foreach (var load in loads)
            {
                if (!_frameGeometryCache.TryGetValue(load.ElementName, out var frame)) continue;

                string primaryGrid = DeterminePrimaryGrid(frame);

                frameItems.Add(new FrameAuditItem
                {
                    Load = load,
                    Frame = frame,
                    PrimaryGrid = primaryGrid,
                    Length = frame.Length2D / 1000.0
                });
            }

            if (frameItems.Count == 0) return;

            var groups = frameItems.GroupBy(f => f.PrimaryGrid);

            foreach (var grp in groups)
            {
                string gridName = grp.Key;
                double totalLength = grp.Sum(f => f.Length);
                double totalForce = totalLength * loadVal;

                string rangeDesc = DetermineCrossAxisRange(grp.ToList());

                string location = gridName;
                string explanation = string.IsNullOrEmpty(rangeDesc)
                    ? $"L = {totalLength:0.00}m"
                    : $"{rangeDesc} (L={totalLength:0.00}m)";

                var elementNames = grp.Select(f => f.Load.ElementName).Distinct().ToList();

                targetList.Add(new AuditEntry
                {
                    GridLocation = location,
                    Explanation = explanation,
                    Quantity = totalLength,
                    QuantityUnit = "m",
                    UnitLoad = loadVal,
                    UnitLoadString = $"{loadVal:0.00} {UnitManager.Info.ForceUnit}/m",
                    TotalForce = totalForce,
                    Direction = dir,
                    ElementList = elementNames
                });
            }
        }

        private class FrameAuditItem
        {
            public RawSapLoad Load;
            public SapFrame Frame;
            public string PrimaryGrid;
            public double Length;
        }

        /// <summary>
        /// Xác định thanh nằm trên trục nào (Trục A, Trục 1, hoặc Xiên)
        /// </summary>
        private string DeterminePrimaryGrid(SapFrame frame)
        {
            double angle = Math.Abs(frame.Angle);
            while (angle > Math.PI) angle -= Math.PI;

            bool isHorizontal = (angle < 0.1 || Math.Abs(angle - Math.PI) < 0.1);
            bool isVertical = (Math.Abs(angle - Math.PI / 2) < 0.1);

            Point2D mid = frame.Midpoint;

            if (isHorizontal)
            {
                string gridY = FindAxisRange(mid.Y, mid.Y, _yGrids, true);
                return $"Trục {gridY}";
            }
            else if (isVertical)
            {
                string gridX = FindAxisRange(mid.X, mid.X, _xGrids, true);
                return $"Trục {gridX}";
            }
            else
            {
                string gridX = FindAxisRange(mid.X, mid.X, _xGrids, true);
                string gridY = FindAxisRange(mid.Y, mid.Y, _yGrids, true);
                return $"Xiên {gridX}-{gridY}";
            }
        }

        /// <summary>
        /// Xác định phạm vi quét của nhóm dầm (VD: Từ trục 1 đến trục 5)
        /// </summary>
        private string DetermineCrossAxisRange(List<FrameAuditItem> items)
        {
            if (items == null || items.Count == 0) return string.Empty;

            var sample = items[0].Frame;
            double angle = Math.Abs(sample.Angle);
            while (angle > Math.PI) angle -= Math.PI;
            bool isHorizontal = (angle < 0.1 || Math.Abs(angle - Math.PI) < 0.1);

            double minVal = double.MaxValue;
            double maxVal = double.MinValue;

            foreach (var item in items)
            {
                if (isHorizontal)
                {
                    minVal = Math.Min(minVal, Math.Min(item.Frame.StartPt.X, item.Frame.EndPt.X));
                    maxVal = Math.Max(maxVal, Math.Max(item.Frame.StartPt.X, item.Frame.EndPt.X));
                }
                else
                {
                    minVal = Math.Min(minVal, Math.Min(item.Frame.StartPt.Y, item.Frame.EndPt.Y));
                    maxVal = Math.Max(maxVal, Math.Max(item.Frame.StartPt.Y, item.Frame.EndPt.Y));
                }
            }

            var crossGrids = isHorizontal ? _xGrids : _yGrids;
            string startGrid = FindAxisRange(minVal, minVal, crossGrids, true);
            string endGrid = FindAxisRange(maxVal, maxVal, crossGrids, true);

            if (startGrid == endGrid) return $"tại {startGrid}";

            string cleanStart = startGrid.Split('(')[0];
            string cleanEnd = endGrid.Split('(')[0];
            return $"{cleanStart}-{cleanEnd}";
        }

        // --- XỬ LÝ TẢI ĐIỂM (POINT) ---
        // Cập nhật lại logic tìm trục cho điểm để fix lỗi "?"
        private void ProcessPointLoads(List<RawSapLoad> loads, double loadVal, string dir, List<AuditEntry> targetList)
        {
            var allPoints = SapUtils.GetAllPoints();

            foreach (var load in loads)
            {
                var ptCoord = allPoints.FirstOrDefault(p => p.Name == load.ElementName);
                if (ptCoord == null) continue;

                string loc = GetGridLocationForPoint(ptCoord);

                targetList.Add(new AuditEntry
                {
                    GridLocation = loc,
                    Explanation = load.ElementName,
                    Quantity = 1,
                    QuantityUnit = "ea",
                    UnitLoad = loadVal,
                    UnitLoadString = $"{loadVal:0.00} {UnitManager.Info.ForceUnit}",
                    TotalForce = loadVal,
                    Direction = dir,
                    ElementList = new List<string> { load.ElementName }
                });
            }
        }

        // Helper class nội bộ để xử lý gom nhóm
        private class PointAuditItem
        {
            public RawSapLoad Load { get; set; }
            public String XName { get; set; }
            public String YName { get; set; }
            public double RawX { get; set; }
            public double RawY { get; set; }
        }

        #endregion

        #region Smart Grid Detection Logic

        /// <summary>
        /// Tạo mô tả trục dạng Range (VD: Trục 1-5 / A-B) dựa trên Bounding Box
        /// </summary>
        private string GetGridRangeDescription(Envelope env)
        {
            // Tìm khoảng trục X
            string xRange = FindAxisRange(env.MinX, env.MaxX, _xGrids);
            // Tìm khoảng trục Y
            string yRange = FindAxisRange(env.MinY, env.MaxY, _yGrids);

            if (string.IsNullOrEmpty(xRange) && string.IsNullOrEmpty(yRange)) return "No Grid";
            if (string.IsNullOrEmpty(xRange)) return $"Trục {yRange}";
            if (string.IsNullOrEmpty(yRange)) return $"Trục {xRange}";

            return $"Trục {xRange} x {yRange}";
        }

        // Cập nhật hàm tìm trục để hỗ trợ snap single point tốt hơn
        private string FindAxisRange(double minVal, double maxVal, List<SapUtils.GridLineRecord> grids, bool isPoint = false)
        {
            if (grids == null || grids.Count == 0) return "?";

            var startGrid = grids.OrderBy(g => Math.Abs(g.Coordinate - minVal)).First();
            double startDiff = minVal - startGrid.Coordinate;

            if (isPoint || Math.Abs(maxVal - minVal) < GRID_SNAP_TOLERANCE)
            {
                // Format: A(+1.2m)
                return FormatGridWithOffset(startGrid.Name, startDiff);
            }

            var endGrid = grids.OrderBy(g => Math.Abs(g.Coordinate - maxVal)).First();
            double endDiff = maxVal - endGrid.Coordinate;

            if (startGrid.Name == endGrid.Name) return FormatGridWithOffset(startGrid.Name, startDiff);

            return $"{FormatGridWithOffset(startGrid.Name, startDiff)}-{FormatGridWithOffset(endGrid.Name, endDiff)}";
        }

        private string FormatGridWithOffset(string name, double offsetMm)
        {
            if (Math.Abs(offsetMm) < GRID_SNAP_TOLERANCE) return name;
            double offsetM = offsetMm / 1000.0;
            return $"{name}({offsetM:+0.#;-0.#}m)";
        }

        private string GetGridLocationForPoint(SapUtils.SapPoint pt)
        {
            string x = FindAxisRange(pt.X, pt.X, _xGrids, true);
            string y = FindAxisRange(pt.Y, pt.Y, _yGrids, true);
            return $"{x}/{y}";
        }

        #endregion

        #region Geometry Helpers

        private void CacheGeometry()
        {
            _frameGeometryCache.Clear();
            _areaGeometryCache.Clear();

            var frames = SapUtils.GetAllFramesGeometry();
            foreach (var f in frames) _frameGeometryCache[f.Name] = f;

            var areas = SapUtils.GetAllAreasGeometry();
            foreach (var a in areas) _areaGeometryCache[a.Name] = a;
        }

        private Polygon CreateNtsPolygon(List<Point2D> pts)
        {
            if (pts == null || pts.Count < 3) return null;
            try
            {
                var coords = pts.Select(p => new Coordinate(p.X, p.Y)).ToList();
                if (!pts[0].Equals(pts.Last())) coords.Add(new Coordinate(pts[0].X, pts[0].Y));
                var ring = _geometryFactory.CreateLinearRing(coords.ToArray());
                return _geometryFactory.CreatePolygon(ring);
            }
            catch { return null; }
        }

        private LineString CreateNtsLineString(Point2D p1, Point2D p2)
        {
            try
            {
                var coords = new[] { new Coordinate(p1.X, p1.Y), new Coordinate(p2.X, p2.Y) };
                return _geometryFactory.CreateLineString(coords);
            }
            catch { return null; }
        }

        private Dictionary<string, double> DetermineStoryElevations(List<RawSapLoad> loads)
        {
            var result = new Dictionary<string, double>();
            var allZ = loads.Select(l => l.ElementZ).Distinct().OrderByDescending(z => z).ToList();
            var zGroups = new List<List<double>>();

            foreach (var z in allZ)
            {
                var existingGroup = zGroups.FirstOrDefault(g => Math.Abs(g.Average() - z) <= STORY_TOLERANCE);
                if (existingGroup != null) existingGroup.Add(z);
                else zGroups.Add(new List<double> { z });
            }

            var stories = _stories.Where(s => s.IsElevation).OrderByDescending(s => s.Elevation).ToList();

            foreach (var group in zGroups)
            {
                double avgZ = group.Average();
                var match = stories.FirstOrDefault(s => Math.Abs(s.Elevation - avgZ) <= STORY_TOLERANCE);
                string name = match != null ? match.Name : $"Z={avgZ / 1000.0:0.0}m";

                if (!result.ContainsKey(name)) result[name] = avgZ;
            }
            return result;
        }

        public bool CheckIfLateralLoad(List<RawSapLoad> loads)
        {
            if (loads == null || loads.Count == 0) return false;

            // Count anything that indicates X/Y direction.
            // Some sources use "X", "Y" while others use "Global X", "X Projected", etc.
            int lateralCount = loads.Count(l =>
            {
                if (string.IsNullOrEmpty(l.Direction)) return false;
                var d = l.Direction.ToUpperInvariant();

                // Ignore obvious Z/gravity-only directions
                if (d.Contains("Z") && !d.Contains("X") && !d.Contains("Y")) return false;

                // Treat any direction string containing 'X' or 'Y' as lateral
                if (d.Contains("X") || d.Contains("Y")) return true;

                // Some tables may use words like 'LATERAL' or 'SHEAR X' etc.
                if (d.Contains("LATERAL") || d.Contains("SHEAR")) return true;

                return false;
            });

            return lateralCount > loads.Count * 0.5; // > 50% là tải ngang
        }

        private string GetLoadTypeDisplayName(string loadType)
        {
            if (loadType.Contains("Area")) return "SÀN - AREA LOAD";
            if (loadType.Contains("Frame")) return "DẦM/CỘT - FRAME LOAD";
            if (loadType.Contains("Point")) return "NÚT - POINT LOAD";
            return loadType.ToUpper();
        }

        #endregion

        #region Report Generation (With Unit Conversion & Word Wrap)

        /// <summary>
        /// Generate text report with bilingual support (English/Vietnamese) and word-wrap for Location & Explanation
        /// </summary>
        /// <param name="report">Audit report data</param>
        /// <param name="targetUnit">Target force unit (kN, Ton, kgf, lb)</param>
        /// <param name="language">Report language: "English" or "Vietnamese" (default: English)</param>
        /// <returns>Formatted text report</returns>
        public string GenerateTextReport(AuditReport report, string targetUnit = "kN", string language = "English")
        {
            var sb = new StringBuilder();
            double forceFactor = 1.0;

            if (string.IsNullOrWhiteSpace(targetUnit)) targetUnit = UnitManager.Info.ForceUnit;
            if (targetUnit.Equals("Ton", StringComparison.OrdinalIgnoreCase)) forceFactor = 1.0 / 9.81;
            else if (targetUnit.Equals("kgf", StringComparison.OrdinalIgnoreCase)) forceFactor = 101.97;
            else if (targetUnit.Equals("lb", StringComparison.OrdinalIgnoreCase)) forceFactor = 224.8;
            else { targetUnit = "kN"; forceFactor = 1.0; }

            bool isVietnamese = language.Equals("Vietnamese", StringComparison.OrdinalIgnoreCase);

            // Header
            sb.AppendLine("===========================================================================================================");
            sb.AppendLine(isVietnamese ? "   BÁO CÁO KIỂM TOÁN TẢI TRỌNG (DTS ENGINE)" : "   SAP2000 LOAD AUDIT REPORT (DTS ENGINE)");
            sb.AppendLine($"   {(isVietnamese ? "Model" : "Model")}    : {report.ModelName}");
            sb.AppendLine($"   {(isVietnamese ? "Load Case" : "Load Case")}: {report.LoadPattern}  |  {(isVietnamese ? "Đơn vị" : "Unit")}: {targetUnit}");
            sb.AppendLine($"   {(isVietnamese ? "Ngày" : "Date")}     : {report.AuditDate:yyyy-MM-dd HH:mm}");
            sb.AppendLine("===========================================================================================================");
            sb.AppendLine();

            foreach (var story in report.Stories)
            {
                double elevM = UnitManager.ToMeter(story.Elevation);
                double storyTotal = story.TotalForce * forceFactor;

                sb.AppendLine("-----------------------------------------------------------------------------------------------------------");
                string storyLabel = isVietnamese ? "TẦNG" : "STORY";
                sb.AppendLine($" {storyLabel}: {story.StoryName} (Z = {elevM:+0.00;-0.00}m)          |   {(isVietnamese ? "TỔNG LỰC TẦNG" : "TOTAL STORY FORCE")}: {storyTotal,12:N2} {targetUnit}");
                sb.AppendLine("-----------------------------------------------------------------------------------------------------------");
                sb.AppendLine();

                foreach (var loadType in story.LoadTypes)
                {
                    // Section header by type
                    string typeHeader = TranslateLoadTypeName(loadType.LoadTypeName, isVietnamese);
                    if (typeHeader.Contains("AREA")) typeHeader = isVietnamese ? "[1] TẢI DIỆN (SÀN)" : "[1] AREA LOADS (SLAB)";
                    else if (typeHeader.Contains("FRAME")) typeHeader = isVietnamese ? "[2] TẢI THANH (DẦM/CỘT)" : "[2] FRAME LOADS (BEAM/COLUMN)";
                    else if (typeHeader.Contains("POINT")) typeHeader = isVietnamese ? "[3] TẢI ĐIỂM (NÚT)" : "[3] POINT LOADS (NODE)";

                    sb.AppendLine($" {typeHeader}");
                    // Optional notes
                    if (typeHeader.Contains("FRAME"))
                        sb.AppendLine(isVietnamese ? " * Ghi chú: Khối lượng (Qty) là tổng chiều dài L của các phần tử chịu tải." : " * Note: Qty represents total length (L) of loaded elements.");
                    if (typeHeader.Contains("POINT"))
                        sb.AppendLine(isVietnamese ? " * Ghi chú: Khối lượng (Qty) là số lượng điểm (No.) có tải." : " * Note: Qty represents count (No.) of loaded points.");

                    sb.AppendLine(" ---------------------------------------------------------------------------------------------------------");
                    if (isVietnamese)
                        sb.AppendLine($" | {"Vị Trí (Trục/Vùng)",-26} | {"Diễn Giải / Kích Thước (m)",-30} | {"Kh.Lượng",-10} | {"Giá Trị Tải",-12} | {"Tổng (" + targetUnit + ")",12} |");
                    else
                        sb.AppendLine($" | {"Location (Grid/Zone)",-26} | {"Calculation / Formula (m)",-30} | {"Qty",-10} | {"Unit Load",-12} | {"Total (" + targetUnit + ")",12} |");
                    sb.AppendLine(" ---------------------------------------------------------------------------------------------------------");

                    foreach (var entry in loadType.Entries)
                    {
                        double displayForce = entry.TotalForce * forceFactor;
                        double displayUnitLoad = entry.UnitLoad * forceFactor;

                        string unitStr = entry.UnitLoadString;
                        if (forceFactor != 1.0)
                        {
                            if (unitStr.Contains("/m²")) unitStr = $"{displayUnitLoad:0.00} {targetUnit}/m²";
                            else if (unitStr.Contains("/m")) unitStr = $"{displayUnitLoad:0.00} {targetUnit}/m";
                            else unitStr = $"{displayUnitLoad:0.00} {targetUnit}";
                        }

                        string loc = entry.GridLocation; // will be wrapped below
                        string desc = entry.Explanation;

                        string qtyStr = (entry.QuantityUnit?.ToLowerInvariant() ?? "").Contains("²")
                            ? $"{entry.Quantity,8:0.00} m²"
                            : (entry.QuantityUnit?.Equals("ea", StringComparison.OrdinalIgnoreCase) == true
                                ? $"{entry.Quantity,8:0} No."
                                : $"{entry.Quantity,8:0.00} m");

                        string forceStr = $"{displayForce:N2}";

                        // Word wrap both columns and print row-by-row
                        var locLines = SplitTextToWidth(loc, 26);
                        var descLines = SplitTextToWidth(desc, 30);

                        int maxLines = Math.Max(locLines.Count, descLines.Count);
                        if (maxLines == 0) maxLines = 1;

                        for (int i = 0; i < maxLines; i++)
                        {
                            string locPart = i < locLines.Count ? locLines[i] : "";
                            string descPart = i < descLines.Count ? descLines[i] : "";

                            string q = i == 0 ? qtyStr : "";
                            string u = i == 0 ? unitStr : "";
                            string t = i == 0 ? forceStr : "";

                            sb.AppendLine(string.Format(" | {0,-26} | {1,-30} | {2,-10} | {3,-12} | {4,12} |",
                                locPart, descPart, q, u, t));
                        }
                    }

                    sb.AppendLine(" ---------------------------------------------------------------------------------------------------------");
                    double typeTotal = loadType.TotalForce * forceFactor;
                    string subTotalLabel = isVietnamese ? "TỔNG NHÓM:" : "SUB-TOTAL:";
                    sb.AppendLine(string.Format(" {0,86} {1,12:N2}", subTotalLabel, typeTotal));
                    sb.AppendLine();
                }
            }

            // Summary & evaluation
            double totalCalc = report.TotalCalculatedForce * forceFactor;
            double baseReact = report.SapBaseReaction * forceFactor;
            double diff = totalCalc - Math.Abs(baseReact);

            sb.AppendLine("===========================================================================================================");
            sb.AppendLine(isVietnamese ? "   TỔNG HỢP & ĐÁNH GIÁ" : "   SUMMARY & EVALUATION");
            string calcLabel = isVietnamese ? "TỔNG CỘNG TÍNH TOÁN" : "TOTAL CALCULATED";
            sb.AppendLine($"   1. {calcLabel}:  {totalCalc,12:N2} {targetUnit}");

            if (report.IsAnalyzed)
            {
                string reactLabel = isVietnamese ? "PHẢN LỰC ĐÁY (SAP2000)" : "SAP2000 BASE REACTION";
                string diffLabel = isVietnamese ? "SAI LỆCH (DIFF)" : "DIFFERENCE (DIFF)";
                sb.AppendLine($"   2. {reactLabel}:  {baseReact,12:N2} {targetUnit}");
                sb.AppendLine($"   3. {diffLabel}:   {diff,12:N2} {targetUnit} ({report.DifferencePercent:0.00}%)");
                sb.AppendLine();
                if (Math.Abs(report.DifferencePercent) < 5.0)
                    sb.AppendLine(isVietnamese ? "   >>> KẾT LUẬN: ĐẠT YÊU CẦU (Sai số < 5%)" : "   >>> CONCLUSION: OK (Error < 5%)");
                else
                    sb.AppendLine(isVietnamese ? "   >>> KẾT LUẬN: CẦN KIỂM TRA LẠI (Sai số lớn)" : "   >>> CONCLUSION: REQUIRES REVIEW (Large error)");
            }
            else
            {
                string notAnalyzedLabel = isVietnamese ? "PHẢN LỰC ĐÁY (SAP2000): [CHƯA PHÂN TÍCH]" : "SAP2000 BASE REACTION: [NOT ANALYZED]";
                sb.AppendLine($"   2. {notAnalyzedLabel}");
                sb.AppendLine(isVietnamese ? "   >>> Vui lòng chạy Run Analysis trong SAP2000 để so sánh chính xác." :
                                      "   >>> Please run Analysis in SAP2000 for accurate comparison.");
            }
            sb.AppendLine("===========================================================================================================");

            return sb.ToString();
        }

        private List<string> SplitTextToWidth(string text, int width)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            string[] words = text.Split(' ');
            StringBuilder currentLine = new StringBuilder();

            foreach (var word in words)
            {
                if (currentLine.Length + word.Length + (currentLine.Length > 0 ? 1 : 0) > width)
                {
                    if (currentLine.Length > 0)
                    {
                        lines.Add(currentLine.ToString());
                        currentLine.Clear();
                    }

                    if (word.Length > width)
                    {
                        string remaining = word;
                        while (remaining.Length > width)
                        {
                            lines.Add(remaining.Substring(0, width));
                            remaining = remaining.Substring(width);
                        }
                        currentLine.Append(remaining);
                    }
                    else
                    {
                        currentLine.Append(word);
                    }
                }
                else
                {
                    if (currentLine.Length > 0) currentLine.Append(" ");
                    currentLine.Append(word);
                }
            }
            if (currentLine.Length > 0) lines.Add(currentLine.ToString());

            return lines;
        }

        /// <summary>
        /// Translate load type display name to target language
        /// </summary>
        private string TranslateLoadTypeName(string original, bool toVietnamese)
        {
            if (!toVietnamese)
            {
                // Vietnamese to English
                if (original.Contains("SÀN")) return "SLAB - AREA LOAD";
                if (original.Contains("DẦM") || original.Contains("CỘT")) return "BEAM/COLUMN - FRAME LOAD";
                if (original.Contains("NÚT")) return "NODE - POINT LOAD";
                return original;
            }

            // Already Vietnamese or default
            return original;
        }

        #endregion
    }
}