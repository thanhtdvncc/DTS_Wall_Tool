using DTS_Engine.Core.Data;
using DTS_Engine.Core.Engines; // NEW: For ModelInventory
using DTS_Engine.Core.Primitives; // NEW: For Vector3D
using SAP2000v1;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// ROBUST SAP2000 DATABASE TABLE READER
    /// 
    /// Gi?i quy?t v?n ??:
    /// 1. Schema Detection - T? ??ng nh?n di?n c?t (không ph? thu?c th? t?)
    /// 2. Local Axes Support - Chuy?n ??i F1/F2/F3 sang Global X/Y/Z
    /// 3. Direction Resolver - Xác ??nh chi?u t?i th?c theo CoordSys
    /// 
    /// Nguyên t?c:
    /// - Không "?oán mò" tên c?t ? Dùng Fuzzy matching
    /// - Không gi? ??nh th? t? ? Dùng Dictionary lookup
    /// - H? tr? nhi?u version SAP ? Schema flexible
    /// </summary>
    public class SapDatabaseReader
    {
        private const double COMPONENT_NEAR_ZERO_ABS = 1e-6;
        private readonly cSapModel _model;
        private readonly Dictionary<string, TableSchema> _schemaCache;
        private readonly ModelInventory _inventory; // NEW: Inventory reference

        public SapDatabaseReader(cSapModel model, ModelInventory inventory = null)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _schemaCache = new Dictionary<string, TableSchema>();
            _inventory = inventory; // Có th? null n?u dùng fallback mode
        }

        #region Schema Detection

        /// <summary>
        /// Table Schema - Metadata c?a b?ng SAP
        /// </summary>
        public class TableSchema
        {
            public string TableName { get; set; }
            public Dictionary<string, int> ColumnMap { get; set; } // ColumnName ? Index
            public string[] FieldKeys { get; set; }
            public string[] TableData { get; set; }
            public int RecordCount { get; set; }
            public int ColumnCount { get; set; }

            public TableSchema()
            {
                ColumnMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            /// <summary>
            /// L?y giá tr? chu?i t?i (row, columnName)
            /// </summary>
            public string GetString(int row, string columnName)
            {
                if (row < 0 || row >= RecordCount) return null;
                if (!ColumnMap.TryGetValue(columnName, out int colIdx)) return null;
                int idx = row * ColumnCount + colIdx;
                if (idx < 0 || idx >= TableData.Length) return null;
                return TableData[idx];
            }

            /// <summary>
            /// L?y giá tr? double t?i (row, columnName)
            /// </summary>
            public double GetDouble(int row, string columnName)
            {
                string val = GetString(row, columnName);
                if (string.IsNullOrEmpty(val)) return 0.0;
                if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
                    return result;
                return 0.0;
            }

            /// <summary>
            /// Ki?m tra c?t có t?n t?i không
            /// </summary>
            public bool HasColumn(string columnName) => ColumnMap.ContainsKey(columnName);

            /// <summary>
            /// Tìm tên c?t theo pattern (Fuzzy matching)
            /// Ví d?: FindColumn("Load") ? "LoadPat" ho?c "OutputCase"
            /// </summary>
            public string FindColumn(params string[] patterns)
            {
                foreach (var pattern in patterns)
                {
                    foreach (var col in ColumnMap.Keys)
                    {
                        if (col.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                            return col;
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// ??c schema c?a b?ng SAP2000
        /// Cache l?i ?? không ph?i ??c l?i nhi?u l?n
        /// </summary>
        public TableSchema GetTableSchema(string tableName, string patternFilter = null)
        {
            string cacheKey = $"{tableName}|{patternFilter ?? "ALL"}";
            if (_schemaCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var schema = new TableSchema { TableName = tableName };

            try
            {
                int tableVer = 0;
                string[] fields = null;
                int numRec = 0;
                string[] tableData = null;
                string[] input = new string[] { };

                int ret = _model.DatabaseTables.GetTableForDisplayArray(
                    tableName, ref input, "All", ref tableVer, ref fields, ref numRec, ref tableData);

                if (ret != 0 || numRec == 0 || fields == null || tableData == null)
                    return schema;

                schema.FieldKeys = fields;
                schema.TableData = tableData;
                schema.RecordCount = numRec;
                schema.ColumnCount = fields.Length;

                // Build column map
                for (int i = 0; i < fields.Length; i++)
                {
                    if (!string.IsNullOrEmpty(fields[i]))
                    {
                        schema.ColumnMap[fields[i].Trim()] = i;
                    }
                }

                _schemaCache[cacheKey] = schema;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetTableSchema failed for '{tableName}': {ex.Message}");
            }

            return schema;
        }

        #endregion

        #region Vector-Based Direction Resolution (NEW STRATEGY)

        /// <summary>
        /// STRATEGY CHUY?N ??I: Parse Direction String thành Local Axis Number
        /// 
        /// INPUT EXAMPLES:
        /// - "Local-1", "Local1", "1" -> 1
        /// - "Local-2", "Local2", "2" -> 2
        /// - "Local-3", "Local3", "3", "Gravity" -> 3
        /// 
        /// OUTPUT: S? th? t? tr?c ??a ph??ng (1, 2, ho?c 3)
        /// </summary>
        private int ParseDirectionString(string direction)
        {
            if (string.IsNullOrEmpty(direction))
                return 3; // Default: Gravity

            string dir = direction.ToUpperInvariant().Trim();

            // Check explicit numbers
            if (dir == "1" || dir.Contains("LOCAL-1") || dir.Contains("LOCAL1"))
                return 1;
            if (dir == "2" || dir.Contains("LOCAL-2") || dir.Contains("LOCAL2"))
                return 2;
            if (dir == "3" || dir.Contains("LOCAL-3") || dir.Contains("LOCAL3"))
                return 3;

            // Check keywords
            if (dir.Contains("GRAVITY") || dir.Contains("GRAV"))
                return 3;

            // Default to Gravity for unknown
            return 3;
        }

        /// <summary>
        /// CORE FUNCTION: Tính Vector l?c t? magnitude và direction string
        /// 
        /// LOGIC:
        /// 1. N?u CoordSys = "GLOBAL": Map tr?c ti?p
        ///    - "Gravity"/"Z" -> (0, 0, -magnitude)
        ///    - "X" -> (magnitude, 0, 0)
        ///    - "Y" -> (0, magnitude, 0)
        /// 
        /// 2. N?u CoordSys = "LOCAL": Dùng Inventory
        ///    - Parse direction string -> Axis number (1, 2, or 3)
        ///    - Lookup element trong Inventory
        ///    - Get LocalAxis vector
        ///    - Multiply: magnitude * LocalAxisVector
        /// 
        /// RETURN: Vector3D (Global coordinates)
        /// </summary>
        private Vector3D CalculateForceVector(
            string elementName, 
            string elementType, 
            double magnitude, 
            string direction, 
            string coordSys)
        {
            // Default to gravity if missing
            if (string.IsNullOrEmpty(direction))
                direction = "Gravity";

            string cs = coordSys?.ToUpperInvariant() ?? "GLOBAL";

            // CASE 1: GLOBAL COORDINATE SYSTEM
            if (cs.Contains("GLOBAL"))
            {
                string dir = direction.ToUpperInvariant();

                if (dir.Contains("GRAVITY") || dir.Contains("GRAV") || dir.Contains("Z"))
                    return new Vector3D(0, 0, -Math.Abs(magnitude)); // Gravity downward

                if (dir.Contains("X"))
                    return new Vector3D(magnitude, 0, 0);

                if (dir.Contains("Y"))
                    return new Vector3D(0, magnitude, 0);

                // Default: Gravity
                return new Vector3D(0, 0, -Math.Abs(magnitude));
            }

            // CASE 2: LOCAL COORDINATE SYSTEM - Need Inventory
            if (_inventory == null)
            {
                // FALLBACK: Assume Gravity
                System.Diagnostics.Debug.WriteLine($"[SapDatabaseReader] No inventory, fallback to Gravity for {elementName}");
                return new Vector3D(0, 0, -Math.Abs(magnitude));
            }

            // Parse direction to axis number
            int axisNumber = ParseDirectionString(direction);

            // Lookup element
            var localAxis = _inventory.GetLocalAxis(elementName, axisNumber);
            if (!localAxis.HasValue)
            {
                // FALLBACK: Element not found in inventory
                System.Diagnostics.Debug.WriteLine($"[SapDatabaseReader] Element '{elementName}' not in inventory, fallback to Gravity");
                return new Vector3D(0, 0, -Math.Abs(magnitude));
            }

            // Calculate: Force = magnitude * LocalAxisVector
            return magnitude * localAxis.Value;
        }

        #endregion

        #region High-Level Load Readers (REFACTORED)

        /// <summary>
        /// ??c Frame Distributed Loads v?i VECTOR-BASED APPROACH
        /// 
        /// STRATEGY:
        /// 1. ??c b?ng d? li?u thô (magnitude, direction string, coordSys)
        /// 2. G?i CalculateForceVector() ?? tính Vector l?c
        /// 3. L?u vào RawSapLoad v?i DirectionX/Y/Z ?ã tính s?n
        /// </summary>
        public List<RawSapLoad> ReadFrameDistributedLoads(string patternFilter = null)
        {
            var loads = new List<RawSapLoad>();
            var schema = GetTableSchema("Frame Loads - Distributed", patternFilter);

            if (schema.RecordCount == 0) return loads;

            // Tìm c?t (Fuzzy matching ?? h? tr? nhi?u version SAP)
            string colFrame = schema.FindColumn("Frame");
            string colPattern = schema.FindColumn("LoadPat", "OutputCase");
            string colDir = schema.FindColumn("Dir");
            string colCoordSys = schema.FindColumn("CoordSys");
            string colFOverLA = schema.FindColumn("FOverLA", "FOverL");
            string colAbsDistA = schema.FindColumn("AbsDistA");
            string colAbsDistB = schema.FindColumn("AbsDistB");

            if (colFrame == null || colPattern == null) return loads;

            // Cache geometry
            var frameGeomMap = new Dictionary<string, double>();
            var frames = SapUtils.GetAllFramesGeometry();
            foreach (var f in frames) frameGeomMap[f.Name] = f.AverageZ;

            for (int r = 0; r < schema.RecordCount; r++)
            {
                string frameName = schema.GetString(r, colFrame);
                string pattern = schema.GetString(r, colPattern);

                if (string.IsNullOrEmpty(frameName) || string.IsNullOrEmpty(pattern))
                    continue;

                // Filter by pattern
                if (!string.IsNullOrEmpty(patternFilter))
                {
                    var patterns = patternFilter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!patterns.Any(p => p.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                // Read raw value
                double rawVal = schema.GetDouble(r, colFOverLA);
                double magnitude = Math.Abs(SapUtils.ConvertLoadToKnPerM(rawVal));

                string dir = schema.GetString(r, colDir) ?? "Gravity";
                string coordSys = schema.GetString(r, colCoordSys) ?? "GLOBAL";

                // NEW: Calculate Force Vector
                var forceVector = CalculateForceVector(frameName, "Frame", magnitude, dir, coordSys);

                var raw = new RawSapLoad
                {
                    ElementName = frameName,
                    LoadPattern = pattern,
                    Value1 = magnitude,
                    LoadType = "FrameDistributed",
                    Direction = dir,
                    CoordSys = coordSys,
                    DistStart = schema.GetDouble(r, colAbsDistA),
                    DistEnd = schema.GetDouble(r, colAbsDistB),
                    ElementZ = frameGeomMap.ContainsKey(frameName) ? frameGeomMap[frameName] : 0
                };

                // Set vector components
                raw.SetForceVector(forceVector);

                loads.Add(raw);
            }

            return loads;
        }

        /// <summary>
        /// ??c t?i Area Uniform To Frame (t?i 1 ph??ng/2 ph??ng truy?n vào d?m)
        /// REFACTORED: Vector-based approach
        /// </summary>
        public List<RawSapLoad> ReadAreaUniformToFrameLoads(string patternFilter = null)
        {
            var loads = new List<RawSapLoad>();
            var schema = GetTableSchema("Area Loads - Uniform To Frame", patternFilter);
            if (schema.RecordCount == 0) return loads;

            // Mapping c?t
            string colArea = schema.FindColumn("Area", "Element");
            string colPattern = schema.FindColumn("LoadPat", "OutputCase");
            string colLoad = schema.FindColumn("UnifLoad", "LoadValue");
            string colDir = schema.FindColumn("Dir", "Direction");
            string colSys = schema.FindColumn("CoordSys", "CSys");
            string colDist = schema.FindColumn("DistType", "Distribution");

            if (colArea == null || colPattern == null) return loads;

            // Cache geometry Area ?? l?y cao ?? Z
            var areaGeomMap = new Dictionary<string, double>();
            var areas = SapUtils.GetAllAreasGeometry();
            foreach (var a in areas) areaGeomMap[a.Name] = a.AverageZ;

            for (int r = 0; r < schema.RecordCount; r++)
            {
                string areaName = schema.GetString(r, colArea);
                string pattern = schema.GetString(r, colPattern);
                if (string.IsNullOrEmpty(areaName) || string.IsNullOrEmpty(pattern)) continue;

                // Filter
                if (!string.IsNullOrEmpty(patternFilter))
                {
                    var patterns = patternFilter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!patterns.Any(p => p.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                double rawVal = schema.GetDouble(r, colLoad);
                double magnitude = Math.Abs(SapUtils.ConvertLoadToKnPerM2(rawVal));

                string dir = schema.GetString(r, colDir) ?? "Gravity";
                string sys = schema.GetString(r, colSys) ?? "GLOBAL";
                string distType = schema.GetString(r, colDist) ?? "Two way";

                // NEW: Calculate Force Vector
                var forceVector = CalculateForceVector(areaName, "Area", magnitude, dir, sys);
                double z = areaGeomMap.ContainsKey(areaName) ? areaGeomMap[areaName] : 0;

                var raw = new RawSapLoad
                {
                    ElementName = areaName,
                    LoadPattern = pattern,
                    Value1 = magnitude,
                    LoadType = "AreaUniformToFrame",
                    Direction = dir,
                    CoordSys = sys,
                    ElementZ = z,
                    DistributionType = distType
                };

                raw.SetForceVector(forceVector);
                loads.Add(raw);
            }
            return loads;
        }

        /// <summary>
        /// ??c ph?n l?c ?áy (Base Reaction) ?? ki?m tra t?ng t?i
        /// </summary>
        public double ReadBaseReaction(string loadPattern, string direction = "Z")
        {
            var schema = GetTableSchema("Base Reactions", loadPattern);
            if (schema.RecordCount == 0) return 0;

            string colCase = schema.FindColumn("OutputCase", "LoadCase", "LoadPat");
            
            // Xác ??nh c?t l?c d?a trên h??ng yêu c?u
            string targetColName = "GlobalFZ"; // M?c ??nh Z
            if (direction == "X") targetColName = "GlobalFX";
            if (direction == "Y") targetColName = "GlobalFY";
            
            string colForce = schema.FindColumn(targetColName, direction == "X" ? "FX" : (direction == "Y" ? "FY" : "FZ"));

            if (colCase == null || colForce == null) return 0;

            for (int i = 0; i < schema.RecordCount; i++)
            {
                string rowCase = schema.GetString(i, colCase);
                if (string.Equals(rowCase, loadPattern, StringComparison.OrdinalIgnoreCase))
                {
                    double val = schema.GetDouble(i, colForce);
                    return SapUtils.ConvertForceToKn(val); // Convert ??n v?
                }
            }
            return 0;
        }

        /// <summary>
        /// ??c Area Uniform Loads v?i Direction ?ã resolve
        /// REFACTORED: Vector-based approach
        /// </summary>
        public List<RawSapLoad> ReadAreaUniformLoads(string patternFilter = null)
        {
            var loads = new List<RawSapLoad>();
            var schema = GetTableSchema("Area Loads - Uniform", patternFilter);

            if (schema.RecordCount == 0) return loads;

            string colArea = schema.FindColumn("Area");
            string colPattern = schema.FindColumn("LoadPat", "OutputCase");
            string colDir = schema.FindColumn("Dir");
            string colCoordSys = schema.FindColumn("CoordSys");
            string colUnifLoad = schema.FindColumn("UnifLoad");

            if (colArea == null || colPattern == null) return loads;

            // Cache geometry
            var areaGeomMap = new Dictionary<string, double>();
            var areas = SapUtils.GetAllAreasGeometry();
            foreach (var a in areas) areaGeomMap[a.Name] = a.AverageZ;

            for (int r = 0; r < schema.RecordCount; r++)
            {
                string areaName = schema.GetString(r, colArea);
                string pattern = schema.GetString(r, colPattern);

                if (string.IsNullOrEmpty(areaName) || string.IsNullOrEmpty(pattern))
                    continue;

                if (!string.IsNullOrEmpty(patternFilter))
                {
                    var patterns = patternFilter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!patterns.Any(p => p.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                double rawVal = schema.GetDouble(r, colUnifLoad);
                double magnitude = Math.Abs(SapUtils.ConvertLoadToKnPerM2(rawVal));

                string dir = schema.GetString(r, colDir) ?? "Gravity";
                string coordSys = schema.GetString(r, colCoordSys) ?? "Local";

                // NEW: Calculate Force Vector
                var forceVector = CalculateForceVector(areaName, "Area", magnitude, dir, coordSys);

                var raw = new RawSapLoad
                {
                    ElementName = areaName,
                    LoadPattern = pattern,
                    Value1 = magnitude,
                    LoadType = "AreaUniform",
                    Direction = dir,
                    CoordSys = coordSys,
                    ElementZ = areaGeomMap.ContainsKey(areaName) ? areaGeomMap[areaName] : 0
                };

                raw.SetForceVector(forceVector);
                loads.Add(raw);
            }

            return loads;
        }

        /// <summary>
        /// ??c Joint Loads (Force) - ??Y ?? F1, F2, F3
        /// REFACTORED: Vector-based approach
        /// </summary>
        public List<RawSapLoad> ReadJointLoads(string patternFilter = null)
        {
            var loads = new List<RawSapLoad>();
            var schema = GetTableSchema("Joint Loads - Force", patternFilter);

            if (schema.RecordCount == 0) return loads;

            string colJoint = schema.FindColumn("Joint");
            string colPattern = schema.FindColumn("LoadPat", "OutputCase");
            string colCoordSys = schema.FindColumn("CoordSys");
            string colF1 = schema.FindColumn("F1");
            string colF2 = schema.FindColumn("F2");
            string colF3 = schema.FindColumn("F3");

            if (colJoint == null || colPattern == null) return loads;

            // Cache geometry
            var pointGeomMap = new Dictionary<string, double>();
            var points = SapUtils.GetAllPoints();
            foreach (var p in points) pointGeomMap[p.Name] = p.Z;

            for (int r = 0; r < schema.RecordCount; r++)
            {
                string joint = schema.GetString(r, colJoint);
                string pattern = schema.GetString(r, colPattern);
                string coordSys = schema.GetString(r, colCoordSys) ?? "GLOBAL";

                if (string.IsNullOrEmpty(joint) || string.IsNullOrEmpty(pattern))
                    continue;

                if (!string.IsNullOrEmpty(patternFilter))
                {
                    var patterns = patternFilter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!patterns.Any(p => p.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                double f1 = schema.GetDouble(r, colF1);
                double f2 = schema.GetDouble(r, colF2);
                double f3 = schema.GetDouble(r, colF3);

                double z = pointGeomMap.ContainsKey(joint) ? pointGeomMap[joint] : 0;

                // Thêm 3 component riêng bi?t
                if (Math.Abs(f1) > 0.001)
                {
                    double magnitude = Math.Abs(SapUtils.ConvertForceToKn(f1));
                    var forceVector = CalculateForceVector(joint, "Point", magnitude, "1", coordSys);

                    var raw = new RawSapLoad
                    {
                        ElementName = joint,
                        LoadPattern = pattern,
                        Value1 = magnitude,
                        LoadType = "PointForce",
                        Direction = "Local-1",
                        CoordSys = coordSys,
                        ElementZ = z
                    };

                    raw.SetForceVector(forceVector * Math.Sign(f1));
                    loads.Add(raw);
                }

                if (Math.Abs(f2) > 0.001)
                {
                    double magnitude = Math.Abs(SapUtils.ConvertForceToKn(f2));
                    var forceVector = CalculateForceVector(joint, "Point", magnitude, "2", coordSys);

                    var raw = new RawSapLoad
                    {
                        ElementName = joint,
                        LoadPattern = pattern,
                        Value1 = magnitude,
                        LoadType = "PointForce",
                        Direction = "Local-2",
                        CoordSys = coordSys,
                        ElementZ = z
                    };

                    raw.SetForceVector(forceVector * Math.Sign(f2));
                    loads.Add(raw);
                }

                if (Math.Abs(f3) > 0.001)
                {
                    double magnitude = Math.Abs(SapUtils.ConvertForceToKn(f3));
                    var forceVector = CalculateForceVector(joint, "Point", magnitude, "3", coordSys);

                    var raw = new RawSapLoad
                    {
                        ElementName = joint,
                        LoadPattern = pattern,
                        Value1 = magnitude,
                        LoadType = "PointForce",
                        Direction = "Local-3",
                        CoordSys = coordSys,
                        ElementZ = z
                    };

                    raw.SetForceVector(forceVector * Math.Sign(f3));
                    loads.Add(raw);
                }
            }

            return loads;
        }

        /// <summary>
        /// Read all loads (frame/area/joint) - RENAMED from ReadAllLoadsWithBaseReaction
        /// BASE REACTION REMOVED: Ng??i dùng s? check th? công trên SAP2000
        /// 
        /// Returns list of loads with full Vector components (DirectionX/Y/Z)
        /// </summary>
        public List<RawSapLoad> ReadAllLoads(string patternFilter)
        {
            var loads = new List<RawSapLoad>();
            loads.AddRange(ReadFrameDistributedLoads(patternFilter));
            loads.AddRange(ReadAreaUniformLoads(patternFilter));
            loads.AddRange(ReadAreaUniformToFrameLoads(patternFilter));
            loads.AddRange(ReadJointLoads(patternFilter));

            // Include frame point loads (legacy)
            try { loads.AddRange(SapUtils.GetAllFramePointLoads(patternFilter)); } catch { }

            return loads;
        }

        #endregion

        #region Direction Vector Resolution (FIX BUG #1 + #3) - DEPRECATED

        // XÓA B? HOÀN TOÀN - Logic c? không còn s? d?ng

        #endregion
    }
}
