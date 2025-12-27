using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;
using DTS_Engine.Drawing;
using DTS_Engine.Drawing.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Commands
{
    public class SectionDrawingCommands
    {
        [CommandMethod("DTS_REBAR_DRAWING")]
        public void DrawRebarSchedule()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            try
            {
                // 1. Chọn dầm (LINE/LWPOLYLINE)
                var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE");
                if (ids == null || ids.Count == 0) return;

                // 2. Điểm chèn bảng (Insertion Point)
                PromptPointOptions ppo = new PromptPointOptions("\nChọn điểm chèn bảng: ");
                PromptPointResult ppr = ed.GetPoint(ppo);
                if (ppr.Status != PromptStatus.OK) return;

                Point3d insertPt = ppr.Value;

                // 3. Đọc dữ liệu từ XData
                List<BeamResultData> beamResults = new List<BeamResultData>();
                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        var obj = tr.GetObject(id, OpenMode.ForRead);
                        var beamRes = XDataUtils.ReadRebarData(obj);
                        if (beamRes != null)
                        {
                            beamResults.Add(beamRes);
                        }
                    }
                    tr.Commit();
                }

                if (beamResults.Count == 0)
                {
                    ed.WriteMessage("\nKhông tìm thấy dữ liệu thép (XData) trên các đối tượng đã chọn.");
                    return;
                }

                // 4. Chuyển đổi dữ liệu sang Drawing Models
                var settings = DtsSettings.Instance.Drawing;
                var extractor = new SectionDataExtractor();
                var rowData = beamResults.Select(b => extractor.Extract(b, settings.ConcreteCover)).ToList();

                // 5. Khởi tạo Orchestrator và thực hiện vẽ
                var config = new TableLayoutConfig();
                var orchestrator = new BeamScheduleOrchestrator(config);

                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);

                    orchestrator.OrchestrateDrawing(btr, rowData, insertPt, settings);

                    tr.Commit();
                }

                ed.WriteMessage($"\nĐã vẽ xong bảng thống kê cho {beamResults.Count} dầm.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\nLỗi: {ex.Message}");
            }
        }
    }
}
