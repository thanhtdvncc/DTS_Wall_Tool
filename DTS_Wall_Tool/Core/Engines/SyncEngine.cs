using Autodesk.AutoCAD.DatabaseServices;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Interfaces;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Engines
{
    /// <summary>
    /// Kết quả đồng bộ cho một phần tử
    /// </summary>
    public class SyncResult
    {
    public string Handle { get; set; }
        public SyncState State { get; set; }
     public string Message { get; set; }
    public bool Success { get; set; }

        // Thông tin thay đổi
        public string OldFrameName { get; set; }
        public string NewFrameName { get; set; }
        public double? OldLoadValue { get; set; }
      public double? NewLoadValue { get; set; }
    }

    /// <summary>
    /// Engine đồng bộ 2 chiều giữa AutoCAD và SAP2000.
    /// 
 /// ⚠️ CLEAN ARCHITECTURE (v2.1+):
    /// - Chỉ làm việc với ILoadBearing interface
    /// - Không còn code xử lý legacy (LoadValue, LoadPattern riêng lẻ)
    /// - Tất cả tải trọng đọc từ List&lt;LoadDefinition&gt; Loads
    /// 
    /// ⚠️ QUY TRÌNH ĐỒNG BỘ:
  /// 1. PULL: Đọc thay đổi từ SAP → Cập nhật Loads trong CAD
  /// 2. PUSH: Ghi Loads từ CAD → SAP (theo LoadType)
    /// 3. DETECT: Phát hiện xung đột và thay đổi
    /// </summary>
    public static class SyncEngine
    {
        #region Configuration

        /// <summary>Tự động tạo frame mới trong SAP khi mapping = NEW</summary>
        public static bool AutoCreateSapFrame = false;

     /// <summary>Cho phép ghi đè tải trọng trong SAP</summary>
        public static bool AllowOverwriteSapLoad = true;

        /// <summary>Load pattern mặc định</summary>
        public static string DefaultLoadPattern = "DL";

        #endregion

        #region PULL: SAP → CAD

        /// <summary>
     /// Đồng bộ thay đổi từ SAP2000 vào CAD (PULL).
        /// - Phát hiện frame bị xóa/chia/merge
        /// - Cập nhật Loads từ SAP
        /// </summary>
        public static List<SyncResult> PullFromSap(List<ObjectId> elementIds, Transaction tr)
      {
            var results = new List<SyncResult>();

          if (!SapUtils.IsConnected)
{
          SapUtils.Connect(out _);
              if (!SapUtils.IsConnected)
       {
         results.Add(new SyncResult
       {
  Success = false,
       Message = "Không thể kết nối SAP2000"
         });
 return results;
      }
      }

   foreach (ObjectId elemId in elementIds)
      {
     var result = PullSingleElement(elemId, tr);
            if (result != null)
         {
           results.Add(result);
           }
     }

        return results;
        }

     /// <summary>
      /// Đồng bộ một phần tử từ SAP
        /// </summary>
        private static SyncResult PullSingleElement(ObjectId elemId, Transaction tr)
        {
            var result = new SyncResult
          {
    Handle = elemId.Handle.ToString(),
       Success = true
            };

         try
       {
                Entity ent = tr.GetObject(elemId, OpenMode.ForWrite) as Entity;
           if (ent == null) return null;

    var elemData = XDataUtils.ReadElementData(ent);
       if (elemData == null || !elemData.HasMapping)
             {
     result.State = SyncState.NotSynced;
          result.Message = "Chưa có mapping";
         return result;
       }

       // Kiểm tra từng mapping
       foreach (var mapping in elemData.Mappings.ToList())
{
       if (mapping.TargetFrame == "New") continue;

     // Kiểm tra frame còn tồn tại không
  if (!SapUtils.FrameExists(mapping.TargetFrame))
          {
      result.State = SyncState.SapDeleted;
          result.OldFrameName = mapping.TargetFrame;

       // Thử tìm frame thay thế
    if (ent is Line line)
        {
         var startPt = new Point2D(line.StartPoint.X, line.StartPoint.Y);
       var endPt = new Point2D(line.EndPoint.X, line.EndPoint.Y);
          double elevation = elemData.BaseZ ?? 0;

          string newFrame = SapUtils.FindReplacementFrame(startPt, endPt, elevation);
 if (!string.IsNullOrEmpty(newFrame))
    {
      mapping.TargetFrame = newFrame;
   result.NewFrameName = newFrame;
       result.State = SyncState.SapModified;
        result.Message = $"Frame đã đổi: {result.OldFrameName} → {newFrame}";
      }
          else
                {
           mapping.TargetFrame = "New";
result.Message = $"Frame {result.OldFrameName} đã bị xóa";
              }
         }
 }
          else
         {
               // Frame vẫn tồn tại - kiểm tra tải trọng
 var sapLoads = SapUtils.GetFrameDistributedLoads(mapping.TargetFrame, DefaultLoadPattern);

      if (sapLoads.Count > 0)
     {
        var sapLoad = sapLoads.First();
         result.NewLoadValue = sapLoad.LoadValue;

          // ⚠️ CLEAN: Chỉ làm việc với ILoadBearing
   if (elemData is ILoadBearing loadBearing)
     {
  var firstLoad = loadBearing.HasLoads ? loadBearing.Loads.FirstOrDefault() : null;
        result.OldLoadValue = firstLoad?.Value;

     if (firstLoad != null && Math.Abs(firstLoad.Value - sapLoad.LoadValue) > 0.01)
           {
    result.State = SyncState.SapModified;
       result.Message = $"Tải SAP: {sapLoad.LoadValue:0.00} (CAD: {firstLoad.Value:0.00})";
           }
           else
  {
 result.State = SyncState.Synced;
        }
     }
   }
        }
      }

      // Lưu thay đổi
     XDataUtils.WriteElementData(ent, elemData, tr);
    }
            catch (Exception ex)
    {
            result.Success = false;
        result.Message = ex.Message;
            }

      return result;
        }

        #endregion

        #region PUSH: CAD → SAP

  /// <summary>
        /// Đồng bộ thay đổi từ CAD vào SAP2000 (PUSH).
        /// - Gán tải trọng từ Loads list
 /// - Phân luồng theo LoadType (DistributedLine/UniformArea/Point)
   /// </summary>
        public static List<SyncResult> PushToSap(List<ObjectId> elementIds, string loadPattern, Transaction tr)
   {
  var results = new List<SyncResult>();

         if (!SapUtils.IsConnected)
  {
        SapUtils.Connect(out _);
                if (!SapUtils.IsConnected)
  {
        results.Add(new SyncResult
          {
      Success = false,
            Message = "Không thể kết nối SAP2000"
  });
      return results;
    }
     }

       // Kiểm tra load pattern tồn tại trong SAP
            if (!SapUtils.LoadPatternExists(loadPattern))
         {
  results.Add(new SyncResult
  {
         Success = false,
                 Message = $"Load pattern '{loadPattern}' không tồn tại trong SAP"
     });
 return results;
            }

            foreach (ObjectId elemId in elementIds)
 {
         var result = PushSingleElement(elemId, loadPattern, tr);
        if (result != null)
    {
    results.Add(result);
      }
          }

 SapUtils.RefreshView();
     return results;
        }

        /// <summary>
        /// Gán tải một phần tử vào SAP.
        /// 
        /// ⚠️ CLEAN ARCHITECTURE:
     /// - Chỉ làm việc với ILoadBearing interface
     /// - Nếu chưa có tải → tự động gọi CalculateLoads()
     /// - Phân luồng theo LoadType
     /// </summary>
  private static SyncResult PushSingleElement(ObjectId elemId, string defaultPattern, Transaction tr)
        {
 var result = new SyncResult
   {
       Handle = elemId.Handle.ToString()
            };

try
            {
       DBObject obj = tr.GetObject(elemId, OpenMode.ForRead);
 var elemData = XDataUtils.ReadElementData(obj);

  if (elemData == null)
          {
        result.Success = false;
          result.Message = "Không có dữ liệu DTS";
        return result;
      }

   // ⚠️ CLEAN: Chỉ làm việc với ILoadBearing
            if (!(elemData is ILoadBearing loadItem))
       {
        result.Success = false;
      result.Message = "Phần tử không hỗ trợ gán tải (không phải ILoadBearing)";
             return result;
       }

       // Nếu chưa có tải → tự động tính toán
       if (!loadItem.HasLoads)
           {
   loadItem.CalculateLoads();
      }

        if (!loadItem.HasLoads)
     {
        result.Success = false;
result.Message = "Không có tải trọng để gán (CalculateLoads không tạo được tải)";
 return result;
     }

       if (!elemData.HasMapping)
                {
                 result.Success = false;
         result.State = SyncState.NewElement;
        result.Message = "Chưa mapping với SAP";
   return result;
    }

    // Thực hiện gán tải (Duyệt qua danh sách Loads)
           int successCount = 0;
      int totalOps = 0;
   var messages = new List<string>();

           foreach (var load in loadItem.Loads)
     {
                    // Lọc mapping phù hợp với loại target
      var validMappings = elemData.Mappings
       .Where(m => m.TargetFrame != "New")
    .Where(m => string.IsNullOrEmpty(m.TargetType) || 
       m.TargetType.Equals(load.TargetElement, StringComparison.OrdinalIgnoreCase))
     .ToList();

        foreach (var mapping in validMappings)
        {
         totalOps++;
     bool assigned = false;

 // PHÂN LUỒNG XỬ LÝ THEO LOẠI TẢI
switch (load.Type)
          {
     case LoadType.DistributedLine:
         // Gán tải phân bố lên Frame (Dầm/Cột)
string pattern = !string.IsNullOrEmpty(load.Pattern) ? load.Pattern : defaultPattern;
       
    assigned = SapUtils.ReplaceFrameLoad(
         mapping.TargetFrame,
        pattern,
    load.Value,
           load.DistI > 0 ? load.DistI : mapping.DistI,
         load.DistJ > 0 ? load.DistJ : mapping.DistJ
        );
     
    if (assigned)
          messages.Add($"{mapping.TargetFrame}[{pattern}]={load.Value:0.00}");
         break;

         case LoadType.UniformArea:
    // TODO: Triển khai SapUtils.AssignAreaLoad cho Slab/ShearWall
    messages.Add($"[SKIP] Area load: {load}");
             break;

      case LoadType.Point:
      // TODO: Triển khai SapUtils.AssignPointLoad
      messages.Add($"[SKIP] Point load: {load}");
         break;
         }

            if (assigned) successCount++;
     }
        }

  result.Success = successCount > 0;
   result.State = result.Success ? SyncState.Synced : SyncState.CadModified;

                // Tạo thông báo chi tiết
     if (successCount > 0)
                {
        result.Message = $"Gán {successCount}/{totalOps}: {string.Join(", ", messages.Take(3))}";
         if (messages.Count > 3) result.Message += $" (+{messages.Count - 3} more)";
       }
else
    {
               result.Message = $"Không gán được tải ({totalOps} attempts)";
    }

                // Cache giá trị để hiển thị
            result.NewLoadValue = loadItem.Loads.FirstOrDefault()?.Value ?? 0;
        }
    catch (Exception ex)
     {
          result.Success = false;
                result.Message = $"Lỗi: {ex.Message}";
            }

            return result;
        }

        #endregion

     #region DETECT: Phát hiện thay đổi

        /// <summary>
        /// Phát hiện trạng thái đồng bộ của phần tử
  /// </summary>
        public static SyncState DetectSyncState(ObjectId elemId, Transaction tr)
        {
       try
       {
       DBObject obj = tr.GetObject(elemId, OpenMode.ForRead);
         var elemData = XDataUtils.ReadElementData(obj);

       if (elemData == null)
    return SyncState.NotSynced;

                if (!elemData.HasMapping)
     return SyncState.NewElement;

    // ⚠️ CLEAN: Chỉ làm việc với ILoadBearing
 if (!(elemData is ILoadBearing loadBearing))
        return SyncState.NotSynced;

    // Kiểm tra từng mapping
   foreach (var mapping in elemData.Mappings)
      {
if (mapping.TargetFrame == "New")
     continue;

    // Frame có tồn tại không?
                if (!SapUtils.FrameExists(mapping.TargetFrame))
         return SyncState.SapDeleted;

    // Lấy giá trị tải CAD
     double? cadLoadValue = loadBearing.HasLoads 
                 ? loadBearing.Loads.FirstOrDefault()?.Value 
         : null;

// Xác định pattern để kiểm tra
   string pattern = DefaultLoadPattern;
         if (loadBearing.HasLoads && !string.IsNullOrEmpty(loadBearing.Loads[0].Pattern))
               pattern = loadBearing.Loads[0].Pattern;

       // Lấy tải từ SAP
     var sapLoads = SapUtils.GetFrameDistributedLoads(mapping.TargetFrame, pattern);
        double sapTotal = sapLoads.Sum(l => l.LoadValue);
               bool sapHas = sapLoads.Count > 0;
             bool cadHas = cadLoadValue.HasValue && cadLoadValue.Value > 0;

                 if (!sapHas && cadHas)
      {
 // CAD có tải nhưng SAP không có
            return SyncState.CadModified;
        }

                    if (sapHas && !cadHas)
          {
              // SAP có tải nhưng CAD không có
   return SyncState.SapModified;
    }

        if (sapHas && cadHas)
        {
if (Math.Abs(cadLoadValue.Value - sapTotal) > 0.01)
      {
         // Giá trị khác nhau → xung đột
    return SyncState.Conflict;
    }
          }
  }

             return SyncState.Synced;
      }
          catch
            {
                return SyncState.NotSynced;
  }
}

   /// <summary>
   /// Quét và phát hiện thay đổi cho danh sách phần tử
  /// </summary>
        public static Dictionary<ObjectId, SyncState> DetectAllChanges(List<ObjectId> elementIds, Transaction tr)
 {
    var result = new Dictionary<ObjectId, SyncState>();

         foreach (var elemId in elementIds)
            {
    result[elemId] = DetectSyncState(elemId, tr);
   }

            return result;
        }

   #endregion

        #region Utility

 /// <summary>
        /// Màu hiển thị theo trạng thái đồng bộ.
        /// Dùng cho Label và highlight trong CAD.
      /// </summary>
        public static int GetSyncStateColor(SyncState state)
        {
   switch (state)
        {
                case SyncState.Synced: return 3;      // Xanh lá
      case SyncState.CadModified: return 2; // Vàng
       case SyncState.SapModified: return 5; // Xanh dương
       case SyncState.Conflict: return 6;    // Magenta
         case SyncState.SapDeleted: return 1;  // Đỏ
       case SyncState.NewElement: return 2;  // Vàng
         default: return 7; // Trắng
     }
        }

  #endregion
    }
}