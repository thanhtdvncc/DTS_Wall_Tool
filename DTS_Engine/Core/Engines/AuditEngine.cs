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

        /// <summary>
        /// Helper: Tính hệ số nhân cho tải trọng (Area, Length, hoặc Point)
        /// FIX BUG #5: Sử dụng case-insensitive lookup và fallback
        /// </summary>
        private double CalculateLoadMultiplier(RawSapLoad load)
        {
            // FIX BUG #5: Normalize element name for lookup
            string elementName = load.ElementName?.Trim();
            if (string.IsNullOrEmpty(elementName)) return 0.0;
            
            // Area loads: Nhân với diện tích (m²)
            if (load.LoadType.Contains("Area"))
            {
                var areaGeom = GetAreaGeometry(elementName);
                if (areaGeom != null)
                    return areaGeom.Area / 1_000_000.0; // mm² -> m²
                
                // FIX: Try case-insensitive search if direct lookup fails
                foreach (var kvp in _areaGeometryCache)
                {
                    if (kvp.Key.Equals(elementName, StringComparison.OrdinalIgnoreCase))
                        return kvp.Value.Area / 1_000_000.0;
                }
                
                // Still not found - return 1.0 as fallback (count-based)
                System.Diagnostics.Debug.WriteLine($"[AuditEngine] Warning: Area geometry not found for '{elementName}'. Using count=1.");
                return 1.0;
            }
            
            // Frame distributed loads: Nhân với chiều dài (m)
            if (load.LoadType.Contains("Frame") && !load.LoadType.Contains("Point"))
            {
                var frameGeom = GetFrameGeometry(elementName);
                if (frameGeom != null)
                {
                    double len = frameGeom.Length2D / 1000.0; // mm -> m
                    
                    // Handle partial distributed loads
                    if (!load.IsRelative && Math.Abs(load.DistEnd - load.DistStart) > 0.001)
                        len = Math.Abs(load.DistEnd - load.DistStart) / 1000.0;
                    
                    return len;
                }
                
                // FIX: Try case-insensitive search
                foreach (var kvp in _frameGeometryCache)
                {
                    if (kvp.Key.Equals(elementName, StringComparison.OrdinalIgnoreCase))
                    {
                        double len = kvp.Value.Length2D / 1000.0;
                        if (!load.IsRelative && Math.Abs(load.DistEnd - load.DistStart) > 0.001)
                            len = Math.Abs(load.DistEnd - load.DistStart) / 1000.0;
                        return len;
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[AuditEngine] Warning: Frame geometry not found for '{elementName}'. Using length=1m.");
                return 1.0;
            }
            
            // Point loads: Hệ số = 1
            if (load.LoadType.Contains("Point"))
                return 1.0;

            return 1.0; // FIX: Changed from 0.0 to 1.0 as safe fallback
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
        /// FIX BUG #6: Sửa logic phân tầng - duyệt từ THẤP LÊN CAO
        /// </summary>
        private List<TempStoryBucket> GroupLoadsByStory(List<RawSapLoad> loads)
        {
            // Bước 1: Xác định cao độ của các tầng từ SAP
            var stories = SapUtils.GetStories()
                .Where(s => s.IsElevation)
                .OrderBy(s => s.Coordinate)
                .ToList();
            
            if (stories.Count == 0)
            {
                // Fallback: Tạo một tầng duy nhất nếu không có thông tin
                var singleBucket = new TempStoryBucket
                {
                    StoryName = "All",
                    Elevation = 0,
                    Loads = loads.ToList()
                };
                return new List<TempStoryBucket> { singleBucket };
            }

            // Bước 2: Tạo buckets cho từng tầng
            var buckets = stories.Select(s => new TempStoryBucket
            {
                StoryName = s.Name ?? s.StoryName ?? $"Z={s.Coordinate}",
                Elevation = s.Coordinate,
                Loads = new List<RawSapLoad>()
            }).ToList();

            // FIX BUG #6: Sort buckets từ CAO xuống THẤP để phân tải đúng
            // Logic: Một phần tử thuộc tầng nào nếu Z của nó >= Elevation của tầng đó 
            //        nhưng < Elevation của tầng trên
            var sortedBuckets = buckets.OrderBy(b => b.Elevation).ToList();

            foreach (var load in loads)
            {
                double z = load.ElementZ;
                bool assigned = false;

                // FIX: Duyệt từ tầng CAO NHẤT xuống để tìm tầng phù hợp
                // Tải thuộc tầng N nếu: Elevation[N] <= Z < Elevation[N+1]
                for (int i = sortedBuckets.Count - 1; i >= 0; i--)
                {
                    double thisElevation = sortedBuckets[i].Elevation;
                    double tolerance = 500.0; // 500mm tolerance
                    
                    // Điều kiện: Z >= Elevation - tolerance
                    if (z >= thisElevation - tolerance)
                    {
                        sortedBuckets[i].Loads.Add(load);
                        assigned = true;
                        break;
                    }
                }

                // Fallback: Gán vào tầng thấp nhất nếu Z quá thấp
                if (!assigned && sortedBuckets.Count > 0)
                {
                    sortedBuckets[0].Loads.Add(load);
                }
            }

            // Trả về theo thứ tự từ thấp lên cao
            return sortedBuckets;
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
                // Fallback: Nếu lỗi thư viện NTS, dùng GeometryCollection (không gộp nhưng không mất)
                processedGeometry = _geometryFactory.CreateGeometryCollection(geometries.ToArray());
            }

            // 3. Tạo Audit Entry từ kết quả
            for (int i = 0; i < processedGeometry.NumGeometries; i++)
            {
                var geom = processedGeometry.GetGeometryN(i);
                if (geom.Area < 1e-6) continue;

                double areaM2 = geom.Area / 1.0e6;
                
                // ✅ FIX: Gọi Smart Shape Analysis
                var shapeResult = AnalyzeShapeStrategy(geom);
                string formula = shapeResult.IsExact ? shapeResult.Formula : $"~{areaM2:0.00}m²";
                
                targetList.Add(new AuditEntry
                {
                    GridLocation = GetGridRangeDescription(geom.EnvelopeInternal),
                    Explanation = formula, // ← Hiện công thức thay vì chỉ số
                    Quantity = areaM2,
                    QuantityUnit = "m²",
                    UnitLoad = loadVal,
                    UnitLoadString = $"{loadVal:0.00} {UnitManager.Info.ForceUnit}/m²",
                    TotalForce = areaM2 * loadVal,
                    Direction = dir,
                    ElementList = validLoads.Select(l => l.ElementName).Distinct().ToList() // ← FIX #5 luôn
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
                
                // ✅ FIX: Tính covered length từ I, J
                double coveredLength = 0;
                if (load.IsRelative)
                {
                    // Relative distance (0-1)
                    coveredLength = frame.Length2D * Math.Abs(load.DistEnd - load.DistStart) / 1000.0;
                }
                else
                {
                    // Absolute distance (mm)
                    coveredLength = Math.Abs(load.DistEnd - load.DistStart) / 1000.0;
                }
                
                // Fallback: Full length nếu không có dist info
                if (coveredLength < 0.001)
                    coveredLength = frame.Length2D / 1000.0;

                frameItems.Add(new FrameAuditItem
                {
                    Load = load,
                    Frame = frame,
                    PrimaryGrid = primaryGrid,
                    Length = coveredLength // ← Dùng covered length thay vì full length
                });
            }

            if (frameItems.Count == 0) return;

            var groups = frameItems.GroupBy(f => f.PrimaryGrid);

            foreach (var grp in groups)
            {
                string gridName = grp.Key;
                double totalLength = grp.Sum(f => f.Length); // ← Giờ đây đúng rồi
                double totalForce = totalLength * loadVal;

                string rangeDesc = DetermineCrossAxisRange(grp.ToList());
                string location = gridName;
                
                // ✅ FIX: Thêm info I-J vào Explanation
                var segments = grp.Select(f => $"{f.Load.ElementName}[{f.Load.DistStart/1000:0.0}-{f.Load.DistEnd/1000:0.0}m]").ToList();
                string explanation = string.IsNullOrEmpty(rangeDesc)
                    ? $"L = {totalLength:0.00}m"
                    : $"{rangeDesc} (L={totalLength:0.00}m) | {string.Join(", ", segments.Take(3))}{(segments.Count > 3 ? "..." : "")}";

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
                    ElementList = elementNames // ← FIX #5
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
            
            // ✅ FIX: Nhóm theo grid intersection
            var pointGroups = new Dictionary<string, List<RawSapLoad>>();
            
            foreach (var load in loads)
            {
                var ptCoord = allPoints.FirstOrDefault(p => p.Name == load.ElementName);
                if (ptCoord == null) continue;

                string loc = GetGridLocationForPoint(ptCoord);
                
                if (!pointGroups.ContainsKey(loc))
                    pointGroups[loc] = new List<RawSapLoad>();
                
                pointGroups[loc].Add(load);
            }
            
            // ✅ FIX: Sắp xếp theo số lượng point (nhiều nhất trước)
            var sortedGroups = pointGroups.OrderByDescending(g => g.Value.Count);
            
            foreach (var group in sortedGroups)
            {
                string location = group.Key;
                var groupLoads = group.Value;
                int count = groupLoads.Count;
                double totalForce = count * loadVal;
                
                // ✅ FIX: Tạo công thức cộng
                string formula = count > 1 
                    ? $"{count} points ({string.Join("+", Enumerable.Range(1, Math.Min(count, 5)))}{ (count > 5 ? "..." : "")})"
                    : groupLoads[0].ElementName;
                
                var elementNames = groupLoads.Select(l => l.ElementName).ToList();
                
                targetList.Add(new AuditEntry
                {
                    GridLocation = location,
                    Explanation = formula, // ← Hiện công thức cộng
                    Quantity = count,
                    QuantityUnit = "ea",
                    UnitLoad = loadVal,
                    UnitLoadString = $"{loadVal:0.00} {UnitManager.Info.ForceUnit}",
                    TotalForce = totalForce,
                    Direction = dir,
                    ElementList = elementNames // ← FIX #5
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
            if (targetUnit.Equals("Tonf", StringComparison.OrdinalIgnoreCase)) forceFactor = 1.0 / 9.81;
            else if (targetUnit.Equals("kgf", StringComparison.OrdinalIgnoreCase)) forceFactor = 101.97;
            else if (targetUnit.Equals("lb", StringComparison.OrdinalIgnoreCase)) forceFactor = 224.8;
            else { targetUnit = "kN"; forceFactor = 1.0; }

            bool isVietnamese = language.Equals("Vietnamese", StringComparison.OrdinalIgnoreCase);
            
            // ✅ FIX: Translation dictionary
            string axisLabel = isVietnamese ? "Trục" : "Axis";
            
            // Header
            sb.AppendLine("===========================================================================================================");
            sb.AppendLine(isVietnamese ? "   KIỂM TOÁN TẢI TRỌNG (DTS ENGINE)" : "   SAP2000 LOAD AUDIT REPORT (DTS ENGINE)");
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
                    if (typeHeader.Contains("AREA")) typeHeader = isVietnamese ? "[1] AREA LOADS (SÀN)" : "[1] AREA LOADS (SLAB)";
                    else if (typeHeader.Contains("FRAME")) typeHeader = isVietnamese ? "[2] FRAME LOADS (DẦM/CỘT)" : "[2] FRAME LOADS (BEAM/COLUMN)";
                    else if (typeHeader.Contains("POINT")) typeHeader = isVietnamese ? "[3] POINT LOADS (NÚT)" : "[3] POINT LOADS (NODE)";

                    sb.AppendLine($" {typeHeader}");
                    // Optional notes
                    if (typeHeader.Contains("FRAME"))
                        sb.AppendLine(isVietnamese ? " * Ghi chú: Khối lượng (Qty) là tổng chiều dài L của các phần tử chịu tải." : " * Note: Qty represents total length (L) of loaded elements.");
                    if (typeHeader.Contains("POINT"))
                        sb.AppendLine(isVietnamese ? " * Ghi chú: Khối lượng (Qty) là số lượng điểm (No.) có tải." : " * Note: Qty represents count (No.) of loaded points.");

                    sb.AppendLine(" ---------------------------------------------------------------------------------------------------------");
                    if (isVietnamese)
                        sb.AppendLine($" | {"Vị Trí (Trục/Vùng)",-26} | {"Diễn Giải / Kích Thước (m)",-30} | {"Kh.Lượng",-10} | {"Tải đơn vị",-12} | {"Tổng (" + targetUnit + ")",12} |");
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

                        // Replace "Trục" → "Axis" nếu English
                        string loc = entry.GridLocation;
                        if (!isVietnamese)
                        {
                            loc = loc.Replace("Trục", "Axis")
                                     .Replace("Xiên", "Diagonal");
                        }
                        
                        string desc = entry.Explanation;

                        string qtyStr = (entry.QuantityUnit?.ToLowerInvariant() ?? "").Contains("²")
                            ? $"{entry.Quantity,8:0.00} m²"
                            : (entry.QuantityUnit?.Equals("ea", StringComparison.OrdinalIgnoreCase) == true
                                ? $"{entry.Quantity,8:0} No."
                                : $"{entry.Quantity,8:0.00} m");

                        string forceStr = $"{displayForce:N2}";

                        // ✅ FIX: Format direction với dấu
                        string dirStr = FormatDirection(entry.Direction, entry); // ← New method

                        // Table header
sb.AppendLine($" | {"Location",-20} | {"Formula",-25} | {"Qty",-10} | {"Load",-12} | {"Dir",-8} | {"Total",12} | {"Elements",-30} |");

                        // Row data
                        string elemList = entry.ElementList != null && entry.ElementList.Count > 0
                            ? string.Join(",", entry.ElementList.Take(5)) + (entry.ElementList.Count > 5 ? "..." : "")
                            : "-";

                        // Word wrap columns
                        var locLines = SplitTextToWidth(loc, 20);
                        var descLines = SplitTextToWidth(desc, 25);

                        int maxLines = Math.Max(locLines.Count, descLines.Count);
                        if (maxLines == 0) maxLines = 1;

                        for (int i = 0; i < maxLines; i++)
                        {
                            string locPart = i < locLines.Count ? locLines[i] : "";
                            string descPart = i < descLines.Count ? descLines[i] : "";
                            string q = i == 0 ? qtyStr : "";
                            string u = i == 0 ? unitStr : "";
                            string d = i == 0 ? dirStr : ""; // ← Direction
                            string t = i == 0 ? forceStr : "";

                            sb.AppendLine(string.Format(" | {0,-20} | {1,-25} | {2,-10} | {3,-12} | {4,-8} | {5,12} | {6,-30} |",
                                locPart, descPart, q, u, d, t, elemList));
                        }
                    }

                    sb.AppendLine(" ---------------------------------------------------------------------------------------------------------");
                    double typeTotal = loadType.TotalForce * forceFactor;
                    string subTotalLabel = isVietnamese ? "TỔNG:" : "SUB-TOTAL:";
                    sb.AppendLine(string.Format(" {0,86} {1,12:N2}", subTotalLabel, typeTotal));
                    sb.AppendLine();
                }
            }

            // Summary & evaluation
            double totalCalc = report.TotalCalculatedForce * forceFactor;

            // NEW: Hiển thị Vector components
            double totalFx = 0, totalFy = 0, totalFz = 0;
            foreach (var story in report.Stories)
            {
                foreach (var loadType in story.LoadTypes)
                {
                    foreach (var entry in loadType.Entries)
                    {
                        // Tính lại từ magnitude và direction (approximation)
                        // Lưu ý: Entry không có trực tiếp DirectionX/Y/Z, chỉ có Direction string
                        // Vì vậy ta cộng dồn TotalForce theo trục dominant
                        if (entry.Direction != null && entry.Direction.ToUpperInvariant().Contains("X"))
                            totalFx += entry.TotalForce;
                        else if (entry.Direction != null && entry.Direction.ToUpperInvariant().Contains("Y"))
                            totalFy += entry.TotalForce;
                        else
                            totalFz += entry.TotalForce;
                    }
                }
            }

            totalFx *= forceFactor;
            totalFy *= forceFactor;
            totalFz *= forceFactor;

            sb.AppendLine("===========================================================================================================");
            sb.AppendLine(isVietnamese ? "   TỔNG HỢP & ĐÁNH GIÁ" : "   SUMMARY & EVALUATION");
            
            string calcLabel = isVietnamese ? "TỔNG CỘNG TÍNH TOÁN" : "TOTAL CALCULATED";
            sb.AppendLine($"   1. {calcLabel}:  {totalCalc,12:N2} {targetUnit}");
            
            // NEW: Hiển thị Vector breakdown
            string vectorLabel = isVietnamese ? "      Phân tích Vector" : "      Vector Breakdown";
            sb.AppendLine($"   {vectorLabel}:");
            sb.AppendLine($"      - Fx (X-direction): {totalFx,12:N2} {targetUnit}");
            sb.AppendLine($"      - Fy (Y-direction): {totalFy,12:N2} {targetUnit}");
            sb.AppendLine($"      - Fz (Z-direction): {totalFz,12:N2} {targetUnit}");

            if (report.IsAnalyzed)
            {
                double baseReact = report.SapBaseReaction * forceFactor;
                double diff = totalCalc - Math.Abs(baseReact);
                
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
                string notAnalyzedLabel = isVietnamese 
                    ? "   2. KIỂM TRA THỦ CÔNG: Vui lòng so sánh Vector trên với Base Reactions trong SAP2000" 
                    : "   2. MANUAL CHECK: Please compare Vector above with Base Reactions in SAP2000";
                sb.AppendLine(notAnalyzedLabel);
                sb.AppendLine();
                sb.AppendLine(isVietnamese 
                    ? "   >>> Để xem Base Reactions: SAP2000 > Display > Show Tables > Analysis Results > Base Reactions"
                    : "   >>> To view Base Reactions: SAP2000 > Display > Show Tables > Analysis Results > Base Reactions");
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

        // ✅ NEW METHOD: Format direction with sign
        private string FormatDirection(string direction, AuditEntry entry)
        {
            // Parse vector components from entry (cần thêm vào AuditEntry class)
            if (direction == null) return "-";
            
            string dir = direction.ToUpperInvariant();
            
            // Simplified: Just show axis (X/Y/Z)
            if (dir.Contains("X")) return "X";
            if (dir.Contains("Y")) return "Y";
            if (dir.Contains("Z") || dir.Contains("GRAVITY")) return "-Z"; // Gravity = -Z
            
            return dir.Length > 8 ? dir.Substring(0, 8) : dir;
        }
    }
}