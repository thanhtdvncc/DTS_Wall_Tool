using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Interfaces;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Commands
{
    /// <summary>
    /// Các lệnh làm việc với SAP2000.
    /// Hỗ trợ đồng bộ 2 chiều.
    /// 
    /// ⚠️ CLEAN ARCHITECTURE (v2.1+):
    /// - Sử dụng ILoadBearing interface
    /// - Tải trọng lưu trong Loads list
    /// - Không còn truy cập trực tiếp LoadValue, LoadPattern
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
                WriteMessage($"Đơn vị hiện tại: {UnitManager.Info}");

                // Read unified grid/story items
                var items = SapUtils.GetStories();
                WriteMessage($"Grid items total: {items.Count}");

                var xItems = items.Where(i => !string.IsNullOrEmpty(i.AxisDir) && i.AxisDir.Trim().Equals("X", StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Coordinate).ToList();
                var yItems = items.Where(i => !string.IsNullOrEmpty(i.AxisDir) && i.AxisDir.Trim().Equals("Y", System.StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Coordinate).ToList();
                var zItems = items.Where(i => !string.IsNullOrEmpty(i.AxisDir) && i.AxisDir.Trim().StartsWith("Z", System.StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Coordinate).ToList();

                WriteMessage($"X axes: {xItems.Count}, Y axes: {yItems.Count}, Z (stories): {zItems.Count}");

                if (zItems.Count > 0)
                {
                    WriteMessage("Stories (Z):");
                    foreach (var z in zItems)
                    {
                        WriteMessage($" - {z.Name} : Z={z.Coordinate}");
                    }
                }
                else
                {
                    WriteMessage("Không tìm thấy Z (stories) trong Grid Lines.");
                }

                if (xItems.Count > 0)
                {
                    WriteMessage("X axes:");
                    int maxPrint = 200;
                    int printed = 0;
                    foreach (var xi in xItems)
                    {
                        if (printed++ >= maxPrint) { WriteMessage($" ... ({xItems.Count - maxPrint} more)"); break; }
                        WriteMessage($" - {xi.Name} : X={xi.Coordinate}");
                    }
                }
                else
                {
                    WriteMessage("Không tìm thấy trục X trong Grid Lines.");
                }

                if (yItems.Count > 0)
                {
                    WriteMessage("Y axes:");
                    int maxPrintY = 200;
                    int printedY = 0;
                    foreach (var yi in yItems)
                    {
                        if (printedY++ >= maxPrintY) { WriteMessage($" ... ({yItems.Count - maxPrintY} more)"); break; }
                        WriteMessage($" - {yi.Name} : Y={yi.Coordinate}");
                    }
                }
                else
                {
                    WriteMessage("Không tìm thấy trục Y trong Grid Lines.");
                }

                // If GetStories returned nothing try to read Grid Lines and extract Z entries
                if (zItems.Count == 0)
                {
                    WriteMessage("GetStories trả về 0, thử đọc Grid Lines để tìm Z (floor) entries...");
                    var grids = SapUtils.GetGridLines();
                    WriteMessage($"Grid lines read: {grids.Count}");

                    var zGrids = grids.Where(g => !string.IsNullOrEmpty(g.Orientation) && g.Orientation.StartsWith("Z", System.StringComparison.OrdinalIgnoreCase)).ToList();
                    WriteMessage($"Grid Z entries: {zGrids.Count}");

                    var derived = new List<DTS_Wall_Tool.Core.Data.StoryData>();
                    foreach (var g in zGrids)
                    {
                        var sd = new DTS_Wall_Tool.Core.Data.StoryData
                        {
                            StoryName = g.Name,
                            Elevation = g.Coordinate,
                            StoryHeight = 3300
                        };
                        derived.Add(sd);
                        WriteMessage($" - Derived story from grid: {sd.StoryName} Z={sd.Elevation}");
                    }

                    if (derived.Count == 0)
                        WriteMessage("Không tìm thấy bản ghi Z trong Grid Lines.");

                    // Extra diagnostics: call DatabaseTables.GetAllFieldsInTable and GetTableForDisplayArray directly to inspect metadata
                    try
                    {
                        var model = SapUtils.GetModel();
                        if (model != null)
                        {
                            WriteMessage("--- Diagnostic: GetAllFieldsInTable for 'Grid Lines' ---");
                            int metaVer = 0;
                            int metaNum = 0;
                            string[] fk = null; string[] fn = null; string[] desc = null; string[] units = null; bool[] imp = null;
                            int mret = model.DatabaseTables.GetAllFieldsInTable("Grid Lines", ref metaVer, ref metaNum, ref fk, ref fn, ref desc, ref units, ref imp);
                            WriteMessage($"GetAllFieldsInTable ret={mret}, metaNum={metaNum}");
                            if (fk != null)
                            {
                                WriteMessage($"FieldKeys: {string.Join(", ", fk)}");
                            }
                            if (fn != null)
                            {
                                WriteMessage($"FieldNames: {string.Join(", ", fn)}");
                            }

                            WriteMessage("--- Diagnostic: GetTableForDisplayArray for 'Grid Lines' ---");
                            string[] fieldKeyListInput = new[] { "" };
                            int tv = 0; string[] fieldsIncluded = null; int numberRecords = 0; string[] tableData = null;
                            int tret = model.DatabaseTables.GetTableForDisplayArray("Grid Lines", ref fieldKeyListInput, "All", ref tv, ref fieldsIncluded, ref numberRecords, ref tableData);
                            WriteMessage($"GetTableForDisplayArray ret={tret}, records={numberRecords}, tv={tv}");
                            if (fieldsIncluded != null) WriteMessage($"FieldsIncluded: {string.Join(",", fieldsIncluded)}");
                            if (tableData != null && tableData.Length > 0)
                            {
                                int cols = (fieldsIncluded != null) ? fieldsIncluded.Length : 0;
                                WriteMessage($"Sample tableData length={tableData.Length}, cols={cols}");
                                // print first row
                                if (numberRecords > 0 && cols > 0)
                                {
                                    var first = new List<string>();
                                    for (int c = 0; c < cols; c++) first.Add(tableData[c] ?? "");
                                    WriteMessage("First row: " + string.Join(" | ", first));
                                }
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        WriteMessage("Diagnostics error: " + ex.Message);
                    }
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

            var validIds = FilterLinkedElements(lineIds);
            if (validIds.Count == 0)
            {
                WriteError("Không có phần tử nào đã Link với Origin.");
                WriteMessage("Chạy DTS_SET_ORIGIN và DTS_LINK trước.");
                return;
            }

            WriteMessage($"Xử lý {validIds.Count} phần tử đã Link...");

            var allFrames = SapUtils.GetAllFramesGeometry();
            AcadUtils.CreateLayer(LABEL_LAYER, 254);

            int syncedCount = 0, modifiedCount = 0, errorCount = 0;
            var sapChangedElements = new List<(ObjectId id, double oldLoad, double newLoad)>();

            UsingTransaction(tr =>
                {
                    var bt = tr.GetObject(Db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                    // FIX: Xóa label cũ của phần tử được chọn trước
                    foreach (ObjectId lineId in validIds)
                    {
                        DeleteEntityLabels(lineId, tr);
                    }

                    foreach (ObjectId lineId in validIds)
                    {
                        var result = SyncSingleElement(lineId, allFrames, btr, tr);

                        if (result.Success)
                        {
                            if (result.State == SyncState.SapModified)
                            {
                                modifiedCount++;
                                // Lưu lại phần tử có thay đổi để prompt sau
                                if (result.OldLoadValue.HasValue && result.NewLoadValue.HasValue)
                                {
                                    sapChangedElements.Add((lineId, result.OldLoadValue.Value, result.NewLoadValue.Value));
                                }
                            }
                            else
                            {
                                syncedCount++;
                            }
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

          // Hỏi có muốn apply tải từ SAP vào CAD không
            if (sapChangedElements.Count > 0)
   {
      WriteMessage($"\nPhát hiện {sapChangedElements.Count} phần tử có tải trong SAP khác với CAD:");

        int showCount = Math.Min(5, sapChangedElements.Count);
           for (int i = 0; i < showCount; i++)
        {
       var elem = sapChangedElements[i];
      WriteMessage($"  - Handle [{elem.id.Handle}]: CAD={elem.oldLoad:0.00} → SAP={elem.newLoad:0.00} kN/m");
 }

    if (sapChangedElements.Count > showCount)
                {
           WriteMessage($"  ... và {sapChangedElements.Count - showCount} phần tử khác");
     }

      PromptKeywordOptions opts = new PromptKeywordOptions("\nCó muốn cập nhật tải từ SAP vào CAD? [Yes/No]");
                opts.Keywords.Add("Yes");
 opts.Keywords.Add("No");
            opts.Keywords.Default = "No";
         opts.AllowNone = true;

PromptResult promptRes = Ed.GetKeywords(opts);

     if (promptRes.Status == PromptStatus.OK && promptRes.StringResult == "Yes")
        {
   int appliedCount = ApplyLoadFromSap(sapChangedElements);
       WriteSuccess($"Đã cập nhật {appliedCount} phần tử với tải từ SAP.");
          }
    else
                {
          WriteMessage("Bỏ qua cập nhật tải. Chỉ hiển thị để so sánh.");
           }
      }
  }

        /// <summary>
        /// Xóa tất cả label thuộc một entity
        /// </summary>
   private void DeleteEntityLabels(ObjectId entityId, Transaction tr)
        {
 try
   {
        string targetHandle = entityId.Handle.ToString();

       TypedValue[] filter = new TypedValue[]
     {
       new TypedValue((int)DxfCode.Start, "MTEXT"),
 new TypedValue((int)DxfCode.LayerName, LABEL_LAYER)
      };

        SelectionFilter selFilter = new SelectionFilter(filter);
             PromptSelectionResult selRes = Ed.SelectAll(selFilter);

        if (selRes.Status == PromptStatus.OK)
           {
    foreach (ObjectId id in selRes.Value.GetObjectIds())
                {
         MText mtext = tr.GetObject(id, OpenMode.ForRead) as MText;
               if (mtext != null && mtext.Contents.Contains(targetHandle))
             {
     mtext.UpgradeOpen();
        mtext.Erase();
                  }
         }
      }
            }
          catch { }
        }

        /// <summary>
 /// Cập nhật Loads từ SAP vào CAD.
        /// ⚠️ CLEAN: Thêm LoadDefinition vào Loads list
        /// </summary>
    private int ApplyLoadFromSap(List<(ObjectId id, double oldLoad, double newLoad)> changes)
        {
          int count = 0;

            UsingTransaction(tr =>
      {
       foreach (var change in changes)
        {
        try
 {
     Line lineEnt = tr.GetObject(change.id, OpenMode.ForWrite) as Line;
             if (lineEnt == null) continue;

     var wallData = XDataUtils.ReadWallData(lineEnt);
if (wallData == null) continue;

      // ⚠️ CLEAN: Cập nhật vào Loads list thay vì LoadValue
              wallData.ClearLoads();
 wallData.Loads.Add(new LoadDefinition
          {
          Pattern = "DL",
          Value = change.newLoad,
           Type = LoadType.DistributedLine,
        TargetElement = "Frame",
   Direction = "Gravity"
    });

 XDataUtils.WriteElementData(lineEnt, wallData, tr);
                count++;
           }
   catch { }
              }
      });

            return count;
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

            var wallData = XDataUtils.ReadWallData(lineEnt);
            if (wallData == null)
            {
                result.Success = false;
 result.Message = "Không có dữ liệu WallData";
  return result;
            }

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

   double wallZ = storyData.Elevation;
            Point2D insertOffset = new Point2D(storyData.OffsetX, storyData.OffsetY);

    if (originObj is Circle circle)
   {
    insertOffset = new Point2D(circle.Center.X, circle.Center.Y);
            }

          Point2D startPt = new Point2D(lineEnt.StartPoint.X, lineEnt.StartPoint.Y);
 Point2D endPt = new Point2D(lineEnt.EndPoint.X, lineEnt.EndPoint.Y);

            var beamsAtZ = allFrames
                .Where(f => f.IsBeam && Math.Abs(f.AverageZ - wallZ) <= MappingEngine.TOLERANCE_Z)
    .ToList();

            double wallThickness = wallData.Thickness ?? 200.0;
        var mapResult = MappingEngine.FindMappings(
                startPt, endPt, wallZ, beamsAtZ,
      insertOffset, wallThickness);

            mapResult.WallHandle = lineId.Handle.ToString();

 // ⚠️ CLEAN: Đọc pattern từ Loads list
       string targetLoadPattern = wallData.GetPrimaryLoadPattern();

            // Cache tải từ SAP vào Loads list (tạm thời)
            var sapLoadsCache = new Dictionary<string, double>();
       foreach (var m in mapResult.Mappings)
            {
      if (m.TargetFrame == "New") continue;
   var detailed = SapUtils.GetFrameDistributedLoadsDetailed(m.TargetFrame);
      foreach (var kv in detailed)
    {
       string pattern = kv.Key;
    var entries = kv.Value;
         double total = entries.Sum(e => e.Value);
if (!sapLoadsCache.ContainsKey(pattern))
        sapLoadsCache[pattern] = 0;
 sapLoadsCache[pattern] += total;
  }
         }

        wallData.BaseZ = wallZ;
   wallData.Height = storyData.StoryHeight;

     // Update mappings
  wallData.Mappings = mapResult.Mappings;

          XDataUtils.UpdateElementData(lineEnt, wallData, tr);

         // Detect change: so sánh tải CAD với SAP
       bool sapChanged = false;
            double? sapTotal = sapLoadsCache.ContainsKey(targetLoadPattern) ? sapLoadsCache[targetLoadPattern] : (double?)null;
    double cadLoadValue = wallData.GetPrimaryLoadValue();

            if (sapTotal.HasValue && cadLoadValue > 0 && Math.Abs(cadLoadValue - sapTotal.Value) > 0.01)
            {
     sapChanged = true;
     result.OldLoadValue = cadLoadValue;
     result.NewLoadValue = sapTotal.Value;
            }

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

LabelUtils.UpdateWallLabels(lineId, wallData, mapResult, tr);

     if (mapResult.HasMapping)
  {
                var firstMap = mapResult.Mappings.First();
                if (sapChanged)
       {
        result.Message = $"-> {firstMap.TargetFrame} [{targetLoadPattern}] (changed)";
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
         try
           {
     DBObject obj = tr.GetObject(kvp.Key, OpenMode.ForRead);
    if (obj == null) continue;

 if (!XDataUtils.HasDtsData(obj))
             continue;

      var state = kvp.Value;
      if (!stats.ContainsKey(state)) stats[state] = 0;
        stats[state]++;

      int color = SyncEngine.GetSyncStateColor(state);
    Entity ent = tr.GetObject(kvp.Key, OpenMode.ForWrite) as Entity;
       if (ent != null) ent.ColorIndex = color;
          }
         catch { }
    }
  });

         int synced = stats.TryGetValue(SyncState.Synced, out var tmp) ? tmp : 0;
            int cadModified = stats.TryGetValue(SyncState.CadModified, out tmp) ? tmp : 0;
       int sapModified = stats.TryGetValue(SyncState.SapModified, out tmp) ? tmp : 0;
            int conflict = stats.TryGetValue(SyncState.Conflict, out tmp) ? tmp : 0;
            int sapDeleted = stats.TryGetValue(SyncState.SapDeleted, out tmp) ? tmp : 0;
            int newElement = stats.TryGetValue(SyncState.NewElement, out tmp) ? tmp : 0;

     WriteMessage("\n[DONE] Trạng thái đồng bộ:");
 if (synced > 0) WriteMessage($" Đã đồng bộ (Xanh lá): {synced}");
            if (cadModified > 0) WriteMessage($" CAD thay đổi (Cyan): {cadModified}");
          if (sapModified > 0) WriteMessage($" SAP thay đổi (Xanh): {sapModified}");
            if (conflict > 0) WriteMessage($" Xung đột (Magenta): {conflict}");
      if (sapDeleted > 0) WriteMessage($" Frame bị xóa (Đỏ): {sapDeleted}");
        if (newElement > 0) WriteMessage($" Phần tử mới (Vàng): {newElement}");
        }

 #endregion
    }
}