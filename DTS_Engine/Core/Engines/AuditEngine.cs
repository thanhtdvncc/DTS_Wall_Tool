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
    /// Engine ki?m to�n t?i tr?ng SAP2000 (Phi�n b?n v2.5 - Dependency Injection Architecture)
    /// - S? d?ng NetTopologySuite d? g?p (Union) h�nh h?c, tr�nh c?t n�t ph?n t?.
    /// - �?nh v? tr?c th�ng minh d?a tr�n BoundingBox (Range).
    /// - H? tr? da don v? hi?n th? qua UnitManager.
    /// - Added: GroupLoadsByStory (s�n d? tu?ng tr�n) v� Word Wrap trong GenerateTextReport.
    /// 
    /// ARCHITECTURE COMPLIANCE (ISO/IEC 25010 - Maintainability):
    /// - Dependency Injection: Nh?n ISapLoadReader qua Constructor
    /// - Separation of Concerns: Engine kh�ng bi?t ngu?n d? li?u (SAP/Excel/SQL)
    /// - Open/Closed Principle: M? r?ng data source m� kh�ng s?a Engine
    /// - Single Responsibility: Ch? x? l� logic audit, kh�ng d?c d? li?u
    /// </summary>
    public class AuditEngine
    {
        #region Constants & Fields

        private const double STORY_TOLERANCE = 200.0; // mm
        private const double GRID_SNAP_TOLERANCE = 250.0; // mm (increased slightly for better snapping)
        private const double MIN_AREA_THRESHOLD_M2 = 0.05;

        // Cache grids t�ch bi?t X v� Y d? t�m ki?m nhanh
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
        /// Constructor v?i Dependency Injection.
        /// 
        /// PRECONDITIONS:
        /// - loadReader ph?i du?c kh?i t?o d?y d? (SapModel + ModelInventory)
        /// - SapUtils.IsConnected = true
        /// 
        /// POSTCONDITIONS:
        /// - _loadReader s?n s�ng g?i ReadAllLoads()
        /// - Grid v� Story data du?c cache
        /// 
        /// DEPENDENCY INJECTION RATIONALE:
        /// - AuditEngine kh�ng t?o Reader ? D? test (Mock ISapLoadReader)
        /// - Thay d?i data source (Excel, SQL) kh�ng c?n s?a Engine
        /// - Tu�n th? SOLID: Dependency Inversion Principle
        /// </summary>
        /// <param name="loadReader">Implementation c?a ISapLoadReader (VD: SapDatabaseReader)</param>
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
        /// Ch?y ki?m to�n cho danh s�ch Load Patterns
        /// </summary>
        public List<AuditReport> RunAudit(string loadPatterns)
        {
            var reports = new List<AuditReport>();
            if (string.IsNullOrEmpty(loadPatterns)) return reports;

            var patterns = loadPatterns.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(p => p.Trim().ToUpper())
                                       .Distinct()
                                       .ToList();

            // Cache geometry m?t l?n d�ng chung
            CacheGeometry();

            foreach (var pattern in patterns)
            {
                var report = RunSingleAudit(pattern);
                if (report != null) reports.Add(report);
            }

            return reports;
        }

        /// <summary>
        /// Ch?y ki?m to�n cho m?t Load Pattern c? th?
        /// REFACTORED: Uses injected ISapLoadReader for data access
        /// 
        /// ARCHITECTURE:
        /// - Data Access: _loadReader.ReadAllLoads() (Abstracted)
        /// - Business Logic: Calculation, Grouping, Processing (In Engine)
        /// - Presentation: GenerateTextReport() (Separate method)
        /// 
        /// PERFORMANCE:
        /// - LoadReader d� cache ModelInventory ? Kh�ng build l?i
        /// - Geometry cache t�i s? d?ng cho nhi?u patterns
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

            // --- CRITICAL v4.4: PROCESS DATA FIRST (Create Report Entries) ---
            // Nh�m theo t?ng v� x? l� chi ti?t (bao g?m c? NTS Union cho Area)
            var storyBuckets = GroupLoadsByStory(allLoads);
            foreach (var bucket in storyBuckets.OrderByDescending(b => b.Elevation))
            {
                var storyGroup = ProcessStory(bucket.StoryName, bucket.Elevation, bucket.Loads);
                if (storyGroup.LoadTypes.Count > 0)
                    report.Stories.Add(storyGroup);
            }

            // --- CRITICAL v4.4: CALCULATE SUMMARY FROM PROCESSED ENTRIES ONLY ---
            // Aggregate ONLY from AuditEntry.ForceX/Y/Z (processed, signed values)
            // This ensures: Report Total = Sum of displayed row Forces
            
            double aggFx = 0;
            double aggFy = 0;
            double aggFz = 0;

            foreach (var story in report.Stories)
            {
                foreach (var loadType in story.LoadTypes)
                {
                    // C?ng d?n vector components t? c�c nh�m t?i d� x? l�
                    // SubTotalFx/Fy/Fz already calculated from AuditEntry.ForceX/Y/Z
                    aggFx += loadType.SubTotalFx;
                    aggFy += loadType.SubTotalFy;
                    aggFz += loadType.SubTotalFz;
                }
            }

            // G�n k?t qu? v�o Report Header
            report.CalculatedFx = aggFx;
            report.CalculatedFy = aggFy;
            report.CalculatedFz = aggFz;

            // Base Reaction = 0 (Check th? c�ng ho?c d?c t? SAP n?u c?n)
            report.SapBaseReaction = 0;
            report.IsAnalyzed = false;

            return report;
        }

        /// <summary>
        /// Helper: L?y geometry c?a Area (v?i cache) - FIX case-insensitive
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
        /// Helper: L?y geometry c?a Frame (v?i cache) - FIX case-insensitive
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

        #region Grouping Loads by Story (New Logic)

        // Temp bucket class
        private class TempStoryBucket
        {
            public string StoryName { get; set; }
            public double Elevation { get; set; }
            public List<RawSapLoad> Loads { get; set; } = new List<RawSapLoad>();
        }

        /// <summary>
        /// Nh�m t?i theo t?ng (Story) d?a tr�n cao d? Z c?a ph?n t?.
        /// FIX BUG #3 v4.1: Handle negative elevations (basement levels) correctly
        /// FIXED: Unified rule - element Z >= story elevation ? belongs to that story
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

            // Gom nh�m theo lo?i t?i (Area, Frame, Point)
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

            // NEW v4.5: STAGE 2 SMART GROUPING
            // Step 1: Group by Location (PreCalculatedGridLoc)
            // Step 2: Within each location, group by VectorKey + Value
            
            var locationGroups = loads.GroupBy(l => l.PreCalculatedGridLoc ?? "Unknown");

            foreach (var locGroup in locationGroups.OrderBy(g => g.Key))
            {
                string location = locGroup.Key;

                // Step 2: Within same location, group by Vector + Value
                var vectorValueGroups = locGroup.GroupBy(l => new 
                { 
                    Val = Math.Round(l.Value1, 3), 
                    VectorKey = l.VectorKey ?? "Unknown"
                });

                foreach (var vvGroup in vectorValueGroups.OrderByDescending(g => Math.Abs(g.Key.Val)))
                {
                    double val = vvGroup.Key.Val;
                    string vectorKey = vvGroup.Key.VectorKey;
                    var subLoads = vvGroup.ToList();

                    // Get direction from first load (all in same vector group)
                    string dir = subLoads.First().Direction ?? "Unknown";

                    // STAGE 3: Geometry Analysis (NTS Processing)
                    if (loadType.Contains("Area"))
                    {
                        ProcessAreaLoadsGroup(subLoads, val, dir, location, typeGroup.Entries);
                    }
                    else if (loadType.Contains("Frame"))
                    {
                        ProcessFrameLoadsGroup(subLoads, val, dir, location, typeGroup.Entries);
                    }
                    else
                    {
                        ProcessPointLoadsGroup(subLoads, val, dir, location, typeGroup.Entries);
                    }
                }
            }

            // CRITICAL v4.4: Calculate vector subtotals ONLY from processed AuditEntry.ForceX/Y/Z
            // This ensures: LoadType Total = Sum of its entries (no raw data interference)
            typeGroup.SubTotalFx = typeGroup.Entries.Sum(e => e.ForceX);
            typeGroup.SubTotalFy = typeGroup.Entries.Sum(e => e.ForceY);
            typeGroup.SubTotalFz = typeGroup.Entries.Sum(e => e.ForceZ);

            typeGroup.Entries = typeGroup.Entries.OrderBy(e => e.GridLocation).ToList();

            return typeGroup;
        }

        // --- X? L� T?I DI?N T�CH (AREA - SMART GEOMETRY RECOGNITION & DECOMPOSITION) ---
        /// <summary>
        /// Process area loads with vector-aware force calculation
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

        // --- NEW v4.5: Process area loads group (same location + same vector)
        /// STAGE 3: Geometry Analysis - NTS processing for shape decomposition
        /// 
        /// KEY CHANGES:
        /// - location parameter is PRE-CALCULATED (from Stage 1)
        /// - No more re-calculating grid location here
        /// - Focus purely on Quantity (area) and Explanation (formula)
        /// - Force calculation uses pre-calculated vector components
        /// </summary>
        private void ProcessAreaLoadsGroup(List<RawSapLoad> loads, double loadVal, string dir, 
            string location, List<AuditEntry> targetList)
        {
            var validLoads = new List<RawSapLoad>();
            var geometries = new List<Geometry>();

            // 1. Thu th?p h�nh h?c
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

            // 2. Th? Union (G?p h�nh)
            Geometry processedGeometry = null;
            try
            {
                processedGeometry = UnaryUnionOp.Union(geometries);
            }
            catch
            {
                processedGeometry = _geometryFactory.CreateGeometryCollection(geometries.ToArray());
            }

            // 3. STAGE 3: Geometry Analysis - Create ONE entry per merged geometry
            for (int i = 0; i < processedGeometry.NumGeometries; i++)
            {
                var geom = processedGeometry.GetGeometryN(i);
                if (geom.Area < 1e-6) continue;

                double areaM2 = geom.Area / 1.0e6;

                // Smart Shape Analysis (Explanation ONLY)
                var shapeResult = AnalyzeShapeStrategy(geom);
                string formula = shapeResult.IsExact ? shapeResult.Formula : $"~{areaM2:0.00}";

                // CRITICAL v4.5: Force calculation from PRE-CALCULATED vector components
                double signedForce = areaM2 * loadVal;
                double fx = 0, fy = 0, fz = 0;

                if (validLoads.Count > 0)
                {
                    var sampleLoad = validLoads[0];
                    var forceVec = sampleLoad.GetForceVector();
                    if (forceVec.Length > 1e-6)
                    {
                        forceVec = forceVec.Normalized * signedForce;
                        fx = forceVec.X;
                        fy = forceVec.Y;
                        fz = forceVec.Z;
                    }
                    else
                    {
                        fz = signedForce;
                    }
                }

                // NEW v4.5: Use PRE-CALCULATED location (no re-calculation)
                targetList.Add(new AuditEntry
                {
                    GridLocation = location, // PRE-CALCULATED in Stage 1
                    Explanation = formula,   // Geometry interpretation (Calculator)
                    Quantity = areaM2,      // Numerical value ONLY
                    QuantityUnit = "m�",
                    UnitLoad = loadVal,
                    UnitLoadString = $"{loadVal:0.00}",
                    TotalForce = signedForce,
                    Direction = dir,
                    DirectionSign = Math.Sign(signedForce),
                    ForceX = fx, // Vector components
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
            /// PHUONG TH?C CH�NH: Ch?y dua gi?a c�c thu?t to�n v� ch?n ngu?i chi?n th?ng.
            /// Ti�u ch� th?ng: S? lu?ng h�nh ch? nh?t �t nh?t (C�ng th?c ng?n nh?t).
            /// N?u b?ng nhau: Ou ti�n thu?t to�n t?o ra kh?i ch�nh (Main Chunk) l?n nh?t.
            /// </summary>
            public static List<Envelope> DecomposeOptimal(Geometry poly)
            {
                if (poly == null || poly.IsEmpty) return new List<Envelope>();

                // --- �?I 1: Matrix Strategy (T�m kh?i l?n nh?t l?p l?i) ---
                var resMatrix = RunMatrixStrategy(poly);

                // --- �?I 2: Slicing Strategy (Qu�t tr?c X v� Y - Dual Axis) ---
                var resSliceX = RunSlicingStrategy(poly, isVertical: false); // C?t ngang
                var resSliceY = RunSlicingStrategy(poly, isVertical: true);  // C?t d?c

                // --- SO S�NH & CH?N ---
                // T?o danh s�ch c�c ?ng vi�n
                var candidates = new List<List<Envelope>> { resMatrix, resSliceX, resSliceY };

                // 1. L?c l?y nh?ng phuong �n c� �t h�nh nh?t (Complexity th?p nh?t)
                int minCount = candidates.Min(c => c.Count);
                var bestCandidates = candidates.Where(c => c.Count == minCount).ToList();

                // 2. N?u ch? c� 1 ?ng vi�n t?t nh?t -> Ch?n lu�n
                if (bestCandidates.Count == 1) return bestCandidates[0];

                // 3. N?u h�a nhau v? s? lu?ng -> Ch?n phuong �n c� h�nh ch? nh?t "ch? d?o" l?n nh?t
                // (Tu duy k? su: Th�ch nh�n th?y m?t con s? to d�ng c?ng v?i m?y s? l?)
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

                // X�y d?ng ma tr?n
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
                // Clone ma tr?n d? kh�ng l�m h?ng d? li?u n?u c?n d�ng l?i (d� ? d�y local var)
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

                // 1. C?t l�t (Slicing)
                for (int i = 0; i < splitCoords.Count - 1; i++)
                {
                    double c1 = splitCoords[i];
                    double c2 = splitCoords[i + 1];

                    // T?o d?i c?t v� t?n
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

                // 2. G?p l?i (Merging) theo chi?u ngu?c l?i
                // N?u c?t d?c (Vertical Slice) -> G?p c�c c?t li?n k? c� c�ng chi?u cao (Y)
                return MergeStrips(strips, mergeAlongX: isVertical);
            }

            private static List<Envelope> MergeStrips(List<Envelope> inputs, bool mergeAlongX)
            {
                if (inputs.Count == 0) return new List<Envelope>();

                // Group theo chi?u cao/r?ng c? d?nh
                var groups = inputs.GroupBy(e => mergeAlongX
                    ? $"{e.MinY:F3}-{e.MaxY:F3}"  // C�ng chi?u cao Y -> G?p ngang X
                    : $"{e.MinX:F3}-{e.MaxX:F3}"); // C�ng chi?u r?ng X -> G?p d?c Y

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

        // --- C?P NH?T AnalyzeShapeStrategy: TH�M TR?NG T�I CHO CHI?N LU?C TR? ---

        private DecompositionResult AnalyzeShapeStrategy(Geometry geom)
        {
            // 1. Check h�nh co b?n (Tuy?t d?i nhanh)
            if (IsRectangle(geom))
                return new DecompositionResult { Formula = FormatRect(geom.EnvelopeInternal), ComplexityScore = 1, IsExact = true };

            if (IsTriangle(geom))
                return new DecompositionResult { Formula = $"Tam gi�c({FormatRect(geom.EnvelopeInternal)})", ComplexityScore = 1, IsExact = true };

            // 2. Ch?y thu?t to�n "�?u th?u" cho chi?n lu?c C?NG (Additive)
            // ��y l� noi Matrix vs Slicing d?u nhau
            var optimalRects = PolygonDecomposer.DecomposeOptimal(geom);

            // S?p x?p k?t qu?: H�nh to nh?t l�n d?u (Tu duy k? su)
            optimalRects = optimalRects.OrderByDescending(r => r.Area).ToList();

            string addFormula = string.Join(" + ", optimalRects.Select(r => FormatRect(r)));
            int addScore = optimalRects.Count;

            // 3. Ch?y chi?n lu?c TR? (Subtractive) n?u ti?m nang
            // Ti�u chu?n: H�nh d?c chi?m > 60% h�nh bao
            var env = geom.EnvelopeInternal;
            if (geom.Area / env.Area > 0.6)
            {
                var subRes = EvaluateSubtractive(geom);

                // QUY?T �?NH CU?I C�NG:
                // Ch? ch?n TR? n?u n� th?c s? g?n hon C?NG (Score nh? hon h?n)
                // V� d?: C?ng ra 5 h�nh, Tr? ra 2 h�nh -> Ch?n Tr?.
                // N?u C?ng ra 3 h�nh, Tr? ra 2 h�nh -> V?n c� th? ch?n C?ng v� n� tu?ng minh hon,
                // tr? khi Tr? l� (1 BoundingBox - 1 L?) r?t d?p.

                if (subRes.IsExact && subRes.ComplexityScore < addScore)
                {
                    return subRes;
                }
            }

            // M?c d?nh ch?n phuong �n C?NG t?i uu nh?t
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
                else if (IsTriangle(v)) voidTerms.Add($"Tam gi�c({FormatRect(v.EnvelopeInternal)})");
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

        // Rectangle detection: area � envelope area (within 1%)
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

        // --- X? L� T?I THANH (FRAME) - SMART GROUPING THEOREM ---
        /// <summary>
        /// Process frame loads with vector-aware force calculation
        /// CRITICAL v4.4: ForceX/Y/Z calculated with CORRECT SIGN from loadVal (already signed from SAP)
        /// 
        /// SIGN CONVENTION:
        /// - loadVal already contains sign from SAP
        /// - Force magnitude = totalLength * loadVal (preserves sign)
        /// - Vector components = forceVector.Normalized * signedForce (preserves direction)
        /// 
        /// RESULT:
        /// - AuditEntry.ForceX/Y/Z have correct signs for summation
        /// - Report displays these values directly (no conversion)
        /// </summary>

        // --- X? L� T?I THANH (FRAME) - SMART GROUPING THEOREM ---
        /// <summary>
        /// NEW v4.5: Process frame loads group (same location + same vector)
        /// STAGE 3: Geometry Analysis - Length calculation and segment details
        /// 
        /// KEY CHANGES:
        /// - location parameter is PRE-CALCULATED (from Stage 1)
        /// - Focus on Quantity (total length) and Explanation (segment details)
        /// - Force calculation uses pre-calculated vector components
        /// </summary>
        private void ProcessFrameLoadsGroup(List<RawSapLoad> loads, double loadVal, string dir, 
            string location, List<AuditEntry> targetList)
        {
            var frameItems = new List<FrameAuditItem>();

            foreach (var load in loads)
            {
                if (!_frameGeometryCache.TryGetValue(load.ElementName, out var frame)) continue;

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
                    PrimaryGrid = "", // Not used anymore (location is pre-calculated)
                    Length = coveredLength,
                    StartM = startM,
                    EndM = endM
                });
            }

            if (frameItems.Count == 0) return;

            // Calculate total length (Quantity)
            double totalLength = frameItems.Sum(f => f.Length);

            // CRITICAL v4.5: Force calculation from PRE-CALCULATED vector
            double signedForce = totalLength * loadVal;
            double fx = 0, fy = 0, fz = 0;

            var sampleLoad = frameItems.First().Load;
            var forceVec = sampleLoad.GetForceVector();
            if (forceVec.Length > 1e-6)
            {
                forceVec = forceVec.Normalized * signedForce;
                fx = forceVec.X;
                fy = forceVec.Y;
                fz = forceVec.Z;
            }
            else
            {
                fz = signedForce;
            }

            // Build segment details (Explanation ONLY - for partial loads)
            var partialSegments = frameItems
                .Where(f => f.StartM > 0.01 || Math.Abs(f.EndM - f.Frame.Length2D * UnitManager.Info.LengthScaleToMeter) > 0.01)
                .Select(f => $"{f.Load.ElementName}_{f.StartM:0.##}to{f.EndM:0.##}")
                .ToList();

            string explanation = partialSegments.Count > 0
                ? string.Join(",", partialSegments)
                : ""; // Empty for uniform loads

            var elementNames = frameItems.Select(f => f.Load.ElementName).Distinct().ToList();

            // NEW v4.5: Use PRE-CALCULATED location
            targetList.Add(new AuditEntry
            {
                GridLocation = location, // PRE-CALCULATED in Stage 1
                Explanation = explanation, // Segment details (Calculator)
                Quantity = totalLength,   // Numerical value ONLY
                QuantityUnit = "m",
                UnitLoad = loadVal,
                UnitLoadString = $"{loadVal:0.00}",
                TotalForce = signedForce,
                Direction = dir,
                DirectionSign = Math.Sign(signedForce),
                ForceX = fx, // Vector components
                ForceY = fy,
                ForceZ = fz,
                ElementList = elementNames
            });
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
        /// X�c d?nh thanh n?m tr�n tr?c n�o (Grid A, Grid 1, ho?c Diagonal)
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
        /// X�c d?nh ph?m vi qu�t c?a nh�m d?m (VD: From Grid 1 to Grid 5)
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

        // --- X? L� T?I �I?M (POINT) - IMPROVED GROUPING ---
        /// <summary>
        /// Process point loads with vector-aware force calculation
        /// CRITICAL v4.4: ForceX/Y/Z calculated with CORRECT SIGN from loadVal (already signed from SAP)
        /// 
        /// SIGN CONVENTION:
        /// - loadVal already contains sign from SAP
        /// - Force magnitude = count * loadVal (preserves sign)
        /// - Vector components = forceVector.Normalized * signedForce (preserves direction)
        /// 
        /// RESULT:
        /// - AuditEntry.ForceX/Y/Z have correct signs for summation
        /// - Report displays these values directly (no conversion)
        /// </summary>

        // --- X? L� T?I �I?M (POINT) - IMPROVED GROUPING ---
        /// <summary>
        /// NEW v4.5: Process point loads group (same location + same vector)
        /// STAGE 3: Count and force calculation
        /// 
        /// KEY CHANGES:
        /// - location parameter is PRE-CALCULATED (from Stage 1)
        /// - Focus on Quantity (count) only
        /// - No explanation needed for points (trivial)
        /// - Force calculation uses pre-calculated vector components
        /// </summary>
        private void ProcessPointLoadsGroup(List<RawSapLoad> loads, double loadVal, string dir, 
            string location, List<AuditEntry> targetList)
        {
            if (loads == null || loads.Count == 0) return;

            int count = loads.Count;

            // CRITICAL v4.5: Force calculation from PRE-CALCULATED vector
            double signedForce = count * loadVal;
            double fx = 0, fy = 0, fz = 0;

            var sampleLoad = loads[0];
            var forceVec = sampleLoad.GetForceVector();
            if (forceVec.Length > 1e-6)
            {
                forceVec = forceVec.Normalized * signedForce;
                fx = forceVec.X;
                fy = forceVec.Y;
                fz = forceVec.Z;
            }
            else
            {
                fz = signedForce;
            }

            // Sort points left to right (visual order)
            var allPoints = SapUtils.GetAllPoints();
            var pointCoords = loads
                .Select(l => new { Load = l, Coord = allPoints.FirstOrDefault(p => p.Name == l.ElementName) })
                .Where(x => x.Coord != null)
                .ToList();

            var sorted = SortPointsLeftToRight(pointCoords);
            var elementNames = sorted.Select(p => p.Load.ElementName).ToList();

            // NEW v4.5: Use PRE-CALCULATED location
            targetList.Add(new AuditEntry
            {
                GridLocation = location, // PRE-CALCULATED in Stage 1
                Explanation = "",        // No explanation needed for points
                Quantity = count,        // Count ONLY
                QuantityUnit = "ea",
                UnitLoad = loadVal,
                UnitLoadString = $"{loadVal:0.00}",
                TotalForce = signedForce,
                Direction = dir,
                DirectionSign = Math.Sign(signedForce),
                ForceX = fx, // Vector components
                ForceY = fy,
                ForceZ = fz,
                ElementList = elementNames
            });
        }

        /// <summary>
        /// Sort points left to right along primary axis (helper for visual order)
        /// </summary>
        private List<T> SortPointsLeftToRight<T>(List<T> points) where T : class
        {
            if (points.Count <= 1) return points;

            // Use reflection to get Coord property (generic helper)
            var coordProp = typeof(T).GetProperty("Coord");
            if (coordProp == null) return points;

            var coords = points.Select(p => coordProp.GetValue(p) as SapUtils.SapPoint).ToList();
            if (coords.Any(c => c == null)) return points;

            double xRange = coords.Max(c => c.X) - coords.Min(c => c.X);
            double yRange = coords.Max(c => c.Y) - coords.Min(c => c.Y);

            if (xRange > yRange)
            {
                return points.OrderBy(p => (coordProp.GetValue(p) as SapUtils.SapPoint).X).ToList();
            }
            else
            {
                return points.OrderBy(p => (coordProp.GetValue(p) as SapUtils.SapPoint).Y).ToList();
            }
        }
        #endregion

        #region Smart Grid Detection Logic

        /// <summary>
        /// T?o m� t? tr?c d?ng Range (VD: Grid 1-5 / A-B) d?a tr�n Bounding Box
        /// FIX: Added English translation support
        /// </summary>
        private string GetGridRangeDescription(Envelope env)
        {
            // T�m kho?ng tr?c X
            string xRange = FindAxisRange(env.MinX, env.MaxX, _xGrids);
            // T�m kho?ng tr?c Y
            string yRange = FindAxisRange(env.MinY, env.MaxY, _yGrids);

            if (string.IsNullOrEmpty(xRange) && string.IsNullOrEmpty(yRange)) return "No Grid";
            if (string.IsNullOrEmpty(xRange)) return $"Grid {yRange}";
            if (string.IsNullOrEmpty(yRange)) return $"Grid {xRange}";

            return $"Grid {xRange} x {yRange}";
        }

        // C?p nh?t h�m t�m tr?c d? h? tr? snap single point t?t hon
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
        /// Cache geometry t? SAP2000 d? tr�nh g?i API nhi?u l?n.
        /// FIX: S? d?ng case-insensitive dictionary
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

            // Drop the axis with smallest span ? project to plane with greatest information
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
            if (loadType.Contains("Area")) return "S�N - AREA LOAD";
            if (loadType.Contains("Frame")) return "D?M/C?T - FRAME LOAD";
            if (loadType.Contains("Point")) return "N�T - POINT LOAD";
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
            sb.AppendLine(isVN ? "   KI?M TO�N T?I TR?NG SAP2000 (DTS ENGINE v4.4)" : "   SAP2000 LOAD AUDIT REPORT (DTS ENGINE v4.4)");
            sb.AppendLine($"   {(isVN ? "D? �n" : "Project")}: {report.ModelName ?? "Unknown"}");
            sb.AppendLine($"   {(isVN ? "T? h?p t?i" : "Load Pattern")}: {report.LoadPattern}");
            sb.AppendLine($"   {(isVN ? "Ng�y t�nh" : "Audit Date")}: {report.AuditDate:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"   {(isVN ? "�on v?" : "Report Unit")}: {targetUnit}");
            sb.AppendLine("".PadRight(150, '='));
            sb.AppendLine();

            foreach (var story in report.Stories.OrderByDescending(s => s.Elevation))
            {
                // CRITICAL v4.4: Calculate story totals from LoadType subtotals (which come from AuditEntry.ForceX/Y/Z)
                double storyFx = story.LoadTypes.Sum(lt => lt.SubTotalFx) * forceFactor;
                double storyFy = story.LoadTypes.Sum(lt => lt.SubTotalFy) * forceFactor;
                double storyFz = story.LoadTypes.Sum(lt => lt.SubTotalFz) * forceFactor;

                sb.AppendLine($"[>] {(isVN ? "T?NG" : "STORY")}: {story.StoryName} | Z={story.Elevation:0}mm [Fx={storyFx:0.00}, Fy={storyFy:0.00}, Fz={storyFz:0.00}]");

                foreach (var loadType in story.LoadTypes)
                {
                    string typeName = GetSpecificLoadTypeName(loadType, isVN);

                    // CRITICAL v4.4: LoadType totals from SubTotalFx/Fy/Fz (which come from AuditEntry.ForceX/Y/Z)
                    double typeFx = loadType.SubTotalFx * forceFactor;
                    double typeFy = loadType.SubTotalFy * forceFactor;
                    double typeFz = loadType.SubTotalFz * forceFactor;

                    sb.AppendLine($"  [{typeName}] [Fx={typeFx:0.00}, Fy={typeFy:0.00}, Fz={typeFz:0.00}]");

                    // Setup c?t
                    string valueUnit = loadType.Entries.FirstOrDefault()?.QuantityUnit ?? "m�";

                    // Column Header formatting
                    string hGrid = (isVN ? "V? tr� tr?c" : "Grid Location").PadRight(30);
                    string hCalc = (isVN ? "Chi ti?t" : "Calculator").PadRight(35);
                    string hValue = $"Value({valueUnit})".PadRight(15);
                    string hUnit = $"Unit Load({targetUnit}/{valueUnit})".PadRight(20);
                    string hDir = (isVN ? "Hu?ng" : "Dir").PadRight(8);
                    string hForce = $"Force({targetUnit})".PadRight(15);
                    string hElem = (isVN ? "Ph?n t?" : "Elements");

                    sb.AppendLine($"    {hGrid}{hCalc}{hValue}{hUnit}{hDir}{hForce}{hElem}");
                    sb.AppendLine($"    {new string('-', 160)}");

                    var allEntries = loadType.Entries;

                    foreach (var entry in allEntries.OrderByDescending(e => Math.Abs(e.TotalForce)))
                    {
                        FormatDataRow(sb, entry, forceFactor, targetUnit);
                    }
                    sb.AppendLine();
                }
                sb.AppendLine(); // D�ng tr?ng gi?a c�c t?ng
            }

            // ====================================================================================
            // CRITICAL v4.4: SUMMARY CALCULATION FROM PROCESSED DATA ONLY
            // Calculate Visual Sums from Processed Data (report.Stories ? LoadTypes ? SubTotalFx/Fy/Fz)
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
            sb.AppendLine(isVN ? "T?NG H?P L?C (T�NH T? B�O C�O):" : "AUDIT SUMMARY (CALCULATED FROM REPORT ROWS):");
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
                sb.AppendLine($"   {(isVN ? "Ph?n l?c SAP" : "SAP Reaction")}: {sapReaction:0.00} {targetUnit}");
                sb.AppendLine($"   {(isVN ? "Sai s?" : "Difference")}: {diff:0.00} {targetUnit} ({diffPercent:0.00}%)");
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
            // Column widths (kh?p v?i header ? tr�n)
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

            // 3. Value (Quantity) - Gi? 2 s? th?p ph�n chu?n
            string value = $"{entry.Quantity:0.00}".PadRight(valueWidth);

            // 4. Unit Load (Gi� tr? hi?n th?) - Apply unit conversion
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

        // Helper c?t chu?i an to�n
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
