using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Utils;

namespace DTS_Wall_Tool.Commands
{
    /// <summary>
    /// Các lệnh quét và nhận diện phần tử
    /// </summary>
    public class ScanCommands : CommandBase
    {
        [CommandMethod("DTS_SCAN_WALL")]
        public void DTS_SCAN_WALL()
        {
            WriteMessage("=== QUÉT VÀ NHẬN DIỆN TƯỜNG ===");

            // Nhập độ dày mặc định
            PromptDoubleOptions thkOpt = new PromptDoubleOptions("\nĐộ dày tường mặc định (mm): ");
            thkOpt.DefaultValue = 200;
            thkOpt.AllowNegative = false;
            thkOpt.AllowZero = false;

            PromptDoubleResult thkRes = Ed.GetDouble(thkOpt);
            double defaultThickness = thkRes.Status == PromptStatus.OK ? thkRes.Value : 200;

            var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
            if (lineIds.Count == 0)
            {
                WriteMessage("Không có Line nào được chọn.");
                return;
            }

            int createdCount = 0;
            int updatedCount = 0;

            UsingTransaction(tr =>
            {
                foreach (ObjectId lineId in lineIds)
                {
                    Entity ent = tr.GetObject(lineId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    // Đọc WallData hiện có
                    WallData wData = XDataUtils.ReadWallData(ent);

                    if (wData == null)
                    {
                        // Tạo mới
                        wData = new WallData
                        {
                            Thickness = defaultThickness
                        };
                        wData.EnsureWallType();
                        createdCount++;
                    }
                    else
                    {
                        // Cập nhật nếu chưa có thickness
                        if (!wData.Thickness.HasValue)
                        {
                            wData.Thickness = defaultThickness;
                            wData.EnsureWallType();
                        }
                        updatedCount++;
                    }

                    XDataUtils.SaveWallData(ent, wData, tr);
                    WriteMessage($"  [{lineId.Handle}]: {wData.WallType}");
                }
            });

            WriteMessage($"\nKết quả: {createdCount} created, {updatedCount} updated");
        }

        [CommandMethod("DTS_CLEAR_DATA")]
        public void DTS_CLEAR_DATA()
        {
            WriteMessage("=== XÓA DỮ LIỆU DTS ===");

            var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
            if (ids.Count == 0)
            {
                WriteMessage("Không có phần tử nào được chọn.");
                return;
            }

            int clearedCount = 0;

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in ids)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForWrite);
                    if (XDataUtils.HasDtsData(obj))
                    {
                        XDataUtils.ClearElementData(obj, tr);
                        clearedCount++;
                    }
                }
            });

            WriteMessage($"\nĐã xóa: {clearedCount} phần tử");
        }
    }
}