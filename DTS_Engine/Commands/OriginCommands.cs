using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using DTS_Engine.Core.Utils;

namespace DTS_Engine.Commands
{
    /// <summary>
    /// Cac lenh quan ly Origin (goc toa do tang).
    /// Tuan thu ISO/IEC 25010: Functional Suitability, Usability.
    /// </summary>
    public class OriginCommands : CommandBase
    {
        private const string ORIGIN_LAYER = "dts_origin";

        /// <summary>
        /// Tao diem goc tang moi (chon diem -> ve).
        /// </summary>
        [CommandMethod("DTS_SET_ORIGIN")]
        public void DTS_SET_ORIGIN()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== THIẾT LẬP GỐC TỌA ĐỘ TẦNG ===");

                // 1. Pick điểm đặt gốc
                var ptOpt = new PromptPointOptions("\nChọn điểm đặt gốc tọa độ: ");
                var ptRes = Ed.GetPoint(ptOpt);
                if (ptRes.Status != PromptStatus.OK) return;

                // 2. Nhap thong tin
                var nameOpt = new PromptStringOptions("\nNhập tên tầng (VD: Tầng 1): ") { AllowSpaces = true };
                var nameRes = Ed.GetString(nameOpt);
                if (nameRes.Status != PromptStatus.OK) return;

                var elevOpt = new PromptDoubleOptions("\nNhập cao độ Z (mm): ") { DefaultValue = 0 };
                var elevRes = Ed.GetDouble(elevOpt);
                if (elevRes.Status != PromptStatus.OK) return;

                // 3. Thuc hien Transaction
                UsingTransaction(tr =>
                {
                    // Dam bao layer ton tai (Mau 1 = Do)
                    AcadUtils.CreateLayer(ORIGIN_LAYER, 1);

                    // Chuyen Point3d cua CAD sang Point2D cua Core
                    var center = new Point2D(ptRes.Value.X, ptRes.Value.Y);

                    // Ve Circle
                    ObjectId circleId = AcadUtils.CreateCircle(center, 500, ORIGIN_LAYER, 1, tr);

                    // Chuan bi du lieu
                    var storyData = new StoryData
                    {
                        StoryName = nameRes.StringResult,
                        Elevation = elevRes.Value,
                        StoryHeight = 3300,
                        OffsetX = center.X,
                        OffsetY = center.Y
                    };

                    // Ghi du lieu
                    DBObject circleObj = tr.GetObject(circleId, OpenMode.ForWrite);
                    XDataUtils.WriteStoryData(circleObj, storyData, tr);
                });

                WriteSuccess($"Đã tạo gốc '{nameRes.StringResult}' tại Z={elevRes.Value}");
            });
        }

        /// <summary>
        /// Xem thong tin cac goc da tao.
        /// </summary>
        [CommandMethod("DTS_SHOW_ORIGIN")]
        public void DTS_SHOW_ORIGIN()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== DANH SÁCH GỐC TỌA ĐỘ ===");
                var circleIds = AcadUtils.SelectAll("CIRCLE");
                int found = 0;

                UsingTransaction(tr =>
                {
                    foreach (ObjectId id in circleIds)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        StoryData data = XDataUtils.ReadStoryData(obj);
                        if (data != null)
                        {
                            WriteMessage($"\n- [{id.Handle}] {data.StoryName}: Z={data.Elevation}, Children={data.ChildHandles?.Count ?? 0}");
                            found++;
                        }
                    }
                });

                if (found == 0)
                    WriteMessage("\nChưa có gốc tọa độ nào được tạo.");
                else
                    WriteSuccess($"Tìm thấy {found} gốc tọa độ.");
            });
        }
    }
}