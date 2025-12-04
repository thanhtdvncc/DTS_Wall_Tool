using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Utils
{
    /// <summary>
    /// Tiện ích chuẩn bị nội dung Label cho việc hiển thị. 
    /// LabelUtils là "quản lý" - chuẩn bị nội dung, màu sắc. 
    /// LabelPlotter là "công nhân" - vẽ theo yêu cầu.
    /// </summary>
    public static class LabelUtils
    {
        private const double TEXT_HEIGHT_MAIN = 120.0;
        private const double TEXT_HEIGHT_SUB = 100.0;
        private const string LABEL_LAYER = "dts_frame_label";

        #region Main API - Universal Element Label

        /// <summary>
        /// Làm mới nhãn cho bất kỳ phần tử nào (Wall, Column, Beam, Slab...)
        /// Chỉ xử lý các phần tử đã được đăng ký (có XData DTS_APP). Tránh vẽ nhãn cho đối tượng 'rác'.
        /// Trả về true nếu đã vẽ/refresh thành công; false nếu đối tượng không có XData hoặc không được hỗ trợ.
        /// </summary>
        /// <param name="entityId">ObjectId của entity</param>
        /// <param name="tr">Transaction đang hoạt động</param>
        /// <returns>true nếu cập nhật thành công, false nếu không thể vẽ</returns>
        public static bool RefreshEntityLabel(ObjectId entityId, Transaction tr)
        {
            // NOTE: Do NOT attempt to plot labels for non-DTS entities here. Selection commands must
            // filter out unregistered objects. This prevents accidental labelling of random geometry.

            Entity ent = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
            if (ent == null) return false;

            // Read ElementData - must exist to proceed
            var elementData = XDataUtils.ReadElementData(ent);
            if (elementData == null)
            {
                // No DTS data on this entity -> do not plot default labels to avoid noise
                return false;
            }

            // If ElementData exists but ElementType is Unknown, warn user and skip.
            // This prevents treating untyped objects as Wall (fallback) and encourages user to run DTS_SET_TYPE.
            if (elementData.ElementType == ElementType.Unknown)
            {
                try
                {
                    AcadUtils.Ed.WriteMessage($"\n[WARN] Đối tượng {entityId.Handle} có dữ liệu DTS nhưng không xác định loại. Vui lòng chạy DTS_SET_TYPE để phân loại.\n");
                }
                catch { }
                return false;
            }

            // Call type-specific label updater. We intentionally call updater even if HasValidData is false
            // because some operations (e.g. mapping results written by sync) may populate mapping lists
            // but not other properties; UpdateWallLabels can still render mapping information.
            switch (elementData.ElementType)
            {
                case ElementType.Wall:
                    var wallData = elementData as WallData;
                    if (wallData != null)
                    {
                        var mapResult = CreateMappingResultFromWallData(wallData, entityId);
                        UpdateWallLabels(entityId, wallData, mapResult, tr);
                        return true;
                    }
                    break;

                case ElementType.Column:
                    var columnData = elementData as ColumnData;
                    if (columnData != null)
                    {
                        UpdateColumnLabels(entityId, columnData, tr);
                        return true;
                    }
                    break;

                case ElementType.Beam:
                    var beamData = elementData as BeamData;
                    if (beamData != null)
                    {
                        UpdateBeamLabels(entityId, beamData, tr);
                        return true;
                    }
                    break;

                case ElementType.Slab:
                    var slabData = elementData as SlabData;
                    if (slabData != null)
                    {
                        UpdateSlabLabels(entityId, slabData, tr);
                        return true;
                    }
                    break;

                case ElementType.Foundation:
                    var foundationData = elementData as FoundationData;
                    if (foundationData != null)
                    {
                        UpdateGenericLabel(entityId, ent, "Móng", foundationData.FoundationType ?? "Foundation", tr);
                        return true;
                    }
                    break;

                case ElementType.ShearWall:
                    var shearWallData = elementData as ShearWallData;
                    if (shearWallData != null)
                    {
                        string wallType = shearWallData.WallType ?? $"SW{shearWallData.Thickness ?? 0:0}";
                        UpdateGenericLabel(entityId, ent, "Vách", wallType, tr);
                        return true;
                    }
                    break;

                case ElementType.Stair:
                    var stairData = elementData as StairData;
                    if (stairData != null)
                    {
                        string stairInfo = stairData.NumberOfSteps.HasValue ? $"{stairData.StairType} ({stairData.NumberOfSteps}b)" : stairData.StairType;
                        UpdateGenericLabel(entityId, ent, "Cầu thang", stairInfo, tr);
                        return true;
                    }
                    break;

                case ElementType.Pile:
                    var pileData = elementData as PileData;
                    if (pileData != null)
                    {
                        string pileType = pileData.PileType ?? $"D{pileData.Diameter ?? 0:0}";
                        UpdateGenericLabel(entityId, ent, "Cọc", pileType, tr);
                        return true;
                    }
                    break;

                case ElementType.Lintel:
                    var lintelData = elementData as LintelData;
                    if (lintelData != null)
                    {
                        string lintelType = lintelData.LintelType ?? $"L{lintelData.Width ?? 0:0}x{lintelData.Height ?? 0:0}";
                        UpdateGenericLabel(entityId, ent, "Lanh tô", lintelType, tr);
                        return true;
                    }
                    break;

                case ElementType.Rebar:
                    var rebarData = elementData as RebarData;
                    if (rebarData != null)
                    {
                        string rebarMark = rebarData.RebarMark ?? $"D{rebarData.Diameter ?? 0:0}";
                        string qtyStr = rebarData.Quantity.HasValue ? $"x{rebarData.Quantity.Value}" : "";
                        UpdateGenericLabel(entityId, ent, "Cốt thép", $"{rebarMark}{qtyStr}", tr);
                        return true;
                    }
                    break;

                default:
                    // Unsupported DTS type - do not plot
                    return false;
            }

            return false;
        }

        /// <summary>
        /// Create a minimal ElementData instance based on entity layer/type (does not write XData)
        /// </summary>
        private static ElementData CreateElementDataFromEntity(Entity ent)
        {
            string layer = (ent.Layer ?? string.Empty).ToUpperInvariant();

            if (layer.Contains("WALL") || layer.Contains("TUONG"))
                return new WallData();
            if (layer.Contains("COL") || layer.Contains("COT"))
                return new ColumnData();
            if (layer.Contains("BEAM") || layer.Contains("DAM"))
                return new BeamData();
            if (layer.Contains("SLAB") || layer.Contains("SAN"))
                return new SlabData();

            if (ent is Line)
                return new WallData();

            return null;
        }

        /// <summary>
        /// Tạo MappingResult từ WallData có sẵn
        /// </summary>
        private static MappingResult CreateMappingResultFromWallData(WallData wData, ObjectId wallId)
        {
            var mapResult = new MappingResult
            {
                WallHandle = wallId.Handle.ToString(),
                Mappings = wData.Mappings ?? new List<MappingRecord>()
            };

            if (mapResult.Mappings.Count > 0)
            {
                double totalCovered = mapResult.Mappings
                    .Where(m => m.TargetFrame != "New")
                    .Sum(m => m.CoveredLength);

                mapResult.WallLength = totalCovered > 0 ? totalCovered : 1000;
            }

            return mapResult;
        }

        #endregion

        #region Default Label Plotting

        private static void PlotDefaultLabel(ObjectId entityId, Entity ent, string xType, Transaction tr)
        {
            // Prepare simple content: [Handle] Type
            string handleText = $"[{entityId.Handle}]";
            string typeText = string.IsNullOrEmpty(xType) ? "Unknown" : xType;

            string content = FormatColor(handleText, 3) + " " + FormatColor(typeText, 7);

            // Ensure layer exists
            AcadUtils.CreateLayer(LABEL_LAYER, LabelPlotter.DEFAULT_COLOR);

            // Choose plotting method based on entity geometry
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            if (ent is Line line)
            {
                // Use 3D overload to keep Z if any
                LabelPlotter.PlotLabel(btr, tr, line.StartPoint, line.EndPoint, content, LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);
            }
            else if (ent is Circle circle)
            {
                var center = new Point2D(circle.Center.X, circle.Center.Y);
                LabelPlotter.PlotPointLabel(btr, tr, center, content, TEXT_HEIGHT_MAIN, LABEL_LAYER);
            }
            else if (ent is Polyline pline)
            {
                // for polyline, show at centroid (approx)
                var ext = pline.GeometricExtents;
                var center = new Point2D((ext.MinPoint.X + ext.MaxPoint.X) / 2.0, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0);
                LabelPlotter.PlotPointLabel(btr, tr, center, content, TEXT_HEIGHT_MAIN, LABEL_LAYER);
            }
            else
            {
                // fallback: entity center
                var centerPt = AcadUtils.GetEntityCenter(ent);
                LabelPlotter.PlotPointLabel(btr, tr, centerPt, content, TEXT_HEIGHT_MAIN, LABEL_LAYER);
            }
        }

        #endregion

        #region Wall Labels

        /// <summary>
        /// Cập nhật nhãn cho Tường sau khi Sync/Mapping
        /// Format:
        ///   Dòng trên: [Handle] W200 DL=7.20 kN/m
        ///   Dòng dưới: to B15 I=0.0to3.5 hoặc to B15 (full 9m)
        /// </summary>
        public static void UpdateWallLabels(ObjectId wallId, WallData wData, MappingResult mapResult, Transaction tr)
        {
            Entity ent = tr.GetObject(wallId, OpenMode.ForRead) as Entity;
            if (ent == null) return;

            Autodesk.AutoCAD.Geometry.Point3d pStart, pEnd;
            if (ent is Line line)
            {
                pStart = line.StartPoint;
                pEnd = line.EndPoint;
            }
            else return;

            // Xác định màu theo trạng thái
            int statusColor = mapResult.GetColorIndex();

            // === DÒNG TRÊN: [Handle] W200 DL=7.20 kN/m ===
            string handleText = FormatColor($"[{wallId.Handle}]", statusColor);
            string wallType = wData.WallType ?? $"W{wData.Thickness ?? 200:0}";
            string loadPattern = wData.LoadPattern ?? "DL";
            double loadValue = wData.LoadValue ?? 0;
            string loadText = $"{wallType} {loadPattern}={loadValue:0.00} kN/m";

            string topContent = $"{handleText} {{\\C7;{loadText}}}";

            // === DÒNG DƯỚI: to B15 I=0.0to3.5 + [SAP:...] ===
            string botContent = GetMappingText(mapResult, wData.LoadPattern ?? "DL");

            // NEW: DÒNG THỨ 3 (tùy chọn): Hiển thị các loadcase khác nếu có
            string thirdContent = null;
            if (wData.LoadCases != null && wData.LoadCases.Count > 1)
            {
                // Lọc bỏ loadcase mặc định (đã hiển thị ở dòng trên)
                var otherLoadCases = wData.LoadCases.Where(kvp => kvp.Key != wData.LoadPattern).ToList();
                if (otherLoadCases.Count > 0)
                {
                    // Hiển thị tối đa 3 loadcase khác
                    var displayCases = otherLoadCases.Take(3)
                        .Select(kvp => $"{kvp.Key}={kvp.Value:0.00}");

                    string moreText = otherLoadCases.Count > 3 ? $" +{otherLoadCases.Count - 3}" : "";
                    thirdContent = FormatColor($"[{string.Join(", ", displayCases)}{moreText}]", 8); // Gray
                }
            }

            // Lấy BlockTableRecord để vẽ
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
                ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            // Vẽ labels
            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, topContent,
                LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);

            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, botContent,
                LabelPosition.MiddleBottom, TEXT_HEIGHT_SUB, LABEL_LAYER);

            // NEW: Vẽ dòng thứ 3 nếu có nhiều loadcase
            if (!string.IsNullOrEmpty(thirdContent))
            {
                // Tính offset xuống thêm một chút so với dòng 2
                var midPt = new Point2D((pStart.X + pEnd.X) / 2.0, (pStart.Y + pEnd.Y) / 2.0);
                var perpDir = new Point2D(-(pEnd.Y - pStart.Y), pEnd.X - pStart.X);
                perpDir = perpDir.Normalized;

                // Offset xuống dưới thêm 150mm so với dòng 2
                var thirdPos = new Point2D(
                    midPt.X + perpDir.X * (TEXT_HEIGHT_SUB + 150),
                    midPt.Y + perpDir.Y * (TEXT_HEIGHT_SUB + 150)
                );

                LabelPlotter.PlotPointLabel(btr, tr, thirdPos, thirdContent, TEXT_HEIGHT_SUB * 0.8, LABEL_LAYER);
            }
        }

        #endregion

        #region Column Labels

        /// <summary>
        /// Cập nhật nhãn cho Cột
        /// Format: [Handle] C400x400
        /// </summary>
        public static void UpdateColumnLabels(ObjectId columnId, ColumnData cData, Transaction tr)
        {
            Entity ent = tr.GetObject(columnId, OpenMode.ForRead) as Entity;
            if (ent == null) return;

            Point2D center2;
            Point3d center3 = new Point3d(0, 0, 0);
            if (ent is Circle circle)
            {
                center3 = circle.Center;
                center2 = new Point2D(center3.X, center3.Y);
            }
            else if (ent is DBPoint point)
            {
                center3 = point.Position;
                center2 = new Point2D(center3.X, center3.Y);
            }
            else return;

            // Xác định màu theo trạng thái mapping
            int statusColor = cData.HasMapping ? 3 : 1; // Green if mapped, red if not

            string handleText = FormatColor($"[{columnId.Handle}]", statusColor);
            string columnType = cData.ColumnType ?? $"C{cData.Width ?? 400:0}x{cData.Depth ?? 400:0}";
            string content = $"{handleText} {{\\C7;{columnType}}}";

            // Lấy BlockTableRecord để vẽ
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
                 ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            // Use PointLabel (2D) but ensure Z is considered for plotting elsewhere if needed
            LabelPlotter.PlotPointLabel(btr, tr, center2, content, TEXT_HEIGHT_MAIN, LABEL_LAYER);
        }

        #endregion

        #region Beam Labels

        /// <summary>
        /// Cập nhật nhãn cho Dầm
        /// Format: [Handle] B300x500
        /// </summary>
        public static void UpdateBeamLabels(ObjectId beamId, BeamData bData, Transaction tr)
        {
            Entity ent = tr.GetObject(beamId, OpenMode.ForRead) as Entity;
            if (ent == null) return;

            Point3d pStart, pEnd;
            if (ent is Line line)
            {
                pStart = line.StartPoint;
                pEnd = line.EndPoint;
            }
            else return;

            // Xác định màu theo trạng thái
            int statusColor = bData.HasMapping ? 3 : 1;

            string handleText = FormatColor($"[{beamId.Handle}]", statusColor);

            // Sử dụng SectionName hoặc tính từ Width x Depth
            string beamType;
            if (!string.IsNullOrEmpty(bData.SectionName))
            {
                beamType = bData.SectionName;
            }
            else if (bData.Width.HasValue && bData.Depth.HasValue)
            {
                beamType = $"B{bData.Width:0}x{bData.Depth:0}";
            }
            else
            {
                beamType = "Beam";
            }

            string content = $"{handleText} {{\\C7;{beamType}}}";

            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
                 ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            // Use 3D overload
            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, content,
                     LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);
        }

        #endregion

        #region Slab Labels

        /// <summary>
        /// Cập nhật nhãn cho Sàn
        /// Format: [Handle] Slab T=120mm
        /// </summary>
        public static void UpdateSlabLabels(ObjectId slabId, SlabData sData, Transaction tr)
        {
            Entity ent = tr.GetObject(slabId, OpenMode.ForRead) as Entity;
            if (ent == null) return;

            Point2D center;
            if (ent is Polyline pline && pline.Closed)
            {
                // Tính trung điểm của polyline
                double sumX = 0, sumY = 0;
                int count = pline.NumberOfVertices;
                for (int i = 0; i < count; i++)
                {
                    var pt = pline.GetPoint2dAt(i);
                    sumX += pt.X;
                    sumY += pt.Y;
                }
                center = new Point2D(sumX / count, sumY / count);
            }
            else return;

            // Xác định màu
            int statusColor = sData.HasMapping ? 3 : 1;

            string handleText = FormatColor($"[{slabId.Handle}]", statusColor);
            string slabType = sData.SlabType ?? $"Slab T={sData.Thickness ?? 120:0}mm";
            string content = $"{handleText} {{\\C7;{slabType}}}";

            // Lấy BlockTableRecord để vẽ
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
          ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            // Vẽ label
            LabelPlotter.PlotPointLabel(btr, tr, center, content, TEXT_HEIGHT_MAIN, LABEL_LAYER);
        }

        #endregion

        #region Generic Element Labels

        /// <summary>
        /// Cập nhật nhãn cho các loại phần tử generic (Foundation, Pile, Lintel, Rebar, ShearWall, Stair)
        /// Format: [Handle] TypeName TypeDetail
        /// </summary>
        private static void UpdateGenericLabel(ObjectId elemId, Entity ent, string typeName, string typeDetail, Transaction tr)
        {
            Point2D center;

            // Xác định tâm theo loại entity
            if (ent is Line line)
            {
                // Cho Line - hiển thị ở giữa line
                var pStart = new Point2D(line.StartPoint.X, line.StartPoint.Y);
                var pEnd = new Point2D(line.EndPoint.X, line.EndPoint.Y);
                center = new Point2D((pStart.X + pEnd.X) / 2.0, (pStart.Y + pEnd.Y) / 2.0);
            }
            else if (ent is Circle circle)
            {
                center = new Point2D(circle.Center.X, circle.Center.Y);
            }
            else if (ent is Polyline pline)
            {
                // Tính trung điểm của polyline
                double sumX = 0, sumY = 0;
                int count = pline.NumberOfVertices;
                for (int i = 0; i < count; i++)
                {
                    var pt = pline.GetPoint2dAt(i);
                    sumX += pt.X;
                    sumY += pt.Y;
                }
                center = new Point2D(sumX / count, sumY / count);
            }
            else
            {
                center = AcadUtils.GetEntityCenter(ent);
            }

            // Xác định màu (green nếu có mapping, red nếu không)
            var elemData = XDataUtils.ReadElementData(ent);
            int statusColor = (elemData != null && elemData.HasMapping) ? 3 : 1;

            string handleText = FormatColor($"[{elemId.Handle}]", statusColor);
            string content = $"{handleText} {{\\C7;{typeName} {typeDetail}}}";

            // Lấy BlockTableRecord để vẽ
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
         ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            // Vẽ label tại vị trí tâm
            if (ent is Line)
            {
                {
                    // For lines, use PlotLabel for better positioning
                    var pStart = new Point2D(((Line)ent).StartPoint.X, ((Line)ent).StartPoint.Y);
                    var pEnd = new Point2D(((Line)ent).EndPoint.X, ((Line)ent).EndPoint.Y);
                    LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, content,
                 LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);
                }
            }
            else
            {
                LabelPlotter.PlotPointLabel(btr, tr, center, content, TEXT_HEIGHT_MAIN, LABEL_LAYER);
            }
        }

        #endregion

        #region Content Formatting

        /// <summary>
        /// Format chuỗi với màu MText
        /// </summary>
        public static string FormatColor(string text, int colorIndex)
        {
            return $"{{\\C{colorIndex};{text}}}";
        }

        /// <summary>
        /// Tạo nội dung text cho phần mapping
        /// Bao gồm cả thông tin tải từ SAP nếu có
        /// </summary>
        public static string GetMappingText(MappingResult res, string loadPattern = "DL")
        {
            if (!res.HasMapping)
                return FormatColor("to New", 1);

            var map = res.Mappings.First();
            if (map.TargetFrame == "New")
                return FormatColor("to New", 1);

            // Read detailed loads from SAP for this frame
            var detailed = SapUtils.GetFrameDistributedLoadsDetailed(map.TargetFrame);

            var lines = new List<string>();

            bool hasAnyLoad = detailed != null && detailed.Count > 0 && detailed.Values.Any(v => v != null && v.Count > 0);
            if (!hasAnyLoad)
            {
                // No loads -> show mapping header only
                string header = $"to {map.TargetFrame}";
                if (map.MatchType == "FULL" || map.MatchType == "EXACT")
                    header += $" (full {map.CoveredLength / 1000.0:0.#}m)";
                else
                    header += $" I={map.DistI / 1000.0:0.0}to{map.DistJ / 1000.0:0.0}";
                lines.Add(header);
            }
            else
            {
                // Has loads -> show only load lines (no redundant 'to ...')
                int maxPatterns = 5;
                int count = 0;
                foreach (var kv in detailed)
                {
                    if (count++ >= maxPatterns) { lines.Add("+more"); break; }

                    string pattern = kv.Key;
                    var entries = kv.Value;
                    double total = entries.Sum(e => e.Value);

                    // Merge segments text
                    var segs = new List<string>();
                    foreach (var e in entries)
                    {
                        foreach (var s in e.Segments)
                        {
                            double i = s.I / 1000.0;
                            double j = s.J / 1000.0;
                            if (Math.Abs(i - 0) < 0.001 && Math.Abs(j - (map.FrameLength / 1000.0)) < 0.001)
                                segs.Add($"full {map.FrameLength / 1000.0:0.#}m");
                            else
                                segs.Add($"{i:0.0}to{j:0.0}");
                        }
                    }

                    string segText = segs.Count > 0 ? $" ({string.Join(",", segs)})" : "";
                    lines.Add($"{map.TargetFrame}: {pattern}={total:0.00}kN/m{segText}");
                }
            }

            int color = res.GetColorIndex();
            string content = string.Join("\\P", lines.Select(l => FormatColor(l, color)));
            return content;
        }

        #endregion
    }
}