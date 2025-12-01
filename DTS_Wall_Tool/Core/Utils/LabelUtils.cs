using Autodesk.AutoCAD.DatabaseServices;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Utils
{
    public static class LabelUtils
    {
        private const double TEXT_HEIGHT_MAIN = 150.0;
        private const double TEXT_HEIGHT_SUB = 120.0;
        private const string LABEL_LAYER = "dts_frame_label";



        /// <summary>
        /// API TỔNG QUÁT: Dispatcher
        /// Kiểm tra Type trước, sau đó gọi hàm vẽ cụ thể.
        /// </summary>
        public static bool RefreshEntityLabel(ObjectId objId, Transaction tr)
        {
            Entity ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
            if (ent == null) return false;

            // 1. Kiểm tra nhanh loại đối tượng trong XData
            ElementType type = XDataUtils.GetElementType(ent);

            // 2. Switch case để xử lý
            switch (type)
            {
                case ElementType.Wall:
                    return ProcessWallLabel(ent, objId, tr);

                case ElementType.Column:
                    // return ProcessColumnLabel(ent, objId, tr); // Mở rộng sau này
                    return false;

                case ElementType.Beam:
                    // return ProcessBeamLabel(ent, objId, tr); // Mở rộng sau này
                    return false;

                default:
                    // Nếu không có XData hợp lệ -> Không vẽ gì cả (hoặc xóa nhãn cũ nếu cần)
                    return false;
            }
        }


        /// <summary>
        /// Logic riêng cho Tường
        /// </summary>
        private static bool ProcessWallLabel(Entity ent, ObjectId objId, Transaction tr)
        {
            // Kiểm tra tính toàn vẹn hình học: Tường phải là LINE
            if (!(ent is Line line)) return false;

            // Đọc dữ liệu
            WallData wData = XDataUtils.ReadWallData(ent);

            // Validate dữ liệu
            if (wData == null) return false;

            // Chỉ vẽ nếu có dữ liệu thực sự hoặc đã mapping
            if (!wData.HasValidData() && wData.Mappings.Count == 0) return false;

            // Chuẩn bị dữ liệu hiển thị
            MappingResult savedResult = new MappingResult
            {
                WallHandle = objId.Handle.ToString(),
                WallLength = line.Length,
                Mappings = wData.Mappings ?? new System.Collections.Generic.List<MappingRecord>()
            };

            // Vẽ
            UpdateWallLabels(objId, wData, savedResult, tr);

            // Cập nhật màu sắc Line theo trạng thái
            if (ent.IsWriteEnabled == false) ent.UpgradeOpen();
            ent.ColorIndex = savedResult.GetColorIndex();

            return true;
        }

        public static void UpdateWallLabels(ObjectId wallId, WallData wData, MappingResult mapResult, Transaction tr)
        {
            Entity ent = tr.GetObject(wallId, OpenMode.ForRead) as Entity;
            if (!(ent is Line line)) return;

            Point2D pStart = new Point2D(line.StartPoint.X, line.StartPoint.Y);
            Point2D pEnd = new Point2D(line.EndPoint.X, line.EndPoint.Y);

            int statusColor = mapResult.GetColorIndex();

            // --- INFO TEXT (Trên) ---
            string topContent;
            string handleText = FormatColor($"[{wallId.Handle}]", statusColor);

            if (wData.Thickness.HasValue || wData.LoadValue.HasValue)
            {
                string wallType = wData.WallType ?? $"W{wData.Thickness ?? 0}";
                // Thêm check null cho LoadValue để hiển thị đẹp hơn
                string loadVal = wData.LoadValue.HasValue ? wData.LoadValue.Value.ToString("0.00") : "0";
                string loadInfo = $"{wallType} {wData.LoadPattern ?? "DL"}={loadVal}";
                topContent = $"{handleText} {FormatColor(loadInfo, 7)}"; // Màu trắng/đen cho thông tin
            }
            else
            {
                topContent = handleText;
            }

            // --- MAPPING TEXT (Dưới) ---
            string botContent = GetMappingText(mapResult);

            // --- VẼ ---
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            // Xóa nhãn cũ (nếu cần logic này phức tạp hơn thì dùng Dictionary ExtensionDictionary để lưu Handle của Label, nhưng tạm thời vẽ đè hoặc xóa lớp cũ như lệnh DTS_CLEANUP là ổn)

            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, topContent, LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);
            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, botContent, LabelPosition.MiddleBottom, TEXT_HEIGHT_SUB, LABEL_LAYER);
        }

        public static string FormatColor(string text, int colorIndex)
        {
            return $"{{\\C{colorIndex};{text}}}";
        }

        public static string GetMappingText(MappingResult res)
        {
            if (!res.HasMapping) return FormatColor("to New", 1); // Đỏ

            var parts = new List<string>();
            foreach (var map in res.Mappings)
            {
                if (map.TargetFrame == "New") continue;

                string part = map.TargetFrame;

                // Nếu Full Dầm -> Chỉ hiện tên dầm (gọn gàng)
                if (map.MatchType == "FULL" || map.MatchType == "EXACT")
                {
                    // Giữ nguyên tên (VD: "B12")
                }
                else
                {
                    // Nếu Partial -> Hiện tọa độ mét
                    // Format: B12(0-3.5)
                    double i = map.DistI / 1000.0;
                    double j = map.DistJ / 1000.0;

                    // Format số: bỏ số 0 vô nghĩa (5.0 -> 5)
                    string sI = i.ToString("0.##");
                    string sJ = j.ToString("0.##");

                    part += $"({sI}-{sJ})";
                }
                parts.Add(part);
            }

            if (parts.Count == 0) return FormatColor("to New", 1);

            // Nối chuỗi: "to 122(5-10), 125"
            string resultText = "to " + string.Join(", ", parts);

            // --- [SỬA ĐỔI] ---
            // Không cộng thêm chữ "(full)" nữa để tránh hiểu nhầm.
            // Màu sắc Xanh/Vàng đã đủ để biểu thị trạng thái Full/Partial Tường.

            return FormatColor(resultText, res.GetColorIndex());
        }
    }
}