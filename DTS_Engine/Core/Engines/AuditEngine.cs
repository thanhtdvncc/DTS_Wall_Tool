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
    /// Engine kiểm toán tải trọng SAP2000 (Phiên bản v2.4 - Smart Grid & Union)
    /// - Sử dụng NetTopologySuite để gộp (Union) hình học, tránh cắt nát phần tử.
    /// - Định vị trục thông minh dựa trên BoundingBox (Range).
    /// - Hỗ trợ đa đơn vị hiển thị qua UnitManager.
    /// </summary>
    public class AuditEngine
    {
        #region Constants & Fields

        private const double STORY_TOLERANCE = 500.0; // mm
        private const double GRID_SNAP_TOLERANCE = 500.0; // mm
        // Minimum area to consider (m^2)
        private const double MIN_AREA_THRESHOLD_M2 = 0.05; // 0.05 m2

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
                // Phân loại Grid X và Y, sắp xếp tăng dần để binary search hoặc linear scan
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
        /// </summary>
        public AuditReport RunSingleAudit(string loadPattern)
        {
            var report = new AuditReport
            {
                LoadPattern = loadPattern,
                AuditDate = DateTime.Now,
                ModelName = SapUtils.GetModelName(),
                UnitInfo = UnitManager.Info.ToString()
            };

            // 1. Thu thập tải trọng từ SAP (đã quy đổi về kN, m, kN/m, kN/m2)
            var allLoads = new List<RawSapLoad>();
            allLoads.AddRange(SapUtils.GetAllFrameDistributedLoads(loadPattern));
            allLoads.AddRange(SapUtils.GetAllFramePointLoads(loadPattern));
            allLoads.AddRange(SapUtils.GetAllAreaUniformLoads(loadPattern));
            allLoads.AddRange(SapUtils.GetAllAreaUniformToFrameLoads(loadPattern));
            allLoads.AddRange(SapUtils.GetAllPointLoads(loadPattern));

            if (allLoads.Count == 0) return report;

            // 2. Xác định danh sách tầng dựa trên cao độ phần tử
            var storyElevations = DetermineStoryElevations(allLoads);

            // 3. Xử lý từng tầng
            foreach (var storyInfo in storyElevations.OrderByDescending(s => s.Value))
            {
                var storyLoads = allLoads.Where(l => Math.Abs(l.ElementZ - storyInfo.Value) <= STORY_TOLERANCE).ToList();
                if (storyLoads.Count == 0) continue;

                var storyGroup = ProcessStory(storyInfo.Key, storyInfo.Value, storyLoads);
                if (storyGroup.LoadTypeGroups.Count > 0)
                    report.Stories.Add(storyGroup);
            }

            // 4. Lấy phản lực đáy tổng cộng để so sánh (kiểm tra cân bằng lực)
            // Lấy theo hướng chủ đạo của Load Pattern (thường là Gravity - Z)
            // Tuy nhiên nếu là gió (X/Y), cần lấy FX/FY. Ở đây lấy vector tổng hoặc mặc định Z.
            report.SapBaseReaction = SapUtils.GetBaseReaction(loadPattern, "Z"); 
            
            // Note: Nếu tải trọng ngang, người dùng nên check FX/FY. 
            // Logic mở rộng: Check hướng tải trong allLoads để quyết định lấy Reaction hướng nào.
            if (CheckIfLateralLoad(allLoads))
            {
                // Nếu chủ yếu là tải ngang, lấy reaction ngang lớn nhất
                double rx = SapUtils.GetBaseReaction(loadPattern, "X");
                double ry = SapUtils.GetBaseReaction(loadPattern, "Y");
                if (Math.Abs(rx) > Math.Abs(report.SapBaseReaction)) report.SapBaseReaction = rx;
                if (Math.Abs(ry) > Math.Abs(report.SapBaseReaction)) report.SapBaseReaction = ry;
            }

            return report;
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
                if (typeResult.ValueGroups.Count > 0)
                    storyGroup.LoadTypeGroups.Add(typeResult);
            }

            return storyGroup;
        }

        private AuditLoadTypeGroup ProcessLoadType(string loadType, List<RawSapLoad> loads)
        {
            var typeGroup = new AuditLoadTypeGroup
            {
                LoadTypeName = GetLoadTypeDisplayName(loadType)
            };

            // Gom nhóm theo giá trị tải (Value1) và Hướng (Direction)
            // Sử dụng Round để gom các giá trị xấp xỉ nhau
            var valueGroups = loads.GroupBy(l => new { Val = Math.Round(l.Value1, 3), Dir = l.Direction });

            foreach (var group in valueGroups.OrderByDescending(g => g.Key.Val))
            {
                var valueResult = new AuditValueGroup
                {
                    LoadValue = group.Key.Val,
                    Direction = group.Key.Dir
                };

                // Phân luồng xử lý hình học dựa trên loại tải
                if (loadType.Contains("Area"))
                {
                    ProcessAreaLoads(group.ToList(), valueResult);
                }
                else if (loadType.Contains("Frame"))
                {
                    ProcessFrameLoads(group.ToList(), valueResult);
                }
                else
                {
                    ProcessPointLoads(group.ToList(), valueResult);
                }

                if (valueResult.Entries.Count > 0)
                    typeGroup.ValueGroups.Add(valueResult);
            }

            return typeGroup;
        }

        // --- XỬ LÝ TẢI DIỆN TÍCH (AREA - SMART GEOMETRY RECOGNITION & DECOMPOSITION) ---
        private void ProcessAreaLoads(List<RawSapLoad> loads, AuditValueGroup valueGroup)
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
                double force = areaM2 * valueGroup.LoadValue;
                string location = GetGridRangeDescription(geom.EnvelopeInternal);

                valueGroup.Entries.Add(new AuditEntry
                {
                    GridLocation = location,
                    Explanation = strategy.Formula,
                    Quantity = areaM2,
                    Force = force,
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
                    double c2 = splitCoords[i+1];
                    
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

        // --- XỬ LÝ TẢI THANH (FRAME - LINE UNION) ---
        private void ProcessFrameLoads(List<RawSapLoad> loads, AuditValueGroup valueGroup)
        {
            var lines = new List<(LineString Line, string Name)>();

            foreach (var load in loads)
            {
                if (_frameGeometryCache.TryGetValue(load.ElementName, out var frame))
                {
                    var line = CreateNtsLineString(frame.StartPt, frame.EndPt);
                    if (line != null && line.IsValid)
                        lines.Add((line, load.ElementName));
                }
            }

            if (lines.Count == 0) return;

            // UNION LineString: Gộp các đoạn thẳng nối tiếp hoặc chồng lấn
            var geometries = lines.Select(l => l.Line).Cast<Geometry>().ToList();
            Geometry unionResult;
            try
            {
                unionResult = UnaryUnionOp.Union(geometries);
            }
            catch
            {
                unionResult = _geometryFactory.CreateGeometryCollection(geometries.ToArray());
            }

            for (int i = 0; i < unionResult.NumGeometries; i++)
            {
                var geom = unionResult.GetGeometryN(i);
                
                // Chiều dài mm -> m
                double lenM = geom.Length / 1000.0;
                double force = lenM * valueGroup.LoadValue;

                string location = GetGridRangeDescription(geom.EnvelopeInternal);
                string explanation = $"L = {lenM:0.00}m";

                // Trace elements
                var containedElements = lines
                    .Where(l => geom.Contains(l.Line) || geom.Intersects(l.Line))
                    .Select(l => l.Name)
                    .ToList();

                valueGroup.Entries.Add(new AuditEntry
                {
                    GridLocation = location,
                    Explanation = explanation,
                    Quantity = lenM,
                    Force = force,
                    ElementList = containedElements
                });
            }
        }

        // --- XỬ LÝ TẢI ĐIỂM (POINT) ---
        private void ProcessPointLoads(List<RawSapLoad> loads, AuditValueGroup valueGroup)
        {
            // 1. Chuẩn bị dữ liệu: Gắn thông tin Grid cho từng điểm tải
            var pointItems = new List<PointAuditItem>();
            var allPoints = SapUtils.GetAllPoints(); // Lấy cache toạ độ điểm

            foreach (var load in loads)
            {
                var ptCoord = allPoints.FirstOrDefault(p => p.Name == load.ElementName);
                if (ptCoord == null) continue; // Skip nếu không tìm thấy tọa độ

                // Tìm Grid X và Grid Y chính xác (Snap)
                var gridX = _xGrids.OrderBy(g => Math.Abs(g.Coordinate - ptCoord.X)).FirstOrDefault();
                var gridY = _yGrids.OrderBy(g => Math.Abs(g.Coordinate - ptCoord.Y)).FirstOrDefault();

                string xName = (gridX != null && Math.Abs(gridX.Coordinate - ptCoord.X) < GRID_SNAP_TOLERANCE) 
                    ? gridX.Name 
                    : null;
                    
                string yName = (gridY != null && Math.Abs(gridY.Coordinate - ptCoord.Y) < GRID_SNAP_TOLERANCE)
                    ? FormatGridWithOffset(gridY.Name, ptCoord.Y - gridY.Coordinate) // Hỗ trợ E(-2.2m)
                    : null;

                pointItems.Add(new PointAuditItem
                {
                    Load = load,
                    XName = xName,
                    YName = yName,
                    RawX = ptCoord.X, // Dùng để sort
                    RawY = ptCoord.Y
                });
            }

            // 2. Chiến lược gom nhóm: Ưu tiên gom theo phương ngang (Y Grids) trước, sau đó dọc (X Grids)
            // Kỹ sư thường đọc bản vẽ theo phương ngang (Trục A, B, C...)
            
            var processedItems = new HashSet<RawSapLoad>();

            // --- PASS 1: Gom theo Trục Y (Ngang) ---
            var groupsY = pointItems.Where(p => p.YName != null)
                                    .GroupBy(p => p.YName)
                                    .OrderBy(g => g.Key);

            foreach (var grp in groupsY)
            {
                var itemsInRow = grp.ToList();
                // Chỉ gom nếu có từ 2 điểm trở lên trên cùng trục
                if (itemsInRow.Count > 1) 
                {
                    double totalForce = itemsInRow.Sum(i => i.Load.Value1);
                    int count = itemsInRow.Count;
                    
                    // Tạo danh sách trục cắt: VD "1, 2, 11, 12"
                    var xLocs = itemsInRow.Select(i => i.XName ?? "?").OrderBy(s => s).Distinct();
                    string crossAxes = string.Join(",", xLocs);
                    if (crossAxes.Length > 20) crossAxes = $"{itemsInRow.Count} vị trí";

                    valueGroup.Entries.Add(new AuditEntry
                    {
                        GridLocation = $"Trục {grp.Key} (x {crossAxes})", // VD: Trục E(-2.2m) (x 1,2,11,12)
                        Explanation = $"{count} vị trí",
                        Quantity = count,
                        Force = totalForce,
                        ElementList = itemsInRow.Select(i => i.Load.ElementName).ToList()
                    });

                    foreach (var item in itemsInRow) processedItems.Add(item.Load);
                }
            }

            // --- PASS 2: Gom theo Trục X (Dọc) cho các điểm còn lại ---
            var remainingItems = pointItems.Where(p => !processedItems.Contains(p.Load)).ToList();
            var groupsX = remainingItems.Where(p => p.XName != null)
                                        .GroupBy(p => p.XName)
                                        .OrderBy(g => g.Key);

            foreach (var grp in groupsX)
            {
                var itemsInCol = grp.ToList();
                if (itemsInCol.Count > 1)
                {
                    double totalForce = itemsInCol.Sum(i => i.Load.Value1);
                    int count = itemsInCol.Count;

                    var yLocs = itemsInCol.Select(i => i.YName ?? "?").OrderBy(s => s).Distinct();
                    string crossAxes = string.Join(",", yLocs);
                     if (crossAxes.Length > 20) crossAxes = $"{itemsInCol.Count} vị trí";

                    valueGroup.Entries.Add(new AuditEntry
                    {
                        GridLocation = $"Trục {grp.Key} (x {crossAxes})",
                        Explanation = $"{count} vị trí",
                        Quantity = count,
                        Force = totalForce,
                        ElementList = itemsInCol.Select(i => i.Load.ElementName).ToList()
                    });

                    foreach (var item in itemsInCol) processedItems.Add(item.Load);
                }
            }

            // --- PASS 3: Các điểm lẻ tẻ còn lại ---
            var leftovers = pointItems.Where(p => !processedItems.Contains(p.Load)).ToList();
            
            // Gom các điểm trùng vị trí (nếu có)
            var uniqueLeftovers = leftovers.GroupBy(p => GetGridLocationForPoint(p.Load.ElementName));
            
            foreach (var grp in uniqueLeftovers)
            {
                double totalForce = grp.Sum(i => i.Load.Value1);
                int count = grp.Count();
                
                valueGroup.Entries.Add(new AuditEntry
                {
                    GridLocation = grp.Key, // Format cũ: (GridX, GridY)
                    Explanation = "1 vị trí",
                    Quantity = count,
                    Force = totalForce,
                    ElementList = grp.Select(i => i.Load.ElementName).ToList()
                });
            }
        }

        // Helper class nội bộ để xử lý gom nhóm
        private class PointAuditItem
        {
            public RawSapLoad Load { get; set; }
            public string XName { get; set; }
            public string YName { get; set; }
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

        private string FindAxisRange(double minVal, double maxVal, List<SapUtils.GridLineRecord> grids)
        {
            if (grids == null || grids.Count == 0) return "?";

            // Tìm trục gần Min nhất
            var startGrid = grids.OrderBy(g => Math.Abs(g.Coordinate - minVal)).First();
            double startDiff = minVal - startGrid.Coordinate;

            // Tìm trục gần Max nhất
            var endGrid = grids.OrderBy(g => Math.Abs(g.Coordinate - maxVal)).First();
            double endDiff = maxVal - endGrid.Coordinate;

            string startName = FormatGridWithOffset(startGrid.Name, startDiff);

            // Nếu cùng một trục (hoặc khoảng cách rất nhỏ)
            if (startGrid.Name == endGrid.Name || Math.Abs(maxVal - minVal) < GRID_SNAP_TOLERANCE)
            {
                return startName;
            }

            string endName = FormatGridWithOffset(endGrid.Name, endDiff);
            return $"{startName}-{endName}";
        }

        private string FormatGridWithOffset(string name, double offsetMm)
        {
            if (Math.Abs(offsetMm) < GRID_SNAP_TOLERANCE) return name;
            
            double offsetM = offsetMm / 1000.0;
            string sign = offsetM > 0 ? "+" : "";
            return $"{name}({sign}{offsetM:0.#}m)";
        }

        private string GetGridLocationForPoint(string elementName)
        {
            var pt = SapUtils.GetAllPoints().FirstOrDefault(p => p.Name == elementName);
            if (pt == null) return elementName;

            string x = FindAxisRange(pt.X, pt.X, _xGrids);
            string y = FindAxisRange(pt.Y, pt.Y, _yGrids);
            return $"({x}, {y})";
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

        private bool CheckIfLateralLoad(List<RawSapLoad> loads)
        {
            int lateralCount = loads.Count(l => l.Direction == "X" || l.Direction == "Y");
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

        #region Report Generation (With Unit Conversion)

        public string GenerateTextReport(AuditReport report)
        {
            var sb = new StringBuilder();
            var uInfo = UnitManager.Info; // Lấy thông tin đơn vị hiển thị

            sb.AppendLine("===================================================================");
            sb.AppendLine("   BÁO CÁO KIỂM TOÁN TẢI TRỌNG - DTS ENGINE");
            sb.AppendLine($"   Ngày: {report.AuditDate:dd/MM/yyyy HH:mm}");
            sb.AppendLine($"   Model: {report.ModelName}");
            sb.AppendLine($"   Load Case: {report.LoadPattern}");
            sb.AppendLine($"   Đơn vị hiển thị: {uInfo.ForceUnit}, {uInfo.LengthUnit}");
            sb.AppendLine("===================================================================");
            sb.AppendLine();

            foreach (var story in report.Stories)
            {
                // Convert cao độ sang mét cho dễ đọc
                double elevM = UnitManager.ToMeter(story.Elevation);
                sb.AppendLine($"--- TẦNG: {story.StoryName} (Z = {elevM:0.00}m) ---");
                sb.AppendLine();

                foreach (var loadType in story.LoadTypeGroups)
                {
                    sb.AppendLine($"[{loadType.LoadTypeName}]");

                    foreach (var valGroup in loadType.ValueGroups)
                    {
                        // Convert giá trị tải sang đơn vị hiển thị (ví dụ kN -> Tấn)
                        // Lưu ý: Giá trị gốc trong AuditValueGroup đang là kN/m2 hoặc kN/m
                        // Chỉ cần format lại string đơn vị, giá trị có thể giữ nguyên hoặc convert nếu cần.
                        // Ở đây ta giả định hiển thị LoadValue theo đơn vị lực User chọn / mét.
                        
                        string unitStr = loadType.LoadTypeName.Contains("AREA") ? $"{uInfo.ForceUnit}/m²" :
                                         loadType.LoadTypeName.Contains("FRAME") ? $"{uInfo.ForceUnit}/m" : uInfo.ForceUnit;

                        double displayLoadVal = ConvertForce(valGroup.LoadValue);

                        sb.AppendLine($"    > Nhóm giá trị: {displayLoadVal:0.00} {unitStr} ({valGroup.Direction})");
                        sb.AppendLine(new string('-', 105));
                        sb.AppendLine(string.Format("    | {0,-35} | {1,-30} | {2,12} | {3,12} |",
                            "Vị trí (Trục)", "Diễn giải", "SL/DT(m/m2)", $"Lực ({uInfo.ForceUnit})"));
                        sb.AppendLine(new string('-', 105));

                        foreach (var entry in valGroup.Entries)
                        {
                            // Convert lực tổng (đang là kN) sang đơn vị hiển thị (Tấn, Kgf...)
                            double displayForce = ConvertForce(entry.Force);

                            string loc = entry.GridLocation.Length > 35 ? entry.GridLocation.Substring(0, 32) + "..." : entry.GridLocation;
                            string exp = entry.Explanation.Length > 30 ? entry.Explanation.Substring(0, 27) + "..." : entry.Explanation;

                            sb.AppendLine(string.Format("    | {0,-35} | {1,-30} | {2,12:0.00} | {3,12:0.00} |",
                                loc, exp, entry.Quantity, displayForce));
                        }

                        double groupTotalForce = ConvertForce(valGroup.TotalForce);
                        sb.AppendLine(new string('-', 105));
                        sb.AppendLine(string.Format("    | {0,-81} | {1,12:n2} |", "TỔNG NHÓM", groupTotalForce));
                        sb.AppendLine();
                    }
                }

                double storyTotal = ConvertForce(story.SubTotalForce);
                sb.AppendLine($">>> TỔNG TẦNG {story.StoryName}: {storyTotal:n2} {uInfo.ForceUnit}");
                sb.AppendLine();
            }

            // Tổng kết
            double totalCalc = ConvertForce(report.TotalCalculatedForce);
            double baseReact = ConvertForce(report.SapBaseReaction);
            double diff = totalCalc - Math.Abs(baseReact);

            sb.AppendLine("===================================================================");
            sb.AppendLine($"TỔNG CỘNG TÍNH TOÁN: {totalCalc:n2} {uInfo.ForceUnit}");
            sb.AppendLine($"SAP2000 BASE REACTION: {baseReact:n2} {uInfo.ForceUnit}");
            sb.AppendLine($"SAI LỆCH: {diff:n2} {uInfo.ForceUnit} ({report.DifferencePercent:0.00}%)");
            
            if (Math.Abs(report.DifferencePercent) < 5.0)
                sb.AppendLine(">>> ĐÁNH GIÁ: OK / CHẤP NHẬN ĐƯỢC");
            else
                sb.AppendLine(">>> ĐÁNH GIÁ: CẦN KIỂM TRA LẠI (> 5%)");
            
            sb.AppendLine("===================================================================");

            return sb.ToString();
        }

        /// <summary>
        /// Generate text report with target force unit override (kN, Ton, kgf, lb)
        /// </summary>
        public string GenerateTextReport(AuditReport report, string targetUnit)
        {
            var sb = new StringBuilder();
            double forceFactor = 1.0;
            // Normalize
            if (string.IsNullOrWhiteSpace(targetUnit)) targetUnit = "kN";
            targetUnit = targetUnit.Trim();

            switch (targetUnit)
            {
                case "Ton": forceFactor = 1.0 / 9.81; break;
                case "kgf": forceFactor = 101.97; break;
                case "lb": forceFactor = 224.8; break;
                default: targetUnit = "kN"; forceFactor = 1.0; break;
            }

            sb.AppendLine("===================================================================");
            sb.AppendLine("   BÁO CÁO KIỂM TOÁN TẢI TRỌNG - DTS ENGINE");
            sb.AppendLine($"   Ngày: {report.AuditDate:dd/MM/yyyy HH:mm}");
            sb.AppendLine($"   Model: {report.ModelName}");
            sb.AppendLine($"   Load Case: {report.LoadPattern}");
            sb.AppendLine($"   Đơn vị hiển thị: {targetUnit}");
            sb.AppendLine("===================================================================");
            sb.AppendLine();

            foreach (var story in report.Stories)
            {
                double elevM = UnitManager.ToMeter(story.Elevation);
                sb.AppendLine($"--- TẦNG: {story.StoryName} (Z = {elevM:0.00}m) ---");
                sb.AppendLine();

                foreach (var loadType in story.LoadTypeGroups)
                {
                    sb.AppendLine($"[{loadType.LoadTypeName}]");

                    foreach (var valGroup in loadType.ValueGroups)
                    {
                        string unitStr = loadType.LoadTypeName.Contains("AREA") ? $"{targetUnit}/m²" :
                                         loadType.LoadTypeName.Contains("FRAME") ? $"{targetUnit}/m" : targetUnit;

                        double displayLoadVal = valGroup.LoadValue * forceFactor;

                        sb.AppendLine($"    > Nhóm giá trị: {displayLoadVal:0.00} {unitStr} ({valGroup.Direction})");
                        sb.AppendLine(new string('-', 105));
                        sb.AppendLine(string.Format("    | {0,-35} | {1,-30} | {2,12} | {3,12} |",
                            "Vị trí (Trục)", "Diễn giải", "SL/DT(m/m2)", $"Lực ({targetUnit})"));
                        sb.AppendLine(new string('-', 105));

                        foreach (var entry in valGroup.Entries)
                        {
                            double displayForce = entry.Force * forceFactor;

                            string loc = entry.GridLocation.Length > 35 ? entry.GridLocation.Substring(0, 32) + "..." : entry.GridLocation;
                            string exp = entry.Explanation.Length > 30 ? entry.Explanation.Substring(0, 27) + "..." : entry.Explanation;

                            sb.AppendLine(string.Format("    | {0,-35} | {1,-30} | {2,12:0.00} | {3,12:0.00} |",
                                loc, exp, entry.Quantity, displayForce));
                        }

                        double groupTotalForce = valGroup.TotalForce * forceFactor;
                        sb.AppendLine(new string('-', 105));
                        sb.AppendLine(string.Format("    | {0,-81} | {1,12:n2} |", "TỔNG NHÓM", groupTotalForce));
                        sb.AppendLine();
                    }
                }

                double storyTotal = story.SubTotalForce * forceFactor;
                sb.AppendLine($">>> TỔNG TẦNG {story.StoryName}: {storyTotal:n2} {targetUnit}");
                sb.AppendLine();
            }

            double totalCalc = report.TotalCalculatedForce * forceFactor;
            double baseReact = report.SapBaseReaction * forceFactor;
            double diff = totalCalc - Math.Abs(baseReact);

            sb.AppendLine("===================================================================");
            sb.AppendLine($"TỔNG CỘNG TÍNH TOÁN: {totalCalc:n2} {targetUnit}");
            sb.AppendLine($"SAP2000 BASE REACTION: {baseReact:n2} {targetUnit}");
            sb.AppendLine($"SAI LỆCH: {diff:n2} {targetUnit} ({report.DifferencePercent:0.00}%)");
            
            if (Math.Abs(report.DifferencePercent) < 5.0)
                sb.AppendLine(">>> ĐÁNH GIÁ: OK / CHẤP NHẬN ĐƯỢC");
            else
                sb.AppendLine(">>> ĐÁNH GIÁ: CẦN KIỂM TRA LẠI (> 5%)");
            
            sb.AppendLine("===================================================================");

            return sb.ToString();
        }

        private double ConvertForce(double forceInKn)
        {
            // Nội bộ là kN. Convert sang đơn vị trong UnitManager
            string targetUnit = UnitManager.Info.ForceUnit.ToUpper();
            switch (targetUnit)
            {
                case "N": return forceInKn * 1000.0;
                case "TON": return forceInKn / 9.81;
                case "KGF": return forceInKn * 101.97;
                case "LB": return forceInKn * 224.8;
                default: return forceInKn; // kN
            }
        }

        #endregion
    }
}
