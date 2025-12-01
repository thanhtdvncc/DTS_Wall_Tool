using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Colors;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Primitives;

namespace DTS_Wall_Tool.Core.Utils
{
    public static class LabelUtils
    {
        // Cấu hình hiển thị
        private const double TEXT_HEIGHT_MAIN = 150.0;
        private const double TEXT_HEIGHT_SUB = 120.0;

        /// <summary>
        /// Cập nhật nhãn cho Tường sau khi Sync/Mapping
        /// </summary>
        public static void UpdateWallLabels(ObjectId wallId, WallData wData, MappingResult mapResult, Transaction tr)
        {
            // 1. Chuẩn bị dữ liệu hình học
            Entity ent = tr.GetObject(wallId, OpenMode.ForRead) as Entity;
            if (ent == null) return;

            Point2D pStart, pEnd;
            if (ent is Line line)
            {
                pStart = new Point2D(line.StartPoint.X, line.StartPoint.Y);
                pEnd = new Point2D(line.EndPoint.X, line.EndPoint.Y);
            }
            else return;

            // 2. Chuẩn bị nội dung Text
            // Màu sắc cho Handle: Xanh (3) nếu map OK, Đỏ (1) nếu lỗi
            int statusColor = mapResult.HasMapping ? 3 : 1;

            // --- Dòng trên (Middle Top): Thông tin Tường ---
            // Format: [\C3HANDLE\C7] W200 DL=10.5
            string handleText = FormatColor($"[{wallId.Handle}]", statusColor);
            string infoText = $"{wData.WallType} {wData.LoadPattern}={wData.LoadValue:0.##}";
            string topContent = $"{handleText} {{\\C7{infoText}}}"; // Màu 7 (Trắng/Đen) cho phần info

            // --- Dòng dưới (Middle Bottom): Thông tin Mapping ---
            // Format: to B15 I=0.0to3.5
            string botContent = GetMappingText(mapResult);

            // 3. Vẽ (Gọi LabelPlotter)
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            // Vẽ dòng trên
            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, topContent, LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN);

            // Vẽ dòng dưới (Nhỏ hơn một chút cho đẹp)
            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, botContent, LabelPosition.MiddleBottom, TEXT_HEIGHT_SUB);
        }

        // --- Helpers ---

        private static string FormatColor(string text, int colorIndex)
        {
            return $"{{\\C{colorIndex};{text}}}";
        }

        private static string GetMappingText(MappingResult res)
        {
            // Nếu chưa map
            if (!res.HasMapping) return FormatColor("to New", 1); // Màu đỏ

            // Nếu map nhiều dầm
            if (res.Mappings.Count > 1)
            {
                var names = res.Mappings.Select(m => m.TargetFrame).Distinct();
                return FormatColor("to " + string.Join(",", names), 2); // Màu vàng
            }

            // Map 1 dầm
            var map = res.Mappings[0];
            if (map.TargetFrame == "New") return FormatColor("to New", 1);

            string result = $"to {map.TargetFrame}";

            if (map.MatchType == "FULL" || map.MatchType == "EXACT")
            {
                result += $" (full {map.CoveredLength / 1000.0:0.#}m)";
            }
            else
            {
                double i = map.DistI / 1000.0;
                double j = map.DistJ / 1000.0;
                result += $" I={i:0.0}to{j:0.0}";
            }

            return FormatColor(result, 2); // Màu vàng
        }

        public static void SetEntityColor(ObjectId id, int colorIndex, Transaction tr)
        {
            Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
            if (ent != null) ent.ColorIndex = colorIndex;
        }
    }
}