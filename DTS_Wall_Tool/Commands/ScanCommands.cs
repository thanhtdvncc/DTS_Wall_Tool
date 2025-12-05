using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Utils;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Commands
{
    /// <summary>
    /// Các lệnh quét và báo cáo thông tin đối tượng
    /// </summary>
    public class ScanCommands : CommandBase
    {
        private const string SCAN_LINK_LAYER = "dts_scan_link";

        [CommandMethod("DTS_SCAN")]
        public void DTS_SCAN()
        {
            WriteMessage("\n=== DTS SCANNER: KIỂM TRA THÔNG TIN ===");
            WriteMessage("Chọn các đối tượng cần kiểm tra...");

            var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
            if (ids.Count == 0)
            {
                WriteMessage("Không có đối tượng nào được chọn.");
                return;
            }

            //1. Thu thập dữ liệu
            var scannedItems = new List<ScanItem>();
            var typeStats = new Dictionary<string, int>();
            int unknownCount = 0;

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in ids)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);

                    // Check for Story Data first
                    var storyData = XDataUtils.ReadStoryData(obj);
                    if (storyData != null)
                    {
                        scannedItems.Add(new ScanItem
                        {
                            ObjId = id,
                            Type = "ORIGIN",
                            Description = $"{storyData.StoryName} (Z={storyData.Elevation})",
                            Center = AcadUtils.GetEntityCenter3d(obj as Entity)
                        });
                        IncrementStat(typeStats, "ORIGIN");
                        continue;
                    }

                    // Check Element Data
                    var elemData = XDataUtils.ReadElementData(obj);
                    if (elemData != null)
                    {
                        string typeName = elemData.ElementType.ToString();
                        string desc = GetElementDescription(elemData);

                        scannedItems.Add(new ScanItem
                        {
                            ObjId = id,
                            Type = typeName,
                            Description = desc,
                            Center = AcadUtils.GetEntityCenter3d(obj as Entity),
                            ElemType = elemData.ElementType
                        });
                        IncrementStat(typeStats, typeName);
                    }
                    else
                    {
                        unknownCount++;
                    }
                }
            });

            //2. Hiển thị báo cáo
            if (scannedItems.Count == 0 && unknownCount == 0) return;

            WriteMessage("\n--------------------------------------------------");
            WriteMessage($" TỔNG QUAN: {ids.Count} đối tượng được chọn");
            if (unknownCount > 0) WriteMessage($" [!] {unknownCount} đối tượng chưa có dữ liệu DTS (Unknown)");

            foreach (var kvp in typeStats)
            {
                WriteMessage($" - {kvp.Key}: {kvp.Value} phần tử");
            }

            // Chế độ chi tiết nếu có scannedItems
            if (scannedItems.Count > 0)
            {
                if (scannedItems.Count <= 10)
                {
                    WriteMessage("\n CHI TIẾT:");
                    foreach (var item in scannedItems)
                    {
                        WriteMessage($" > [{item.Type}] Handle:{item.ObjId.Handle} | {item.Description}");
                    }
                }
                else
                {
                    WriteMessage($"\n (Chọn quá nhiều đối tượng, ẩn chi tiết)");
                }
            }

            WriteMessage("--------------------------------------------------");

            //3. Hỏi vẽ Link visual
            if (scannedItems.Count > 0)
            {
                // Only offer drawing when more than one scanned item
                if (scannedItems.Count > 1)
                {
                    PromptKeywordOptions pko = new PromptKeywordOptions("\nBạn có muốn vẽ tia liên kết tới các phần tử này không? [Yes/No]: ");
                    pko.Keywords.Add("Yes");
                    pko.Keywords.Add("No");
                    pko.Keywords.Default = "No";

                    PromptResult res = Ed.GetKeywords(pko);
                    if (res.Status == PromptStatus.OK && res.StringResult == "Yes")
                    {
                        PromptPointOptions ppo = new PromptPointOptions("\nChọn điểm gốc để bắn tia: ");
                        PromptPointResult ppr = Ed.GetPoint(ppo);

                        if (ppr.Status == PromptStatus.OK)
                        {
                            DrawScanLinks(ppr.Value, scannedItems);
                        }
                    }
                }
                else
                {
                    // Single scanned item - do not offer drawing
                }
            }
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

            // Confirm before clear
            var pko = new PromptKeywordOptions("\nXác nhận xóa toàn bộ dữ liệu DTS của các đối tượng này? [Yes/No]: ", "Yes No");
            if (Ed.GetKeywords(pko).StringResult != "Yes") return;

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

            WriteSuccess($"Đã xóa dữ liệu của {clearedCount} phần tử");
        }

        #region Helpers

        private class ScanItem
        {
            public ObjectId ObjId { get; set; }
            public string Type { get; set; }
            public ElementType ElemType { get; set; } = ElementType.Unknown;
            public string Description { get; set; }
            public Point3d Center { get; set; }
        }

        private void IncrementStat(Dictionary<string, int> stats, string key)
        {
            if (!stats.ContainsKey(key)) stats[key] = 0;
            stats[key]++;
        }

        private string GetElementDescription(ElementData data)
        {
            string info = "";

            // Common info
            string linkInfo = data.IsLinked ? $"Linked:{data.OriginHandle}" : "Unlinked";

            if (data is WallData w)
            {
                // ⚠️ CLEAN: Đọc từ Loads list thay vì LoadValue
                double loadValue = w.GetPrimaryLoadValue();
                info = $"{w.WallType} T={w.Thickness} Load={loadValue:0.00}";
            }
            else if (data is ColumnData c)
                info = $"{c.ColumnType} {c.Material}";
            else if (data is BeamData b)
                info = $"{b.SectionName} {b.BeamType}";
            else if (data is SlabData s)
                info = $"{s.SlabName} T={s.Thickness}";
            else
                info = data.ToString();

            return $"{info} ({linkInfo})";
        }

        /// <summary>
        /// Draw scan links. Returns count of drawn lines and prints a legend summary (color -> type).
        /// </summary>
        private int DrawScanLinks(Point3d originPt, List<ScanItem> items)
        {
            AcadUtils.CreateLayer(SCAN_LINK_LAYER, 7); // Default White

            int count = 0;
            var typeCounts = new Dictionary<string, int>();
            var typeColors = new Dictionary<string, int>();

            UsingTransaction(tr =>
            {
                var bt = (BlockTable)tr.GetObject(Db.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (var item in items)
                {
                    // Màu theo loại phần tử
                    int color = GetColorByType(item.ElemType);
                    if (item.Type == "ORIGIN") color = 1; // Red

                    // Vẽ đường line3D
                    Line line = new Line(originPt, item.Center);
                    line.Layer = SCAN_LINK_LAYER;
                    line.ColorIndex = color;

                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
                    count++;

                    // Tally
                    if (!typeCounts.ContainsKey(item.Type)) typeCounts[item.Type] = 0;
                    typeCounts[item.Type]++;
                    if (!typeColors.ContainsKey(item.Type)) typeColors[item.Type] = color;
                }
            });

            // Updated output message
            WriteSuccess($"[OK] Đã vẽ {count} đường link cho:");

            // Print concise summary
            foreach (var kv in typeColors)
            {
                int c = kv.Value;
                string cname = ColorNameFromIndex(c);
                int cnt = typeCounts.ContainsKey(kv.Key) ? typeCounts[kv.Key] : 0;
                WriteMessage($" - {kv.Key}: ({cname}) - {cnt} đường");
            }

            return count;
        }

        private int GetColorByType(ElementType type)
        {
            switch (type)
            {
                case ElementType.Wall: return 1; // Red
                case ElementType.Column: return 2; // Yellow
                case ElementType.Beam: return 3; // Green
                case ElementType.Slab: return 4; // Cyan
                case ElementType.StoryOrigin: return 1;
                default: return 7; // White
            }
        }

        private string ColorNameFromIndex(int idx)
        {
            switch (idx)
            {
                case 1: return "Red";
                case 2: return "Yellow";
                case 3: return "Green";
                case 4: return "Cyan";
                case 7: return "White";
                default: return "Custom";
            }
        }

        #endregion
    }
}