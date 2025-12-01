using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Core.Utils;
using DTS_Wall_Tool.Models;
using System.Collections.Generic;

namespace DTS_Wall_Tool.Commands
{
    /// <summary>
    /// Các lệnh xử lý centerline
    /// </summary>
    public class ProcessCommands : CommandBase
    {
        private const string OUTPUT_LAYER = "dts_centerlines";

        /// <summary>
        /// Xử lý tường thành centerline
        /// </summary>
        [CommandMethod("DTS_PROCESS")]
        public void DTS_PROCESS()
        {
            WriteMessage("=== XỬ LÝ TƯỜNG -> CENTERLINE ===");

            // Chọn lines
            var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
            if (lineIds.Count == 0)
            {
                WriteMessage("Không có đối tượng nào được chọn.");
                return;
            }

            WriteMessage($"Đã chọn {lineIds.Count} đường.. .");

            // Nhập các độ dày cần detect
            PromptStringOptions thickOpt = new PromptStringOptions("\nNhập các độ dày cần detect (phân cách bằng dấu phẩy, VD: 100,200,220): ")
            {
                DefaultValue = "100,110,200,220",
                AllowSpaces = false
            };

            PromptResult thickRes = Ed.GetString(thickOpt);
            string thickStr = thickRes.Status == PromptStatus.OK ? thickRes.StringResult : "100,110,200,220";

            var thicknesses = new List<double>();
            foreach (var s in thickStr.Split(','))
            {
                if (double.TryParse(s.Trim(), out double t))
                    thicknesses.Add(t);
            }

            if (thicknesses.Count == 0)
            {
                thicknesses = new List<double> { 100, 110, 200, 220 };
            }

            // Thu thập segments
            var segments = new List<WallSegment>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId lineId in lineIds)
                {
                    Line lineEnt = tr.GetObject(lineId, OpenMode.ForRead) as Line;
                    if (lineEnt == null) continue;

                    var segment = new WallSegment(
                        new Point2D(lineEnt.StartPoint.X, lineEnt.StartPoint.Y),
                        new Point2D(lineEnt.EndPoint.X, lineEnt.EndPoint.Y)
                    )
                    {
                        Handle = lineId.Handle.ToString(),
                        Layer = lineEnt.Layer
                    };

                    segments.Add(segment);
                }
            });

            WriteMessage($"Thu thập được {segments.Count} segments.");

            // Xử lý
            var processor = new WallSegmentProcessor
            {
                WallThicknesses = thicknesses,
                AngleTolerance = 5.0,
                DistanceTolerance = 10.0,
                AutoJoinGapDistance = 300.0,
                EnableAutoExtend = true
            };

            var centerlines = processor.Process(segments);

            WriteMessage($"\nKết quả xử lý:");
            WriteMessage($"  - Merged segments: {processor.MergedSegmentsCount}");
            WriteMessage($"  - Detected pairs: {processor.DetectedPairsCount}");
            WriteMessage($"  - Recovered gaps: {processor.RecoveredGapsCount}");
            WriteMessage($"  - Output centerlines: {centerlines.Count}");

            // Vẽ kết quả
            AcadUtils.CreateLayer(OUTPUT_LAYER, 4); // Cyan
            AcadUtils.ClearLayer(OUTPUT_LAYER);

            UsingTransaction(tr =>
            {
                foreach (var cl in centerlines)
                {
                    AcadUtils.CreateLine(cl.AsSegment, OUTPUT_LAYER, 4, tr);
                }
            });

            WriteSuccess($"Đã tạo {centerlines.Count} centerlines trên layer '{OUTPUT_LAYER}'.");
        }

        /// <summary>
        /// Xóa kết quả centerline
        /// </summary>
        [CommandMethod("DTS_CLEAR_CL")]
        public void DTS_CLEAR_CL()
        {
            AcadUtils.ClearLayer(OUTPUT_LAYER);
            WriteSuccess("Đã xóa centerlines.");
        }
    }
}