using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Core.Utils;

namespace DTS_Wall_Tool.Commands
{
    public class OriginCommands : CommandBase
    {
        private const string ORIGIN_LAYER = "dts_origin";

        /// <summary>
        /// Tạo điểm gốc tầng mới (Pick điểm -> Vẽ)
        /// </summary>
        [CommandMethod("DTS_SET_ORIGIN")]
        public void DTS_SET_ORIGIN()
        {
            WriteMessage("\n=== THIẾT LẬP GỐC TỌA ĐỘ TẦNG ===");

            // 1. Pick điểm đặt gốc
            PromptPointOptions ptOpt = new PromptPointOptions("\nChọn điểm đặt gốc tọa độ: ");
            PromptPointResult ptRes = Ed.GetPoint(ptOpt);
            if (ptRes.Status != PromptStatus.OK) return;

            // 2. Nhập thông tin
            PromptStringOptions nameOpt = new PromptStringOptions("\nNhập tên tầng (VD: Tang 1): ") { AllowSpaces = true };
            PromptResult nameRes = Ed.GetString(nameOpt);
            if (nameRes.Status != PromptStatus.OK) return;

            PromptDoubleOptions elevOpt = new PromptDoubleOptions("\nNhập cao độ Z (mm): ") { DefaultValue = 0 };
            PromptDoubleResult elevRes = Ed.GetDouble(elevOpt);
            if (elevRes.Status != PromptStatus.OK) return;

            // 3. Thực hiện Transaction
            UsingTransaction(tr =>
            {
                // Đảm bảo layer tồn tại (Màu 1 = Đỏ)
                AcadUtils.CreateLayer(ORIGIN_LAYER, 1);

                // Chuyển Point3d của CAD sang Point2D của Core
                Point2D center = new Point2D(ptRes.Value.X, ptRes.Value.Y);

                // Sử dụng hàm Core để vẽ Circle
                ObjectId circleId = AcadUtils.CreateCircle(center, 500, ORIGIN_LAYER, 1, tr);

                // Chuẩn bị dữ liệu chuẩn ISO
                StoryData storyData = new StoryData
                {
                    StoryName = nameRes.StringResult,
                    Elevation = elevRes.Value,
                    // Các trường khác để mặc định hoặc tính toán sau
                    StoryHeight = 3300,
                    OffsetX = center.X,
                    OffsetY = center.Y
                };

                // Ghi dữ liệu bằng hàm Core
                DBObject circleObj = tr.GetObject(circleId, OpenMode.ForWrite);
                XDataUtils.WriteStoryData(circleObj, storyData, tr);
            });

            WriteSuccess($"Đã tạo gốc '{nameRes.StringResult}' tại Z={elevRes.Value}");
        }

        /// <summary>
        /// Xem thông tin các gốc đã tạo
        /// </summary>
        [CommandMethod("DTS_SHOW_ORIGIN")]
        public void DTS_SHOW_ORIGIN()
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
                        WriteMessage($"\n- [{id.Handle}] {data.StoryName}: Z={data.Elevation}");
                        found++;
                    }
                }
            });

            if (found == 0) WriteMessage("\nChưa có gốc tọa độ nào được tạo.");
        }
    }
}