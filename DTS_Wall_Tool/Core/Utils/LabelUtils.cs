using Autodesk.AutoCAD.DatabaseServices;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Primitives;
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
                var pStart = new Point2D(line.StartPoint.X, line.StartPoint.Y);
                var pEnd = new Point2D(line.EndPoint.X, line.EndPoint.Y);
                LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, content, LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);
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

            Point2D pStart, pEnd;
            if (ent is Line line)
            {
                pStart = new Point2D(line.StartPoint.X, line.StartPoint.Y);
                pEnd = new Point2D(line.EndPoint.X, line.EndPoint.Y);
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

            // === DÒNG DƯỚI: to B15 I=0.0to3.5 ===
            string botContent = GetMappingText(mapResult, wData.LoadPattern ?? "DL");

            // Lấy BlockTableRecord để vẽ
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
           ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            // Vẽ labels
            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, topContent,
           LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);

            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, botContent,
                            LabelPosition.MiddleBottom, TEXT_HEIGHT_SUB, LABEL_LAYER);
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

            Point2D center;
            if (ent is Circle circle)
            {
                center = new Point2D(circle.Center.X, circle.Center.Y);
            }
            else if (ent is DBPoint point)
            {
                center = new Point2D(point.Position.X, point.Position.Y);
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

            // Vẽ label tại vị trí cột (point label)
            LabelPlotter.PlotPointLabel(btr, tr, center, content, TEXT_HEIGHT_MAIN, LABEL_LAYER);
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

            Point2D pStart, pEnd;
            if (ent is Line line)
            {
                pStart = new Point2D(line.StartPoint.X, line.StartPoint.Y);
                pEnd = new Point2D(line.EndPoint.X, line.EndPoint.Y);
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

            // Lấy BlockTableRecord để vẽ
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
                 ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            // Vẽ label
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
            // Nếu chưa map
            if (!res.HasMapping)
                return FormatColor("to New", 1);

            // Nếu map nhiều dầm
            if (res.Mappings.Count > 1)
            {
                var names = res.Mappings.Select(m => m.TargetFrame).Distinct();
                return FormatColor("to " + string.Join(",", names), 3);
            }

            // Map 1 dầm
            var map = res.Mappings[0];
            if (map.TargetFrame == "New")
                return FormatColor("to New", 1);

            string result = $"to {map.TargetFrame}";

            if (map.MatchType == "FULL" || map.MatchType == "EXACT")
            {
                result += $" (full {map.CoveredLength / 1000.0:0.#}m)";
            }
            else
            {
                double i = map.DistI / 1000.0;
                double j = map.DistJ / 1000.0;
                result += $" I={i:0. 0}to{j:0.0}";
            }

            // Thêm thông tin tải từ SAP nếu có
            if (SapUtils.IsConnected && map.TargetFrame != "New")
            {
                var sapLoads = SapUtils.GetFrameDistributedLoads(map.TargetFrame, loadPattern);
                if (sapLoads.Count > 0)
                {
                    double sapLoad = sapLoads.Sum(l => l.LoadValue);
                    result += $" [SAP:{sapLoad:0. 00}]";
                }
            }

            int color = res.GetColorIndex();
            return FormatColor(result, color);
        }

        /// <summary>
        /// Tạo label text với thông tin đầy đủ từ SAP
        /// </summary>
        public static string GetDetailedLabel(WallData wData, MappingResult mapResult)
        {
            var lines = new List<string>();

            // Line 1: Wall info
            string wallType = wData.WallType ?? $"W{wData.Thickness ?? 200:0}";
            lines.Add($"{wallType} T={wData.Thickness:0}mm H={wData.Height:0}mm");

            // Line 2: Load info
            if (wData.LoadValue.HasValue)
            {
                lines.Add($"{wData.LoadPattern}={wData.LoadValue:0.00} kN/m");
            }

            // Line 3: Mapping info
            if (mapResult.HasMapping)
            {
                var map = mapResult.Mappings.First();
                lines.Add($"-> {map.TargetFrame} ({map.MatchType})");
            }
            else
            {
                lines.Add("-> NEW");
            }

            return string.Join("\\P", lines); // \P = newline in MText
        }

        #endregion

        #region Sync Status Labels

        /// <summary>
        /// Tạo label hiển thị trạng thái đồng bộ
        /// </summary>
        public static string GetSyncStatusLabel(SyncState state, string details = null)
        {
            string statusText;
            int color;

            switch (state)
            {
                case SyncState.Synced:
                    statusText = "Synced";
                    color = 3;
                    break;
                case SyncState.CadModified:
                    statusText = "CAD Changed";
                    color = 2;
                    break;
                case SyncState.SapModified:
                    statusText = "SAP Changed";
                    color = 5;
                    break;
                case SyncState.Conflict:
                    statusText = "Conflict";
                    color = 6;
                    break;
                case SyncState.SapDeleted:
                    statusText = "SAP Deleted";
                    color = 1;
                    break;
                case SyncState.NewElement:
                    statusText = "New";
                    color = 4;
                    break;
                default:
                    statusText = "?Unknown";
                    color = 7;
                    break;
            }

            string result = FormatColor(statusText, color);
            if (!string.IsNullOrEmpty(details))
            {
                result += $" {details}";
            }

            return result;
        }

        #endregion
    }
}