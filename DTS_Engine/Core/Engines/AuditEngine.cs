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
    /// Engine ki?m toán t?i tr?ng t? SAP2000.
    /// T? ??ng gom nhóm theo T?ng -> Lo?i t?i -> Giá tr?.
    /// S? d?ng NetTopologySuite ?? union và slice geometry.
    /// 
    /// ?? QUY TRÌNH X? LÝ:
    /// 1. Data Extraction: L?y toàn b? t?i tr?ng t? SAP2000 API
    /// 2. Spatial Mapping: Xác ??nh t?ng và v? trí tr?c
    /// 3. Grouping: Gom nhóm theo t?ng, lo?i t?i, giá tr?
    /// 4. Calculation: Tính di?n tích/chi?u dài và t?ng l?c
    /// 5. Reporting: Xu?t báo cáo ??nh d?ng k? s?
    /// </summary>
    public class AuditEngine
    {
        #region Constants

        /// <summary>Dung sai Z ?? xác ??nh cùng t?ng (mm)</summary>
        private const double STORY_TOLERANCE = 500.0;

        /// <summary>Dung sai giá tr? t?i ?? gom nhóm (%)</summary>
        private const double VALUE_TOLERANCE_PERCENT = 1.0;

        /// <summary>H? s? quy ??i mm² sang m²</summary>
        private const double MM2_TO_M2 = 1.0 / 1000000.0;

        /// <summary>H? s? quy ??i mm sang m</summary>
        private const double MM_TO_M = 1.0 / 1000.0;

        #endregion

        #region Fields

        private List<SapUtils.GridLineRecord> _grids;
        private List<SapUtils.GridStoryItem> _stories;
        private Dictionary<string, SapFrame> _frameGeometryCache;
        private Dictionary<string, SapArea> _areaGeometryCache;
        private GeometryFactory _geometryFactory;

        #endregion

        #region Constructor

        public AuditEngine()
        {
            _geometryFactory = new GeometryFactory();

            // Cache grids và stories t? SAP
            if (SapUtils.IsConnected)
            {
                _grids = SapUtils.GetGridLines();
                _stories = SapUtils.GetStories();
            }
            else
            {
                _grids = new List<SapUtils.GridLineRecord>();
                _stories = new List<SapUtils.GridStoryItem>();
            }

            _frameGeometryCache = new Dictionary<string, SapFrame>();
            _areaGeometryCache = new Dictionary<string, SapArea>();
        }

        #endregion

        #region Main Audit Method

        /// <summary>
        /// Ch?y ki?m toán cho m?t ho?c nhi?u Load Pattern.
        /// </summary>
        /// <param name="loadPatterns">Danh sách pattern cách nhau b?ng d?u ph?y</param>
        /// <returns>Danh sách báo cáo theo t?ng pattern</returns>
        public List<AuditReport> RunAudit(string loadPatterns)
        {
            var reports = new List<AuditReport>();

            if (string.IsNullOrEmpty(loadPatterns))
                return reports;

            // Parse patterns
            var patterns = loadPatterns.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(p => p.Trim().ToUpper())
                                       .Distinct()
                                       .ToList();

            foreach (var pattern in patterns)
            {
                var report = RunSingleAudit(pattern);
                if (report != null)
                    reports.Add(report);
            }

            return reports;
        }

        /// <summary>
        /// Ch?y ki?m toán cho m?t Load Pattern
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

            // 1. Cache geometry
            CacheGeometry();

            // 2. Thu th?p t?t c? các lo?i t?i
            var allLoads = new List<RawSapLoad>();
            allLoads.AddRange(SapUtils.GetAllFrameDistributedLoads(loadPattern));
            allLoads.AddRange(SapUtils.GetAllFramePointLoads(loadPattern));
            allLoads.AddRange(SapUtils.GetAllAreaUniformLoads(loadPattern));
            allLoads.AddRange(SapUtils.GetAllAreaUniformToFrameLoads(loadPattern));
            allLoads.AddRange(SapUtils.GetAllPointLoads(loadPattern));

            if (allLoads.Count == 0)
            {
                return report; // Không có t?i -> báo cáo r?ng
            }

            // 3. Xác ??nh danh sách t?ng (d?a trên Z c?a ph?n t?)
            var storyElevations = DetermineStoryElevations(allLoads);

            // 4. Nhóm theo t?ng
            foreach (var storyInfo in storyElevations.OrderByDescending(s => s.Value))
            {
                var storyLoads = allLoads.Where(l =>
                    Math.Abs(l.ElementZ - storyInfo.Value) <= STORY_TOLERANCE).ToList();

                if (storyLoads.Count == 0) continue;

                var storyGroup = ProcessStory(storyInfo.Key, storyInfo.Value, storyLoads);
                if (storyGroup.LoadTypeGroups.Count > 0)
                    report.Stories.Add(storyGroup);
            }

            // 5. L?y ph?n l?c ?áy ?? ??i chi?u
            report.SapBaseReaction = SapUtils.GetBaseReactionZ(loadPattern);

            return report;
        }

        #endregion

        #region Processing Methods

        /// <summary>
        /// X? lý m?t t?ng
        /// </summary>
        private AuditStoryGroup ProcessStory(string storyName, double elevation, List<RawSapLoad> loads)
        {
            var storyGroup = new AuditStoryGroup
            {
                StoryName = storyName,
                Elevation = elevation
            };

            // Nhóm theo lo?i t?i
            var loadTypeGroups = loads.GroupBy(l => l.LoadType);

            foreach (var typeGroup in loadTypeGroups)
            {
                var typeResult = ProcessLoadType(typeGroup.Key, typeGroup.ToList());
                if (typeResult.ValueGroups.Count > 0)
                    storyGroup.LoadTypeGroups.Add(typeResult);
            }

            return storyGroup;
        }

        /// <summary>
        /// X? lý m?t lo?i t?i (Frame/Area/Point)
        /// </summary>
        private AuditLoadTypeGroup ProcessLoadType(string loadType, List<RawSapLoad> loads)
        {
            var typeGroup = new AuditLoadTypeGroup
            {
                LoadTypeName = GetLoadTypeDisplayName(loadType)
            };

            // Nhóm theo giá tr? t?i (v?i dung sai)
            var valueGroups = GroupByValue(loads);

            foreach (var valGroup in valueGroups.OrderByDescending(g => g.Key))
            {
                var valueResult = ProcessValueGroup(loadType, valGroup.Key, valGroup.ToList());
                if (valueResult.Entries.Count > 0)
                    typeGroup.ValueGroups.Add(valueResult);
            }

            return typeGroup;
        }

        /// <summary>
        /// X? lý nhóm cùng giá tr? t?i
        /// </summary>
        private AuditValueGroup ProcessValueGroup(string loadType, double loadValue, List<RawSapLoad> loads)
        {
            var valueGroup = new AuditValueGroup
            {
                LoadValue = loadValue,
                Direction = loads.FirstOrDefault()?.Direction ?? "Gravity"
            };

            switch (loadType)
            {
                case "AreaUniform":
                case "AreaUniformToFrame":
                    ProcessAreaLoads(loads, valueGroup);
                    break;

                case "FrameDistributed":
                    ProcessFrameDistributedLoads(loads, valueGroup);
                    break;

                case "FramePoint":
                case "PointForce":
                    ProcessPointLoads(loads, valueGroup);
                    break;

                default:
                    ProcessGenericLoads(loads, valueGroup);
                    break;
            }

            return valueGroup;
        }

        /// <summary>
        /// X? lý t?i Area - Union geometry và tính di?n tích
        /// </summary>
        private void ProcessAreaLoads(List<RawSapLoad> loads, AuditValueGroup valueGroup)
        {
            // Nhóm theo v? trí tr?c
            var gridGroups = loads.GroupBy(l => GetGridLocation(l.ElementName, "Area"));

            foreach (var gridGroup in gridGroups.OrderBy(g => g.Key))
            {
                var elemNames = gridGroup.Select(l => l.ElementName).ToList();
                var polygons = new List<Polygon>();

                foreach (var elemName in elemNames)
                {
                    if (_areaGeometryCache.TryGetValue(elemName, out var area))
                    {
                        var poly = CreateNtsPolygon(area.BoundaryPoints);
                        if (poly != null && poly.IsValid)
                            polygons.Add(poly);
                    }
                }

                if (polygons.Count == 0) continue;

                // Union ?? lo?i b? overlap
                Geometry unioned;
                try
                {
                    unioned = UnaryUnionOp.Union(polygons.Cast<Geometry>().ToList());
                }
                catch
                {
                    // Fallback n?u union fail
                    unioned = polygons.First();
                }

                // Tính di?n tích và t?o di?n gi?i
                double totalAreaMm2 = unioned.Area;
                double totalAreaM2 = totalAreaMm2 * MM2_TO_M2;
                double force = totalAreaM2 * valueGroup.LoadValue;

                string explanation = FormatAreaExplanation(unioned, polygons.Count);

                valueGroup.Entries.Add(new AuditEntry
                {
                    GridLocation = gridGroup.Key,
                    Explanation = explanation,
                    Quantity = totalAreaM2,
                    Force = force,
                    ElementList = elemNames
                });
            }
        }

        /// <summary>
        /// X? lý t?i Frame phân b? - Tính t?ng chi?u dài
        /// </summary>
        private void ProcessFrameDistributedLoads(List<RawSapLoad> loads, AuditValueGroup valueGroup)
        {
            // Nhóm theo v? trí tr?c
            var gridGroups = loads.GroupBy(l => GetGridLocation(l.ElementName, "Frame"));

            foreach (var gridGroup in gridGroups.OrderBy(g => g.Key))
            {
                var elemNames = gridGroup.Select(l => l.ElementName).ToList();
                var lengths = new List<double>();

                foreach (var elemName in elemNames)
                {
                    if (_frameGeometryCache.TryGetValue(elemName, out var frame))
                    {
                        lengths.Add(frame.Length2D);
                    }
                }

                if (lengths.Count == 0) continue;

                double totalLengthMm = lengths.Sum();
                double totalLengthM = totalLengthMm * MM_TO_M;
                double force = totalLengthM * valueGroup.LoadValue;

                // T?o di?n gi?i chi?u dài
                string explanation = FormatLengthExplanation(lengths);

                valueGroup.Entries.Add(new AuditEntry
                {
                    GridLocation = gridGroup.Key,
                    Explanation = explanation,
                    Quantity = totalLengthM,
                    Force = force,
                    ElementList = elemNames
                });
            }
        }

        /// <summary>
        /// X? lý t?i t?p trung
        /// </summary>
        private void ProcessPointLoads(List<RawSapLoad> loads, AuditValueGroup valueGroup)
        {
            // Nhóm theo v? trí tr?c
            var gridGroups = loads.GroupBy(l => GetGridLocation(l.ElementName, "Point"));

            foreach (var gridGroup in gridGroups.OrderBy(g => g.Key))
            {
                var elemNames = gridGroup.Select(l => l.ElementName).ToList();
                double totalForce = gridGroup.Sum(l => l.Value1);
                int count = gridGroup.Count();

                string explanation = count == 1
                    ? $"P = {totalForce:0.00} kN"
                    : $"{count} ?i?m × avg = {totalForce:0.00} kN";

                valueGroup.Entries.Add(new AuditEntry
                {
                    GridLocation = gridGroup.Key,
                    Explanation = explanation,
                    Quantity = count,
                    Force = totalForce,
                    ElementList = elemNames
                });
            }
        }

        /// <summary>
        /// X? lý t?i generic (fallback)
        /// </summary>
        private void ProcessGenericLoads(List<RawSapLoad> loads, AuditValueGroup valueGroup)
        {
            var gridGroups = loads.GroupBy(l => GetGridLocation(l.ElementName, "Unknown"));

            foreach (var gridGroup in gridGroups)
            {
                var elemNames = gridGroup.Select(l => l.ElementName).ToList();
                double totalValue = gridGroup.Sum(l => l.Value1);

                valueGroup.Entries.Add(new AuditEntry
                {
                    GridLocation = gridGroup.Key,
                    Explanation = $"{gridGroup.Count()} ph?n t?",
                    Quantity = gridGroup.Count(),
                    Force = totalValue,
                    ElementList = elemNames
                });
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Cache geometry t? SAP
        /// </summary>
        private void CacheGeometry()
        {
            _frameGeometryCache.Clear();
            _areaGeometryCache.Clear();

            // Cache frames
            var frames = SapUtils.GetAllFramesGeometry();
            foreach (var f in frames)
            {
                _frameGeometryCache[f.Name] = f;
            }

            // Cache areas
            var areas = SapUtils.GetAllAreasGeometry();
            foreach (var a in areas)
            {
                _areaGeometryCache[a.Name] = a;
            }
        }

        /// <summary>
        /// Xác ??nh danh sách t?ng t? cao ?? ph?n t?
        /// </summary>
        private Dictionary<string, double> DetermineStoryElevations(List<RawSapLoad> loads)
        {
            var result = new Dictionary<string, double>();

            // L?y t?t c? Z t? loads
            var allZ = loads.Select(l => l.ElementZ).Distinct().OrderByDescending(z => z).ToList();

            // Nhóm Z g?n nhau thành m?t t?ng
            var zGroups = new List<List<double>>();
            foreach (var z in allZ)
            {
                var existingGroup = zGroups.FirstOrDefault(g => Math.Abs(g.Average() - z) <= STORY_TOLERANCE);
                if (existingGroup != null)
                {
                    existingGroup.Add(z);
                }
                else
                {
                    zGroups.Add(new List<double> { z });
                }
            }

            // Map v?i story t? Grid n?u có
            var zStories = _stories.Where(s => s.IsElevation).OrderByDescending(s => s.Elevation).ToList();
            int storyIndex = 1;

            foreach (var group in zGroups.OrderByDescending(g => g.Average()))
            {
                double avgZ = group.Average();

                // Tìm story g?n nh?t
                var matchingStory = zStories.FirstOrDefault(s => Math.Abs(s.Elevation - avgZ) <= STORY_TOLERANCE);

                string storyName;
                if (matchingStory != null)
                {
                    storyName = matchingStory.Name;
                }
                else
                {
                    storyName = $"Z={avgZ / 1000.0:0.0}m";
                }

                if (!result.ContainsKey(storyName))
                {
                    result[storyName] = avgZ;
                }

                storyIndex++;
            }

            return result;
        }

        /// <summary>
        /// Nhóm loads theo giá tr? (v?i dung sai)
        /// </summary>
        private IEnumerable<IGrouping<double, RawSapLoad>> GroupByValue(List<RawSapLoad> loads)
        {
            // Round giá tr? ?? gom nhóm
            return loads.GroupBy(l => Math.Round(l.Value1, 2));
        }

        /// <summary>
        /// Xác ??nh v? trí theo tr?c
        /// </summary>
        private string GetGridLocation(string elementName, string elementType)
        {
            Point2D center = Point2D.Origin;

            if (elementType == "Frame" && _frameGeometryCache.TryGetValue(elementName, out var frame))
            {
                center = frame.Midpoint;
            }
            else if (elementType == "Area" && _areaGeometryCache.TryGetValue(elementName, out var area))
            {
                center = area.Centroid;
            }
            else if (elementType == "Point")
            {
                var points = SapUtils.GetAllPoints();
                var pt = points.FirstOrDefault(p => p.Name == elementName);
                if (pt != null)
                {
                    center = new Point2D(pt.X, pt.Y);
                }
            }

            // Tìm tr?c g?n nh?t
            string xGrid = FindNearestGrid(center.X, "X");
            string yGrid = FindNearestGrid(center.Y, "Y");

            if (!string.IsNullOrEmpty(xGrid) && !string.IsNullOrEmpty(yGrid))
            {
                return $"Tr?c {xGrid} / {yGrid}";
            }
            else if (!string.IsNullOrEmpty(xGrid))
            {
                return $"Tr?c {xGrid}";
            }
            else if (!string.IsNullOrEmpty(yGrid))
            {
                return $"Tr?c {yGrid}";
            }

            return $"({center.X / 1000:0.0}, {center.Y / 1000:0.0})";
        }

        /// <summary>
        /// Tìm tr?c g?n nh?t
        /// </summary>
        private string FindNearestGrid(double coord, string axis)
        {
            var grids = _grids.Where(g =>
                g.Orientation.Equals(axis, StringComparison.OrdinalIgnoreCase))
                .OrderBy(g => Math.Abs(g.Coordinate - coord))
                .ToList();

            if (grids.Count == 0) return null;

            var nearest = grids.First();
            if (Math.Abs(nearest.Coordinate - coord) > 5000) // > 5m thì không match
                return null;

            return nearest.Name;
        }

        /// <summary>
        /// T?o polygon NTS t? danh sách ?i?m
        /// </summary>
        private Polygon CreateNtsPolygon(List<Point2D> pts)
        {
            if (pts == null || pts.Count < 3) return null;

            try
            {
                var coords = new List<Coordinate>();
                foreach (var p in pts)
                {
                    coords.Add(new Coordinate(p.X, p.Y));
                }

                // ?óng polygon
                if (!pts[0].Equals(pts.Last()))
                {
                    coords.Add(new Coordinate(pts[0].X, pts[0].Y));
                }

                var ring = _geometryFactory.CreateLinearRing(coords.ToArray());
                return _geometryFactory.CreatePolygon(ring);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// T?o di?n gi?i di?n tích
        /// </summary>
        private string FormatAreaExplanation(Geometry geom, int originalCount)
        {
            if (geom == null) return "N/A";

            // Ki?m tra n?u là rectangle
            if (geom is Polygon poly && IsApproximateRectangle(poly))
            {
                var env = poly.EnvelopeInternal;
                double w = env.Width * MM_TO_M;
                double h = env.Height * MM_TO_M;
                return $"{w:0.0} x {h:0.0}";
            }

            // Polygon ph?c t?p
            double areaM2 = geom.Area * MM2_TO_M2;
            if (originalCount > 1)
            {
                return $"Union({originalCount}) = {areaM2:0.00}m²";
            }

            return $"Poly = {areaM2:0.00}m²";
        }

        /// <summary>
        /// T?o di?n gi?i chi?u dài
        /// </summary>
        private string FormatLengthExplanation(List<double> lengths)
        {
            if (lengths.Count == 0) return "N/A";

            if (lengths.Count == 1)
            {
                return $"L = {lengths[0] * MM_TO_M:0.0}m";
            }

            // Hi?n th? t?i ?a 5 chi?u dài
            var display = lengths.Take(5).Select(l => $"{l * MM_TO_M:0.0}").ToList();
            string result = string.Join(" + ", display);

            if (lengths.Count > 5)
            {
                result += $" +...({lengths.Count - 5})";
            }

            return result;
        }

        /// <summary>
        /// Ki?m tra polygon có g?n nh? hình ch? nh?t không
        /// </summary>
        private bool IsApproximateRectangle(Polygon poly)
        {
            if (poly.NumPoints != 5) return false; // 4 ??nh + 1 ?i?m ?óng

            var env = poly.EnvelopeInternal;
            double envArea = env.Area;
            double polyArea = poly.Area;

            // N?u di?n tích g?n b?ng envelope -> là HCN
            return Math.Abs(envArea - polyArea) / envArea < 0.05; // 5% tolerance
        }

        /// <summary>
        /// L?y tên hi?n th? cho lo?i t?i
        /// </summary>
        private string GetLoadTypeDisplayName(string loadType)
        {
            switch (loadType)
            {
                case "AreaUniform": return "SÀN - UNIFORM LOAD (kN/m²)";
                case "AreaUniformToFrame": return "SÀN - UNIFORM TO FRAME (kN/m²)";
                case "FrameDistributed": return "D?M/T??NG - DISTRIBUTED (kN/m)";
                case "FramePoint": return "D?M - POINT LOAD (kN)";
                case "PointForce": return "?I?M - POINT FORCE (kN)";
                case "JointMass": return "KH?I L??NG - JOINT MASS";
                default: return loadType.ToUpper();
            }
        }

        #endregion

        #region Report Generation

        /// <summary>
        /// T?o báo cáo d?ng text
        /// </summary>
        public string GenerateTextReport(AuditReport report)
        {
            var sb = new StringBuilder();

            sb.AppendLine("===================================================================");
            sb.AppendLine("   BÁO CÁO KI?M TOÁN T?I TR?NG - DTS ENGINE");
            sb.AppendLine($"   Ngày: {report.AuditDate:dd/MM/yyyy HH:mm}");
            sb.AppendLine($"   Model: {report.ModelName}");
            sb.AppendLine($"   Load Case: {report.LoadPattern}");
            sb.AppendLine($"   ??n v?: {report.UnitInfo}");
            sb.AppendLine("===================================================================");
            sb.AppendLine();

            foreach (var story in report.Stories)
            {
                sb.AppendLine($"--- T?NG: {story.StoryName} (Z = {story.Elevation / 1000.0:0.0}m) ---");
                sb.AppendLine();

                foreach (var loadType in story.LoadTypeGroups)
                {
                    sb.AppendLine($"[{loadType.LoadTypeName}]");
                    sb.AppendLine();

                    foreach (var valGroup in loadType.ValueGroups)
                    {
                        sb.AppendLine($"    > Nhóm giá tr?: {valGroup.LoadValue:0.00} ({valGroup.Direction})");
                        sb.AppendLine(new string('-', 95));
                        sb.AppendLine(string.Format("    | {0,-22} | {1,-32} | {2,10} | {3,12} |",
                            "V? trí (Tr?c)", "Di?n gi?i", "SL/DT", "L?c (kN)"));
                        sb.AppendLine(new string('-', 95));

                        foreach (var entry in valGroup.Entries)
                        {
                            string loc = entry.GridLocation.Length > 22
                                ? entry.GridLocation.Substring(0, 19) + "..."
                                : entry.GridLocation;

                            string exp = entry.Explanation.Length > 32
                                ? entry.Explanation.Substring(0, 29) + "..."
                                : entry.Explanation;

                            sb.AppendLine(string.Format("    | {0,-22} | {1,-32} | {2,10:0.00} | {3,12:0.00} |",
                                loc, exp, entry.Quantity, entry.Force));
                        }

                        sb.AppendLine(new string('-', 95));
                        sb.AppendLine(string.Format("    | {0,-57} | {1,10:0.00} | {2,12:0.00} |",
                            $"T?NG NHÓM {valGroup.LoadValue:0.00}",
                            valGroup.TotalQuantity, valGroup.TotalForce));
                        sb.AppendLine(new string('-', 95));
                        sb.AppendLine();
                    }
                }

                sb.AppendLine($">>> T?NG T?NG {story.StoryName}: {story.SubTotalForce:n2} kN");
                sb.AppendLine();
            }

            sb.AppendLine("===================================================================");
            sb.AppendLine($"T?NG C?NG TÍNH TOÁN: {report.TotalCalculatedForce:n2} kN");

            if (Math.Abs(report.SapBaseReaction) > 0.01)
            {
                sb.AppendLine($"SAP2000 BASE REACTION (Z): {report.SapBaseReaction:n2} kN");
                sb.AppendLine($"SAI L?CH: {report.Difference:n2} kN ({report.DifferencePercent:0.00}%)");

                if (Math.Abs(report.DifferencePercent) < 1.0)
                {
                    sb.AppendLine(">>> KI?M TRA: OK (sai l?ch < 1%)");
                }
                else if (Math.Abs(report.DifferencePercent) < 5.0)
                {
                    sb.AppendLine(">>> KI?M TRA: CH?P NH?N (sai l?ch < 5%)");
                }
                else
                {
                    sb.AppendLine(">>> KI?M TRA: C?N XEM XÉT (sai l?ch > 5%)");
                }
            }
            else
            {
                sb.AppendLine("SAP2000 BASE REACTION: Ch?a có (model ch?a ch?y phân tích)");
            }

            sb.AppendLine("===================================================================");

            return sb.ToString();
        }

        /// <summary>
        /// T?o báo cáo chi ti?t bao g?m danh sách ph?n t?
        /// </summary>
        public string GenerateDetailedReport(AuditReport report)
        {
            var sb = new StringBuilder();

            sb.AppendLine(GenerateTextReport(report));
            sb.AppendLine();
            sb.AppendLine("=== CHI TI?T PH?N T? ===");
            sb.AppendLine();

            foreach (var story in report.Stories)
            {
                sb.AppendLine($"--- {story.StoryName} ---");

                foreach (var loadType in story.LoadTypeGroups)
                {
                    foreach (var valGroup in loadType.ValueGroups)
                    {
                        foreach (var entry in valGroup.Entries)
                        {
                            if (entry.ElementList.Count > 0)
                            {
                                sb.AppendLine($"  {entry.GridLocation}:");
                                sb.AppendLine($"    Ph?n t?: {string.Join(", ", entry.ElementList.Take(20))}");
                                if (entry.ElementList.Count > 20)
                                {
                                    sb.AppendLine($"    ... và {entry.ElementList.Count - 20} ph?n t? khác");
                                }
                            }
                        }
                    }
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        #endregion
    }
}
