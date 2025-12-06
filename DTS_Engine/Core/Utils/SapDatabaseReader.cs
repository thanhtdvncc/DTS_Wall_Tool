using DTS_Engine.Core.Data;
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
        private readonly cSapModel _model;
        private readonly Dictionary<string, TableSchema> _schemaCache;

        public SapDatabaseReader(cSapModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _schemaCache = new Dictionary<string, TableSchema>();
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

        #region Load Direction Resolver (UPGRADED)

        /// <summary>
        /// UPGRADED: Resolved Load Direction with full global components
        /// </summary>
        public class ResolvedDirection
        {
            public string GlobalAxis { get; set; } // Dominant: "X", "Y", "Z"
            public double Sign { get; set; } // Overall sign: +1 or -1

            // NEW: Full global components
            public double Gx { get; set; }
            public double Gy { get; set; }
            public double Gz { get; set; }

            public string Description { get; set; }

            public override string ToString() => GlobalAxis;
        }

        /// <summary>
        /// UPGRADED: Full transformation from Local to Global
        /// </summary>
        public ResolvedDirection ResolveDirection(string elementName, string elementType, string direction, string coordSys)
        {
            var result = new ResolvedDirection();

            // Parse direction string to get local axis number
            int localAxis = ParseDirectionToAxis(direction);

            // Case 1: GLOBAL CoordSys - Direct mapping
            if (string.Equals(coordSys, "GLOBAL", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(direction, "Gravity", StringComparison.OrdinalIgnoreCase) || localAxis == 3)
                {
                    result.Gx = 0; result.Gy = 0; result.Gz = -1.0;
                    result.GlobalAxis = "Z";
                    result.Sign = -1.0;
                    result.Description = "Global Gravity";
                }
                else if (localAxis == 1 || direction.Contains("X"))
                {
                    result.Gx = 1.0; result.Gy = 0; result.Gz = 0;
                    result.GlobalAxis = "X";
                    result.Sign = 1.0;
                    result.Description = "Global X";
                }
                else if (localAxis == 2 || direction.Contains("Y"))
                {
                    result.Gx = 0; result.Gy = 1.0; result.Gz = 0;
                    result.GlobalAxis = "Y";
                    result.Sign = 1.0;
                    result.Description = "Global Y";
                }
                else
                {
                    result.Gx = 0; result.Gy = 0; result.Gz = -1.0;
                    result.GlobalAxis = "Z";
                    result.Sign = -1.0;
                    result.Description = "Global Z (default)";
                }

                return result;
            }

            // Case 2: LOCAL CoordSys - Need transformation
            LocalAxesInfo axes = null;

            if (elementType == "Frame")
                axes = GetFrameLocalAxes(elementName);
            else if (elementType == "Area")
                axes = GetAreaLocalAxes(elementName);

            // Transform local axis to global
            if (axes != null && axes.LocalToGlobal != null)
            {
                var (gx, gy, gz) = TransformLocalToGlobal(axes.LocalToGlobal, localAxis);
                result.Gx = gx;
                result.Gy = gy;
                result.Gz = gz;

                // Determine dominant axis
                double absX = Math.Abs(gx);
                double absY = Math.Abs(gy);
                double absZ = Math.Abs(gz);

                if (absX > absY && absX > absZ)
                {
                    result.GlobalAxis = "X";
                    result.Sign = Math.Sign(gx);
                }
                else if (absY > absX && absY > absZ)
                {
                    result.GlobalAxis = "Y";
                    result.Sign = Math.Sign(gy);
                }
                else
                {
                    result.GlobalAxis = "Z";
                    result.Sign = Math.Sign(gz);
                }

                result.Description = $"Local {localAxis} ? Global ({gx:F2}, {gy:F2}, {gz:F2})";
            }
            else
            {
                // Fallback: Assume Local 3 = Gravity
                if (localAxis == 3 || string.Equals(direction, "Gravity", StringComparison.OrdinalIgnoreCase))
                {
                    result.Gx = 0; result.Gy = 0; result.Gz = -1.0;
                    result.GlobalAxis = "Z";
                    result.Sign = -1.0;
                    result.Description = "Local 3 (fallback Gravity)";
                }
                else
                {
                    result.Gx = 0; result.Gy = 0; result.Gz = -1.0;
                    result.GlobalAxis = "Z";
                    result.Sign = -1.0;
                    result.Description = "Unknown (default Gravity)";
                }
            }

            return result;
        }

        /// <summary>
        /// NEW: Parse direction string to axis number (1, 2, or 3)
        /// </summary>
        private int ParseDirectionToAxis(string direction)
        {
            if (string.IsNullOrEmpty(direction)) return 3;

            direction = direction.ToUpperInvariant().Trim();

            if (direction == "1" || direction.Contains("LOCAL-1") || direction.Contains("LOCAL1"))
                return 1;
            if (direction == "2" || direction.Contains("LOCAL-2") || direction.Contains("LOCAL2"))
                return 2;
            if (direction == "3" || direction.Contains("LOCAL-3") || direction.Contains("LOCAL3"))
                return 3;

            // Gravity = Local 3
            if (direction.Contains("GRAVITY") || direction.Contains("GRAV"))
                return 3;

            return 3; // Default
        }

        /// <summary>
        /// NEW: Transform local axis to global coordinates
        /// </summary>
        private (double X, double Y, double Z) TransformLocalToGlobal(double[] matrix, int localAxis)
        {
            if (matrix == null || matrix.Length < 9)
                return (0, 0, -1);

            // Extract column (localAxis-1) from transformation matrix
            int col = localAxis - 1;

            return (matrix[col], matrix[3 + col], matrix[6 + col]);
        }

        #endregion

        // --- Upgraded Local Axes Support ---
        #region Local Axes Support (UPGRADED)

        /// <summary>
        /// Local Axes Information - UPGRADED with geometry analysis
        /// </summary>
        public class LocalAxesInfo
        {
            public string ElementName { get; set; }
            public string ElementType { get; set; } // "Frame", "Area", "Point"
            public double Angle { get; set; } // Rotation angle (deg)
            public bool IsAdvanced { get; set; }

            // NEW: Normal vector for Area (to detect vertical walls)
            public Vector3D Normal { get; set; }

            // NEW: Transformation matrix [3x3] stored as [0..8]
            public double[] LocalToGlobal { get; set; }

            // Quick helpers
            public bool IsVertical => Normal != null && Math.Abs(Normal.Z) < 0.1;
            public bool IsHorizontal => Normal != null && Math.Abs(Normal.Z) > 0.9;
        }

        /// <summary>
        /// Simple 3D vector helper used for axis computations
        /// </summary>
        public class Vector3D
        {
            public double X, Y, Z;
            public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }
            public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
            public Vector3D Normalize()
            {
                double l = Length;
                if (l < 1e-9) return new Vector3D(0, 0, 1);
                return new Vector3D(X / l, Y / l, Z / l);
            }
            public static Vector3D Cross(Vector3D a, Vector3D b)
            {
                return new Vector3D(
                    a.Y * b.Z - a.Z * b.Y,
                    a.Z * b.X - a.X * b.Z,
                    a.X * b.Y - a.Y * b.X);
            }
        }

        // Simple cache
        private Dictionary<string, LocalAxesInfo> _axesCache = new Dictionary<string, LocalAxesInfo>();

        /// <summary>
        /// UPGRADED: Get Frame Local Axes with transformation matrix built from geometry
        /// </summary>
        public LocalAxesInfo GetFrameLocalAxes(string frameName)
        {
            if (_axesCache.TryGetValue($"F:{frameName}", out var cached)) return cached;
            try
            {
                double ang = 0; bool advanced = false;
                int ret = _model.FrameObj.GetLocalAxes(frameName, ref ang, ref advanced);
                if (ret == 0)
                {
                    var info = new LocalAxesInfo
                    {
                        ElementName = frameName,
                        ElementType = "Frame",
                        Angle = ang,
                        IsAdvanced = advanced,
                        LocalToGlobal = BuildFrameTransform(frameName, ang)
                    };
                    _axesCache[$"F:{frameName}"] = info;
                    return info;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// UPGRADED: Get Area Local Axes (compute normal from geometry)
        /// </summary>
        public LocalAxesInfo GetAreaLocalAxes(string areaName)
        {
            if (_axesCache.TryGetValue($"A:{areaName}", out var cached)) return cached;
            try
            {
                double ang = 0; bool advanced = false;
                int ret = _model.AreaObj.GetLocalAxes(areaName, ref ang, ref advanced);
                if (ret == 0)
                {
                    var info = new LocalAxesInfo { ElementName = areaName, ElementType = "Area", Angle = ang, IsAdvanced = advanced };
                    var geom = SapUtils.GetAreaGeometry(areaName);
                    if (geom != null && geom.BoundaryPoints != null && geom.BoundaryPoints.Count >= 3 && geom.ZValues != null && geom.ZValues.Count >= 3)
                    {
                        var p0 = new Vector3D(geom.BoundaryPoints[0].X, geom.BoundaryPoints[0].Y, geom.ZValues[0]);
                        var p1 = new Vector3D(geom.BoundaryPoints[1].X, geom.BoundaryPoints[1].Y, geom.ZValues[1]);
                        var p2 = new Vector3D(geom.BoundaryPoints[2].X, geom.BoundaryPoints[2].Y, geom.ZValues[2]);
                        var v1 = new Vector3D(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z);
                        var v2 = new Vector3D(p2.X - p0.X, p2.Y - p0.Y, p2.Z - p0.Z);
                        info.Normal = Vector3D.Cross(v1, v2).Normalize();
                        info.LocalToGlobal = BuildAreaTransform(info.Normal, ang);
                    }
                    _axesCache[$"A:{areaName}"] = info;
                    return info;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Build a simplified transform for a frame using geometry orientation and local angle
        /// Returns a 3x3 matrix stored row-major [r0c0, r0c1, r0c2, r1c0, ...]
        /// </summary>
        private double[] BuildFrameTransform(string frameName, double angleDeg)
        {
            var frame = SapUtils.GetFrameGeometry(frameName);
            if (frame == null) return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            double dx = frame.EndPt.X - frame.StartPt.X;
            double dy = frame.EndPt.Y - frame.StartPt.Y;
            double dz = frame.Z2 - frame.Z1;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-6) return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            double l1x = dx / len, l1y = dy / len, l1z = dz / len;
            double rad = angleDeg * Math.PI / 180.0; double cos = Math.Cos(rad); double sin = Math.Sin(rad);
            // Build approximate in-plane vectors
            double l2x = -l1y * cos, l2y = l1x * cos, l2z = 0;
            double l3x = -l1y * sin, l3y = l1x * sin, l3z = 1.0;
            double len2 = Math.Sqrt(l2x * l2x + l2y * l2y + l2z * l2z);
            double len3 = Math.Sqrt(l3x * l3x + l3y * l3y + l3z * l3z);
            if (len2 > 1e-6) { l2x /= len2; l2y /= len2; l2z /= len2; }
            if (len3 > 1e-6) { l3x /= len3; l3y /= len3; l3z /= len3; }
            return new double[] { l1x, l2x, l3x, l1y, l2y, l3y, l1z, l2z, l3z };
        }

        /// <summary>
        /// Build transform for area based on normal and rotation
        /// </summary>
        private double[] BuildAreaTransform(Vector3D normal, double angleDeg)
        {
            // local3 = normal
            if (normal == null) return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            Vector3D n = normal.Normalize();
            Vector3D local1;
            if (Math.Abs(n.Z) > 0.9) local1 = new Vector3D(1, 0, 0);
            else local1 = Vector3D.Cross(new Vector3D(0, 0, 1), n).Normalize();
            Vector3D local2 = Vector3D.Cross(n, local1).Normalize();
            double rad = angleDeg * Math.PI / 180.0; double cos = Math.Cos(rad); double sin = Math.Sin(rad);
            Vector3D rot1 = new Vector3D(local1.X * cos - local2.X * sin, local1.Y * cos - local2.Y * sin, local1.Z * cos - local2.Z * sin);
            Vector3D rot2 = new Vector3D(local1.X * sin + local2.X * cos, local1.Y * sin + local2.Y * cos, local1.Z * sin + local2.Z * cos);
            return new double[] { rot1.X, rot2.X, n.X, rot1.Y, rot2.Y, n.Y, rot1.Z, rot2.Z, n.Z };
        }

        #endregion

        #region High-Level Load Readers

        /// <summary>
        /// ??c Frame Distributed Loads v?i Direction ?ã resolve
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

                double val = schema.GetDouble(r, colFOverLA);
                val = SapUtils.ConvertLoadToKnPerM(val);

                string dir = schema.GetString(r, colDir) ?? "Gravity";
                string coordSys = schema.GetString(r, colCoordSys) ?? "GLOBAL";

                // Resolve direction
                var resolved = ResolveDirection(frameName, "Frame", dir, coordSys);

                loads.Add(new RawSapLoad
                {
                    ElementName = frameName,
                    LoadPattern = pattern,
                    Value1 = Math.Abs(val),
                    LoadType = "FrameDistributed",
                    Direction = resolved.ToString(),
                    GlobalAxis = resolved.GlobalAxis,
                    DirectionSign = resolved.Sign,
                    CoordSys = coordSys,
                    DistStart = schema.GetDouble(r, colAbsDistA),
                    DistEnd = schema.GetDouble(r, colAbsDistB),
                    ElementZ = frameGeomMap.ContainsKey(frameName) ? frameGeomMap[frameName] : 0,

                    // Global components
                    DirectionX = val * resolved.Gx,
                    DirectionY = val * resolved.Gy,
                    DirectionZ = val * resolved.Gz
                });
            }

            return loads;
        }

        /// <summary>
        /// ??c Area Uniform Loads v?i Direction ?ã resolve
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

                double val = schema.GetDouble(r, colUnifLoad);
                val = SapUtils.ConvertLoadToKnPerM2(val);

                string dir = schema.GetString(r, colDir) ?? "Gravity";
                string coordSys = schema.GetString(r, colCoordSys) ?? "Local";

                var resolved = ResolveDirection(areaName, "Area", dir, coordSys);

                loads.Add(new RawSapLoad
                {
                    ElementName = areaName,
                    LoadPattern = pattern,
                    Value1 = Math.Abs(val),
                    LoadType = "AreaUniform",
                    Direction = resolved.ToString(),
                    GlobalAxis = resolved.GlobalAxis,
                    DirectionSign = resolved.Sign,
                    CoordSys = coordSys,
                    ElementZ = areaGeomMap.ContainsKey(areaName) ? areaGeomMap[areaName] : 0,

                    // Global components
                    DirectionX = val * resolved.Gx,
                    DirectionY = val * resolved.Gy,
                    DirectionZ = val * resolved.Gz
                });
            }

            return loads;
        }

        /// <summary>
        /// ??c Joint Loads (Force) - ??Y ?? F1, F2, F3
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
                    var resolved = ResolveDirection(joint, "Joint", "1", coordSys);
                    double convertedF1 = SapUtils.ConvertForceToKn(f1);

                    loads.Add(new RawSapLoad
                    {
                        ElementName = joint,
                        LoadPattern = pattern,
                        Value1 = Math.Abs(convertedF1),
                        LoadType = "PointForce",
                        Direction = resolved.ToString(),
                        GlobalAxis = resolved.GlobalAxis,
                        DirectionSign = resolved.Sign * Math.Sign(f1),
                        CoordSys = coordSys,
                        ElementZ = z,

                        // Global components
                        DirectionX = convertedF1 * resolved.Gx,
                        DirectionY = convertedF1 * resolved.Gy,
                        DirectionZ = convertedF1 * resolved.Gz
                    });
                }

                if (Math.Abs(f2) > 0.001)
                {
                    var resolved = ResolveDirection(joint, "Joint", "2", coordSys);
                    double convertedF2 = SapUtils.ConvertForceToKn(f2);

                    loads.Add(new RawSapLoad
                    {
                        ElementName = joint,
                        LoadPattern = pattern,
                        Value1 = Math.Abs(convertedF2),
                        LoadType = "PointForce",
                        Direction = resolved.ToString(),
                        GlobalAxis = resolved.GlobalAxis,
                        DirectionSign = resolved.Sign * Math.Sign(f2),
                        CoordSys = coordSys,
                        ElementZ = z,

                        // Global components
                        DirectionX = convertedF2 * resolved.Gx,
                        DirectionY = convertedF2 * resolved.Gy,
                        DirectionZ = convertedF2 * resolved.Gz
                    });
                }

                if (Math.Abs(f3) > 0.001)
                {
                    var resolved = ResolveDirection(joint, "Joint", "3", coordSys);
                    double convertedF3 = SapUtils.ConvertForceToKn(f3);

                    loads.Add(new RawSapLoad
                    {
                        ElementName = joint,
                        LoadPattern = pattern,
                        Value1 = Math.Abs(convertedF3),
                        LoadType = "PointForce",
                        Direction = resolved.ToString(),
                        GlobalAxis = resolved.GlobalAxis,
                        DirectionSign = resolved.Sign * Math.Sign(f3),
                        CoordSys = coordSys,
                        ElementZ = z,

                        // Global components
                        DirectionX = convertedF3 * resolved.Gx,
                        DirectionY = convertedF3 * resolved.Gy,
                        DirectionZ = convertedF3 * resolved.Gz
                    });
                }
            }

            return loads;
        }

        #endregion
    }
}
