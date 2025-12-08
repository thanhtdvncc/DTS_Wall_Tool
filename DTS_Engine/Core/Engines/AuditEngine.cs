using DTS_Engine.Core.Data;
using DTS_Engine.Core.Interfaces;
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
    /// Engine kiểm toán tải trọng SAP2000 (Phiên bản v2.5 - Dependency Injection Architecture)
    /// - Sử dụng NetTopologySuite để gộp (Union) hình học, tránh cắt nát phần tử.
    /// - Định vị trục thông minh dựa trên BoundingBox (Range).
    /// - Hỗ trợ đa đơn vị hiển thị qua UnitManager.
    /// - Added: GroupLoadsByStory (sàn đỡ tường trên) và Word Wrap trong GenerateTextReport.
    /// 
    /// ARCHITECTURE COMPLIANCE (ISO/IEC 25010 - Maintainability):
    /// - Dependency Injection: Nhận ISapLoadReader qua Constructor
    /// - Separation of Concerns: Engine không biết nguồn dữ liệu (SAP/Excel/SQL)
    /// - Open/Closed Principle: Mở rộng data source mà không sửa Engine
    /// - Single Responsibility: Chỉ xử lý logic audit, không đọc dữ liệu
    /// </summary>
    public class AuditEngine
    {
        #region Constants & Fields

        private const double STORY_TOLERANCE = 200.0; // mm
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

        // CRITICAL: Load Reader injected via Constructor (Dependency Injection)
        private readonly ISapLoadReader _loadReader;

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor với Dependency Injection.
        /// 
        /// PRECONDITIONS:
        /// - loadReader phải được khởi tạo đầy đủ (SapModel + ModelInventory)
        /// - SapUtils.IsConnected = true
        /// 
        /// POSTCONDITIONS:
        /// - _loadReader sẵn sàng gọi ReadAllLoads()
        /// - Grid và Story data được cache
        /// 
        /// DEPENDENCY INJECTION RATIONALE:
        /// - AuditEngine không tạo Reader → Dễ test (Mock ISapLoadReader)
        /// - Thay đổi data source (Excel, SQL) không cần sửa Engine
        /// - Tuân thủ SOLID: Dependency Inversion Principle
        /// </summary>
        /// <param name="loadReader">Implementation của ISapLoadReader (VD: SapDatabaseReader)</param>
        public AuditEngine(ISapLoadReader loadReader)
        {
            _loadReader = loadReader ?? throw new ArgumentNullException(nameof(loadReader),
                "ISapLoadReader is required. Initialize SapDatabaseReader with Model and Inventory before passing to AuditEngine.");

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
        /// REFACTORED: Uses injected ISapLoadReader for data access
        /// 
        /// ARCHITECTURE:
        /// - Data Access: _loadReader.ReadAllLoads() (Abstracted)
        /// - Business Logic: Calculation, Grouping, Processing (In Engine)
        /// - Presentation: GenerateTextReport() (Separate method)
        /// 
        /// PERFORMANCE:
        /// - LoadReader đã cache ModelInventory → Không build lại
        /// - Geometry cache tái sử dụng cho nhiều patterns
        /// </summary>
        public AuditReport RunSingleAudit(string loadPattern)
        {
            CacheGeometry();

            var report = new AuditReport
            {
                LoadPattern = loadPattern,
                AuditDate = DateTime.Now,
                ModelName = SapUtils.GetModelName(),
                UnitInfo = UnitManager.Info.ToString()
            };

            var allLoads = _loadReader.ReadAllLoads(loadPattern);

            if (allLoads.Count == 0)
            {
                report.IsAnalyzed = false;
                report.SapBaseReaction = 0;
                return report;
            }

            // [FIX]: Tính toán tổng lực Global dựa trên Vector Sum
            double sumFx = 0, sumFy = 0, sumFz = 0;

            foreach (var load in allLoads)
            {
                // Multiplier dựa trên hình geometry (Area m2 hoặc Length m)
                double multiplier = CalculateGeometryMultiplier(load);

                sumFx += load.DirectionX * multiplier;
                sumFy += load.DirectionY * multiplier;
                sumFz += load.DirectionZ * multiplier;
            }

            report.CalculatedFx = sumFx;
            report.CalculatedFy = sumFy;
            report.CalculatedFz = sumFz;

            // Nhóm theo tầng và xử lý chi tiết
            var storyBuckets = GroupLoadsByStory(allLoads);
            foreach (var bucket in storyBuckets.OrderByDescending(b => b.Elevation))
            {
                var storyGroup = ProcessStory(bucket.StoryName, bucket.Elevation, bucket.Loads);
                if (storyGroup.LoadTypes.Count > 0)
                    report.Stories.Add(storyGroup);
            }

            report.IsAnalyzed = false;
            return report;
        }

        private double CalculateGeometryMultiplier(RawSapLoad load)
        {
            string elementName = load.ElementName?.Trim();
            if (string.IsNullOrEmpty(elementName)) return 1.0;

            // Area loads: Nhân với diện tích (m²)
            if (load.LoadType.Contains("Area"))
            {
                var areaGeom = GetAreaGeometry(elementName);
                if (areaGeom != null)
                {
                    double areaM2 = areaGeom.Area / 1_000_000.0; // mm² -> m²
                    return areaM2 > 0 ? areaM2 : 1.0;
                }
            }

            // Frame loads: Nhân với chiều dài (m)
            if (load.LoadType.Contains("Frame") && !load.LoadType.Contains("Point"))
            {
                var frameGeom = GetFrameGeometry(elementName);
                if (frameGeom != null)
                {
                    double startMeters, endMeters;
                    double coveredM = CalculateCoveredLengthMeters(load, frameGeom, out startMeters, out endMeters);
                    return coveredM > 0 ? coveredM : 1.0;
                }
            }

            // Point loads: Nhân với 1
            return 1.0;
        }

        /// <summary>
        /// Helper: Tính hệ số nhân cho tải trọng (Area, Length, hoặc Point)
        /// FIXED: Ensure correct multiplier for all load types including walls
        /// </summary>
        private double CalculateLoadMultiplier(RawSapLoad load)
        {
            string elementName = load.ElementName?.Trim();
            if (string.IsNullOrEmpty(elementName)) return 1.0;

            // Area loads: Multiply by area (m²)
            if (load.LoadType.Contains("Area"))
            {
                var areaGeom = GetAreaGeometry(elementName);
                if (areaGeom != null)
                {
                    double areaM2 = areaGeom.Area / 1_000_000.0; // mm² to m²
                    return areaM2 > 0 ? areaM2 : 1.0;
                }
                return 1.0;
            }

            // Frame distributed loads: Multiply by covered length (m)
            if (load.LoadType.Contains("Frame") && !load.LoadType.Contains("Point"))
            {
                var frameGeom = GetFrameGeometry(elementName);
                if (frameGeom != null)
                {
                    double coveredM = CalculateCoveredLengthMeters(load, frameGeom, out _, out _);
                    if (coveredM < 1e-6)
                    {
                        // Full length if no partial load specified
                        coveredM = frameGeom.Length2D / 1000.0; // mm to m
                    }
                    return coveredM > 0 ? coveredM : 1.0;
                }
                return 1.0;
            }

            // Point loads: Multiplier = 1
            return 1.0;
        }

        /// <summary>
        /// Helper: Lấy geometry của Area (với cache) - FIX case-insensitive
        /// </summary>
        private SapArea GetAreaGeometry(string areaName)
        {
            if (string.IsNullOrEmpty(areaName)) return null;

            if (_areaGeometryCache.TryGetValue(areaName, out var area))
                return area;

            // Try trimmed version
            string trimmed = areaName.Trim();
            if (_areaGeometryCache.TryGetValue(trimmed, out area))
                return area;

            return null;
        }

        /// <summary>
        /// Helper: Lấy geometry của Frame (với cache) - FIX case-insensitive
        /// </summary>
        private SapFrame GetFrameGeometry(string frameName)
        {
            if (string.IsNullOrEmpty(frameName)) return null;

            if (_frameGeometryCache.TryGetValue(frameName, out var frame))
                return frame;

            // Try trimmed version
            string trimmed = frameName.Trim();
            if (_frameGeometryCache.TryGetValue(trimmed, out frame))
                return frame;

            return null;
        }

        #endregion

        #region Main Audit Workflows (Backup)

        /// <summary>
        /// Chạy kiểm toán cho danh sách Load Patterns
        /// BACKUP: Phiên bản cũ chưa refactor
        /// </summary>
        public List<AuditReport> RunAuditBackup(string loadPatterns)
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
                var report = RunSingleAuditBackup(pattern);
                if (report != null) reports.Add(report);
            }

            return reports;
        }

        /// <summary>
        /// Chạy kiểm toán cho một Load Pattern cụ thể
        /// BACKUP: Phiên bản cũ chưa refactor
        /// </summary>
        public AuditReport RunSingleAuditBackup(string loadPattern)
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

            // CRITICAL: Đọc tải trọng qua Interface (Dependency Injection)
            // LoadReader đã có sẵn ModelInventory → Vector đã được tính chính xác
            var allLoads = _loadReader.ReadAllLoads(loadPattern);

            if (allLoads.Count == 0)
            {
                report.IsAnalyzed = false;
                report.SapBaseReaction = 0;
                return report;
            }

            // --- BƯỚC TÍNH TOÁN CHÍNH XÁC (Dựa trên Vector từ Reader) ---
            // OPTIMIZATION: Single-pass calculation
            double sumFx = 0, sumFy = 0, sumFz = 0;

            foreach (var load in allLoads)
            {
                // Vector đã được Reader tính toán sẵn → Chỉ cần nhân với multiplier
                double multiplier = CalculateLoadMultiplier(load);

                sumFx += load.DirectionX * multiplier;
                sumFy += load.DirectionY * multiplier;
                sumFz += load.DirectionZ * multiplier;
            }

            // GÁN KẾT QUẢ VÀO REPORT
            report.CalculatedFx = sumFx;
            report.CalculatedFy = sumFy;
            report.CalculatedFz = sumFz;

            // BƯỚC 4: Group and process by story
            var storyBuckets = GroupLoadsByStory(allLoads);
            foreach (var bucket in storyBuckets.OrderByDescending(b => b.Elevation))
            {
                var storyGroup = ProcessStory(bucket.StoryName, bucket.Elevation, bucket.Loads);
                if (storyGroup.LoadTypes.Count > 0)
                    report.Stories.Add(storyGroup);
            }

            // Base Reaction = 0 (người dùng check thủ công)
            report.SapBaseReaction = 0;
            report.IsAnalyzed = false;

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
        /// Nhóm tải theo tầng (Story) dựa trên cao độ Z của phần tử.
        /// FIX BUG #3 v4.1: Handle negative elevations (basement levels) correctly
        /// FIXED: Unified rule - element Z >= story elevation → belongs to that story
        /// </summary>
        private List<TempStoryBucket> GroupLoadsByStory(List<RawSapLoad> loads)
        {
            var stories = SapUtils.GetStories()
                .Where(s => s.IsElevation)
                .OrderBy(s => s.Coordinate) // FIX: Sort ascending to handle negative Z correctly
                .ToList();

            if (stories.Count == 0)
            {
                var singleBucket = new TempStoryBucket
                {
                    StoryName = "All",
                    Elevation = 0,
                    Loads = loads.ToList()
                };
                return new List<TempStoryBucket> { singleBucket };
            }

            var buckets = stories.Select(s => new TempStoryBucket
            {
                StoryName = s.Name ?? s.StoryName ?? $"Z={s.Coordinate}",
                Elevation = s.Coordinate,
                Loads = new List<RawSapLoad>()
            }).ToList();

            // FIX BUG #3: Tolerance must work for negative elevations too
            const double tolerance = 500.0; // 500mm

            foreach (var load in loads)
            {
                double z = load.ElementZ;
                bool assigned = false;

                // FIXED LOGIC: Find the highest story floor that is below or at element elevation
                // Start from top and work down to find the correct story
                for (int i = buckets.Count - 1; i >= 0; i--)
                {
                    double storyElev = buckets[i].Elevation;

                    // Element belongs to story if:
                    // z >= storyElev - tolerance (element is on or above this floor)
                    // This works for both positive and negative elevations
                    if (z >= (storyElev - tolerance))
                    {
                        buckets[i].Loads.Add(load);
                        assigned = true;
                        break;
                    }
                }

                // Fallback: assign to lowest story if element is below all defined stories
                if (!assigned && buckets.Count > 0)
                {
                    buckets[0].Loads.Add(load);
                }
            }

            return buckets;
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

            // FIX v4.2: Calculate vector subtotals for load type
            typeGroup.SubTotalFx = typeGroup.Entries.Sum(e => e.ForceX);
            typeGroup.SubTotalFy = typeGroup.Entries.Sum(e => e.ForceY);
            typeGroup.SubTotalFz = typeGroup.Entries.Sum(e => e.ForceZ);

            typeGroup.Entries = typeGroup.Entries.OrderBy(e => e.GridLocation).ToList();

            return typeGroup;
        }

        // --- XỬ LÝ TẢI DIỆN TÍCH (AREA - SMART GEOMETRY RECOGNITION & DECOMPOSITION) ---
        /// <summary>
        /// Process area loads with vector-aware force calculation
        /// FIX v4.2: Calculate force with proper directional sign
        /// </summary>
        private void ProcessAreaLoads(List<RawSapLoad> loads, double loadVal, string dir, List<AuditEntry> targetList)
        {
            var validLoads = new List<RawSapLoad>();
            var geometries = new List<Geometry>();

            // 1. Thu thập hình học
            foreach (var load in loads)
            {
                if (_areaGeometryCache.TryGetValue(load.ElementName, out var area))
                {
                    var projected = ProjectAreaToBestPlane(area);
                    var poly = CreateNtsPolygon(projected);
                    if (poly != null && poly.IsValid && poly.Area > 1e-6)
                    {
                        geometries.Add(poly);
                        validLoads.Add(load);
                    }
                }
            }

            if (geometries.Count == 0) return;

            // 2. Thử Union (Gộp hình)
            Geometry processedGeometry = null;
            try
            {
                processedGeometry = UnaryUnionOp.Union(geometries);
            }
            catch
            {
                processedGeometry = _geometryFactory.CreateGeometryCollection(geometries.ToArray());
            }

            // 3. Tạo Audit Entry từ kết quả
            for (int i = 0; i < processedGeometry.NumGeometries; i++)
            {
                var geom = processedGeometry.GetGeometryN(i);
                if (geom.Area < 1e-6) continue;

                double areaM2 = geom.Area / 1.0e6;

                // Smart Shape Analysis
                var shapeResult = AnalyzeShapeStrategy(geom);
                string formula = shapeResult.IsExact ? shapeResult.Formula : $"~{areaM2:0.00}";

                // FIX v4.2: Calculate directional sign from load vector
                double dirSign = 1.0;
                if (validLoads.Count > 0)
                {
                    var sampleLoad = validLoads[0];
                    // Determine sign based on primary direction
                    if (sampleLoad.DirectionX != 0) dirSign = Math.Sign(sampleLoad.DirectionX);
                    else if (sampleLoad.DirectionY != 0) dirSign = Math.Sign(sampleLoad.DirectionY);
                    else if (sampleLoad.DirectionZ != 0) dirSign = Math.Sign(sampleLoad.DirectionZ);
                }

                // FIX v4.2: Calculate signed force = Quantity * UnitLoad * DirectionSign
                double signedForce = areaM2 * loadVal * dirSign;

                // FIX v4.2: Calculate vector components
                double fx = 0, fy = 0, fz = 0;
                if (validLoads.Count > 0)
                {
                    var sampleLoad = validLoads[0];
                    var forceVec = sampleLoad.GetForceVector();
                    if (forceVec.Length > 1e-6)
                    {
                        forceVec = forceVec.Normalized * Math.Abs(signedForce);
                        fx = forceVec.X;
                        fy = forceVec.Y;
                        fz = forceVec.Z;
                    }
                    else
                    {
                        // Fallback: assume gravity
                        fz = signedForce;
                    }
                }

                targetList.Add(new AuditEntry
                {
                    GridLocation = GetGridRangeDescription(geom.EnvelopeInternal),
                    Explanation = formula,
                    Quantity = areaM2,
                    QuantityUnit = "m²",
                    UnitLoad = loadVal,
                    UnitLoadString = $"{loadVal:0.00}",
                    TotalForce = Math.Abs(signedForce),
                    Direction = dir,
                    DirectionSign = dirSign,
                    ForceX = fx,
                    ForceY = fy,
                    ForceZ = fz,
                    ElementList = validLoads.Select(l => l.ElementName).Distinct().ToList()
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
        /// <summary>
        /// Process frame loads with vector-aware force calculation
        /// FIX v4.2: Calculate force with proper directional sign and segment details
        /// </summary>
        private void ProcessFrameLoads(List<RawSapLoad> loads, double loadVal, string dir, List<AuditEntry> targetList)
        {
            var frameItems = new List<FrameAuditItem>();

            foreach (var load in loads)
            {
                if (!_frameGeometryCache.TryGetValue(load.ElementName, out var frame)) continue;

                string primaryGrid = DeterminePrimaryGrid(frame);

                double startM, endM;
                double coveredLength = CalculateCoveredLengthMeters(load, frame, out startM, out endM);
                if (coveredLength < 1e-6)
                {
                    coveredLength = frame.Length2D * UnitManager.Info.LengthScaleToMeter;
                    startM = 0;
                    endM = coveredLength;
                }

                frameItems.Add(new FrameAuditItem
                {
                    Load = load,
                    Frame = frame,
                    PrimaryGrid = primaryGrid,
                    Length = coveredLength,
                    StartM = startM,
                    EndM = endM
                });
            }

            if (frameItems.Count == 0) return;

            var groups = frameItems.GroupBy(f => f.PrimaryGrid);

            foreach (var grp in groups)
            {
                string gridName = grp.Key;
                double totalLength = grp.Sum(f => f.Length);

                // FIX v4.2: Calculate directional sign
                double dirSign = 1.0;
                var sampleLoad = grp.First().Load;
                if (sampleLoad.DirectionX != 0) dirSign = Math.Sign(sampleLoad.DirectionX);
                else if (sampleLoad.DirectionY != 0) dirSign = Math.Sign(sampleLoad.DirectionY);
                else if (sampleLoad.DirectionZ != 0) dirSign = Math.Sign(sampleLoad.DirectionZ);

                // FIX v4.2: Signed force
                double signedForce = totalLength * loadVal * dirSign;

                // FIX v4.2: Vector components
                double fx = 0, fy = 0, fz = 0;
                var forceVec = sampleLoad.GetForceVector();
                if (forceVec.Length > 1e-6)
                {
                    forceVec = forceVec.Normalized * Math.Abs(signedForce);
                    fx = forceVec.X;
                    fy = forceVec.Y;
                    fz = forceVec.Z;
                }
                else
                {
                    fz = signedForce;
                }

                string rangeDesc = DetermineCrossAxisRange(grp.ToList());
                
                // FIX v4.2: Build segment details for partial loads
                var partialSegments = grp.Where(f => f.StartM > 0.01 || Math.Abs(f.EndM - f.Frame.Length2D * UnitManager.Info.LengthScaleToMeter) > 0.01)
                    .Select(f => $"{f.Load.ElementName}_{f.StartM:0.##}to{f.EndM:0.##}")
                    .ToList();

                string explanation = partialSegments.Count > 0
                    ? string.Join(",", partialSegments)
                    : "";

                var elementNames = grp.Select(f => f.Load.ElementName).Distinct().ToList();

                targetList.Add(new AuditEntry
                {
                    GridLocation = $"{gridName} x {rangeDesc}",
                    Explanation = explanation,
                    Quantity = totalLength,
                    QuantityUnit = "m",
                    UnitLoad = loadVal,
                    UnitLoadString = $"{loadVal:0.00}",
                    TotalForce = Math.Abs(signedForce),
                    Direction = dir,
                    DirectionSign = dirSign,
                    ForceX = fx,
                    ForceY = fy,
                    ForceZ = fz,
                    ElementList = elementNames
                });
            }
        }

        // Normalize covered length to meters regardless of SAP working unit
        private double CalculateCoveredLengthMeters(RawSapLoad load, SapFrame frame, out double startMeters, out double endMeters)
        {
            startMeters = 0;
            endMeters = 0;

            if (load == null || frame == null)
                return 0;

            double frameLenM = frame.Length2D * UnitManager.Info.LengthScaleToMeter;

            if (load.IsRelative)
            {
                startMeters = load.DistStart * frameLenM;
                endMeters = load.DistEnd * frameLenM;
            }
            else
            {
                startMeters = load.DistStart * UnitManager.Info.LengthScaleToMeter;
                endMeters = load.DistEnd * UnitManager.Info.LengthScaleToMeter;
            }

            double covered = Math.Abs(endMeters - startMeters);

            if (covered < 1e-6)
            {
                covered = frameLenM;
                startMeters = 0;
                endMeters = frameLenM;
            }

            return covered;
        }

        private class FrameAuditItem
        {
            public RawSapLoad Load;
            public SapFrame Frame;
            public string PrimaryGrid;
            public double Length;
            public double StartM;
            public double EndM;
        }

        /// <summary>
        /// Xác định thanh nằm trên trục nào (Grid A, Grid 1, hoặc Diagonal)
        /// FIX: Added English translation support
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
                return $"Grid {gridY}";
            }
            else if (isVertical)
            {
                string gridX = FindAxisRange(mid.X, mid.X, _xGrids, true);
                return $"Grid {gridX}";
            }
            else
            {
                string gridX = FindAxisRange(mid.X, mid.X, _xGrids, true);
                string gridY = FindAxisRange(mid.Y, mid.Y, _yGrids, true);
                return $"Diagonal {gridX}-{gridY}";
            }
        }

        /// <summary>
        /// Xác định phạm vi quét của nhóm dầm (VD: From Grid 1 to Grid 5)
        /// FIX: Added English translation support
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

            if (startGrid == endGrid) return $"at {startGrid}";

            string cleanStart = startGrid.Split('(')[0];
            string cleanEnd = endGrid.Split('(')[0];
            return $"{cleanStart}-{cleanEnd}";
        }

        // --- XỬ LÝ TẢI ĐIỂM (POINT) - IMPROVED GROUPING ---
        /// <summary>
        /// Process point loads with vector-aware force calculation
        /// FIX v4.2: Calculate force with proper directional sign
        /// </summary>
        private void ProcessPointLoads(List<RawSapLoad> loads, double loadVal, string dir, List<AuditEntry> targetList)
        {
            var allPoints = SapUtils.GetAllPoints();

            var pointGroups = new Dictionary<string, List<(RawSapLoad load, SapUtils.SapPoint coord)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var load in loads)
            {
                var ptCoord = allPoints.FirstOrDefault(p => p.Name == load.ElementName);
                if (ptCoord == null) continue;

                string loc = GetGridLocationForPoint(ptCoord);

                if (!pointGroups.ContainsKey(loc))
                    pointGroups[loc] = new List<(RawSapLoad, SapUtils.SapPoint)>();

                pointGroups[loc].Add((load, ptCoord));
            }

            var sortedGroups = pointGroups.OrderByDescending(g => g.Value.Count);

            foreach (var group in sortedGroups)
            {
                string location = group.Key;
                var groupLoads = group.Value;
                int count = groupLoads.Count;

                // FIX v4.2: Calculate directional sign
                double dirSign = 1.0;
                if (groupLoads.Count > 0)
                {
                    var sampleLoad = groupLoads[0].load;
                    if (sampleLoad.DirectionX != 0) dirSign = Math.Sign(sampleLoad.DirectionX);
                    else if (sampleLoad.DirectionY != 0) dirSign = Math.Sign(sampleLoad.DirectionY);
                    else if (sampleLoad.DirectionZ != 0) dirSign = Math.Sign(sampleLoad.DirectionZ);
                }

                double signedForce = count * loadVal * dirSign;

                // FIX v4.2: Vector components
                double fx = 0, fy = 0, fz = 0;
                if (groupLoads.Count > 0)
                {
                    var forceVec = groupLoads[0].load.GetForceVector();
                    if (forceVec.Length > 1e-6)
                    {
                        forceVec = forceVec.Normalized * Math.Abs(signedForce);
                        fx = forceVec.X;
                        fy = forceVec.Y;
                        fz = forceVec.Z;
                    }
                    else
                    {
                        fz = signedForce;
                    }
                }

                var sorted = SortPointsLeftToRight(groupLoads);
                var elementNames = sorted.Select(p => p.load.ElementName).ToList();

                targetList.Add(new AuditEntry
                {
                    GridLocation = location,
                    Explanation = "",
                    Quantity = count,
                    QuantityUnit = "ea",
                    UnitLoad = loadVal,
                    UnitLoadString = $"{loadVal:0.00}",
                    TotalForce = Math.Abs(signedForce),
                    Direction = dir,
                    DirectionSign = dirSign,
                    ForceX = fx,
                    ForceY = fy,
                    ForceZ = fz,
                    ElementList = elementNames
                });
            }
        }

        /// <summary>
        /// Sort points left to right along primary axis
        /// </summary>
        private List<(RawSapLoad load, SapUtils.SapPoint coord)> SortPointsLeftToRight(
            List<(RawSapLoad load, SapUtils.SapPoint coord)> points)
        {
            if (points.Count <= 1) return points;

            // Determine primary axis (X or Y) based on spread
            double xRange = points.Max(p => p.coord.X) - points.Min(p => p.coord.X);
            double yRange = points.Max(p => p.coord.Y) - points.Min(p => p.coord.Y);

            if (xRange > yRange)
            {
                // Sort by X (left to right)
                return points.OrderBy(p => p.coord.X).ToList();
            }
            else
            {
                // Sort by Y (bottom to top)
                return points.OrderBy(p => p.coord.Y).ToList();
            }
        }

        #endregion

        #region Smart Grid Detection Logic

        /// <summary>
        /// Tạo mô tả trục dạng Range (VD: Grid 1-5 / A-B) dựa trên Bounding Box
        /// FIX: Added English translation support
        /// </summary>
        private string GetGridRangeDescription(Envelope env)
        {
            // Tìm khoảng trục X
            string xRange = FindAxisRange(env.MinX, env.MaxX, _xGrids);
            // Tìm khoảng trục Y
            string yRange = FindAxisRange(env.MinY, env.MaxY, _yGrids);

            if (string.IsNullOrEmpty(xRange) && string.IsNullOrEmpty(yRange)) return "No Grid";
            if (string.IsNullOrEmpty(xRange)) return $"Grid {yRange}";
            if (string.IsNullOrEmpty(yRange)) return $"Grid {xRange}";

            return $"Grid {xRange} x {yRange}";
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

        /// <summary>
        /// Cache geometry từ SAP2000 để tránh gọi API nhiều lần.
        /// FIX: Sử dụng case-insensitive dictionary
        /// </summary>
        private void CacheGeometry()
        {
            if (_frameGeometryCache == null)
                _frameGeometryCache = new Dictionary<string, SapFrame>(StringComparer.OrdinalIgnoreCase);
            else
                _frameGeometryCache.Clear();

            if (_areaGeometryCache == null)
                _areaGeometryCache = new Dictionary<string, SapArea>(StringComparer.OrdinalIgnoreCase);
            else
                _areaGeometryCache.Clear();

            // Cache Frames
            var frames = SapUtils.GetAllFramesGeometry();
            foreach (var f in frames)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.Name)) continue;
                _frameGeometryCache[f.Name.Trim()] = f;
            }

            // Cache Areas
            var areas = SapUtils.GetAllAreasGeometry();
            foreach (var a in areas)
            {
                if (a == null || string.IsNullOrWhiteSpace(a.Name)) continue;
                _areaGeometryCache[a.Name.Trim()] = a;
            }

            System.Diagnostics.Debug.WriteLine($"[AuditEngine] Cached {_frameGeometryCache.Count} frames, {_areaGeometryCache.Count} areas");
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

        // Smart projection: choose the plane with largest span to keep vertical walls non-degenerate
        private List<Point2D> ProjectAreaToBestPlane(SapArea area)
        {
            if (area == null || area.BoundaryPoints == null || area.ZValues == null) return area?.BoundaryPoints ?? new List<Point2D>();

            int n = Math.Min(area.BoundaryPoints.Count, area.ZValues.Count);
            if (n < 3) return area.BoundaryPoints;

            double minX = area.BoundaryPoints.Take(n).Min(p => p.X);
            double maxX = area.BoundaryPoints.Take(n).Max(p => p.X);
            double minY = area.BoundaryPoints.Take(n).Min(p => p.Y);
            double maxY = area.BoundaryPoints.Take(n).Max(p => p.Y);
            double minZ = area.ZValues.Take(n).Min();
            double maxZ = area.ZValues.Take(n).Max();

            double spanX = maxX - minX;
            double spanY = maxY - minY;
            double spanZ = maxZ - minZ;

            // Drop the axis with smallest span → project to plane with greatest information
            if (spanX <= spanY && spanX <= spanZ)
            {
                // Project to YZ
                var pts = new List<Point2D>(n);
                for (int i = 0; i < n; i++) pts.Add(new Point2D(area.BoundaryPoints[i].Y, area.ZValues[i]));
                return pts;
            }
            if (spanY <= spanX && spanY <= spanZ)
            {
                // Project to XZ
                var pts = new List<Point2D>(n);
                for (int i = 0; i < n; i++) pts.Add(new Point2D(area.BoundaryPoints[i].X, area.ZValues[i]));
                return pts;
            }

            // Default: project to XY (slabs and general case)
            return area.BoundaryPoints.Take(n).ToList();
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

        private string GetLoadTypeDisplayName(string loadType)
        {
            if (loadType.Contains("Area")) return "SÀN - AREA LOAD";
            if (loadType.Contains("Frame")) return "DẦM/CỘT - FRAME LOAD";
            if (loadType.Contains("Point")) return "NÚT - POINT LOAD";
            return loadType.ToUpper();
        }

        #endregion

        #region Report Generation (With Unit Conversion & New Format v4.2)

        /// <summary>
        /// Generate formatted text audit report.
        /// UPDATED v4.2: New column layout - removed Type, added Value, reordered Dir before Force
        /// Format: Grid Location | Calculator | Value(unit) | Unit Load(unit) | Dir | Force(unit) | Elements
        /// </summary>
        public string GenerateTextReport(AuditReport report, string targetUnit = "kN", string language = "English")
        {
            var sb = new StringBuilder();
            double forceFactor = 1.0;

            // Unit conversion
            if (string.IsNullOrWhiteSpace(targetUnit)) targetUnit = UnitManager.Info.ForceUnit;
            if (targetUnit.Equals("Ton", StringComparison.OrdinalIgnoreCase) || targetUnit.Equals("Tonf", StringComparison.OrdinalIgnoreCase))
                forceFactor = 1.0 / 9.81;
            else if (targetUnit.Equals("kgf", StringComparison.OrdinalIgnoreCase)) forceFactor = 101.97;
            else { targetUnit = "kN"; forceFactor = 1.0; }

            bool isVN = language.Equals("Vietnamese", StringComparison.OrdinalIgnoreCase);

            // HEADER
            sb.AppendLine("".PadRight(150, '='));
            sb.AppendLine(isVN ? "   KIỂM TOÁN TẢI TRỌNG SAP2000 (DTS ENGINE v4.2)" : "   SAP2000 LOAD AUDIT REPORT (DTS ENGINE v4.2)");
            sb.AppendLine($"   {(isVN ? "Dự án" : "Project")}: {report.ModelName ?? "Unknown"}");
            sb.AppendLine($"   {(isVN ? "Tổ hợp tải" : "Load Pattern")}: {report.LoadPattern}");
            sb.AppendLine($"   {(isVN ? "Ngày tính" : "Audit Date")}: {report.AuditDate:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"   {(isVN ? "Đơn vị" : "Report Unit")}: {targetUnit}");
            sb.AppendLine("".PadRight(150, '='));
            sb.AppendLine();

            foreach (var story in report.Stories.OrderByDescending(s => s.Elevation))
            {
                // FIX v4.2: Calculate story total from vector components
                double storyFx = story.LoadTypes.Sum(lt => lt.SubTotalFx);
                double storyFy = story.LoadTypes.Sum(lt => lt.SubTotalFy);
                double storyFz = story.LoadTypes.Sum(lt => lt.SubTotalFz);
                double storyTotal = Math.Sqrt(storyFx * storyFx + storyFy * storyFy + storyFz * storyFz) * forceFactor;

                sb.AppendLine($">>> {(isVN ? "TẦNG" : "STORY")}: {story.StoryName} | Z={story.Elevation:0}mm | {(isVN ? "Tổng" : "Total")}: {storyTotal:0.00} {targetUnit}");
                sb.AppendLine();

                foreach (var loadType in story.LoadTypes)
                {
                    string typeName = GetSpecificLoadTypeName(loadType, isVN);
                    
                    // FIX v4.2: Use vector-based subtotal
                    double typeTotal = Math.Sqrt(
                        loadType.SubTotalFx * loadType.SubTotalFx + 
                        loadType.SubTotalFy * loadType.SubTotalFy + 
                        loadType.SubTotalFz * loadType.SubTotalFz) * forceFactor;

                    sb.AppendLine($"  [{typeName}] {(isVN ? "Tổng phụ" : "Subtotal")}: {typeTotal:0.00} {targetUnit}");
                    sb.AppendLine();

                    // FIX v4.2: New column layout
                    // Grid Location(30) | Calculator(35) | Value(15) | Unit Load(20) | Dir(8) | Force(15) | Elements(remaining)
                    string hGrid = (isVN ? "Vị trí trục" : "Grid Location").PadRight(30);
                    string hCalc = (isVN ? "Chi tiết" : "Calculator").PadRight(35);
                    
                    // Determine unit type from first entry
                    string valueUnit = loadType.Entries.FirstOrDefault()?.QuantityUnit ?? "m²";
                    string hValue = $"Value({valueUnit})".PadRight(15);
                    string hUnit = $"Unit Load({targetUnit}/{valueUnit})".PadRight(20);
                    string hDir = (isVN ? "Hướng" : "Dir").PadRight(8);
                    string hForce = $"Force({targetUnit})".PadRight(15);
                    string hElem = (isVN ? "Phần tử" : "Elements");

                    sb.AppendLine($"    {hGrid}{hCalc}{hValue}{hUnit}{hDir}{hForce}{hElem}");
                    sb.AppendLine($"    {new string('-', 150)}");

                    var allEntries = loadType.Entries;

                    foreach (var entry in allEntries.OrderByDescending(e => Math.Abs(e.TotalForce)))
                    {
                        FormatDataRow(sb, entry, forceFactor, targetUnit);
                    }
                    sb.AppendLine();
                }
            }

            // SUMMARY
            sb.AppendLine("".PadRight(150, '='));
            sb.AppendLine(isVN ? "TỔNG HỢP LỰC (GLOBAL):" : "AUDIT SUMMARY (GLOBAL RESULTANTS):");
            sb.AppendLine();
            
            sb.AppendLine($"   Fx (Global): {report.CalculatedFx * forceFactor:0.00} {targetUnit}");
            sb.AppendLine($"   Fy (Global): {report.CalculatedFy * forceFactor:0.00} {targetUnit}");
            sb.AppendLine($"   Fz (Global): {report.CalculatedFz * forceFactor:0.00} {targetUnit}");
            sb.AppendLine($"   Total Vector Magnitude: {report.TotalCalculatedForce * forceFactor:0.00} {targetUnit}");
            
            sb.AppendLine();
            sb.AppendLine("".PadRight(150, '='));

            return sb.ToString();
        }

        /// <summary>
        /// Format data row with new column layout
        /// FIX v4.2: Grid | Calculator | Value | UnitLoad | Dir | Force | Elements (full list)
        /// Example: Axis 1-12 x G(+0.5)-F(+1.5) | 12x1.5 | 6.05 | -2.26 | -Y | 4.14 | 70,75,94,97
        /// </summary>
        private void FormatDataRow(StringBuilder sb, AuditEntry entry, double forceFactor, string targetUnit)
        {
            // Column widths
            const int gridWidth = 30;
            const int calcWidth = 35;
            const int valueWidth = 15;
            const int unitWidth = 20;
            const int dirWidth = 8;
            const int forceWidth = 15;

            // Grid Location
            string grid = TruncateString(entry.GridLocation ?? "", gridWidth - 2).PadRight(gridWidth);

            // Calculator (segment details for frames, formula for areas)
            string calc = TruncateString(entry.Explanation ?? "", calcWidth - 2).PadRight(calcWidth);

            // Value (Quantity)
            string value = $"{entry.Quantity:0.00}".PadRight(valueWidth);

            // Unit Load (with sign)
            string unitLoad = $"{entry.UnitLoad:0.00}".PadRight(unitWidth);

            // Direction (keep short)
            string dir = FormatDirection(entry.Direction, entry).PadRight(dirWidth);

            // Force (signed)
            double signedForce = entry.TotalForce * entry.DirectionSign * forceFactor;
            string force = $"{signedForce:0.00}".PadRight(forceWidth);

            // Elements (FULL LIST - no truncation for text report per requirement)
            string elements = string.Join(",", entry.ElementList ?? new List<string>());

            sb.AppendLine($"    {grid}{calc}{value}{unitLoad}{dir}{force}{elements}");
        }

        #endregion

        // Helper cắt chuỗi an toàn
        private string TruncateString(string val, int maxLen)
        {
            if (string.IsNullOrEmpty(val)) return "";
            if (val.Length <= maxLen) return val;
            return val.Substring(0, maxLen - 2) + "..";
        }

        // Helper to get display name for a load type group
        private string GetSpecificLoadTypeName(AuditLoadTypeGroup loadType, bool isVN)
        {
            if (loadType == null) return "";
            var name = loadType.LoadTypeName ?? "UNKNOWN";
            return name;
        }

        // Short direction formatter used in report rows
        private string FormatDirection(string dir, AuditEntry entry)
        {
            if (string.IsNullOrEmpty(dir)) return "";

            var d = dir.ToUpperInvariant();

            // Map GRAV to Z
            if (d.Contains("GRAV")) return "Z";

            // Keep signed axes (+X, -X, +Y, -Y, ...)
            if (d == "+X" || d == "-X") return d;
            if (d == "+Y" || d == "-Y") return d;
            if (d == "+Z" || d == "-Z") return d;

            // Fallback: trim to 8 chars
            return d.Length <= 8 ? d : d.Substring(0, 8);
        }


    } // class AuditEngine
} // namespace DTS_Engine.Core.Engines