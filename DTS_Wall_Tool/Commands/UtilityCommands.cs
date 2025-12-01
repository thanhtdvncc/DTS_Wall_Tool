using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Utils;

namespace DTS_Wall_Tool.Commands
{
    /// <summary>
    /// Các lệnh tiện ích
    /// </summary>
    public class UtilityCommands : CommandBase
    {
        /// <summary>
        /// Lệnh test cơ bản
        /// </summary>
        [CommandMethod("DTS_HELP")]
        public void DTS_HELP()
        {
            WriteMessage("╔══════════════════════════════════════════════════════════════╗");
            WriteMessage("║           DTS TOOL - DANH SÁCH LỆNH                          ║");
            WriteMessage("╠══════════════════════════════════════════════════════════════╣");
            WriteMessage("║ THIẾT LẬP:                                                   ║");
            WriteMessage("║   DTS_SET_ORIGIN  - Thiết lập Origin cho tầng                ║");
            WriteMessage("║   DTS_LINK      - Liên kết phần tử với Origin                ║");
            WriteMessage("║   DTS_UNLINK - Xóa liên kết phần tử                          ║");
            WriteMessage("║   DTS_SHOW_LINK   - Hiển thị thông tin liên kết              ║");
            WriteMessage("╠══════════════════════════════════════════════════════════════╣");
            WriteMessage("║ SAP2000:                                                     ║");
            WriteMessage("║   DTS_TEST_SAP - Kiểm tra kết nối SAP2000                    ║");
            WriteMessage("║   DTS_GET_FRAMES  - Lấy danh sách frames từ SAP              ║");
            WriteMessage("║   DTS_SYNC_SAP    - Đồng bộ SAP → CAD (PULL)                 ║");
            WriteMessage("║   DTS_PUSH_LOAD   - Gán tải CAD → SAP (PUSH)                 ║");
            WriteMessage("║   DTS_CHECK_SYNC  - Kiểm tra trạng thái đồng bộ              ║");
            WriteMessage("╠══════════════════════════════════════════════════════════════╣");
            WriteMessage("║ TÍNH TOÁN:                                                   ║");
            WriteMessage("║   DTS_CALC_LOAD   - Tính tải trọng tường                     ║");
            WriteMessage("║   DTS_SCAN_WALL   - Quét và nhận diện tường                  ║");
            WriteMessage("╠══════════════════════════════════════════════════════════════╣");
            WriteMessage("║ HIỂN THỊ & DỌN DẸP:                                          ║");
            WriteMessage("║   DTS_SHOW_LABEL  - Hiển thị/Cập nhật nhãn phần tử           ║");
            WriteMessage("║   DTS_CLEAR_LABEL - Xóa tất cả nhãn                          ║");
            WriteMessage("║   DTS_CLEANUP     - Dọn dẹp tất cả layer tạm                 ║");
            WriteMessage("╠══════════════════════════════════════════════════════════════╣");
            WriteMessage("║ MÀU SẮC TRẠNG THÁI:                                          ║");
            WriteMessage("║   Xanh lá (3)  - Đã đồng bộ / Full match                     ║");
            WriteMessage("║   Vàng (2)     - CAD thay đổi / Partial match                ║");
            WriteMessage("║   Xanh dương(5)- SAP thay đổi                                ║");
            WriteMessage("║   Đỏ (1)       - Không map / SAP đã xóa                      ║");
            WriteMessage("║ Magenta (6)  - Xung đột                                      ║");
            WriteMessage("║   Cyan (4)     - Phần tử mới                                 ║");
            WriteMessage("╚══════════════════════════════════════════════════════════════╝");
        }

        [CommandMethod("DTS_VERSION")]
        public void DTS_VERSION()
        {
            WriteMessage("╔══════════════════════════════════════════════════════════════╗");
            WriteMessage("║  DTS ENGINE v2.0.0                                           ║");
            WriteMessage("║  BY THANHTDVNCC / CTCI VIETNAM                               ║");
            WriteMessage("║  ISO/IEC 25010 Compliant                                     ║");
            WriteMessage("╚══════════════════════════════════════════════════════════════╝");
        }


        /// <summary>
        /// Hiển thị/Cập nhật nhãn cho các phần tử đã có dữ liệu
        /// </summary>
        [CommandMethod("DTS_SHOW_LABEL", CommandFlags.UsePickSet)]
        public void DTS_SHOW_LABEL()
        {
            WriteMessage("HIỂN THỊ NHÃN PHẦN TỬ");

            var selection = AcadUtils.SelectObjectsOnScreen("");
            if (selection.Count == 0)
            {
                WriteMessage("Không có đối tượng nào được chọn.");
                return;
            }

            AcadUtils.CreateLayer("dts_frame_label", 254);

            int successCount = 0;
            int ignoreCount = 0;

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in selection)
                {
                    if (LabelUtils.RefreshEntityLabel(id, tr))
                    {
                        successCount++;
                    }
                    else
                    {
                        ignoreCount++;
                    }
                }
            });

            WriteMessage($"Đã cập nhật nhãn: {successCount} phần tử");
            if (ignoreCount > 0)
            {
                WriteMessage($"Bỏ qua: {ignoreCount} phần tử (chưa có dữ liệu DTS)");
                WriteMessage("Gợi ý: Dùng lệnh DTS_SET hoặc DTS_SCAN để gán dữ liệu trước.");
            }
        }



        /// <summary>
        /// Tính tải trọng tường
        /// </summary>
        [CommandMethod("DTS_CALC_LOAD")]
        public void DTS_CALC_LOAD()
        {
            WriteMessage("TÍNH TẢI TRỌNG TƯỜNG");

            // Hiển thị bảng tra nhanh
            var loadTable = LoadCalculator.GetQuickLoadTable();

            WriteMessage("\nBảng tra nhanh (chiều cao 3300mm, trừ dầm 400mm):");
            WriteMessage("---------------------------------------");
            WriteMessage("| Độ dày (mm) | Tải (kN/m) |");
            WriteMessage("---------------------------------------");

            foreach (var item in loadTable)
            {
                WriteMessage($"| {item.Key,11} | {item.Value,10:0.00} |");
            }

            WriteMessage("---------------------------------------");

            // Cho phép nhập tùy chỉnh
            var thicknessOpt = new Autodesk.AutoCAD.EditorInput.PromptDoubleOptions("\nNhập độ dày tường để tính (mm, 0 để bỏ qua): ")
            {
                DefaultValue = 0,
                AllowNegative = false
            };

            var thicknessRes = Ed.GetDouble(thicknessOpt);
            if (thicknessRes.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK && thicknessRes.Value > 0)
            {
                var calc = new LoadCalculator();
                double load = calc.CalculateLineLoadWithDeduction(thicknessRes.Value);
                WriteMessage($"Tường {thicknessRes.Value}mm: {load:0.00} kN/m");
            }
        }

        /// <summary>
        /// Xóa tất cả nhãn trên layer dts_frame_label
        /// </summary>
        [CommandMethod("DTS_CLEAR_LABEL")]
        public void DTS_CLEAR_LABEL()
        {
            WriteMessage("XÓA TẤT CẢ NHÃN");

            AcadUtils.ClearLayer("dts_frame_label");
            AcadUtils.ClearLayer("dts_labels");

            WriteSuccess("Đã xóa tất cả nhãn");
        }

        /// <summary>
        /// Xóa tất cả layer tạm
        /// </summary>
        [CommandMethod("DTS_CLEANUP")]
        public void DTS_CLEANUP()
        {
            WriteMessage("DỌN DẸP LAYER TẠM");

            string[] tempLayers = {
        "dts_linkmap",
     "dts_highlight",
 "dts_mapping",
                "dts_labels",
 "dts_temp",
          "dts_frame_label"
       };

            int totalCleared = 0;
            foreach (var layer in tempLayers)
            {
                AcadUtils.ClearLayer(layer);
                totalCleared++;
            }

            WriteSuccess($"Đã dọn dẹp {totalCleared} layer tạm");
        }
    }
}