using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Utils;
using DTS_Wall_Tool.Core.Primitives;
using System.Collections.Generic;
using System.Linq;
using System;

namespace DTS_Wall_Tool.Commands
{
    /// <summary>
    /// Các lệnh quản lý liên kết (Link) giữa phần tử và Origin
    /// </summary>
    public class LinkCommands : CommandBase
    {
        /// <summary>
        /// Liên kết phần tử với Origin
        /// 
        /// ⚠️ QUAN TRỌNG - QUY TẮC LINK:
        /// - CHỈ link các phần tử ĐÃ CÓ DTS_APP (đã được đăng ký)
        /// - KHÔNG tự động tạo dữ liệu cho phần tử rác
        /// - Bỏ qua tất cả phần tử không có XData DTS_APP
        /// </summary>
        [CommandMethod("DTS_LINK")]
        public void DTS_LINK()
        {
            // Ngắn gọn: yêu cầu chọn đối tượng
            WriteMessage("Chọn đối tượng cần link...");

            // Chọn toàn bộ vùng - tự động nhận diện Origin và các phần tử
            var allIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
            if (allIds.Count ==0)
            {
                WriteMessage("Không có đối tượng nào được chọn.");
                return;
            }

            ObjectId originId = ObjectId.Null;
            List<ObjectId> elementIds = new List<ObjectId>();

            // Bước1: Tìm Origin trong vùng chọn
            UsingTransaction(tr =>
            {
                foreach (ObjectId id in allIds)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);

                    // Kiểm tra xem có phải Origin không
                    var storyData = XDataUtils.ReadStoryData(obj);
                    if (storyData != null)
                    {
                        originId = id;
                    }
                    else
                    {
                        // CHỈ thêm phần tử ĐÃ CÓ XData DTS_APP
                        var elemData = XDataUtils.ReadElementData(obj);
                        if (elemData != null)
                        {
                            elementIds.Add(id);
                        }
                    }
                }
            });

            // Nếu không tìm thấy Origin
            if (originId == ObjectId.Null)
            {
                WriteMessage("KHÔNG TÌM THẤY ORIGIN trong vùng chọn!");
                WriteMessage("Hướng dẫn: Chạy lệnh DTS_SET_ORIGIN để tạo gốc tọa độ trước.");
                return;
            }

            if (elementIds.Count ==0)
            {
                WriteMessage("Không có phần tử DTS_APP nào để liên kết.");
                WriteMessage("Hướng dẫn: Chạy DTS_SET hoặc DTS_SCAN để đăng ký phần tử trước.");
                return;
            }

            // Bước2: Thực hiện Link
            int linkedCount =0;
            int skippedCount =0;

            // Thống kê theo loại
            Dictionary<ElementType, int> typeStats = new Dictionary<ElementType, int>();

            UsingTransaction(tr =>
            {
                DBObject originObj = tr.GetObject(originId, OpenMode.ForWrite);
                StoryData storyData = XDataUtils.ReadStoryData(originObj);

                foreach (ObjectId elemId in elementIds)
                {
                    Entity ent = tr.GetObject(elemId, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    // Đọc ElementData (phải tồn tại vì đã lọc ở trên)
                    ElementData elemData = XDataUtils.ReadElementData(ent);
                    if (elemData == null) continue;

                    // Kiểm tra đã link chưa
                    if (elemData.IsLinked && elemData.OriginHandle == originId.Handle.ToString())
                    {
                        skippedCount++;
                        // vẫn giữ link line ghi nhận sau
                        // thống kê loại nếu có
                        if (!typeStats.ContainsKey(elemData.ElementType)) typeStats[elemData.ElementType] =0;
                        typeStats[elemData.ElementType]++;
                        continue;
                    }

                    // Thực hiện link
                    elemData.OriginHandle = originId.Handle.ToString();
                    elemData.BaseZ = storyData.Elevation;
                    elemData.Height = storyData.StoryHeight;

                    XDataUtils.WriteElementData(ent, elemData, tr);

                    // Cập nhật ChildHandles của Origin
                    string childHandle = elemId.Handle.ToString();
                    if (!storyData.ChildHandles.Contains(childHandle))
                    {
                        storyData.ChildHandles.Add(childHandle);
                    }

                    linkedCount++;

                    // Thống kê theo loại
                    if (!typeStats.ContainsKey(elemData.ElementType))
                        typeStats[elemData.ElementType] =0;
                    typeStats[elemData.ElementType]++;
                }

                XDataUtils.WriteStoryData(originObj, storyData, tr);
            });

            // Bước3: Vẽ đường link cho tất cả phần tử đã chọn (hiện trạng sau thao tác)
            DrawExistingLinksForElements(elementIds);

            // Báo cáo kết quả - tổng hợp ngắn gọn với tên loại
            if (typeStats.Count >0)
            {
                var parts = typeStats.OrderBy(x => x.Key)
                    .Select(kvp => $"{kvp.Value} {GetElementTypeDisplayName(kvp.Key)}")
                    .ToArray();

                // Lấy thông tin origin để hiển thị
                StoryData originStory = null;
                UsingTransaction(tr =>
                {
                    var o = tr.GetObject(originId, OpenMode.ForRead);
                    originStory = XDataUtils.ReadStoryData(o);
                });

                string originInfo = originStory != null ? $" với Origin {originStory.StoryName} {originStory.Elevation:0}mm" : string.Empty;

                WriteSuccess($"Đã link {string.Join(", ", parts)}{originInfo}");
            }
            else
            {
                WriteMessage($"Đã link: {linkedCount} phần tử{(skippedCount >0 ? $" (bỏ qua {skippedCount} đã link trước đó)" : "")}");
            }

            if (skippedCount >0)
                WriteMessage($"Bỏ qua: {skippedCount} phần tử (đã link trước đó)");
        }

        /// <summary>
        /// Vẽ đường link màu vàng từ Origin đến các phần tử
        /// </summary>
        private void DrawLinkLines(ObjectId originId, List<ObjectId> elementIds)
        {
            // Tạo layer nếu chưa có
            AcadUtils.CreateLayer("dts_linkmap",2); // Màu vàng =2

            UsingTransaction(tr =>
            {
                // Lấy tâm của Origin
                Entity originEnt = tr.GetObject(originId, OpenMode.ForRead) as Entity;
                var originCenter = AcadUtils.GetEntityCenter(originEnt);

                // Vẽ đường từ Origin đến từng phần tử
                foreach (ObjectId elemId in elementIds)
                {
                    Entity elemEnt = tr.GetObject(elemId, OpenMode.ForRead) as Entity;
                    var elemCenter = AcadUtils.GetEntityCenter(elemEnt);

                    // Vẽ đường màu vàng
                    AcadUtils.CreateLine(originCenter, elemCenter, "dts_linkmap",2, tr);
                }
            });
        }

        /// <summary>
        /// Vẽ đường link hiện có cho các phần tử (nhóm theo Origin)
        /// </summary>
        private void DrawExistingLinksForElements(List<ObjectId> elementIds)
        {
            // Nhóm theo Origin
            var groups = new Dictionary<ObjectId, List<ObjectId>>();

            UsingTransaction(tr =>
            {
                foreach (var elemId in elementIds)
                {
                    DBObject obj = tr.GetObject(elemId, OpenMode.ForRead);
                    var elemData = XDataUtils.ReadElementData(obj);
                    if (elemData == null || !elemData.IsLinked) continue;

                    var originId = AcadUtils.GetObjectIdFromHandle(elemData.OriginHandle);
                    if (originId == ObjectId.Null) continue;

                    if (!groups.ContainsKey(originId)) groups[originId] = new List<ObjectId>();
                    groups[originId].Add(elemId);
                }
            });

            foreach (var kvp in groups)
            {
                DrawLinkLines(kvp.Key, kvp.Value);
            }
        }

        [CommandMethod("DTS_UNLINK")]
        public void DTS_UNLINK()
        {
            WriteMessage("Chọn đối tượng để unlink...");

            var elementIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
            if (elementIds.Count ==0)
            {
                WriteMessage("Không có phần tử nào được chọn.");
                return;
            }

            int unlinkedCount =0;
            int skippedCount =0;

            // Group unlinked stats by origin -> type counts
            var originGroups = new Dictionary<ObjectId, Dictionary<ElementType, int>>();
            var unlinkedElementsList = new List<ObjectId>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId elemId in elementIds)
                {
                    DBObject obj = tr.GetObject(elemId, OpenMode.ForWrite);
                    var elemData = XDataUtils.ReadElementData(obj);

                    if (elemData != null && elemData.IsLinked)
                    {
                        // Determine origin id before removal
                        var originId = AcadUtils.GetObjectIdFromHandle(elemData.OriginHandle);

                        // Save type stats per origin
                        if (!originGroups.ContainsKey(originId)) originGroups[originId] = new Dictionary<ElementType, int>();
                        if (!originGroups[originId].ContainsKey(elemData.ElementType)) originGroups[originId][elemData.ElementType] =0;
                        originGroups[originId][elemData.ElementType]++;

                        // Remove link
                        XDataUtils.RemoveLink(obj, tr);
                        unlinkedCount++;
                        unlinkedElementsList.Add(elemId);
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
            });

            // After unlink, remove visual link lines associated with these elements
            if (unlinkedElementsList.Count >0)
            {
                RemoveLinkLinesForElements(unlinkedElementsList);
            }

            // Also redraw remaining links for selection
            DrawExistingLinksForElements(elementIds);

            // Report results grouped by origin
            if (originGroups.Count >0)
            {
                foreach (var kv in originGroups)
                {
                    ObjectId originId = kv.Key;
                    var typeDict = kv.Value;

                    // Build type parts
                    var parts = typeDict.OrderBy(x => x.Key)
                        .Select(p => $"{p.Value} {GetElementTypeDisplayName(p.Key)}")
                        .ToArray();

                    // Get origin info
                    string originInfo = string.Empty;
                    if (originId != ObjectId.Null)
                    {
                        UsingTransaction(tr =>
                        {
                            try
                            {
                                var o = tr.GetObject(originId, OpenMode.ForRead);
                                var story = XDataUtils.ReadStoryData(o);
                                if (story != null)
                                    originInfo = $" với Origin {story.StoryName} {story.Elevation:0}mm";
                                else
                                {
                                    // fallback to element origin name
                                    var pe = XDataUtils.ReadElementData(o);
                                    if (pe != null)
                                        originInfo = $" với Origin (handle {originId.Handle})";
                                }
                            }
                            catch { }
                        });
                    }

                    WriteSuccess($"Đã Unlink {string.Join(", ", parts)}{originInfo}");
                }
            }
            else
            {
                WriteMessage($"Đã Unlink: {unlinkedCount} phần tử");
            }

            if (skippedCount >0)
                WriteMessage($"Bỏ qua: {skippedCount} phần tử (không có link)");
        }

        /// <summary>
        /// Remove dts_linkmap visual lines that are connected to given elements (by center proximity)
        /// </summary>
        private void RemoveLinkLinesForElements(List<ObjectId> elementIds)
        {
            const double TOL =0.001; // tolerance for point comparison

            // Build list of centers for elements
            var centers = new List<Point2dWrapper>();

            UsingTransaction(tr =>
            {
                foreach (var id in elementIds)
                {
                    try
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        var c = AcadUtils.GetEntityCenter(ent);
                        centers.Add(new Point2dWrapper(c));
                    }
                    catch { }
                }

                // Iterate modelspace and delete lines on dts_linkmap that match
                BlockTable bt = (BlockTable)tr.GetObject(AcadUtils.Db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var toDelete = new List<ObjectId>();
                foreach (ObjectId objId in btr)
                {
                    try
                    {
                        var e = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                        if (e == null) continue;
                        if (!e.Layer.Equals("dts_linkmap", StringComparison.OrdinalIgnoreCase)) continue;

                        // Only consider Line entities
                        var line = e as Line;
                        if (line == null) continue;

                        var p1 = new Point2dWrapper(new Point2D(line.StartPoint.X, line.StartPoint.Y));
                        var p2 = new Point2dWrapper(new Point2D(line.EndPoint.X, line.EndPoint.Y));

                        // If either endpoint is close to any element center, mark for delete
                        bool match = centers.Any(c => c.DistanceTo(p1) < TOL || c.DistanceTo(p2) < TOL);
                        if (match)
                        {
                            toDelete.Add(objId);
                        }
                    }
                    catch { }
                }

                // Delete marked entities
                foreach (var id in toDelete)
                {
                    try
                    {
                        var ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent != null) ent.Erase();
                    }
                    catch { }
                }
            });
        }

        // Simple wrapper for2D point to compute distance
        private struct Point2dWrapper
        {
            public double X, Y;
            public Point2dWrapper(Point2D p) { X = p.X; Y = p.Y; }
            public double DistanceTo(Point2dWrapper other)
            {
                double dx = X - other.X;
                double dy = Y - other.Y;
                return Math.Sqrt(dx * dx + dy * dy);
            }
        }

        /// <summary>
        /// Hiển thị thông tin liên kết và vẽ đường link
        /// 
        /// ⚠️ QUY TẮC:
        /// - Hỗ trợ chọn NHIỀU đối tượng (không chỉ1)
        /// - Tự động kiểm soát không vẽ trùng lặp (cha-con)
        /// - KHÔNG hiển thị thông báo vẽ link (chỉ vẽ im lặng)
        /// </summary>
        [CommandMethod("DTS_SHOW_LINK")]
        public void DTS_SHOW_LINK()
        {
            WriteMessage("Chọn đối tượng để show link...");

            // Chọn nhiều đối tượng
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
            if (selectedIds.Count ==0)
            {
                WriteMessage("Không có đối tượng nào được chọn.");
                return;
            }

            // Vẽ link hiện có cho các phần tử đã chọn và đếm tổng số phần tử đã link
            int linkedCount =0;

            // Tạo nhóm origin->elements để vẽ (tránh vẽ trùng)
            var groups = new Dictionary<ObjectId, List<ObjectId>>();

            // Thống kê theo loại và đếm phần tử không có thuộc tính
            var typeStats = new Dictionary<ElementType, int>();
            int unknownLinked =0;

            UsingTransaction(tr =>
            {
                foreach (ObjectId objId in selectedIds)
                {
                    DBObject obj = tr.GetObject(objId, OpenMode.ForRead);

                    var storyData = XDataUtils.ReadStoryData(obj);
                    if (storyData != null)
                    {
                        // For origin entries, collect their child handles if exist
                        if (storyData.ChildHandles.Count >0)
                        {
                            var childIds = new List<ObjectId>();
                            foreach (var handle in storyData.ChildHandles)
                            {
                                var childId = AcadUtils.GetObjectIdFromHandle(handle);
                                if (childId != ObjectId.Null)
                                {
                                    childIds.Add(childId);
                                }
                            }

                            if (childIds.Count >0)
                            {
                                // group by this origin
                                if (!groups.ContainsKey(objId)) groups[objId] = new List<ObjectId>();
                                groups[objId].AddRange(childIds);

                                // count types for these children
                                foreach (var c in childIds)
                                {
                                    var childObj = tr.GetObject(c, OpenMode.ForRead);
                                    var childData = XDataUtils.ReadElementData(childObj);
                                    if (childData == null)
                                    {
                                        unknownLinked++;
                                    }
                                    else
                                    {
                                        if (!typeStats.ContainsKey(childData.ElementType)) typeStats[childData.ElementType] =0;
                                        typeStats[childData.ElementType]++;
                                    }
                                    linkedCount++;
                                }
                            }
                        }

                        continue;
                    }

                    var elemData = XDataUtils.ReadElementData(obj);
                    if (elemData != null && elemData.IsLinked)
                    {
                        var originId = AcadUtils.GetObjectIdFromHandle(elemData.OriginHandle);
                        if (originId != ObjectId.Null)
                        {
                            if (!groups.ContainsKey(originId)) groups[originId] = new List<ObjectId>();
                            groups[originId].Add(objId);

                            // count type
                            if (elemData.ElementType == ElementType.Unknown)
                            {
                                unknownLinked++;
                            }
                            else
                            {
                                if (!typeStats.ContainsKey(elemData.ElementType)) typeStats[elemData.ElementType] =0;
                                typeStats[elemData.ElementType]++;
                            }

                            linkedCount++;
                        }
                    }
                }
            });

            // Vẽ tất cả nhóm
            foreach (var kvp in groups)
            {
                // remove duplicates
                var uniqueList = kvp.Value.Distinct().ToList();
                DrawLinkLines(kvp.Key, uniqueList);
            }

            // Báo cáo tổng hợp
            if (linkedCount ==0)
            {
                WriteMessage("Không có link nào giữa các đối tượng đã chọn và bất kỳ Origin nào.");
                return;
            }

            // Nếu có các phần tử không có thuộc tính
            if (unknownLinked >0)
            {
                WriteMessage($"Đã tìm thấy {unknownLinked} phần tử không có thuộc tính đang được link. Vui lòng chạy DTS_SET_TYPE cho phần tử để phân loại.");
            }

            if (typeStats.Count >0)
            {
                var parts = typeStats.OrderBy(x => x.Key)
                    .Select(kvp => $"{kvp.Value} phần tử {GetElementTypeDisplayName(kvp.Key)}")
                    .ToArray();

                WriteMessage($"Đã tìm thấy {string.Join(", ", parts)} đang được link.");
            }
            else if (unknownLinked >0)
            {
                // already reported unknowns above
            }
            else
            {
                WriteMessage($"Đã tìm thấy {linkedCount} phần tử đang được link.");
            }
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

        private string GetElementTypeDisplayName(ElementType type)
        {
            switch (type)
            {
                case ElementType.Beam: return "Dầm";
                case ElementType.Column: return "Cột";
                case ElementType.Slab: return "Sàn";
                case ElementType.Wall: return "Tường";
                case ElementType.Foundation: return "Móng";
                case ElementType.Stair: return "Cầu thang";
                case ElementType.Pile: return "Cọc";
                case ElementType.Lintel: return "Lanh tô";
                case ElementType.Rebar: return "Cốt thép";
                case ElementType.ShearWall: return "Vách";
                case ElementType.StoryOrigin: return "Origin";
                case ElementType.ElementOrigin: return "Element Origin";
                default: return "Khác/Không xác định";
            }
        }
    }
}