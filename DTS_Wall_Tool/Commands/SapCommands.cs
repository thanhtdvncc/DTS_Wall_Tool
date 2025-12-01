using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Core.Utils;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Commands
{
    /// <summary>
    /// Các lệnh làm việc với SAP2000
    /// Hỗ trợ đồng bộ 2 chiều
    /// </summary>
    public class SapCommands : CommandBase
    {
        private const string LABEL_LAYER = "dts_frame_label";
        private const string WALL_LAYER = "DTS_WALL_DIAGRAM";

        #region Connection & Info

        [CommandMethod("DTS_TEST_SAP")]
        public void DTS_TEST_SAP()
        {
            WriteMessage("KIỂM TRA KẾT NỐI SAP2000");

            bool connected = SapUtils.Connect(out string message);
            WriteMessage(message);

            if (connected)
            {
                WriteMessage($"Số Frame: {SapUtils.CountFrames()}");
                WriteMessage($"Load Patterns: {string.Join(", ", SapUtils.GetLoadPatterns())}");

                var stories = SapUtils.GetStories();
                WriteMessage($"Số tầng: {stories.Count}");
                foreach (var story in stories)
                {
                    WriteMessage($"  - {story}");
                }
            }
        }

        [CommandMethod("DTS_GET_FRAMES")]
        public void DTS_GET_FRAMES()
        {
            WriteMessage("LẤY FRAMES TỪ SAP2000");

            if (!SapUtils.IsConnected)
            {
                if (!SapUtils.Connect(out string msg))
                {
                    WriteError(msg);
                    return;
                }
            }

            var frames = SapUtils.GetAllFramesGeometry();
            int beamCount = frames.Count(f => f.IsBeam);
            int columnCount = frames.Count(f => !f.IsBeam);

            WriteMessage($"Tổng: {frames.Count} | Dầm: {beamCount} | Cột: {columnCount}");
        }

        #endregion

        #region Sync Commands

        /// <summary>
        /// Đồng bộ từ SAP2000 vào CAD (PULL)
        /// Cập nhật mapping và tải trọng từ SAP
        /// </summary>
        [CommandMethod("DTS_SYNC_SAP")]
        public void DTS_SYNC_SAP()
        {
            WriteMessage("ĐỒNG BỘ TỪ SAP2000 → CAD");

            if (!EnsureSapConnection()) return;

            var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
            if (lineIds.Count == 0)
            {
                WriteMessage("Không có phần tử nào được chọn.");
                return;
            }

            // Lọc chỉ lấy phần tử có XData và đã Link
            var validIds = FilterLinkedElements(lineIds);
            if (validIds.Count == 0)
            {
                WriteError("Không có phần tử nào đã Link với Origin.");
                WriteMessage("Chạy DTS_SET_ORIGIN và DTS_LINK trước.");
                return;
            }

            WriteMessage($"Xử lý {validIds.Count} phần tử đã Link...");

            // Lấy tất cả frames từ SAP
            var allFrames = SapUtils.GetAllFramesGeometry();

            // Xóa label cũ
            AcadUtils.CreateLayer(LABEL_LAYER, 254);
            AcadUtils.ClearLayer(LABEL_LAYER);

            int syncedCount = 0, modifiedCount = 0, errorCount = 0;

            UsingTransaction(tr =>
                       {
                           var bt = tr.GetObject(Db.BlockTableId, OpenMode.ForRead) as BlockTable;
                           var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                           foreach (ObjectId lineId in validIds)
                           {
                               var result = SyncSingleElement(lineId, allFrames, btr, tr);

                               if (result.Success)
                               {
                                   if (result.State == SyncState.SapModified)
                                       modifiedCount++;
                                   else
                                       syncedCount++;
                               }
                               else
                               {
                                   errorCount++;
                                   WriteError($"  [{result.Handle}]: {result.Message}");
                               }
                           }
                       });

            WriteMessage($"\nĐồng bộ OK: {syncedCount}");
            WriteMessage($"SAP thay đổi: {modifiedCount}");
            if (errorCount > 0)
                WriteMessage($"Lỗi: {errorCount}");
        }

        /// <summary>
        /// Gán tải từ CAD vào SAP2000 (PUSH)
        /// </summary>
        [CommandMethod("DTS_PUSH_LOAD")]
        public void DTS_PUSH_LOAD()
        {
            WriteMessage("GÁN TẢI TỪ CAD → SAP2000");

            if (!EnsureSapConnection()) return;

            var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
            if (lineIds.Count == 0)
            {
                WriteMessage("Không có phần tử nào được chọn.");
                return;
            }

            // Nhập load pattern
            PromptStringOptions patternOpt = new PromptStringOptions("\nLoad Pattern (mặc định DL): ");
            patternOpt.DefaultValue = "DL";
            var patternRes = Ed.GetString(patternOpt);
            string loadPattern = string.IsNullOrEmpty(patternRes.StringResult) ? "DL" : patternRes.StringResult;

            if (!SapUtils.LoadPatternExists(loadPattern))
            {
                WriteError($"Load pattern '{loadPattern}' không tồn tại trong SAP!");
                return;
            }

            UsingTransaction(tr =>
      {
          var results = SyncEngine.PushToSap(lineIds, loadPattern, tr);

          int success = results.Count(r => r.Success);
          int failed = results.Count(r => !r.Success);

          foreach (var r in results)
          {
              if (!r.Success)
                  WriteError($"  [{r.Handle}]: {r.Message}");
          }

          WriteMessage($"\nThành công: {success}");
          if (failed > 0)
              WriteMessage($"Thất bại: {failed}");
      });

            SapUtils.RefreshView();
        }

        /// <summary>
        /// Phát hiện thay đổi và xung đột
        /// </summary>
        [CommandMethod("DTS_CHECK_SYNC")]
        public void DTS_CHECK_SYNC()
        {
            WriteMessage("KIỂM TRA TRẠNG THÁI ĐỒNG BỘ");

            if (!EnsureSapConnection()) return;

            var lineIds = AcadUtils.SelectObjectsOnScreen("LINE");
            if (lineIds.Count == 0)
            {
                WriteMessage("Không có phần tử nào được chọn.");
                return;
            }

            var stats = new Dictionary<SyncState, int>();

            UsingTransaction(tr =>
                  {
                      var changes = SyncEngine.DetectAllChanges(lineIds, tr);

                      foreach (var kvp in changes)
                      {
                          var state = kvp.Value;
                          if (!stats.ContainsKey(state)) stats[state] = 0;
                          stats[state]++;

                          // Cập nhật màu theo trạng thái
                          int color = SyncEngine.GetSyncStateColor(state);
                          Entity ent = tr.GetObject(kvp.Key, OpenMode.ForWrite) as Entity;
                          if (ent != null) ent.ColorIndex = color;
                      }
                  });

            // Dictionary<TKey,TValue>.GetValueOrDefault is not available on .NET Framework 4.8,
            // use TryGetValue to remain compatible.
            int synced = stats.TryGetValue(SyncState.Synced, out var tmp) ? tmp : 0;
            int cadModified = stats.TryGetValue(SyncState.CadModified, out tmp) ? tmp : 0;
            int sapModified = stats.TryGetValue(SyncState.SapModified, out tmp) ? tmp : 0;
            int conflict = stats.TryGetValue(SyncState.Conflict, out tmp) ? tmp : 0;
            int sapDeleted = stats.TryGetValue(SyncState.SapDeleted, out tmp) ? tmp : 0;
            int newElement = stats.TryGetValue(SyncState.NewElement, out tmp) ? tmp : 0;

            WriteMessage("\nThống kê:");
            if (synced > 0) WriteMessage($"  Đã đồng bộ (Xanh lá): {synced}");
            if (cadModified > 0) WriteMessage($"  CAD thay đổi (Vàng): {cadModified}");
            if (sapModified > 0) WriteMessage($"  SAP thay đổi (Xanh): {sapModified}");
            if (conflict > 0) WriteMessage($"  Xung đột (Magenta): {conflict}");
            if (sapDeleted > 0) WriteMessage($"  Frame bị xóa (Đỏ): {sapDeleted}");
            if (newElement > 0) WriteMessage($"  Phần tử mới (Cyan): {newElement}");
        }

        #endregion

        #region Helper Methods

        private bool EnsureSapConnection()
        {
            if (!SapUtils.IsConnected)
            {
                if (!SapUtils.Connect(out string msg))
                {
                    WriteError(msg);
                    return false;
                }
            }
            return true;
        }

        private List<ObjectId> FilterLinkedElements(List<ObjectId> ids)
        {
            var result = new List<ObjectId>();

            UsingTransaction(tr =>
        {
            foreach (var id in ids)
            {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
       var elemData = XDataUtils.ReadElementData(obj);

               if (elemData != null && elemData.IsLinked)
 {
   result.Add(id);
     }
  }
            });

            return result;
        }

        private SyncResult SyncSingleElement(ObjectId lineId, List<SapFrame> allFrames,
     BlockTableRecord btr, Transaction tr)
  {
         var result = new SyncResult
            {
         Handle = lineId.Handle.ToString(),
            Success = true
            };

         Line lineEnt = tr.GetObject(lineId, OpenMode.ForWrite) as Line;
       if (lineEnt == null)
            {
                result.Success = false;
result.Message = "Không phải Line";
    return result;
      }

            // Đọc WallData
   var wallData = XDataUtils.ReadWallData(lineEnt);
            if (wallData == null)
            {
         result.Success = false;
  result.Message = "Không có dữ liệu WallData";
 return result;
      }

   // Lấy thông tin từ Origin
 if (!wallData.IsLinked)
            {
     result.Success = false;
     result.Message = "Chưa Link với Origin";
                return result;
     }

 ObjectId originId = AcadUtils.GetObjectIdFromHandle(wallData.OriginHandle);
     if (originId == ObjectId.Null)
            {
   result.Success = false;
 result.Message = "Origin không tồn tại";
    return result;
    }

          DBObject originObj = tr.GetObject(originId, OpenMode.ForRead);
  StoryData storyData = XDataUtils.ReadStoryData(originObj);
            if (storyData == null)
   {
     result.Success = false;
              result.Message = "Origin không có StoryData";
                return result;
      }

        // Lấy thông tin geometry
            double wallZ = storyData.Elevation;
          Point2D insertOffset = new Point2D(storyData.OffsetX, storyData.OffsetY);

 // Nếu Origin là Circle, lấy tâm làm offset
            if (originObj is Circle circle)
      {
    insertOffset = new Point2D(circle.Center.X, circle.Center.Y);
       }

       Point2D startPt = new Point2D(lineEnt.StartPoint.X, lineEnt.StartPoint.Y);
            Point2D endPt = new Point2D(lineEnt.EndPoint.X, lineEnt.EndPoint.Y);

 // Lọc dầm theo cao độ
            var beamsAtZ = allFrames
        .Where(f => f.IsBeam && System.Math.Abs(f.AverageZ - wallZ) <= MappingEngine.TOLERANCE_Z)
.ToList();

     // Thực hiện mapping
            double wallThickness = wallData.Thickness ?? 200.0;
            var mapResult = MappingEngine.FindMappings(
  startPt, endPt, wallZ, beamsAtZ,
                insertOffset, wallThickness);

     mapResult.WallHandle = lineId.Handle.ToString();

    // Kiểm tra thay đổi từ SAP
         bool sapChanged = false;
 foreach (var mapping in mapResult.Mappings)
          {
         if (mapping.TargetFrame == "New") continue;

    // Đọc tải trọng hiện có trong SAP
   var sapLoads = SapUtils.GetFrameDistributedLoads(mapping.TargetFrame, wallData.LoadPattern ?? "DL");
 if (sapLoads.Count > 0)
              {
         double sapLoadValue = sapLoads.Sum(l => l.LoadValue);

            // So sánh với CAD
         if (wallData.LoadValue.HasValue &&
     System.Math.Abs(wallData.LoadValue.Value - sapLoadValue) > 0.01)
              {
      sapChanged = true;
      result.OldLoadValue = wallData.LoadValue;
            result.NewLoadValue = sapLoadValue;
          }
}
            }

       // Cập nhật WallData
            wallData.Mappings = mapResult.Mappings;
    wallData.BaseZ = wallZ;
      wallData.Height = storyData.StoryHeight;
XDataUtils.WriteElementData(lineEnt, wallData, tr);

     // Cập nhật màu line
  int colorIndex = mapResult.GetColorIndex();
  if (sapChanged)
            {
       colorIndex = SyncEngine.GetSyncStateColor(SyncState.SapModified);
    result.State = SyncState.SapModified;
   }
 else
            {
    result.State = mapResult.HasMapping ? SyncState.Synced : SyncState.NewElement;
       }
 lineEnt.ColorIndex = colorIndex;

      // Vẽ Label
      LabelUtils.UpdateWallLabels(lineId, wallData, mapResult, tr);

     // Tạo message
         if (mapResult.HasMapping)
            {
                var firstMap = mapResult.Mappings.First();
                if (sapChanged)
      {
        result.Message = $"-> {firstMap.TargetFrame} (SAP load: {result.NewLoadValue:0.00} kN/m)";
       }
      else
            {
             result.Message = $"-> {firstMap.TargetFrame} ({firstMap.MatchType})";
                }
            }
 else
            {
                result.Message = "-> NEW";
            }

            return result;
        }

        #endregion
    }
}