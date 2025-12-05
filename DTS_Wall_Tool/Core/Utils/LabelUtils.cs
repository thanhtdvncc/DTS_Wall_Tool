using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Interfaces;
using DTS_Wall_Tool.Core.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Utils
{
    /// <summary>
  /// Tiện ích chuẩn bị nội dung Label cho việc hiển thị. 
    /// LabelUtils là "quản lý" - chuẩn bị nội dung, màu sắc. 
    /// LabelPlotter là "công nhân" - vẽ theo yêu cầu.
    /// 
    /// ⚠️ CLEAN ARCHITECTURE (v2.1+):
    /// - Đọc tải trọng từ List&lt;LoadDefinition&gt; Loads (ILoadBearing)
    /// - Không còn đọc từ LoadValue, LoadPattern riêng lẻ
    /// - Sử dụng UnitManager để hiển thị đơn vị đúng
    /// </summary>
    public static class LabelUtils
    {
private const double TEXT_HEIGHT_MAIN = 120.0;
    private const double TEXT_HEIGHT_SUB = 100.0;
   private const string LABEL_LAYER = "dts_frame_label";

     #region Main API - Universal Element Label

   /// <summary>
    /// Làm mới nhãn cho bất kỳ phần tử nào (Wall, Column, Beam, Slab...)
        /// </summary>
        public static bool RefreshEntityLabel(ObjectId entityId, Transaction tr)
  {
          Entity ent = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
        if (ent == null) return false;

            var elementData = XDataUtils.ReadElementData(ent);
    if (elementData == null)
            {
     return false;
        }

            if (elementData.ElementType == ElementType.Unknown)
        {
     try
             {
    AcadUtils.Ed.WriteMessage($"\n[WARN] Đối tượng {entityId.Handle} có dữ liệu DTS nhưng không xác định loại.\n");
        }
     catch { }
      return false;
   }

   switch (elementData.ElementType)
          {
   case ElementType.Wall:
               var wallData = elementData as WallData;
         if (wallData != null)
       {
        var mapResult = CreateMappingResultFromWallData(wallData, entityId);
     UpdateWallLabels(entityId, wallData, mapResult, tr);
   return true;
 }
              break;

       case ElementType.Column:
var columnData = elementData as ColumnData;
      if (columnData != null)
        {
      UpdateColumnLabels(entityId, columnData, tr);
     return true;
      }
         break;

       case ElementType.Beam:
      var beamData = elementData as BeamData;
   if (beamData != null)
 {
      UpdateBeamLabels(entityId, beamData, tr);
           return true;
   }
                    break;

    case ElementType.Slab:
          var slabData = elementData as SlabData;
         if (slabData != null)
           {
      UpdateSlabLabels(entityId, slabData, tr);
      return true;
     }
       break;

             // ...existing code for other element types...
           case ElementType.Foundation:
       var foundationData = elementData as FoundationData;
            if (foundationData != null)
         {
 UpdateGenericLabel(entityId, ent, "Móng", foundationData.FoundationType ?? "Foundation", tr);
       return true;
        }
               break;

    case ElementType.ShearWall:
        var shearWallData = elementData as ShearWallData;
             if (shearWallData != null)
        {
        string wallType = shearWallData.WallType ?? $"SW{shearWallData.Thickness ?? 0:0}";
           UpdateGenericLabel(entityId, ent, "Vách", wallType, tr);
         return true;
      }
     break;

        case ElementType.Stair:
         var stairData = elementData as StairData;
   if (stairData != null)
  {
        string stairInfo = stairData.NumberOfSteps.HasValue ? $"{stairData.StairType} ({stairData.NumberOfSteps}b)" : stairData.StairType;
  UpdateGenericLabel(entityId, ent, "Cầu thang", stairInfo, tr);
         return true;
   }
    break;

     case ElementType.Pile:
          var pileData = elementData as PileData;
     if (pileData != null)
                 {
            string pileType = pileData.PileType ?? $"D{pileData.Diameter ?? 0:0}";
       UpdateGenericLabel(entityId, ent, "Cọc", pileType, tr);
   return true;
  }
      break;

    case ElementType.Lintel:
             var lintelData = elementData as LintelData;
        if (lintelData != null)
     {
    string lintelType = lintelData.LintelType ?? $"L{lintelData.Width ?? 0:0}x{lintelData.Height ?? 0:0}";
      UpdateGenericLabel(entityId, ent, "Lanh tô", lintelType, tr);
       return true;
                }
     break;

 case ElementType.Rebar:
        var rebarData = elementData as RebarData;
     if (rebarData != null)
   {
    string rebarMark = rebarData.RebarMark ?? $"D{rebarData.Diameter ?? 0:0}";
             string qtyStr = rebarData.Quantity.HasValue ? $"x{rebarData.Quantity.Value}" : "";
         UpdateGenericLabel(entityId, ent, "Cốt thép", $"{rebarMark}{qtyStr}", tr);
       return true;
    }
 break;

    default:
 return false;
      }

       return false;
        }

        private static MappingResult CreateMappingResultFromWallData(WallData wData, ObjectId wallId)
        {
   var mapResult = new MappingResult
   {
     WallHandle = wallId.Handle.ToString(),
       Mappings = wData.Mappings ?? new List<MappingRecord>()
          };

          if (mapResult.Mappings.Count > 0)
            {
                double totalCovered = mapResult.Mappings
    .Where(m => m.TargetFrame != "New")
           .Sum(m => m.CoveredLength);

mapResult.WallLength = totalCovered > 0 ? totalCovered : 1000;
            }

return mapResult;
        }

        #endregion

      #region Wall Labels

    /// <summary>
        /// Cập nhật nhãn cho Tường.
        /// 
        /// ⚠️ CLEAN ARCHITECTURE:
        /// - Đọc tải từ Loads list (ILoadBearing)
        /// - Sử dụng GetPrimaryLoadValue() và GetPrimaryLoadPattern()
        /// - Hiển thị đơn vị từ UnitManager
        /// 
        /// Format:
        ///   Dòng trên: [Handle] W200 DL=7.20 kN/m
        ///   Dòng dưới: to B15 I=0.0to3.5 hoặc to B15 (full 9m)
        /// </summary>
        public static void UpdateWallLabels(ObjectId wallId, WallData wData, MappingResult mapResult, Transaction tr)
{
            Entity ent = tr.GetObject(wallId, OpenMode.ForRead) as Entity;
          if (ent == null) return;

      Point3d pStart, pEnd;
         if (ent is Line line)
       {
       pStart = line.StartPoint;
         pEnd = line.EndPoint;
  }
     else return;

 // Xác định màu theo trạng thái mapping
     int statusColor = mapResult.GetColorIndex();

  // ========== DÒNG TRÊN: [Handle] W200 DL=7.20 kN/m ==========
            string handleText = FormatColor($"[{wallId.Handle}]", statusColor);
            string wallType = wData.WallType ?? $"W{wData.Thickness ?? 200:0}";

       // ⚠️ CLEAN: Đọc từ Loads list thay vì LoadValue/LoadPattern
            double loadValue = wData.GetPrimaryLoadValue();
          string loadPattern = wData.GetPrimaryLoadPattern();
  string loadUnit = UnitManager.Info.GetLineLoadUnit(); // "kN/m"

   string loadText = $"{wallType} {loadPattern}={loadValue:0.00} {loadUnit}";
            string topContent = $"{handleText} {{\\C7;{loadText}}}";

  // ========== DÒNG DƯỚI: to B15 I=0.0to3.5 + [SAP:...] ==========
  string botContent = GetMappingText(mapResult, loadPattern);

    // ========== DÒNG THỨ 3 (tùy chọn): Hiển thị các loadcase khác ==========
    string thirdContent = null;
            if (wData.HasLoads && wData.Loads.Count > 1)
            {
        // Lọc bỏ loadcase đầu tiên (đã hiển thị ở dòng trên)
                var otherLoads = wData.Loads.Skip(1).Take(3).ToList();
                if (otherLoads.Count > 0)
     {
      var displayCases = otherLoads.Select(l => $"{l.Pattern}={l.Value:0.00}");
    string moreText = wData.Loads.Count > 4 ? $" +{wData.Loads.Count - 4}" : "";
 thirdContent = FormatColor($"[{string.Join(", ", displayCases)}{moreText}]", 8); // Gray
                }
            }

   // Lấy BlockTableRecord để vẽ
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
  ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            // Vẽ labels
       LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, topContent,
          LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);

            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, botContent,
            LabelPosition.MiddleBottom, TEXT_HEIGHT_SUB, LABEL_LAYER);

          // Vẽ dòng thứ 3 nếu có nhiều loadcase
            if (!string.IsNullOrEmpty(thirdContent))
          {
     var midPt = new Point2D((pStart.X + pEnd.X) / 2.0, (pStart.Y + pEnd.Y) / 2.0);
       var perpDir = new Point2D(-(pEnd.Y - pStart.Y), pEnd.X - pStart.X);
       perpDir = perpDir.Normalized;

     var thirdPos = new Point2D(
              midPt.X + perpDir.X * (TEXT_HEIGHT_SUB + 150),
          midPt.Y + perpDir.Y * (TEXT_HEIGHT_SUB + 150)
       );

          LabelPlotter.PlotPointLabel(btr, tr, thirdPos, thirdContent, TEXT_HEIGHT_SUB * 0.8, LABEL_LAYER);
            }
        }

        #endregion

        #region Column Labels

        public static void UpdateColumnLabels(ObjectId columnId, ColumnData cData, Transaction tr)
        {
    Entity ent = tr.GetObject(columnId, OpenMode.ForRead) as Entity;
            if (ent == null) return;

            Point3d startPt, endPt;

        if (ent is Line line)
  {
     startPt = line.StartPoint;
    endPt = line.EndPoint;
            }
else
     {
      Point3d center = AcadUtils.GetEntityCenter3d(ent);
    startPt = center;
  endPt = new Point3d(center.X, center.Y, center.Z + 1.0);
      }

  int statusColor = cData.HasMapping ? 3 : 1;

      string handleText = FormatColor($"[{columnId.Handle}]", statusColor);
  string columnType = cData.ColumnType ?? $"C{cData.Width ?? 400:0}x{cData.Depth ?? 400:0}";
            string content = $"{handleText} {{\\C7;{columnType}}}";

      BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
                ent.Database.CurrentSpaceId, OpenMode.ForWrite);

  LabelPlotter.PlotLabel(btr, tr, startPt, endPt, content, LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);
        }

        #endregion

        #region Beam Labels

        /// <summary>
   /// Cập nhật nhãn cho Dầm.
        /// ⚠️ CLEAN: Hiển thị tải từ Loads list nếu có.
        /// </summary>
        public static void UpdateBeamLabels(ObjectId beamId, BeamData bData, Transaction tr)
 {
        Entity ent = tr.GetObject(beamId, OpenMode.ForRead) as Entity;
         if (ent == null) return;

    Point3d pStart, pEnd;
        if (ent is Line line)
          {
         pStart = line.StartPoint;
    pEnd = line.EndPoint;
            }
 else return;

 int statusColor = bData.HasMapping ? 3 : 1;

    string handleText = FormatColor($"[{beamId.Handle}]", statusColor);

            string beamType;
            if (!string.IsNullOrEmpty(bData.SectionName))
          {
         beamType = bData.SectionName;
    }
 else if (bData.Width.HasValue && bData.Depth.HasValue)
            {
   beamType = $"B{bData.Width:0}x{bData.Depth:0}";
            }
      else
     {
                beamType = "Beam";
 }

// Hiển thị tải nếu có (từ Loads list)
    string loadText = "";
     if (bData.HasLoads)
     {
     var firstLoad = bData.Loads.First();
    loadText = $" {firstLoad.Pattern}={firstLoad.Value:0.00}";
      }

    string content = $"{handleText} {{\\C7;{beamType}{loadText}}}";

            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
   ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, content,
                LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);
 }

    #endregion

    #region Slab Labels

   /// <summary>
        /// Cập nhật nhãn cho Sàn.
        /// ⚠️ CLEAN: Hiển thị tải từ Loads list (kN/m²).
        /// </summary>
        public static void UpdateSlabLabels(ObjectId slabId, SlabData sData, Transaction tr)
        {
            Entity ent = tr.GetObject(slabId, OpenMode.ForRead) as Entity;
 if (ent == null) return;

            Point2D center;
            if (ent is Polyline pline && pline.Closed)
  {
        double sumX = 0, sumY = 0;
  int count = pline.NumberOfVertices;
       for (int i = 0; i < count; i++)
         {
       var pt = pline.GetPoint2dAt(i);
           sumX += pt.X;
       sumY += pt.Y;
   }
          center = new Point2D(sumX / count, sumY / count);
    }
   else return;

            int statusColor = sData.HasMapping ? 3 : 1;

    string handleText = FormatColor($"[{slabId.Handle}]", statusColor);
 string slabType = sData.SlabName ?? $"S{sData.Thickness ?? 120:0}";

  // Hiển thị tải từ Loads list
            string loadText = "";
     if (sData.HasLoads)
            {
  double totalLoad = sData.Loads.Sum(l => l.Value);
    loadText = $" {totalLoad:0.00} {UnitManager.Info.GetAreaLoadUnit()}";
       }

            string content = $"{handleText} {{\\C7;{slabType}{loadText}}}";

       BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
     ent.Database.CurrentSpaceId, OpenMode.ForWrite);

     LabelPlotter.PlotPointLabel(btr, tr, center, content, TEXT_HEIGHT_MAIN, LABEL_LAYER);
        }

    #endregion

      #region Generic Element Labels

        private static void UpdateGenericLabel(ObjectId elemId, Entity ent, string typeName, string typeDetail, Transaction tr)
        {
     Point2D center;

 if (ent is Line line)
            {
          var pStart = new Point2D(line.StartPoint.X, line.StartPoint.Y);
   var pEnd = new Point2D(line.EndPoint.X, line.EndPoint.Y);
     center = new Point2D((pStart.X + pEnd.X) / 2.0, (pStart.Y + pEnd.Y) / 2.0);
     }
            else if (ent is Circle circle)
 {
  center = new Point2D(circle.Center.X, circle.Center.Y);
 }
            else if (ent is Polyline pline)
     {
                double sumX = 0, sumY = 0;
        int count = pline.NumberOfVertices;
       for (int i = 0; i < count; i++)
      {
         var pt = pline.GetPoint2dAt(i);
           sumX += pt.X;
        sumY += pt.Y;
  }
         center = new Point2D(sumX / count, sumY / count);
  }
          else
            {
      center = AcadUtils.GetEntityCenter(ent);
   }

  var elemData = XDataUtils.ReadElementData(ent);
   int statusColor = (elemData != null && elemData.HasMapping) ? 3 : 1;

         string handleText = FormatColor($"[{elemId.Handle}]", statusColor);
            string content = $"{handleText} {{\\C7;{typeName} {typeDetail}}}";

         BlockTableRecord btr = (BlockTableRecord)tr.GetObject(
     ent.Database.CurrentSpaceId, OpenMode.ForWrite);

            if (ent is Line lineEnt)
          {
       LabelPlotter.PlotLabel(btr, tr, lineEnt.StartPoint, lineEnt.EndPoint, content,
          LabelPosition.MiddleTop, TEXT_HEIGHT_MAIN, LABEL_LAYER);
            }
   else
            {
              LabelPlotter.PlotPointLabel(btr, tr, center, content, TEXT_HEIGHT_MAIN, LABEL_LAYER);
            }
        }

        #endregion

      #region Content Formatting

        public static string FormatColor(string text, int colorIndex)
  {
    return $"{{\\C{colorIndex};{text}}}";
        }

        public static string GetMappingText(MappingResult res, string loadPattern = "DL")
        {
   if (!res.HasMapping)
     return FormatColor("to New", 1);

            var map = res.Mappings.First();
      if (map.TargetFrame == "New")
 return FormatColor("to New", 1);

    var detailed = SapUtils.GetFrameDistributedLoadsDetailed(map.TargetFrame);

  var lines = new List<string>();

   bool hasAnyLoad = detailed != null && detailed.Count > 0 && detailed.Values.Any(v => v != null && v.Count > 0);
            if (!hasAnyLoad)
       {
   string header = $"to {map.TargetFrame}";
     if (map.MatchType == "FULL" || map.MatchType == "EXACT")
             header += $" (full {map.CoveredLength / 1000.0:0.#}m)";
      else
  header += $" I={map.DistI / 1000.0:0.0}to{map.DistJ / 1000.0:0.0}";
             lines.Add(header);
     }
            else
            {
     int maxPatterns = 5;
                int count = 0;
    foreach (var kv in detailed)
       {
      if (count++ >= maxPatterns) { lines.Add("+more"); break; }

     string pattern = kv.Key;
         var entries = kv.Value;
                double total = entries.Sum(e => e.Value);

          var segs = new List<string>();
               foreach (var e in entries)
    {
               foreach (var s in e.Segments)
{
          double i = s.I / 1000.0;
       double j = s.J / 1000.0;
           if (Math.Abs(i - 0) < 0.001 && Math.Abs(j - (map.FrameLength / 1000.0)) < 0.001)
           segs.Add($"full {map.FrameLength / 1000.0:0.#}m");
        else
   segs.Add($"{i:0.0}to{j:0.0}");
        }
      }

              string segText = segs.Count > 0 ? $" ({string.Join(",", segs)})" : "";
       lines.Add($"{map.TargetFrame}: {pattern}={total:0.00}{UnitManager.Info.GetLineLoadUnit()}{segText}");
                }
}

   int color = res.GetColorIndex();
string content = string.Join("\\P", lines.Select(l => FormatColor(l, color)));
            return content;
        }

        #endregion
    }
}