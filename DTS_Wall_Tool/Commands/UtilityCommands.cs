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
            WriteMessage("=== DTS WALL TOOL ===");
            WriteMessage("Version: 2.0");
            WriteMessage("Author: DTS Engineering");
            WriteMessage("thanhtdvncc");
            WriteMessage("");
            WriteMessage("Commands:");
            WriteMessage("  DTS_SCAN      - Quét thông tin tường");
            WriteMessage("  DTS_SET       - Gán thông tin tường");
            WriteMessage("  DTS_CLEAR     - Xóa thông tin tường");
            WriteMessage("  DTS_LINK      - Liên kết tường với origin");
            WriteMessage("  DTS_SHOW_LINK - Hiển thị liên kết");
            WriteMessage("  DTS_BREAK_LINK- Xóa liên kết");
            WriteMessage("  DTS_SET_ORIGIN- Thiết lập origin");
            WriteMessage("  DTS_TEST_SAP  - Kiểm tra kết nối SAP2000");
            WriteMessage("  DTS_GET_FRAMES- Lấy danh sách frames");
            WriteMessage("  DTS_TEST_MAP  - Test mapping tường-dầm");
            WriteMessage("  DTS_ASSIGN_LOAD - Gán tải lên SAP2000");
            WriteMessage("  DTS_CALC_LOAD - Tính tải trọng tường");
        }

        /// <summary>
        /// Hiển thị/Cập nhật nhãn cho các phần tử đã có dữ liệu
        /// </summary>
        [CommandMethod("DTS_SHOW_LABEL", CommandFlags.UsePickSet)]
        public void DTS_SHOW_LABEL()
        {
            WriteMessage("=== HIỂN THỊ NHÃN PHẦN TỬ (UPDATE) ===");

            var selection = AcadUtils.SelectObjectsOnScreen("");
            if (selection.Count == 0)
            {
                WriteMessage("\nKhông có đối tượng nào được chọn.");
                return;
            }

            AcadUtils.CreateLayer("dts_frame_label", 254);

            int successCount = 0;
            int ignoreCount = 0;

            UsingTransaction(tr =>
            {
                // Mở BlockTableRecord để ghi (LabelPlotter cần cái này)
                // Lưu ý: LabelUtils.UpdateWallLabels cũng gọi GetObject ForWrite, 
                // nhưng tốt nhất là mở ở ngoài này nếu truyền vào. 
                // Tuy nhiên theo thiết kế hiện tại LabelUtils tự mở BTR, nên ta chỉ cần Transaction.

                foreach (ObjectId id in selection)
                {
                    // Hàm trả về true nếu vẽ thành công, false nếu object không có XData hợp lệ
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

            WriteSuccess($"Đã cập nhật nhãn: {successCount} đối tượng.");
            if (ignoreCount > 0)
            {
                WriteMessage($"\nBỏ qua: {ignoreCount} đối tượng (Do chưa có dữ liệu DTS_WALL).");
                WriteMessage("\nGợi ý: Dùng lệnh DTS_SET hoặc DTS_SCAN để gán dữ liệu trước.");
            }
        }



        /// <summary>
        /// Tính tải trọng tường
        /// </summary>
        [CommandMethod("DTS_CALC_LOAD")]
        public void DTS_CALC_LOAD()
        {
            WriteMessage("=== TÍNH TẢI TRỌNG TƯỜNG ===");

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
                WriteMessage($"\nTường {thicknessRes.Value}mm: {load:0. 00} kN/m");
            }
        }

        /// <summary>
        /// Xóa tất cả layer tạm
        /// </summary>
        [CommandMethod("DTS_CLEANUP")]
        public void DTS_CLEANUP()
        {
            WriteMessage("=== DỌN DẸP LAYER TẠM ===");

            // Thêm "dts_frame_label" vào danh sách
            string[] tempLayers = { 
                "dts_linkmap", 
                "dts_highlight", 
                "dts_mapping", 
                "dts_labels", 
                "dts_temp",
                "dts_frame_label" // Quan trọng: Layer chứa text
            };

            foreach (var layer in tempLayers)
            {
                AcadUtils.ClearLayer(layer); // ClearLayer trả về void, không cộng dồn
            }

            WriteSuccess($"Đã dọn dẹp sạch sẽ các layer tạm.");
        }


    }
}