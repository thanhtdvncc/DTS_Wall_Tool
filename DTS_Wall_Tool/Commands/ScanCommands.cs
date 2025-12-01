using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Utils;

namespace DTS_Wall_Tool.Commands
{
    /// <summary>
    /// Các lệnh scan và set dữ liệu tường
    /// </summary>
    public class ScanCommands : CommandBase
    {
        /// <summary>
        /// Quét và hiển thị thông tin tường
        /// </summary>
        [CommandMethod("DTS_SCAN")]
        public void DTS_SCAN()
        {
            WriteMessage("=== QUÉT THÔNG TIN TƯỜNG ===");

            var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
            if (lineIds.Count == 0)
            {
                WriteMessage("Không có đối tượng nào được chọn.");
                return;
            }

            int hasData = 0;
            int noData = 0;

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in lineIds)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    WallData wData = XDataUtils.ReadWallData(obj);

                    string handle = id.Handle.ToString();

                    if (wData != null && wData.HasValidData())
                    {
                        WriteMessage($"  [{handle}]: {wData}");
                        hasData++;
                    }
                    else
                    {
                        noData++;
                    }
                }
            });

            WriteMessage($"Tổng: {lineIds.Count} | Có data: {hasData} | Chưa có: {noData}");
        }

        /// <summary>
        /// Gán thuộc tính Tường (An toàn)
        /// </summary>
        [CommandMethod("DTS_SET_WALL")]
        public void DTS_SET_WALL()
        {
            WriteMessage("\n=== THIẾT LẬP THUỘC TÍNH TƯỜNG (SET WALL) ===");

            // 1. Chọn đối tượng (Line)
            var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
            if (lineIds.Count == 0) return;

            // 2. Nhập Độ dày (Cho phép Null)
            double? inputThickness = null;
            PromptDoubleOptions thickOpt = new PromptDoubleOptions("\nNhập độ dày tường (Enter để bỏ qua/giữ nguyên): ");
            thickOpt.AllowNegative = false;
            thickOpt.AllowZero = false;
            thickOpt.AllowNone = true; // Cho phép Enter ra None

            PromptDoubleResult thickRes = Ed.GetDouble(thickOpt);
            if (thickRes.Status == PromptStatus.OK)
            {
                inputThickness = thickRes.Value;
            }
            else if (thickRes.Status != PromptStatus.None) // Nếu Cancel hoặc Error
            {
                return;
            }

            // 3. Nhập Loại tường (Cho phép Null/Rỗng)
            string inputType = null;
            PromptStringOptions typeOpt = new PromptStringOptions("\nNhập tên loại tường (Enter để bỏ qua/giữ nguyên): ");
            typeOpt.AllowSpaces = false;

            PromptResult typeRes = Ed.GetString(typeOpt);
            if (typeRes.Status == PromptStatus.OK && !string.IsNullOrEmpty(typeRes.StringResult))
            {
                inputType = typeRes.StringResult;
            }

            // 4. Thực hiện Gán (Batch Processing)
            int successCount = 0;
            int skipCount = 0; // Đếm số đối tượng bị bỏ qua do sai loại

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in lineIds)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForWrite);

                    // Tạo object chứa data muốn update
                    // Các trường null sẽ không được gửi vào hàm Save
                    WallData newData = new WallData
                    {
                        Thickness = inputThickness,
                        WallType = inputType,
                        // KHÔNG gán LoadPattern mặc định ở đây nữa để tránh rác
                    };

                    // Gọi hàm Save an toàn
                    bool success = XDataUtils.SaveWallData(obj, newData, tr);

                    if (success)
                    {
                        successCount++;
                    }
                    else
                    {
                        skipCount++;
                        // Có thể log handle ra để user biết
                        // WriteMessage($"\n[Cảnh báo] Bỏ qua đối tượng {id.Handle} vì nó không phải là Tường (hoặc chưa Clear).");
                    }
                }
            });

            // 5. Báo cáo kết quả
            if (inputThickness == null && inputType == null)
            {
                WriteMessage("\nKhông có thông tin nào được nhập. Không có thay đổi.");
            }
            else
            {
                WriteSuccess($"Đã cập nhật {successCount} tường.");
                if (skipCount > 0)
                {
                    WriteError($"{skipCount} đối tượng bị bỏ qua vì đang là loại khác (Dầm/Cột...). Hãy dùng DTS_CLEAR_XDATA trước nếu muốn chuyển đổi.");
                }
            }
        }


        /// <summary>
        /// Xóa thông Element Data của các đối tượng đã chọn
        /// </summary>
        [CommandMethod("DTS_CLEAR_ELEMENT")]
        public void DTS_CLEAR()
        {
            WriteMessage("=== XÓA THÔNG TIN ĐỐI TƯỢNG ===");

            var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
            if (lineIds.Count == 0)
            {
                WriteMessage("Không có đối tượng nào được chọn.");
                return;
            }

            int count = 0;
            UsingTransaction(tr =>
            {
                foreach (ObjectId id in lineIds)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForWrite);
                    XDataUtils.ClearElementData(obj, tr);
                    count++;
                }
            });

            WriteSuccess($"Đã xóa thông tin của {count} đối tượng.");
        }
    }
}