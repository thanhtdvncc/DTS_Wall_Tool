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

        // ĐƠN VỊ: Tất cả tính toán nội bộ dùng UNIT từ SAP hiện tại (thường là mm)
        // GroupLoadsByStory: dùng load.ElementZ từ SapDatabaseReader (đơn vị SAP gốc)
        // ProcessAreaLoads: switch sang kN_m_C (mét) để tính area
        private const double STORY_TOLERANCE_MM = 200.0; // mm - cho grouping stories
        private const double GRID_SNAP_TOLERANCE_MM = 250.0; // mm - cho snap grids
        private const double MIN_AREA_THRESHOLD_M2 = 0.0001; // m² - threshold cho area

        // Cache grids tách biệt X và Y để tìm kiếm nhanh
        private List<SapUtils.GridLineRecord> _xGrids;
        private List<SapUtils.GridLineRecord> _yGrids;
        private List<SapUtils.GridStoryItem> _stories;

        // Model Inventory (Source of Truth for Geometry & Vectors)
        private readonly ModelInventory _inventory;

        // NTS Factory
        private GeometryFactory _geometryFactory;

        // CRITICAL: Load Reader injected via Constructor (Dependency Injection)
        private readonly ISapLoadReader _loadReader;

        // Geometry caches (used by CacheGeometry and legacy code paths)
        private Dictionary<string, SapFrame> _frameGeometryCache;
        private Dictionary<string, SapArea> _areaGeometryCache;

        /// <summary>
        /// Call back để in log debug ra ngoài (Command Line).
        /// Nếu null thì không in gì cả (Normal mode).
        /// </summary>
        public Action<string> DebugLogger { get; set; }

        private void Log(string message)
        {
            DebugLogger?.Invoke(message);
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor với Dependency Injection.
        /// Updated to accept ModelInventory.
        /// </summary>
        public AuditEngine(ISapLoadReader loadReader, ModelInventory inventory)
        {
            _loadReader = loadReader ?? throw new ArgumentNullException(nameof(loadReader),
                "ISapLoadReader is required.");
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory), 
                "ModelInventory is required used for Vector Axis lookup.");

            _geometryFactory = new GeometryFactory();

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

            var allLoads = _loadReader.ReadAllLoads(loadPattern);

            if (allLoads.Count == 0)
            {
                report.IsAnalyzed = false;
                report.SapBaseReaction = 0;
                return report;
            }

            // --- PROCESS DATA ---
            var storyBuckets = GroupLoadsByStory(allLoads);
            Log($"[AUDIT-START] Identified {storyBuckets.Count} potential stories.");
            
            foreach (var bucket in storyBuckets.OrderByDescending(b => b.Elevation))
            {
                if (bucket.Loads.Count == 0) continue;
                
                Log($"[AUDIT-PROC] Processing Story {bucket.StoryName} with {bucket.Loads.Count} loads...");
                
                var storyGroup = ProcessStory(bucket.StoryName, bucket.Elevation, bucket.Loads);
                
                if (storyGroup != null && storyGroup.LoadTypes.Count > 0)
                {
                    report.Stories.Add(storyGroup);
                    Log($"[AUDIT-ADD] Story {bucket.StoryName} added to report.");
                }
                else
                {
                     Log($"[AUDIT-WARN] Story {bucket.StoryName} processed but result empty/null.");
                }
            }

            // --- CALCULATE SUMMARY ---
            double aggFx = 0;
            double aggFy = 0;
            double aggFz = 0;

            foreach (var story in report.Stories)
            {
                foreach (var loadType in story.LoadTypes)
                {
                    aggFx += loadType.SubTotalFx;
                    aggFy += loadType.SubTotalFy;
                    aggFz += loadType.SubTotalFz;
                }
            }

            report.CalculatedFx = aggFx;
            report.CalculatedFy = aggFy;
            report.CalculatedFz = aggFz;

            // Base Reaction = 0 (Check thủ công hoặc đọc từ SAP nếu cần)
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
        /// Determine Z-coordinate for story assignment directly from Inventory.
        /// </summary>
        private double DetermineElementStoryZ(RawSapLoad load)
        {
            if (load == null) return 0.0;

            var info = _inventory.GetElement(load.ElementName);
            if (info != null)
            {
                return info.GetStoryElevation();
            }

            // Fallback to raw ElementZ from reader
            return load.ElementZ;
        }

        /// <summary>
        /// Nhóm tải theo tầng (Story) dựa trên cao độ Z của phần tử.
        /// Uses DetermineElementStoryZ to pick top for vertical elements.
        /// </summary>
        /// <summary>
        /// Nhóm tải theo tầng (Story) dựa trên cao độ Z của phần tử.
        /// REFACTORED: Dynamic Clustering (VBA-style) to catch intermediate levels.
        /// </summary>
        private List<TempStoryBucket> GroupLoadsByStory(List<RawSapLoad> loads)
        {
            // 1. Collect all valid Z-coordinates
            var distinctZ = loads.Select(l => Math.Round(l.ElementZ, 1)) // Round to 0.1mm to reduce noise
                                 .Distinct()
                                 .OrderBy(z => z)
                                 .ToList();

            if (distinctZ.Count == 0) return new List<TempStoryBucket>();

            // 2. Cluster Z-coordinates into physical levels (using tolerance)
            // Each cluster represents a physical floor (e.g. 500mm, 4500mm, 6500mm)
            var clusters = new List<List<double>>();
            if (distinctZ.Any())
            {
                var currentCluster = new List<double> { distinctZ[0] };
                clusters.Add(currentCluster);

                for (int i = 1; i < distinctZ.Count; i++)
                {
                    double z = distinctZ[i];
                    double prevZ = currentCluster.Average(); // Compare with cluster center/avg
                    
                    if (Math.Abs(z - prevZ) < STORY_TOLERANCE_MM) 
                    {
                        currentCluster.Add(z);
                    }
                    else
                    {
                        currentCluster = new List<double> { z };
                        clusters.Add(currentCluster);
                    }
                }
            }

            // 3. Create buckets from clusters
            var buckets = new List<TempStoryBucket>();
            var sortedStories = _stories.OrderBy(s => s.Coordinate).ToList(); // Defined SAP Stories

            foreach (var cluster in clusters)
            {
                double avgZ = cluster.Average();
                
                // Try to name the cluster using SAP Stories
                string bucketName = $"Elevation Z={avgZ:0}"; // Default name
                
                // Find matching SAP story
                var match = sortedStories.FirstOrDefault(s => Math.Abs(s.Coordinate - avgZ) < STORY_TOLERANCE_MM);
                if (match != null)
                {
                    bucketName = match.Name ?? match.StoryName ?? $"Z={match.Coordinate}";
                }

                buckets.Add(new TempStoryBucket
                {
                    StoryName = bucketName,
                    Elevation = avgZ,
                    Loads = new List<RawSapLoad>()
                });
            }

            // 4. Assign loads to the nearest bucket
            // Since we built buckets from the actual data, every load MUST find a home nearby.
            foreach (var load in loads)
            {
                double z = load.ElementZ;
                var bestBucket = buckets.FirstOrDefault(b => Math.Abs(b.Elevation - z) < STORY_TOLERANCE_MM);

                // Fallback: Find absolute nearest (shouldn't happen often if clustering is correct)
                if (bestBucket == null)
                {
                    bestBucket = buckets.OrderBy(b => Math.Abs(b.Elevation - z)).First();
                }

                bestBucket.Loads.Add(load);
            }

            // 5. Remove empty buckets and Sort Descending (Top-down)
            var result = buckets.Where(b => b.Loads.Count > 0)
                                .OrderByDescending(b => b.Elevation)
                                .ToList();

            Log($"[STORY-SUMMARY] Dynamic Grouping: {result.Count} levels identified from {loads.Count} loads.");
            foreach (var b in result)
            {
                Log($"   > {b.StoryName} (Z~{b.Elevation:F0}): {b.Loads.Count} loads");
            }

            return result;
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

            // DEBUG: Log all load types received
            var loadTypeSummary = loads.GroupBy(l => l.LoadType).Select(g => $"{g.Key}:{g.Count()}");
            Log($"   [LOADTYPES] {storyName}: {string.Join(", ", loadTypeSummary)}");

            // Gom nhóm theo loại tải (Area, Frame, Point)
            var loadTypeGroups = loads.GroupBy(l => l.LoadType);

            foreach (var typeGroup in loadTypeGroups)
            {
                var typeResult = ProcessLoadType(typeGroup.Key, typeGroup.ToList());
                if (typeResult.Entries != null && typeResult.Entries.Count > 0)
                    storyGroup.LoadTypes.Add(typeResult);
                else
                    Log($"   [STORY-SKIP] Type {typeGroup.Key} in {storyName} produced 0 entries.");
            }

            Log($"   [STORY-DONE] {storyName}: {storyGroup.LoadTypes.Count} load types, {storyGroup.LoadTypes.Sum(t => t.Entries.Count)} total entries.");
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

            // [FIX v4.13] Area loads: Chỉ group theo Value (BỎ Direction!)
            // Vì ProcessAreaLoads sẽ tự xử lý Direction dựa trên effectiveAxis của element.
            // Frame/Point loads: Vẫn group theo cả Value và Direction.
            
            if (loadType.Contains("Area"))
            {
                // Area: Group CHỈ theo Value
                var valueOnlyGroups = loads.GroupBy(l => Math.Round(l.Value1, 3));
                foreach (var group in valueOnlyGroups.OrderByDescending(g => g.Key))
                {
                    double val = group.Key;
                    var subLoads = group.ToList();
                    ProcessAreaLoads(subLoads, val, "", typeGroup.Entries);
                }
            }
            else
            {
                // Frame/Point: Group theo Value + Direction
                var valueGroups = loads.GroupBy(l => new { Val = Math.Round(l.Value1, 3), Dir = l.Direction });
                foreach (var group in valueGroups.OrderByDescending(g => g.Key.Val))
                {
                    double val = group.Key.Val;
                    string dir = group.Key.Dir;
                    var subLoads = group.ToList();

                    if (loadType.Contains("Frame"))
                    {
                        ProcessFrameLoads(subLoads, val, dir, typeGroup.Entries);
                    }
                    else
                    {
                        ProcessPointLoads(subLoads, val, dir, typeGroup.Entries);
                    }
                }
            }

            // CRITICAL v4.4: Calculate vector subtotals ONLY from processed AuditEntry.ForceX/Y/Z
            // This ensures: LoadType Total = Sum of its entries (no raw data interference)
            typeGroup.SubTotalFx = typeGroup.Entries.Sum(e => e.ForceX);
            typeGroup.SubTotalFy = typeGroup.Entries.Sum(e => e.ForceY);
            typeGroup.SubTotalFz = typeGroup.Entries.Sum(e => e.ForceZ);

            // [FIX v4.9] User-defined Sorting: Direction (+X,-X,+Y,-Y) -> Grid Alpha -> Grid Num
            typeGroup.Entries = typeGroup.Entries
                .OrderBy(e => GetDirectionSortIndex(e.Direction))
                .ThenBy(e => GetGridAlphaSort(e.GridLocation))
                .ThenBy(e => GetGridNumericSort(e.GridLocation))
                .ToList();

            return typeGroup;
        }

        // --- SORTING HELPERS (v4.9) ---

        private int GetDirectionSortIndex(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return 9;
            dir = dir.ToUpper().Trim();
            if (dir.Contains("+X")) return 1;
            if (dir.Contains("-X")) return 2;
            if (dir.Contains("+Y")) return 3;
            if (dir.Contains("-Y")) return 4;
            return 5; // Others (Z, etc)
        }

        private string GetGridAlphaSort(string location)
        {
            if (string.IsNullOrEmpty(location)) return "ZZZ";
            // Remove offset parts "(...)" to avoid parsing units as text
            var mainPart = location.Split('(')[0]; 
            var tokens = mainPart.Split(new[] { ' ', '-', 'x', '/', ',' }, StringSplitOptions.RemoveEmptyEntries);
            
            // Find first token that is a Letter (A, B, AA...) and NOT keywords
            var alphas = tokens.Where(t => 
                char.IsLetter(t[0]) && 
                !t.Equals("Grid", StringComparison.OrdinalIgnoreCase) && 
                !t.Equals("No", StringComparison.OrdinalIgnoreCase)
            ).OrderBy(t => t); // Order to find Min (A < B)

            return alphas.FirstOrDefault() ?? "ZZZ";
        }

        private double GetGridNumericSort(string location)
        {
            if (string.IsNullOrEmpty(location)) return 999999;
             // Remove offset parts "(...)" to avoid parsing offset numbers
            var mainPart = location.Split('(')[0];
            var tokens = mainPart.Split(new[] { ' ', '-', 'x', '/', ',' }, StringSplitOptions.RemoveEmptyEntries);

            var nums = new List<double>();
            foreach (var t in tokens)
            {
                if (double.TryParse(t, out double d)) nums.Add(d);
            }

            return nums.Count > 0 ? nums.Min() : 999999;
        }

        // --- XỬ LÝ TẢI DIỆN TÍCH (AREA - SMART GEOMETRY RECOGNITION & DECOMPOSITION) ---
        /// <summary>
        /// Process area loads with vector-aware force calculation
        ///  4.5 SỬA LỖI GỘP SAI KHÔNG GIAN ---
        /// CRITICAL v4.4: ForceX/Y/Z calculated with CORRECT SIGN from loadVal (already signed from SAP)
        /// 
        /// SIGN CONVENTION:
        /// - loadVal already contains sign from SAP (negative for downward gravity)
        /// - No manual sign adjustment needed
        /// - Force magnitude = areaM2 * loadVal (preserves sign)
        /// - Vector components = forceVector.Normalized * signedForce (preserves direction)
        /// 
        /// RESULT:
        /// - AuditEntry.ForceX/Y/Z have correct signs for summation
        /// - Report displays these values directly (no conversion)
        /// </summary>
        private struct AreaGroupingKey : IEquatable<AreaGroupingKey>
        {
            public string GlobalAxis; // "Global +Z", "Global -X", etc.
            public double Position;   // Coordinate (rounded to 50mm)
            public double LoadValue;  // Signed Value
            public string LoadDir;    // SAP Direction String (e.g. "Local 3")
            public int DirectionSign; // +1/-1

            public bool Equals(AreaGroupingKey other)
            {
                return string.Equals(GlobalAxis, other.GlobalAxis) &&
                       Math.Abs(Position - other.Position) < 50.0 &&
                       Math.Abs(LoadValue - other.LoadValue) < 0.001 &&
                       string.Equals(LoadDir, other.LoadDir) &&
                       DirectionSign == other.DirectionSign;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 23 + (GlobalAxis?.GetHashCode() ?? 0);
                    hash = hash * 23 + Math.Round(Position / 50.0).GetHashCode();
                    hash = hash * 23 + Math.Round(LoadValue, 3).GetHashCode();
                    hash = hash * 23 + (LoadDir?.GetHashCode() ?? 0);
                    hash = hash * 23 + DirectionSign;
                    return hash;
                }
            }
        }


        /// <summary>
        /// ProcessAreaLoads v5.1 - DIRECT API với Unit Switch
        /// - Tạm chuyển SAP về kN_m_C để lấy tọa độ mét, load kN/m²
        /// - Sau xử lý, trả lại unit gốc
        /// </summary>
        private void ProcessAreaLoads(List<RawSapLoad> loads, double loadVal, string dir, List<AuditEntry> targetList)
        {
            Log($"[ProcessAreaLoads v5.1] Processing {loads.Count} loads. Val={loadVal}");

            var model = SapUtils.GetModel();
            if (model == null) { Log("[ERROR] Cannot get SAP Model"); return; }

            // === BƯỚC 1: LƯU UNIT HIỆN TẠI VÀ CHUYỂN SANG kN_m_C ===
            var originalUnit = SapUtils.GetSapCurrentUnit();
            bool unitSwitched = false;
            try
            {
                // Chuyển SAP về kN_m_C (mét, kN) - đơn vị chuẩn
                if (model.SetPresentUnits(SAP2000v1.eUnits.kN_m_C) == 0)
                {
                    unitSwitched = true;
                    Log($"   [UNIT] Switched to kN_m_C (was {originalUnit})");
                }
            }
            catch { }

            // DEBUG TRACE
            var _trace = new System.Text.StringBuilder();
            _trace.AppendLine(new string('=', 150));
            _trace.AppendLine($"[ProcessAreaLoads v5.1 - kN_m_C Mode] Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _trace.AppendLine($"Original Unit: {originalUnit}, Switched: {unitSwitched}");
            _trace.AppendLine($"Input: {loads.Count} loads, LoadValue={loadVal} (already in kN/m²)");
            _trace.AppendLine(new string('-', 150));
            _trace.AppendLine($"{"Elem",-8} {"mat[2,5,8]",-20} {"Axis",-12} {"Sign",-6} {"AreaM2",-10} {"PlaneBin",-10} GroupKey");
            _trace.AppendLine(new string('-', 150));

            var groups = new Dictionary<string, List<(RawSapLoad Load, double AreaM2, string Axis, int Sign, SapArea Geom)>>();


            foreach (var load in loads)
            {
                string name = load.ElementName;

                // 1. LẤY MA TRẬN TRANSFORMATION TỪ API
                double[] mat = new double[9];
                int ret = model.AreaObj.GetTransformationMatrix(name, ref mat, true);
                if (ret != 0) { _trace.AppendLine($"{name,-8} [SKIP: No Matrix]"); continue; }

                // Global Direction = Cột 3 của ma trận (Local 3 -> Global)
                double gx = mat[2], gy = mat[5], gz = mat[8];
                string globalAxis = "Mix"; int sign = 1;
                if (Math.Abs(gx) > 0.7) { globalAxis = "Global X"; sign = gx > 0 ? 1 : -1; }
                else if (Math.Abs(gy) > 0.7) { globalAxis = "Global Y"; sign = gy > 0 ? 1 : -1; }
                else if (Math.Abs(gz) > 0.7) { globalAxis = "Global Z"; sign = gz > 0 ? 1 : -1; }

                // 2. LẤY TỌA ĐỘ ĐIỂM TỪ API
                int numPts = 0; string[] ptNames = null;
                ret = model.AreaObj.GetPoints(name, ref numPts, ref ptNames);
                if (ret != 0 || numPts < 3) { _trace.AppendLine($"{name,-8} [SKIP: No Points]"); continue; }

                var pts = new List<(double x, double y, double z)>();
                foreach (var pn in ptNames)
                {
                    double x = 0, y = 0, z = 0;
                    if (model.PointObj.GetCoordCartesian(pn, ref x, ref y, ref z) == 0)
                        pts.Add((x, y, z));
                }
                if (pts.Count < 3) continue;

                // 3. TÍNH DIỆN TÍCH 3D (tọa độ đã ở mét do unit switch)
                double sumX = 0, sumY = 0, sumZ = 0;
                var p0 = pts[0];
                for (int k = 1; k < pts.Count - 1; k++)
                {
                    var p1 = pts[k]; var p2 = pts[k + 1];
                    double v1x = p1.x - p0.x, v1y = p1.y - p0.y, v1z = p1.z - p0.z;
                    double v2x = p2.x - p0.x, v2y = p2.y - p0.y, v2z = p2.z - p0.z;
                    sumX += v1y * v2z - v1z * v2y;
                    sumY += v1z * v2x - v1x * v2z;
                    sumZ += v1x * v2y - v1y * v2x;
                }
                // Tọa độ đã ở mét (kN_m_C) -> diện tích trực tiếp là m²
                double areaM2 = 0.5 * Math.Sqrt(sumX * sumX + sumY * sumY + sumZ * sumZ);

                // 4. PLANE COORDINATE (mét)
                double planeCoord = globalAxis.Contains("X") ? pts.Average(p => p.x) :
                                   globalAxis.Contains("Y") ? pts.Average(p => p.y) : pts.Average(p => p.z);
                // Tolerance 0.5m cho grouping walls cùng plane
                long planeBin = (long)Math.Round(planeCoord / 0.5);

                // 5. GROUP KEY
                string groupKey = globalAxis.Contains("Z")
                    ? $"SLAB|{(load.Direction ?? "").Trim().ToUpperInvariant()}|{load.Value1:F4}"
                    : $"WALL|{globalAxis}|{load.Value1:F4}|{planeBin}|{sign}";

                // Tạo SapArea để dùng cho NTS
                var sapArea = new SapArea { Name = name, BoundaryPoints = pts.Select(p => new Point2D(p.x, p.y)).ToList(), ZValues = pts.Select(p => p.z).ToList() };

                _trace.AppendLine($"{name,-8} ({gx:F2},{gy:F2},{gz:F2})     {globalAxis,-12} {sign,-6} {areaM2,-10:F2} {planeBin,-10} {groupKey}");

                if (!groups.ContainsKey(groupKey))
                    groups[groupKey] = new List<(RawSapLoad, double, string, int, SapArea)>();
                groups[groupKey].Add((load, areaM2, globalAxis, sign, sapArea));
            }

            Log($"[ProcessAreaLoads] Created {groups.Count} groups.");
            _trace.AppendLine(); _trace.AppendLine($"Total Groups: {groups.Count}");
            _trace.AppendLine(new string('-', 150));

            // XỬ LÝ TỪNG NHÓM
            foreach (var kv in groups)
            {
                try
                {
                    var items = kv.Value;
                    if (items.Count == 0) continue;

                    bool isSlab = kv.Key.StartsWith("SLAB|");
                    string globalAxis = items[0].Axis;
                    int sign = items[0].Sign;
                    var elementNames = items.Select(x => x.Load.ElementName).Distinct().ToList();
                    double totalArea = items.Sum(x => x.AreaM2);

                    _trace.AppendLine($">>> {kv.Key} | Elements: {string.Join(",", elementNames)} | Area={totalArea:F2}");

                    if (totalArea < MIN_AREA_THRESHOLD_M2) { _trace.AppendLine("    [SKIP: Area<Threshold]"); continue; }

                    // NTS GEOMETRY
                    Geometry combinedGeom = null;
                    try
                    {
                        foreach (var item in items)
                        {
                            var projPts = ProjectAreaToBestPlane(item.Geom);
                            var poly = CreateNtsPolygon(projPts);
                            if (poly?.IsValid == true) combinedGeom = combinedGeom == null ? poly : combinedGeom.Union(poly);
                        }
                    }
                    catch { }

                    string formula = combinedGeom != null && !combinedGeom.IsEmpty ? FormatGeomGrouping(combinedGeom) : $"~{totalArea:0.00}";
                    string location = GetGroupGridLocation(elementNames);

                    // FORCE CALCULATION
                    double loadValue = items[0].Load.Value1;
                    double force = loadValue * totalArea * sign;
                    double fx = 0, fy = 0, fz = 0;

                    if (isSlab)
                    {
                        string loadDir = (items[0].Load.Direction ?? "").Trim().ToUpperInvariant();
                        if (loadDir.Contains("GRAVITY")) fz = -Math.Abs(force);
                        else if (loadDir.Contains("X")) fx = force;
                        else if (loadDir.Contains("Y")) fy = force;
                        else fz = force;
                    }
                    else
                    {
                        if (globalAxis.Contains("X")) fx = force;
                        else if (globalAxis.Contains("Y")) fy = force;
                        else fz = force;
                    }

                    string dirDisplay = globalAxis.Contains("X") ? (sign > 0 ? "+X" : "-X") :
                                        globalAxis.Contains("Y") ? (sign > 0 ? "+Y" : "-Y") :
                                        (sign > 0 ? "+Z" : "-Z");

                    _trace.AppendLine($"    Force={loadValue:F4}*{totalArea:F2}*{sign}={force:F2} | Dir={dirDisplay} | Fx={fx:F2},Fy={fy:F2},Fz={fz:F2}");

                    targetList.Add(new AuditEntry
                    {
                        GridLocation = location,
                        Explanation = formula,
                        Quantity = totalArea,
                        QuantityUnit = "m²",
                        UnitLoad = loadValue,
                        UnitLoadString = $"{loadValue:0.00}",
                        TotalForce = force,
                        Direction = dirDisplay,
                        DirectionSign = sign,
                        ForceX = fx, ForceY = fy, ForceZ = fz,
                        ElementList = elementNames,
                        StructuralType = isSlab ? "Slab Elements" : "Wall Elements"
                    });
                    Log($"   [ADDED] {location}: {dirDisplay}, F={force:F2}");
                }
                catch (Exception ex) { _trace.AppendLine($"    [ERROR] {ex.Message}"); }
            }

            // WRITE DEBUG FILE
            _trace.AppendLine(new string('=', 150));
            _trace.AppendLine($"Total Entries: {targetList.Count}");
            try
            {
                string file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"AuditTrace_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                System.IO.File.WriteAllText(file, _trace.ToString());
                System.Diagnostics.Process.Start("notepad.exe", file);
            }
            catch { }

            // === BƯỚC CUỐI: TRẢ LẠI UNIT GỐC ===
            if (unitSwitched)
            {
                try
                {
                    model.SetPresentUnits(originalUnit);
                    Log($"   [UNIT] Restored to {originalUnit}");
                }
                catch { }
            }
        }

        // [ADDED v4.5] Helper for grouping geometry terms (Already in Meters after unit switch)
        private string FormatGeomGrouping(Geometry geom)
        {
            var terms = new List<string>();
            for (int i = 0; i < geom.NumGeometries; i++)
            {
                var g = geom.GetGeometryN(i);
                var env = g.EnvelopeInternal;
                // Tọa độ đã ở mét sau unit switch
                string term = $"{env.Width:0.##}*{env.Height:0.##}";
                terms.Add(term);
            }

            // Group by term
            var groups = terms.GroupBy(t => t)
                              .Select(g => g.Count() > 1 ? $"{g.Count()}*({g.Key})" : g.Key);
            
            return string.Join(" + ", groups);
        }

        private string GetGroupGridLocation(List<string> elements)
        {
            if (elements == null || elements.Count == 0) return "Unknown";
            
            // 1. Calculate Bounding Box of the group
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            bool found = false;

            foreach (var name in elements)
            {
                var info = _inventory.GetElement(name);
                if (info == null) continue;

                if (info.FrameGeometry != null)
                {
                     minX = Math.Min(minX, Math.Min(info.FrameGeometry.StartPt.X, info.FrameGeometry.EndPt.X));
                     maxX = Math.Max(maxX, Math.Max(info.FrameGeometry.StartPt.X, info.FrameGeometry.EndPt.X));
                     minY = Math.Min(minY, Math.Min(info.FrameGeometry.StartPt.Y, info.FrameGeometry.EndPt.Y));
                     maxY = Math.Max(maxY, Math.Max(info.FrameGeometry.StartPt.Y, info.FrameGeometry.EndPt.Y));
                     found = true;
                }
                else if (info.AreaGeometry != null && info.AreaGeometry.BoundaryPoints.Count > 0)
                {
                     foreach (var pt in info.AreaGeometry.BoundaryPoints)
                     {
                         minX = Math.Min(minX, pt.X);
                         maxX = Math.Max(maxX, pt.X);
                         minY = Math.Min(minY, pt.Y);
                         maxY = Math.Max(maxY, pt.Y);
                     }
                     found = true;
                }
            }

            if (!found) return "Geometry N/A";

            // 2. Find intersecting grids
            string gridX = FindGridRange(_xGrids, minX, maxX);
            string gridY = FindGridRange(_yGrids, minY, maxY);

            return $"Grid {gridX} x {gridY}";
        }

        private string FindGridRange(List<SapUtils.GridLineRecord> grids, double min, double max)
        {
            if (grids == null || grids.Count == 0) return "?";

            // Tolerance for snapping (200mm)
            double tol = 200.0;
            
            var startGrid = grids.FirstOrDefault(g => Math.Abs(g.Coordinate - min) < tol);
            var endGrid = grids.LastOrDefault(g => Math.Abs(g.Coordinate - max) < tol);

            // If exact match
            if (startGrid != null && endGrid != null)
            {
                return startGrid == endGrid ? startGrid.Name : $"{startGrid.Name}-{endGrid.Name}";
            }

            // If range covers multiple grids
            var covered = grids.Where(g => g.Coordinate >= min - tol && g.Coordinate <= max + tol).ToList();
            if (covered.Count > 1) 
                return $"{covered.First().Name}-{covered.Last().Name}";
            if (covered.Count == 1)
                return covered.First().Name;

            // [FIX v4.8] Robust Nearest Grid + Offset logic
            // Instead of displaying raw coordinates (~7300..7300), find the closest grid.
            
            double width = max - min;
            
            // Case A: Narrow object (Wall/Beam) -> Single Nearest Grid
            if (width < 1000.0) 
            {
                double center = (min + max) / 2.0;
                var nearest = grids.OrderBy(g => Math.Abs(g.Coordinate - center)).FirstOrDefault();
                if (nearest != null)
                {
                    double offset = center - nearest.Coordinate;
                    double offsetM = offset / 1000.0;
                    
                    // Format: "A (+1.2m)" or "A (-0.5m)" or just "A" if very close
                    if (Math.Abs(offset) < 50.0) return nearest.Name; // Practically on grid
                    return $"{nearest.Name} ({offsetM:+#.0;-#.0}m)";
                }
            }
            // Case B: Wide range (Slab width) -> Start & End Nearest Grids
            else
            {
                 var nearStart = grids.OrderBy(g => Math.Abs(g.Coordinate - min)).FirstOrDefault();
                 var nearEnd = grids.OrderBy(g => Math.Abs(g.Coordinate - max)).FirstOrDefault();
                 
                 if (nearStart != null && nearEnd != null)
                 {
                     if (nearStart == nearEnd) return nearStart.Name; // Should be covered by Case A usually
                     
                     double offStart = min - nearStart.Coordinate;
                     double offEnd = max - nearEnd.Coordinate;
                     
                     string sPart = Math.Abs(offStart) < 50 ? nearStart.Name : $"{nearStart.Name}({offStart/1000.0:+#.0;-#.0}m)";
                     string ePart = Math.Abs(offEnd) < 50 ? nearEnd.Name : $"{nearEnd.Name}({offEnd/1000.0:+#.0;-#.0}m)";
                     
                     return $"{sPart}-{ePart}";
                 }
            }
        
            // Ultimate fallback
            return $"(~{min:0}..{max:0})";
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
            /// Nếu bằng nhau: Ơu tiên thuật toán tạo ra khối chính (Main Chunk) lớn nhất.
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
        private void ProcessFrameLoads(List<RawSapLoad> loads, double loadVal, string dir, List<AuditEntry> targetList)
        {
            Log($"[ProcessFrameLoads] Processing {loads.Count} loads. Val={loadVal}, Dir={dir}");
            
            var frameItems = new List<FrameAuditItem>();
            var areaLoads = new List<RawSapLoad>(); // Phần tử Area bị đánh nhầm type
            int skipCount = 0;

            foreach (var load in loads)
            {
                var info = _inventory.GetElement(load.ElementName);
                if (info == null)
                {
                    Log($"   [SKIP] {load.ElementName}: info=null");
                    skipCount++;
                    continue;
                }
                
                // FIX: Nếu element là Area mà load type là FrameDistributed 
                // -> Đây là load tường/sàn, route sang ProcessAreaLoads
                if (info.ElementType == "Area" || info.FrameGeometry == null)
                {
                    Log($"   [REROUTE] {load.ElementName}: Type={info.ElementType}, routing to Area processing");
                    areaLoads.Add(load);
                    continue;
                }
                
                var frame = info.FrameGeometry;

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

            Log($"[ProcessFrameLoads] Valid frames: {frameItems.Count}, Rerouted to Area: {areaLoads.Count}, Skipped: {skipCount}");
            
            // Process rerouted Area loads
            if (areaLoads.Count > 0)
            {
                Log($"[ProcessFrameLoads] Processing {areaLoads.Count} rerouted loads as Area...");
                ProcessAreaLoads(areaLoads, loadVal, dir, targetList);
            }
            
            if (frameItems.Count == 0) return;

            // [FIX v4.10] Group by Grid AND Structural Type to split Beams/Columns
            var groups = frameItems.GroupBy(f => new 
            { 
                Grid = f.PrimaryGrid, 
                Type = (Math.Abs(f.Frame.Z1 - f.Frame.Z2) > 500 || f.Frame.IsVertical) ? "Column Elements" : "Beam Elements"
            });

            foreach (var grp in groups)
            {
                string gridName = grp.Key.Grid;
                string structType = grp.Key.Type;
                double totalLength = grp.Sum(f => f.Length);

                // CRITICAL v4.4: Calculate SIGNED force from loadVal (already contains SAP sign)
                double signedForce = totalLength * loadVal;

                // --- FORCE DIRECTION CALCULATION (FIXED v4.5) ---
                double fx = 0, fy = 0, fz = 0;
                string loadDir = dir.ToUpper();
                
                if (loadDir.Contains("GRAVITY")) fz = -Math.Abs(signedForce);
                else if (loadDir.Contains("GLOBAL X")) fx = signedForce;
                else if (loadDir.Contains("GLOBAL Y")) fy = signedForce;
                else if (loadDir.Contains("GLOBAL Z")) fz = signedForce;
                else
                {
                    // Fallback to legacy parsing if "Global" is not explicit
                    if (dir.Contains("X")) fx = signedForce;
                    else if (dir.Contains("Y")) fy = signedForce;
                    else if (dir.Contains("Z")) fz = signedForce;
                }

                string rangeDesc = DetermineCrossAxisRange(grp.ToList());

                // Build segment details for partial loads
                var partialSegments = grp.Where(f => f.StartM > 0.01 || Math.Abs(f.EndM - f.Frame.Length2D * UnitManager.Info.LengthScaleToMeter) > 0.01)
                    .Select(f => $"{f.Load.ElementName}_{f.StartM:0.##}to{f.EndM:0.##}")
                    .ToList();

                string explanation = partialSegments.Count > 0
                    ? string.Join(",", partialSegments)
                    : "";

                var elementNames = grp.Select(f => f.Load.ElementName).Distinct().ToList();
                
                Log($"   -> Frame Group [{gridName}]: {elementNames.Count} elements, Length={totalLength:F2}m, Force={signedForce:F2}");

                // Clean Direction String
                string dirDisplay = dir.Replace("Global ", "");
                if (dir.Contains("X")) dirDisplay = (Math.Sign(signedForce) > 0 ? "+" : "-") + "X";
                else if (dir.Contains("Y")) dirDisplay = (Math.Sign(signedForce) > 0 ? "+" : "-") + "Y";
                else if (dir.Contains("Z")) dirDisplay = (Math.Sign(signedForce) > 0 ? "+" : "-") + "Z";

                // CRITICAL v4.4: Store signed values - these will be displayed and summed
                targetList.Add(new AuditEntry
                {
                    GridLocation = $"{gridName} x {rangeDesc}",
                    Explanation = explanation,
                    Quantity = totalLength,
                    QuantityUnit = "m",
                    UnitLoad = loadVal, // Signed value from SAP
                    UnitLoadString = $"{loadVal:0.00}",
                    TotalForce = signedForce, // Signed magnitude
                    Direction = dirDisplay,
                    DirectionSign = Math.Sign(signedForce), // Sign for display
                    ForceX = fx, // Signed component
                    ForceY = fy, // Signed component
                    ForceZ = fz, // Signed component
                    ElementList = elementNames,
                    StructuralType = structType
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
        private void ProcessPointLoads(List<RawSapLoad> loads, double loadVal, string dir, List<AuditEntry> targetList)
        {
            var pointGroups = new Dictionary<string, List<(RawSapLoad load, SapUtils.SapPoint coord)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var load in loads)
            {
                var info = _inventory.GetElement(load.ElementName);
                if (info == null || info.PointGeometry == null) continue;
                var ptCoord = info.PointGeometry;

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

                // CRITICAL v4.4: Calculate SIGNED force from loadVal (already contains SAP sign)
                double signedForce = count * loadVal;

                // --- FORCE DIRECTION CALCULATION (FIXED v4.5) ---
                double fx = 0, fy = 0, fz = 0;
                string loadDir = dir.ToUpper();
                
                if (loadDir.Contains("GRAVITY"))
                {
                    fz = -Math.Abs(signedForce);
                }
                else if (loadDir.Contains("GLOBAL X"))
                {
                    fx = signedForce;
                }
                else if (loadDir.Contains("GLOBAL Y"))
                {
                    fy = signedForce;
                }
                else if (loadDir.Contains("GLOBAL Z"))
                {
                    fz = signedForce;
                }
                else
                {
                    // Fallback using raw string check
                    if (dir.Contains("X")) fx = signedForce;
                    else if (dir.Contains("Y")) fy = signedForce;
                    else if (dir.Contains("Z")) fz = signedForce;
                    else
                    {
                        // Fallback: assume gravity
                        fz = signedForce;
                    }
                }

                var sorted = SortPointsLeftToRight(groupLoads);
                var elementNames = sorted.Select(p => p.load.ElementName).ToList();

                Log($"   -> Point Group [{location}]: {count} items, Force={signedForce:F2}");

                // Clean Direction String
                string dirDisplay = dir.Replace("Global ", "");
                if (dir.Contains("X")) dirDisplay = (Math.Sign(signedForce) > 0 ? "+" : "-") + "X";
                else if (dir.Contains("Y")) dirDisplay = (Math.Sign(signedForce) > 0 ? "+" : "-") + "Y";
                else if (dir.Contains("Z")) dirDisplay = (Math.Sign(signedForce) > 0 ? "+" : "-") + "Z";

                // CRITICAL v4.4: Store signed values - these will be displayed and summed
                targetList.Add(new AuditEntry
                {
                    GridLocation = location,
                    Explanation = "",
                    Quantity = count,
                    QuantityUnit = "ea",
                    UnitLoad = loadVal, // Signed value from SAP
                    UnitLoadString = $"{loadVal:0.00}",
                    TotalForce = signedForce, // Signed magnitude
                    Direction = dirDisplay,
                    DirectionSign = Math.Sign(signedForce), // Sign for display
                    ForceX = fx, // Signed component
                    ForceY = fy, // Signed component
                    ForceZ = fz, // Signed component
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

        private string FindAxisRange(double minVal, double maxVal, List<SapUtils.GridLineRecord> grids, bool isPoint = false)
        {
            // [DEBUG] Add count to diagnose why it thinks grids are missing
            if (grids == null || grids.Count == 0) return $"(~{minVal:0}..{maxVal:0})[cnt={grids?.Count ?? -1}]";

            // Find nearest start grid
            var startGrid = grids.OrderBy(g => Math.Abs(g.Coordinate - minVal)).First();
            double startDiff = minVal - startGrid.Coordinate;

            if (isPoint || Math.Abs(maxVal - minVal) < GRID_SNAP_TOLERANCE_MM)
            {
                // Format: A(+1.2m)
                return FormatGridWithOffset(startGrid.Name, startDiff);
            }

            // Find nearest end grid
            var endGrid = grids.OrderBy(g => Math.Abs(g.Coordinate - maxVal)).First();
            double endDiff = maxVal - endGrid.Coordinate;

            if (startGrid.Name == endGrid.Name) return FormatGridWithOffset(startGrid.Name, startDiff);

            // [FIX v4.5] Clean range format
            return $"{FormatGridWithOffset(startGrid.Name, startDiff)}-{FormatGridWithOffset(endGrid.Name, endDiff)}";
        }

        private string FormatGridWithOffset(string name, double offsetMm)
        {
            if (Math.Abs(offsetMm) < GRID_SNAP_TOLERANCE_MM) return name;
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
                var existingGroup = zGroups.FirstOrDefault(g => Math.Abs(g.Average() - z) <= STORY_TOLERANCE_MM);
                if (existingGroup != null) existingGroup.Add(z);
                else zGroups.Add(new List<double> { z });
            }

            var stories = _stories.Where(s => s.IsElevation).OrderByDescending(s => s.Elevation).ToList();

            foreach (var group in zGroups)
            {
                double avgZ = group.Average();
                var match = stories.FirstOrDefault(s => Math.Abs(s.Elevation - avgZ) <= STORY_TOLERANCE_MM);
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
        /// UPDATED v4.4: New column layout - removed Type, added Value, reordered Dir before Force
        /// Format: Grid Location | Calculator | Value(unit) | Unit Load(unit) | Dir | Force(unit) | Elements
        /// 
        /// CRITICAL v4.4 SUMMARY CALCULATION:
        /// - Summary calculated ONLY from processed AuditEntry.ForceX/Y/Z (already in report.Stories)
        /// - NO fallback to report.CalculatedFx/Fy/Fz (those are also from processed data)
        /// - Ensures: Visual Sum = Calculated Sum = Sum of displayed Force values
        /// </summary>
        public string GenerateTextReport(AuditReport report, string targetUnit = "kN", string language = "English")
        {
            var sb = new StringBuilder();
            double forceFactor = 1.0;

            // Unit conversion setup
            if (string.IsNullOrWhiteSpace(targetUnit)) targetUnit = UnitManager.Info.ForceUnit;
            if (targetUnit.Equals("Ton", StringComparison.OrdinalIgnoreCase) || targetUnit.Equals("Tonf", StringComparison.OrdinalIgnoreCase))
                forceFactor = 1.0 / 9.81;
            else if (targetUnit.Equals("kgf", StringComparison.OrdinalIgnoreCase)) forceFactor = 101.97;
            else { targetUnit = "kN"; forceFactor = 1.0; }

            bool isVN = language.Equals("Vietnamese", StringComparison.OrdinalIgnoreCase);

            // HEADER CHUNG
            sb.AppendLine("".PadRight(150, '='));
            sb.AppendLine(isVN ? "   KIỂM TOÁN TẢI TRỌNG SAP2000 (DTS ENGINE v4.4)" : "   SAP2000 LOAD AUDIT REPORT (DTS ENGINE v4.4)");
            sb.AppendLine($"   {(isVN ? "Dự án" : "Project")}: {report.ModelName ?? "Unknown"}");
            sb.AppendLine($"   {(isVN ? "Tổ hợp tải" : "Load Pattern")}: {report.LoadPattern}");
            sb.AppendLine($"   {(isVN ? "Ngày tính" : "Audit Date")}: {report.AuditDate:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"   {(isVN ? "Đơn vị" : "Report Unit")}: {targetUnit}");
            sb.AppendLine("".PadRight(150, '='));
            sb.AppendLine();

            foreach (var story in report.Stories.OrderByDescending(s => s.Elevation))
            {
                // CRITICAL v4.4: Calculate story totals from LoadType subtotals (which come from AuditEntry.ForceX/Y/Z)
                double storyFx = story.LoadTypes.Sum(lt => lt.SubTotalFx) * forceFactor;
                double storyFy = story.LoadTypes.Sum(lt => lt.SubTotalFy) * forceFactor;
                double storyFz = story.LoadTypes.Sum(lt => lt.SubTotalFz) * forceFactor;

                sb.AppendLine($"[>] {(isVN ? "TẦNG" : "STORY")}: {story.StoryName} | Z={story.Elevation:0}mm [Fx={storyFx:0.00}, Fy={storyFy:0.00}, Fz={storyFz:0.00}]");

                foreach (var loadType in story.LoadTypes)
                {
                    string typeName = GetSpecificLoadTypeName(loadType, isVN);

                    // CRITICAL v4.4: LoadType totals from SubTotalFx/Fy/Fz (which come from AuditEntry.ForceX/Y/Z)
                    double typeFx = loadType.SubTotalFx * forceFactor;
                    double typeFy = loadType.SubTotalFy * forceFactor;
                    double typeFz = loadType.SubTotalFz * forceFactor;

                    // CRITICAL v4.4: LoadType totals hidden from table header as per request v4.10
                    // but we still calculate them for internal validation if needed.
                    // Removed: sb.AppendLine($"  [{typeName}] [Fx={typeFx:0.00}, Fy={typeFy:0.00}, Fz={typeFz:0.00}]");

                    // Setup cột (variable needed inside loop)
                    string valueUnit = loadType.Entries.FirstOrDefault()?.QuantityUnit ?? "m²";

                    // [FIX v4.10] Split by Structural Type (Slab, Wall, Beam, Column)
                    // Create separate tables for each structural type.
                    var typeGroups = loadType.Entries.GroupBy(e => e.StructuralType).OrderBy(g => g.Key);

                    foreach (var subGroup in typeGroups)
                    {
                        string structHeader = subGroup.Key;
                        // Basic translation for VN
                        if (isVN)
                        {
                            if (structHeader == "Slab Elements") structHeader = "Sàn (Slab)";
                            else if (structHeader == "Wall Elements") structHeader = "Vách (Wall)";
                            else if (structHeader == "Beam Elements") structHeader = "Dầm (Beam)";
                            else if (structHeader == "Column Elements") structHeader = "Cột (Column)";
                        }

                        // Column Header formatting (Last column takes StructType name, e.g. "Slab Elements")
                        string hGrid = (isVN ? "Vị trí trục" : "Grid Location").PadRight(30);
                        string hCalc = (isVN ? "Chi tiết" : "Calculator").PadRight(35);
                        string hValue = $"Value({valueUnit})".PadRight(15);
                        string hUnit = $"Unit Load({targetUnit}/{valueUnit})".PadRight(20);
                        string hDir = (isVN ? "Hướng" : "Dir").PadRight(8);
                        string hForce = $"Force({targetUnit})".PadRight(15);
                        string hElem = structHeader; 

                        sb.AppendLine($"    {hGrid}{hCalc}{hValue}{hUnit}{hDir}{hForce}{hElem}");
                        sb.AppendLine($"    {new string('-', 160)}");

                        // Entries are already sorted in ProcessLoadType
                        foreach (var entry in subGroup)
                        {
                            FormatDataRow(sb, entry, forceFactor, targetUnit);
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }
                sb.AppendLine(); // Dòng trống giữa các tầng
            }

            // ====================================================================================
            // CRITICAL v4.4: SUMMARY CALCULATION FROM PROCESSED DATA ONLY
            // Calculate Visual Sums from Processed Data (report.Stories → LoadTypes → SubTotalFx/Fy/Fz)
            // This ensures: Summary = Sum of all displayed force values (100% consistency)
            // ====================================================================================

            // 1. Calculate Visual Sums from report.Stories (already populated with processed data)
            double visualFx = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFx));
            double visualFy = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFy));
            double visualFz = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFz));

            // 2. Apply unit conversion factor
            double displayFx = visualFx * forceFactor;
            double displayFy = visualFy * forceFactor;
            double displayFz = visualFz * forceFactor;

            // 3. Calculate Resultant Magnitude
            double displayTotal = Math.Sqrt(displayFx * displayFx + displayFy * displayFy + displayFz * displayFz);

            // 4. Print Summary
            sb.AppendLine("".PadRight(150, '='));
            sb.AppendLine(isVN ? "TỔNG HỢP LỰC (TÍNH TỪ BÁO CÁO):" : "AUDIT SUMMARY (CALCULATED FROM REPORT ROWS):");
            sb.AppendLine();
            sb.AppendLine($"   Fx (Global): {displayFx:0.00} {targetUnit}");
            sb.AppendLine($"   Fy (Global): {displayFy:0.00} {targetUnit}");
            sb.AppendLine($"   Fz (Global): {displayFz:0.00} {targetUnit}");
            sb.AppendLine($"   Magnitude  : {displayTotal:0.00} {targetUnit}");

            // 5. Compare with SAP Base Reaction (Reference only)
            if (report.IsAnalyzed && Math.Abs(report.SapBaseReaction) > 0.001)
            {
                double sapReaction = Math.Abs(report.SapBaseReaction) * forceFactor;
                double diff = displayTotal - sapReaction;
                double diffPercent = (sapReaction > 0) ? (diff / sapReaction) * 100.0 : 0;

                sb.AppendLine();
                sb.AppendLine($"   {(isVN ? "Phản lực SAP" : "SAP Reaction")}: {sapReaction:0.00} {targetUnit}");
                sb.AppendLine($"   {(isVN ? "Sai số" : "Difference")}: {diff:0.00} {targetUnit} ({diffPercent:0.00}%)");
            }

            sb.AppendLine();
            sb.AppendLine("".PadRight(150, '='));

            return sb.ToString();
        }

        /// <summary>
        /// Format data row with new column layout
        /// CRITICAL v4.4: Display Force directly from AuditEntry.ForceX/Y/Z (already processed and signed)
        /// 
        /// DISPLAY STRATEGY:
        /// - Show processed Force components converted to target unit
        /// - NO re-calculation from Quantity * UnitLoad (that's for verification only)
        /// - Ensures displayed Force = stored Force (100% consistency)
        /// </summary>
        private void FormatDataRow(StringBuilder sb, AuditEntry entry, double forceFactor, string targetUnit)
        {
            // Column widths (khớp với header ở trên)
            const int gridWidth = 30;
            const int calcWidth = 35;
            const int valueWidth = 15;
            const int unitWidth = 20;
            const int dirWidth = 8;
            const int forceWidth = 15;

            // 1. Grid Location
            string grid = TruncateString(entry.GridLocation ?? "", gridWidth - 2).PadRight(gridWidth);

            // 2. Calculator
            string calc = TruncateString(entry.Explanation ?? "", calcWidth - 2).PadRight(calcWidth);

            // 3. Value (Quantity) - Giữ 2 số thập phân chuẩn
            string value = $"{entry.Quantity:0.00}".PadRight(valueWidth);

            // 4. Unit Load (Giá trị hiển thị) - Apply unit conversion
            double displayUnitLoad = entry.UnitLoad * forceFactor;
            string unitLoad = $"{displayUnitLoad:0.00}".PadRight(unitWidth);

            // 5. Direction
            string dir = FormatDirection(entry.Direction, entry).PadRight(dirWidth);

            // CRITICAL v4.4: Display Force from stored ForceX/Y/Z (magnitude of vector)
            // Calculate magnitude from vector components (preserves sign via component sum)
            double forceX = entry.ForceX * forceFactor;
            double forceY = entry.ForceY * forceFactor;
            double forceZ = entry.ForceZ * forceFactor;

            // Display the primary component (largest magnitude) to match user expectation
            // For most cases, this will be ForceZ (gravity), ForceX (lateral X), or ForceY (lateral Y)
            double displayForce = 0;
            if (Math.Abs(forceX) > Math.Abs(forceY) && Math.Abs(forceX) > Math.Abs(forceZ))
                displayForce = forceX;
            else if (Math.Abs(forceY) > Math.Abs(forceZ))
                displayForce = forceY;
            else
                displayForce = forceZ;

            string force = $"{displayForce:0.00}".PadRight(forceWidth);

            // 7. Elements
            string elements = CompressElementList(entry.ElementList ?? new List<string>(), maxDisplay: 80);

            sb.AppendLine($"    {grid}{calc}{value}{unitLoad}{dir}{force}{elements}");
        }

        // Helper cắt chuỗi an toàn
        private string TruncateString(string val, int maxLen)
        {
            if (string.IsNullOrEmpty(val)) return "";
            if (val.Length <= maxLen) return val;
            return val.Substring(0, maxLen - 2) + "..";
        }

        /// <summary>
        /// Compress element list with range detection (e.g., "1,2,3,4,5" -> "1to5")
        /// Smart compression for numeric sequences
        /// </summary>
        private string CompressElementList(List<string> elements, int maxDisplay = 80)
        {
            if (elements == null || elements.Count == 0) return "";

            var numericElements = new List<int>();
            var nonNumericElements = new List<string>();

            foreach (var elem in elements)
            {
                if (int.TryParse(elem, out int num)) numericElements.Add(num);
                else nonNumericElements.Add(elem);
            }

            numericElements.Sort();
            var compressed = new List<string>();

            if (numericElements.Count > 0)
            {
                int rangeStart = numericElements[0];
                int rangeLast = numericElements[0];

                for (int i = 1; i < numericElements.Count; i++)
                {
                    int current = numericElements[i];
                    if (current == rangeLast + 1)
                    {
                        rangeLast = current;
                    }
                    else
                    {
                        if (rangeLast == rangeStart) compressed.Add(rangeStart.ToString());
                        else if (rangeLast == rangeStart + 1) { compressed.Add(rangeStart.ToString()); compressed.Add(rangeLast.ToString()); }
                        else compressed.Add($"{rangeStart}to{rangeLast}");

                        rangeStart = current;
                        rangeLast = current;
                    }
                }

                // final range
                if (rangeLast == rangeStart) compressed.Add(rangeStart.ToString());
                else if (rangeLast == rangeStart + 1) { compressed.Add(rangeStart.ToString()); compressed.Add(rangeLast.ToString()); }
                else compressed.Add($"{rangeStart}to{rangeLast}");
            }

            compressed.AddRange(nonNumericElements);

            string result = string.Join(",", compressed);
            if (result.Length > maxDisplay && maxDisplay > 0) return result.Substring(0, maxDisplay - 5) + "...";
            return result;
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

        #endregion

    }
}