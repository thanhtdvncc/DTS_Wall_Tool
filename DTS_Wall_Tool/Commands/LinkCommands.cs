using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Utils;

namespace DTS_Wall_Tool.Commands
{
    /// <summary>
    /// Các lệnh quản lý liên kết (Link) giữa phần tử và Origin
    /// </summary>
    public class LinkCommands : CommandBase
    {
        [CommandMethod("DTS_LINK")]
        public void DTS_LINK()
        {
            WriteMessage("=== LIÊN KẾT PHẦN TỬ VỚI ORIGIN ===");

            // 1. Chọn Origin
            PromptEntityOptions originOpt = new PromptEntityOptions("\nChọn Origin (Circle trên layer dts_origin): ");
            originOpt.SetRejectMessage("\nChỉ chấp nhận Circle.");
            originOpt.AddAllowedClass(typeof(Circle), true);

            PromptEntityResult originRes = Ed.GetEntity(originOpt);
            if (originRes.Status != PromptStatus.OK)
            {
                WriteMessage("Đã hủy lệnh.");
                return;
            }

            ObjectId originId = originRes.ObjectId;

            // 2.  Chọn các phần tử cần link
            var elementIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (elementIds.Count == 0)
            {
                WriteMessage("Không có phần tử nào được chọn.");
                return;
            }

            WriteMessage($"Đã chọn {elementIds.Count} phần tử.");

            int linkedCount = 0;
            int skippedCount = 0;
            int createdCount = 0;

            UsingTransaction(tr =>
            {
                DBObject originObj = tr.GetObject(originId, OpenMode.ForWrite);
                StoryData storyData = XDataUtils.ReadStoryData(originObj);

                if (storyData == null)
                {
                    WriteError("Origin chưa được thiết lập.  Chạy DTS_SET_ORIGIN trước.");
                    return;
                }

                WriteMessage($"Origin: {storyData.StoryName}, Z={storyData.Elevation:0}mm");

                foreach (ObjectId elemId in elementIds)
                {
                    Entity ent = tr.GetObject(elemId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    // Đọc ElementData
                    ElementData elemData = XDataUtils.ReadElementData(ent);

                    if (elemData == null)
                    {
                        elemData = CreateElementDataForEntity(ent);
                        if (elemData == null)
                        {
                            skippedCount++;
                            continue;
                        }
                        createdCount++;
                    }

                    if (elemData.IsLinked && elemData.OriginHandle == originId.Handle.ToString())
                    {
                        skippedCount++;
                        continue;
                    }

                    // Thực hiện link
                    elemData.OriginHandle = originId.Handle.ToString();
                    elemData.BaseZ = storyData.Elevation;
                    elemData.Height = storyData.StoryHeight;

                    XDataUtils.WriteElementData(ent, elemData, tr);

                    string childHandle = elemId.Handle.ToString();
                    if (!storyData.ChildHandles.Contains(childHandle))
                    {
                        storyData.ChildHandles.Add(childHandle);
                    }

                    linkedCount++;
                    WriteMessage($"  [{elemId.Handle}] {elemData.ElementType} -> Linked");
                }

                XDataUtils.WriteStoryData(originObj, storyData, tr);
            });

            WriteMessage($"\nKết quả: {linkedCount} linked, {createdCount} created, {skippedCount} skipped");
        }

        [CommandMethod("DTS_UNLINK")]
        public void DTS_UNLINK()
        {
            WriteMessage("=== XÓA LIÊN KẾT PHẦN TỬ ===");

            var elementIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
            if (elementIds.Count == 0)
            {
                WriteMessage("Không có phần tử nào được chọn.");
                return;
            }

            int unlinkedCount = 0;

            UsingTransaction(tr =>
            {
                foreach (ObjectId elemId in elementIds)
                {
                    DBObject obj = tr.GetObject(elemId, OpenMode.ForWrite);
                    var elemData = XDataUtils.ReadElementData(obj);

                    if (elemData != null && elemData.IsLinked)
                    {
                        XDataUtils.RemoveLink(obj, tr);
                        unlinkedCount++;
                        WriteMessage($"  [{elemId.Handle}] Unlinked");
                    }
                }
            });

            WriteMessage($"\nKết quả: {unlinkedCount} unlinked");
        }

        [CommandMethod("DTS_SHOW_LINK")]
        public void DTS_SHOW_LINK()
        {
            WriteMessage("=== THÔNG TIN LIÊN KẾT ===");

            PromptEntityOptions opt = new PromptEntityOptions("\nChọn phần tử: ");
            PromptEntityResult res = Ed.GetEntity(opt);
            if (res.Status != PromptStatus.OK) return;

            UsingTransaction(tr =>
            {
                DBObject obj = tr.GetObject(res.ObjectId, OpenMode.ForRead);

                var storyData = XDataUtils.ReadStoryData(obj);
                if (storyData != null)
                {
                    WriteMessage($"  Loại: STORY ORIGIN");
                    WriteMessage($"  Tên: {storyData.StoryName}");
                    WriteMessage($"  Cao độ: {storyData.Elevation:0} mm");
                    WriteMessage($"  Chiều cao tầng: {storyData.StoryHeight:0} mm");
                    WriteMessage($"  Số phần tử con: {storyData.ChildCount}");
                    return;
                }

                var elemData = XDataUtils.ReadElementData(obj);
                if (elemData != null)
                {
                    WriteMessage($"  Loại: {elemData.ElementType}");
                    WriteMessage($"  Linked: {elemData.IsLinked}");
                    if (elemData.IsLinked)
                    {
                        WriteMessage($"  Origin Handle: {elemData.OriginHandle}");
                        WriteMessage($"  BaseZ: {elemData.BaseZ:0} mm");
                    }
                    WriteMessage($"  Has Mapping: {elemData.HasMapping}");

                    if (elemData is WallData wall)
                    {
                        WriteMessage($"  Thickness: {wall.Thickness:0} mm");
                        WriteMessage($"  WallType: {wall.WallType}");
                        WriteMessage($"  Load: {wall.LoadValue:0. 00} kN/m");
                    }
                    return;
                }

                WriteMessage("  Không có dữ liệu DTS.");
            });
        }

        private ElementData CreateElementDataForEntity(Entity ent)
        {
            string layer = ent.Layer.ToUpperInvariant();

            if (layer.Contains("WALL") || layer.Contains("TUONG"))
                return new WallData();
            else if (layer.Contains("COL") || layer.Contains("COT"))
                return new ColumnData();
            else if (layer.Contains("BEAM") || layer.Contains("DAM"))
                return new BeamData();
            else if (layer.Contains("SLAB") || layer.Contains("SAN"))
                return new SlabData();

            if (ent is Line)
                return new WallData();

            return null;
        }
    }
}