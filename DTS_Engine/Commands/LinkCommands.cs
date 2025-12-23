using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Commands
{
    /// <summary>
    /// Quản lý liên kết phần tử (Smart Linking System).
    /// Hỗ trợ liên kết Cha-Con và Reference (nhánh phụ).
    /// Sử dụng VisualUtils để hiển thị tạm thời, không làm bẩn bản vẽ.
    /// Tuân thủ ISO/IEC 25010: Functional Suitability, Reliability, Maintainability.
    /// </summary>
    public class LinkCommands : CommandBase
    {
        #region 1. DTS_LINK_ORIGIN (Gán phần tử vào Story/Trục)

        /// <summary>
        /// Liên kết các phần tử với Story Origin.
        /// Quét chọn vùng chứa Origin và các phần tử cần liên kết.
        /// </summary>
        [CommandMethod("DTS_LINK_ORIGIN")]
        public void DTS_LINK_ORIGIN()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== LIÊN KẾT VỚI ORIGIN (STORY) ===");

                WriteMessage("Quét chọn vùng chứa Origin và các phần tử...");
                var allIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
                if (allIds.Count == 0) return;

                ObjectId originId = ObjectId.Null;
                var childIds = new List<ObjectId>();

                UsingTransaction(tr =>
                {
                    foreach (ObjectId id in allIds)
                    {
                        if (id.IsErased) continue;

                        DBObject obj = SafeGetObject(tr, id, OpenMode.ForRead);
                        if (obj == null) continue;

                        if (XDataUtils.ReadStoryData(obj) != null)
                            originId = id;
                        else if (XDataUtils.ReadElementData(obj) != null)
                            childIds.Add(id);
                    }
                });

                if (originId == ObjectId.Null)
                {
                    WriteError("Không tìm thấy Origin nào trong vùng chọn.");
                    return;
                }

                if (childIds.Count == 0)
                {
                    WriteMessage("Không có phần tử con nào để liên kết.");
                    return;
                }

                var report = ExecuteSmartLink(childIds, originId, isStoryOrigin: true);
                PrintLinkReport(report);
            });
        }

        #endregion

        #region 2. DTS_LINK (Liên kết Cha - Con kết cấu)

        /// <summary>
        /// Tạo liên kết Cha - Con.
        /// Logic mới: Nếu đã có Cha chính, tự động thêm vào Reference (nhánh phụ).
        /// </summary>
        [CommandMethod("DTS_LINK")]
        public void DTS_LINK()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== THIẾT LẬP LIÊN KẾT PHẦN TỬ ===");

                // Dọn dẹp visual cũ
                VisualUtils.ClearAll();

                // Bước 1: Chọn Con
                WriteMessage("\n1. Chọn các phần tử CON cần liên kết:");
                var childIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
                if (childIds.Count == 0) return;

                WriteMessage($"   Đã chọn {childIds.Count} phần tử con.");

                // Bước 2: Chọn Cha
                var peo = new PromptEntityOptions("\n2. Chọn phần tử CHA (Origin, Dầm, Cột...):");
                var per = Ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK) return;

                ObjectId parentId = per.ObjectId;

                if (childIds.Contains(parentId))
                {
                    childIds.Remove(parentId);
                    WriteMessage("   (Đã loại bỏ đối tượng Cha khỏi danh sách Con)");
                }

                // Highlight Cha
                VisualUtils.HighlightObject(parentId, 4); // Cyan

                var report = ExecuteSmartLink(childIds, parentId, isStoryOrigin: false);
                PrintLinkReport(report);
            });
        }

        #endregion

        #region Core Smart Linking Logic

        /// <summary>
        /// Kết quả liên kết để báo cáo.
        /// </summary>
        private class LinkReport
        {
            public string ParentName { get; set; } = "Unknown";
            public string ParentHandle { get; set; }
            public int PrimaryCount { get; set; }
            public int ReferenceCount { get; set; }
            public int AlreadyLinkedCount { get; set; }
            public int NoDataCount { get; set; }
            public int CycleCount { get; set; }
            public int HierarchyErrorCount { get; set; }
            public List<ObjectId> LinkedIds { get; } = new List<ObjectId>();
            public List<ObjectId> SkippedIds { get; } = new List<ObjectId>();
            public int TotalSuccess => PrimaryCount + ReferenceCount;
        }

        /// <summary>
        /// Thực hiện liên kết thông minh sử dụng XDataUtils.RegisterLink (Atomic 2-Way).
        /// </summary>
        private LinkReport ExecuteSmartLink(List<ObjectId> childIds, ObjectId parentId, bool isStoryOrigin)
        {
            var report = new LinkReport { ParentHandle = parentId.Handle.ToString() };

            UsingTransaction(tr =>
            {
                DBObject parentObj = SafeGetObject(tr, parentId, OpenMode.ForWrite);
                if (parentObj == null) return;

                // Xác định loại Cha & thông tin
                ElementType parentType = ElementType.Unknown;
                var storyData = XDataUtils.ReadStoryData(parentObj);
                var parentElemData = XDataUtils.ReadElementData(parentObj);

                if (storyData != null)
                {
                    parentType = ElementType.StoryOrigin;
                    report.ParentName = $"Origin {storyData.StoryName} (Z={storyData.Elevation:0}mm)";
                }
                else if (parentElemData != null)
                {
                    parentType = parentElemData.ElementType;
                    report.ParentName = $"{parentElemData.ElementType} [{report.ParentHandle}]";
                }
                else
                {
                    // Tự động gán StoryData nếu chưa có dữ liệu DTS
                    var autoOrigin = new StoryData { StoryName = "AutoOrigin", Elevation = 0 };
                    XDataUtils.WriteStoryData(parentObj, autoOrigin, tr);
                    storyData = autoOrigin;
                    parentType = ElementType.StoryOrigin;
                    report.ParentName = "Origin AutoOrigin (0mm)";
                }

                foreach (ObjectId childId in childIds)
                {
                    if (childId == parentId) continue;

                    Entity childEnt = SafeGetObject(tr, childId, OpenMode.ForWrite) as Entity;
                    if (childEnt == null) continue;

                    var childData = XDataUtils.ReadElementData(childEnt);
                    if (childData == null)
                    {
                        report.NoDataCount++;
                        report.SkippedIds.Add(childId);
                        continue;
                    }

                    // Quy tắc 1: Kiểm tra phân cấp
                    if (!LinkRules.CanBePrimaryParent(parentType, childData.ElementType))
                    {
                        report.HierarchyErrorCount++;
                        report.SkippedIds.Add(childId);
                        continue;
                    }

                    // Quy tắc 2: Kiểm tra không tạo chu trình (chỉ áp dụng nếu cha không phải Story)
                    if (!isStoryOrigin && LinkRules.DetectCycle(parentObj, childEnt.Handle.ToString(), tr))
                    {
                        report.CycleCount++;
                        report.SkippedIds.Add(childId);
                        continue;
                    }

                    // Quyết định loại Link: Primary hoặc Reference
                    bool isReference = !string.IsNullOrEmpty(childData.OriginHandle) &&
                                       childData.OriginHandle != report.ParentHandle;

                    // Gọi hàm Atomic 2-Way
                    var result = XDataUtils.RegisterLink(childEnt, parentObj, isReference, tr);

                    switch (result)
                    {
                        case LinkRegistrationResult.Primary:
                            report.PrimaryCount++;
                            report.LinkedIds.Add(childId);
                            break;
                        case LinkRegistrationResult.Reference:
                            report.ReferenceCount++;
                            report.LinkedIds.Add(childId);
                            break;
                        case LinkRegistrationResult.AlreadyLinked:
                            report.AlreadyLinkedCount++;
                            break;
                        default:
                            report.SkippedIds.Add(childId);
                            break;
                    }
                }
            });

            // Hiển thị visual cho các phần tử đã liên kết
            if (report.LinkedIds.Count > 0)
            {
                VisualUtils.DrawLinkLines(parentId, report.LinkedIds, 3); // Green
                VisualUtils.HighlightObjects(report.LinkedIds, 3);
            }

            // Hiển thị visual cho các phần tử bị bỏ qua
            if (report.SkippedIds.Count > 0)
            {
                VisualUtils.HighlightObjects(report.SkippedIds, 1); // Red
            }

            return report;
        }

        /// <summary>
        /// In báo cáo liên kết chi tiết.
        /// </summary>
        private void PrintLinkReport(LinkReport r)
        {
            WriteSuccess($"Kết quả liên kết với [{r.ParentName}]:");

            if (r.PrimaryCount > 0)
                WriteMessage($"  + {r.PrimaryCount} liên kết CHÍNH (Primary) được tạo.");
            if (r.ReferenceCount > 0)
                WriteMessage($"  + {r.ReferenceCount} liên kết PHỤ (Reference) được thêm.");
            if (r.AlreadyLinkedCount > 0)
                WriteMessage($"  = {r.AlreadyLinkedCount} phần tử đã liên kết trước đó (bỏ qua).");

            // Báo cáo lỗi
            if (r.NoDataCount > 0)
                WriteMessage($"  - {r.NoDataCount} phần tử chưa có dữ liệu DTS.");
            if (r.HierarchyErrorCount > 0)
                WriteMessage($"  - {r.HierarchyErrorCount} phần tử phân cấp không hợp lệ.");
            if (r.CycleCount > 0)
                WriteWarning($"  - {r.CycleCount} phần tử tham chiếu vòng (Cycle).");

            if (r.TotalSuccess == 0 && r.AlreadyLinkedCount == 0)
                WriteWarning("Không có phần tử nào được liên kết.");

            WriteMessage("\n(Sử dụng DTS_CLEAR_VISUAL để xóa hiển thị tạm thời)");
        }

        #endregion

        #region 3. DTS_SHOW_LINK (Hiển thị liên kết & Kiểm tra)

        /// <summary>
        /// Hiển thị các liên kết và kiểm tra tính toàn vẹn.
        /// Tự động phát hiện và xử lý: Con mất cha (Orphan), Cha chứa con đã bị xóa (Ghost).
        /// </summary>
        [CommandMethod("DTS_SHOW_LINK")]
        public void DTS_SHOW_LINK()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== HIỂN THỊ LIÊN KẾT & KIỂM TRA ===");

                var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
                if (ids.Count == 0) return;

                VisualUtils.ClearAll();

                var orphans = new List<ObjectId>();
                var ghostRefs = new Dictionary<ObjectId, int>();
                var validLinks = new Dictionary<ObjectId, List<ObjectId>>();

                UsingTransaction(tr =>
                {
                    foreach (ObjectId id in ids)
                    {
                        if (id.IsErased) continue;
                        DBObject obj = SafeGetObject(tr, id, OpenMode.ForRead);
                        if (obj == null) continue;

                        // Kiểm tra vai trò là Con
                        var elemData = XDataUtils.ReadElementData(obj);
                        if (elemData != null && elemData.IsLinked)
                        {
                            ObjectId parentId = AcadUtils.GetObjectIdFromHandle(elemData.OriginHandle);

                            if (IsValidObject(tr, parentId))
                            {
                                if (!validLinks.ContainsKey(parentId))
                                    validLinks[parentId] = new List<ObjectId>();
                                validLinks[parentId].Add(id);
                            }
                            else
                            {
                                orphans.Add(id);
                            }
                        }

                        // Kiểm tra vai trò là Cha
                        List<string> childHandles = null;
                        var storyData = XDataUtils.ReadStoryData(obj);
                        if (storyData != null)
                            childHandles = storyData.ChildHandles;
                        else if (elemData != null)
                            childHandles = elemData.ChildHandles;

                        if (childHandles != null && childHandles.Count > 0)
                        {
                            int ghosts = 0;
                            foreach (string h in childHandles)
                            {
                                ObjectId cId = AcadUtils.GetObjectIdFromHandle(h);
                                if (!IsValidObject(tr, cId))
                                {
                                    ghosts++;
                                }
                                else
                                {
                                    if (!validLinks.ContainsKey(id))
                                        validLinks[id] = new List<ObjectId>();
                                    if (!validLinks[id].Contains(cId))
                                        validLinks[id].Add(cId);
                                }
                            }
                            if (ghosts > 0) ghostRefs[id] = ghosts;
                        }
                    }
                });

                // Vẽ liên kết hợp lệ
                if (validLinks.Count > 0)
                {
                    int totalLinks = 0;
                    foreach (var kv in validLinks)
                    {
                        VisualUtils.DrawLinkLines(kv.Key, kv.Value, 3); // Green
                        totalLinks += kv.Value.Count;
                    }
                    WriteSuccess($"Hiển thị {totalLinks} liên kết hợp lệ.");
                }

                // Dọn dẹp "con ma" (Ghost Children)
                if (ghostRefs.Count > 0)
                {
                    int totalGhosts = ghostRefs.Values.Sum();
                    WriteMessage($"\nĐang dọn dẹp {totalGhosts} tham chiếu rác (con đã bị xóa)...");

                    UsingTransaction(tr =>
                    {
                        foreach (var kv in ghostRefs)
                        {
                            CleanUpGhostChildren(tr, kv.Key);
                        }
                    });
                    WriteSuccess("Đã làm sạch dữ liệu Cha.");
                }

                // Xử lý "mồ côi" (Orphans)
                if (orphans.Count > 0)
                {
                    VisualUtils.HighlightObjects(orphans, 1); // Red
                    WriteWarning($"Phát hiện {orphans.Count} phần tử MẤT CHA (Cha đã bị xóa).");

                    var pko = new PromptKeywordOptions("\nChọn cách xử lý: [Unlink/ReLink/Ignore]: ");
                    pko.Keywords.Add("Unlink");
                    pko.Keywords.Add("ReLink");
                    pko.Keywords.Add("Ignore");
                    pko.Keywords.Default = "Ignore";

                    var res = Ed.GetKeywords(pko);

                    if (res.Status == PromptStatus.OK)
                    {
                        if (res.StringResult == "Unlink")
                        {
                            BreakOrphanLinks(orphans);
                        }
                        else if (res.StringResult == "ReLink")
                        {
                            WriteMessage("\nChọn cha mới cho các phần tử:");
                            var peo = new PromptEntityOptions("\nChọn Cha mới: ");
                            var per = Ed.GetEntity(peo);

                            if (per.Status == PromptStatus.OK)
                            {
                                bool validParent = false;
                                UsingTransaction(tr =>
                                {
                                    var pObj = SafeGetObject(tr, per.ObjectId, OpenMode.ForRead);
                                    if (pObj != null && (XDataUtils.ReadStoryData(pObj) != null || XDataUtils.ReadElementData(pObj) != null))
                                        validParent = true;
                                });

                                if (!validParent)
                                {
                                    WriteError("Đối tượng được chọn không có dữ liệu DTS.");
                                }
                                else
                                {
                                    // BUGFIX: Xóa OriginHandle cũ (đã gây) trước khi ReLink
                                    // Để ExecuteSmartLink nhận diện đây là Primary link, không phải Reference
                                    RelinkOrphansToNewParent(orphans, per.ObjectId);
                                }
                            }
                        }
                        else
                        {
                            WriteMessage("Đã bỏ qua. Liên kết lỗi vẫn tồn tại.");
                        }
                    }
                }
                else if (validLinks.Count == 0)
                {
                    WriteMessage("Không tìm thấy liên kết nào trong các đối tượng đã chọn.");
                }

                WriteMessage("\n(Sử dụng DTS_CLEAR_VISUAL để xóa hiển thị tạm thời)");
            });
        }

        #endregion

        #region 4. DTS_UNLINK (Gỡ liên kết cụ thể)

        /// <summary>
        /// Gỡ liên kết cụ thể giữa Con và Cha.
        /// Nếu gỡ Cha chính, sẽ tự động dọn Reference đầu tiên lên làm Cha chính.
        /// </summary>
        [CommandMethod("DTS_UNLINK")]
        public void DTS_UNLINK()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== GỬ LIÊN KẾT CỤ THỂ ===");

                VisualUtils.ClearAll();

                // Bước 1: Chọn nhiều phần tử CON
                WriteMessage("\n1. Chọn các phần tử CON cần gỡ liên kết:");
                var childIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
                if (childIds.Count == 0) return;

                WriteMessage($"   Đã chọn {childIds.Count} phần tử con.");
                VisualUtils.HighlightObjects(childIds, 6); // Magenta

                // Bước 2: Xác định tất cả các cha chung (để tô màu)
                var allParents = new HashSet<ObjectId>();
                UsingTransaction(tr =>
                {
                    foreach (ObjectId childId in childIds)
                    {
                        if (childId.IsErased) continue;
                        var obj = SafeGetObject(tr, childId, OpenMode.ForRead);
                        if (obj == null) continue;

                        var handles = XDataUtils.GetAllParentHandles(obj);
                        foreach (var h in handles)
                        {
                            var pid = AcadUtils.GetObjectIdFromHandle(h);
                            if (pid != ObjectId.Null) allParents.Add(pid);
                        }
                    }
                });

                if (allParents.Count == 0)
                {
                    WriteMessage("Không có phần tử nào có liên kết.");
                    VisualUtils.ClearAll();
                    return;
                }

                // Tô sáng các cha
                VisualUtils.HighlightObjects(allParents.ToList(), 2); // Yellow
                WriteMessage($"Các phần tử đang liên kết với {allParents.Count} đối tượng cha (đang tô vàng).");

                // Bước 3: Chọn Cha cần gỡ
                var peoParent = new PromptEntityOptions("\n2. Chọn đối tượng CHA muốn gỡ bỏ:");
                var resParent = Ed.GetEntity(peoParent);

                if (resParent.Status == PromptStatus.OK)
                {
                    int successCount = 0;
                    int failCount = 0;
                    string parentHandle = resParent.ObjectId.Handle.ToString();

                    UsingTransaction(tr =>
                    {
                        foreach (ObjectId childId in childIds)
                        {
                            if (childId.IsErased) continue;
                            var childObj = SafeGetObject(tr, childId, OpenMode.ForWrite);
                            if (childObj == null)
                            {
                                failCount++;
                                continue;
                            }

                            // Gọi hàm Atomic 2-Way
                            bool success = XDataUtils.UnregisterLink(childObj, parentHandle, tr);
                            if (success)
                                successCount++;
                            else
                                failCount++;
                        }
                    });

                    if (successCount > 0)
                        WriteSuccess($"Đã gỡ liên kết {successCount} phần tử với Cha [{parentHandle}].");
                    if (failCount > 0)
                        WriteMessage($"  {failCount} phần tử không có liên kết với Cha này.");
                }

                VisualUtils.ClearAll();
            });
        }

        #endregion

        #region 5. DTS_CLEAR_LINK (Xoa toan bo lien ket)

        /// <summary>
        /// Xoa sach moi lien ket cua doi tuong (Reset ve trang thai tu do).
        /// </summary>
        [CommandMethod("DTS_CLEAR_LINK")]
        public void DTS_CLEAR_LINK()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== XOÁ TOÀN BỘ LIÊN KẾT ===");

                var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
                if (ids.Count == 0) return;

                int count = 0;
                int protectedOrigins = 0;

                UsingTransaction(tr =>
                {
                    foreach (var id in ids)
                    {
                        var obj = SafeGetObject(tr, id, OpenMode.ForWrite);
                        if (obj == null) continue;

                        // Bao ve Origin
                        if (XDataUtils.ReadStoryData(obj) != null)
                        {
                            protectedOrigins++;
                            continue;
                        }

                        // Kiem tra co lien ket khong
                        if (XDataUtils.HasAnyLink(obj))
                        {
                            // Goi ham Atomic 2-Way
                            XDataUtils.ClearAllLinks(obj, tr);
                            count++;
                        }
                    }
                });

                WriteSuccess($"Đã xóa liên kết của {count} phần tử.");
                if (protectedOrigins > 0)
                    WriteMessage($"Bỏ qua {protectedOrigins} phần tử Origin (không thể xóa liên kết Origin).");
            });
        }

        #endregion

        #region 6. DTS_CLEAR_VISUAL (Don dep hien thi tam)

        /// <summary>
        /// Xoa tat ca hien thi tam thoi (Transient Graphics).
        /// </summary>
        [CommandMethod("DTS_CLEAR_VISUAL")]
        public void DTS_CLEAR_VISUAL()
        {
            VisualUtils.ClearAll();
            WriteSuccess("Đã xóa hiển thị tạm thời.");
        }

        #endregion

        #region Safety Helpers

        private DBObject SafeGetObject(Transaction tr, ObjectId id, OpenMode mode)
        {
            if (id == ObjectId.Null || id.IsErased) return null;
            try { return tr.GetObject(id, mode); }
            catch { return null; }
        }

        private bool IsValidObject(Transaction tr, ObjectId id)
        {
            return SafeGetObject(tr, id, OpenMode.ForRead) != null;
        }

        private void CleanUpGhostChildren(Transaction tr, ObjectId parentId)
        {
            try
            {
                DBObject parentObj = tr.GetObject(parentId, OpenMode.ForWrite);
                var story = XDataUtils.ReadStoryData(parentObj);
                var elem = XDataUtils.ReadElementData(parentObj);

                List<string> handles = story != null ? story.ChildHandles : elem?.ChildHandles;
                if (handles == null) return;

                var validHandles = new List<string>();
                foreach (string h in handles)
                {
                    ObjectId cid = AcadUtils.GetObjectIdFromHandle(h);
                    if (IsValidObject(tr, cid))
                        validHandles.Add(h);
                }

                if (validHandles.Count != handles.Count)
                {
                    if (story != null)
                    {
                        story.ChildHandles = validHandles;
                        XDataUtils.WriteStoryData(parentObj, story, tr);
                    }
                    else if (elem != null)
                    {
                        elem.ChildHandles = validHandles;
                        XDataUtils.WriteElementData(parentObj, elem, tr);
                    }
                }
            }
            catch { }
        }

        private void BreakOrphanLinks(List<ObjectId> orphans)
        {
            int count = 0;
            UsingTransaction(tr =>
            {
                foreach (ObjectId id in orphans)
                {
                    DBObject obj = SafeGetObject(tr, id, OpenMode.ForWrite);
                    if (obj == null) continue;

                    var data = XDataUtils.ReadElementData(obj);
                    if (data != null)
                    {
                        data.OriginHandle = null;
                        XDataUtils.WriteElementData(obj, data, tr);
                        count++;
                    }
                }
            });

            WriteSuccess($"Đã cắt liên kết cho {count} phần tử mồ côi.");
        }

        /// <summary>
        /// ReLink orphans den cha moi.
        /// Khac voi ExecuteSmartLink: Clear OriginHandle cu truoc de dam bao tao Primary link.
        /// </summary>
        private void RelinkOrphansToNewParent(List<ObjectId> orphanIds, ObjectId newParentId)
        {
            int successCount = 0;
            int failCount = 0;
            string parentName = "Unknown";

            UsingTransaction(tr =>
            {
                DBObject parentObj = SafeGetObject(tr, newParentId, OpenMode.ForWrite);
                if (parentObj == null) return;

                // Lay ten cha de bao cao
                var storyData = XDataUtils.ReadStoryData(parentObj);
                var parentElemData = XDataUtils.ReadElementData(parentObj);

                if (storyData != null)
                    parentName = $"Origin {storyData.StoryName} (Z={storyData.Elevation:0}mm)";
                else if (parentElemData != null)
                    parentName = $"{parentElemData.ElementType} [{newParentId.Handle}]";

                foreach (ObjectId orphanId in orphanIds)
                {
                    DBObject orphanObj = SafeGetObject(tr, orphanId, OpenMode.ForWrite);
                    if (orphanObj == null)
                    {
                        failCount++;
                        continue;
                    }

                    var orphanData = XDataUtils.ReadElementData(orphanObj);
                    if (orphanData == null)
                    {
                        failCount++;
                        continue;
                    }

                    // BUGFIX: Clear OriginHandle cu (da gay) truoc khi gan cha moi
                    orphanData.OriginHandle = null;
                    XDataUtils.WriteElementData(orphanObj, orphanData, tr);

                    // Goi RegisterLink voi isReference = false de tao Primary link
                    var result = XDataUtils.RegisterLink(orphanObj, parentObj, isReference: false, tr);

                    if (result == LinkRegistrationResult.Primary || result == LinkRegistrationResult.AlreadyLinked)
                        successCount++;
                    else
                        failCount++;
                }
            });

            // Báo cáo
            WriteSuccess($"Đã ReLink {successCount} phần tử đến [{parentName}].");
            if (failCount > 0)
                WriteWarning($"{failCount} phần tử không thể ReLink.");

            // Hien thi visual
            if (successCount > 0)
            {
                VisualUtils.DrawLinkLines(newParentId, orphanIds, 3); // Green
                VisualUtils.HighlightObjects(orphanIds, 3);
            }

            WriteMessage("\n(Sử dụng DTS_CLEAR_VISUAL để xóa hiển thị tạm thời)");
        }

        #endregion

        #region V5: DTS_REBAR_LINK (Beam Group Star Topology)

        /// <summary>
        /// V5: Tạo Star Topology cho nhóm dầm.
        /// Chọn nhiều dầm → Phần tử bên trái nhất (S1) trở thành "Mother".
        /// </summary>
        [CommandMethod("DTS_REBAR_LINK")]
        public void DTS_REBAR_LINK()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== LIÊN KẾT NHÓM DẦM (STAR TOPOLOGY) ===");
                WriteMessage("Chọn các dầm cần gom nhóm (S1 = dầm trái nhất sẽ là Mother):");

                var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
                if (ids.Count < 2)
                {
                    WriteMessage("Cần chọn ít nhất 2 dầm để tạo liên kết nhóm.");
                    return;
                }

                VisualUtils.ClearAll();

                var topologyBuilder = new Core.Algorithms.TopologyBuilder();
                int linkCount = 0;

                UsingTransaction(tr =>
                {
                    // BuildGraph sẽ tự động sắp xếp L->R và thiết lập Star Topology
                    var sortedTopologies = topologyBuilder.BuildGraph(ids, tr, autoEstablishLinks: true);

                    if (sortedTopologies.Count < 2)
                    {
                        WriteMessage("Không tìm thấy đủ dầm hợp lệ.");
                        return;
                    }

                    // Mother = S1 (left-most)
                    var mother = sortedTopologies[0];
                    var children = sortedTopologies.Skip(1).ToList();

                    WriteMessage($"Mother (S1): {mother.Handle} tại X={mother.StartPoint.X:F0}");

                    foreach (var child in children)
                    {
                        WriteMessage($"  → Child {child.SpanId}: {child.Handle} tại X={child.StartPoint.X:F0}");
                        linkCount++;
                    }

                    // Highlight
                    var motherObjId = mother.ObjectId;
                    var childObjIds = children.Select(c => c.ObjectId).ToList();

                    VisualUtils.HighlightObject(motherObjId, 4); // Cyan for Mother
                    VisualUtils.HighlightObjects(childObjIds, 3); // Green for Children
                    VisualUtils.DrawLinkLines(motherObjId, childObjIds, 3);
                });

                WriteSuccess($"Đã tạo Star Topology với {linkCount} liên kết.");
                WriteMessage("Chạy DTS_REBAR_CALCULATE để tính thép cho nhóm này.");
                WriteMessage("\n(Sử dụng DTS_CLEAR_VISUAL để xóa hiển thị tạm thời)");
            });
        }

        #endregion

        #region V5: DTS_REBAR_UNLINK (Break Beam Group)

        /// <summary>
        /// V5: Tách dầm ra khỏi nhóm. Có option để downstream beams follow.
        /// </summary>
        [CommandMethod("DTS_REBAR_UNLINK")]
        public void DTS_REBAR_UNLINK()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== TÁCH DẦM KHỎI NHÓM ===");
                WriteMessage("Chọn dầm cần tách ra khỏi nhóm:");

                var peo = new PromptEntityOptions("\nChọn dầm: ");
                peo.SetRejectMessage("\nChỉ chọn LINE hoặc POLYLINE.");
                peo.AddAllowedClass(typeof(Line), false);
                peo.AddAllowedClass(typeof(Polyline), false);

                var per = Ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK) return;

                ObjectId childId = per.ObjectId;

                // Hỏi user về downstream behavior
                var pko = new PromptKeywordOptions(
                    "\nCác dầm phía sau có follow theo dầm này không? [Yes/No]: ");
                pko.Keywords.Add("Yes");
                pko.Keywords.Add("No");
                pko.Keywords.Default = "No";

                var resKw = Ed.GetKeywords(pko);
                bool followDownstream = resKw.Status == PromptStatus.OK && resKw.StringResult == "Yes";

                int unlinkCount = 0;
                string motherHandle = null;

                UsingTransaction(tr =>
                {
                    var childObj = tr.GetObject(childId, OpenMode.ForWrite);
                    if (childObj == null) return;

                    var elemData = XDataUtils.ReadElementData(childObj);
                    if (elemData == null || string.IsNullOrEmpty(elemData.OriginHandle))
                    {
                        WriteMessage("Dầm này không thuộc nhóm nào.");
                        return;
                    }

                    motherHandle = elemData.OriginHandle;

                    // Get Mother
                    var motherId = AcadUtils.GetObjectIdFromHandle(motherHandle);
                    if (motherId == ObjectId.Null)
                    {
                        WriteMessage("Không tìm thấy Mother.");
                        return;
                    }

                    var motherObj = tr.GetObject(motherId, OpenMode.ForWrite);

                    if (followDownstream)
                    {
                        // Get all children of the same mother
                        var motherData = XDataUtils.ReadElementData(motherObj);
                        var allChildHandles = motherData?.ChildHandles ?? new List<string>();

                        // Build topology to find order
                        var allChildIds = allChildHandles
                            .Select(h => AcadUtils.GetObjectIdFromHandle(h))
                            .Where(id => id != ObjectId.Null)
                            .ToList();
                        allChildIds.Add(motherId);

                        var topologyBuilder = new Core.Algorithms.TopologyBuilder();
                        var sortedTopologies = topologyBuilder.BuildGraph(allChildIds, tr, autoEstablishLinks: false);

                        // Find index of selected child
                        var selectedHandle = childId.Handle.ToString();
                        int selectedIdx = sortedTopologies.FindIndex(t => t.Handle == selectedHandle);

                        if (selectedIdx < 0)
                        {
                            WriteMessage("Không tìm thấy dầm trong topology.");
                            return;
                        }

                        // Unlink selected + all to the right
                        var toUnlink = sortedTopologies.Skip(selectedIdx).ToList();

                        foreach (var topo in toUnlink)
                        {
                            var unlinkObj = tr.GetObject(topo.ObjectId, OpenMode.ForWrite);
                            XDataUtils.ClearAllLinks(unlinkObj, tr);
                            unlinkCount++;
                        }

                        // Re-link downstream beams to selected child as new Mother
                        if (toUnlink.Count > 1)
                        {
                            var newMotherObj = tr.GetObject(toUnlink[0].ObjectId, OpenMode.ForWrite);
                            for (int i = 1; i < toUnlink.Count; i++)
                            {
                                var downstreamObj = tr.GetObject(toUnlink[i].ObjectId, OpenMode.ForWrite);
                                XDataUtils.RegisterLink(downstreamObj, newMotherObj, isReference: false, tr);
                            }
                            WriteMessage($"Dầm [{selectedHandle}] trở thành Mother mới cho {toUnlink.Count - 1} dầm downstream.");
                        }
                    }
                    else
                    {
                        // Just unlink the selected child
                        XDataUtils.UnregisterLink(childObj, motherHandle, tr);
                        unlinkCount = 1;
                    }
                });

                if (unlinkCount > 0)
                {
                    WriteSuccess($"Đã tách {unlinkCount} dầm khỏi nhóm [Mother: {motherHandle}].");
                }
            });
        }

        #endregion

        #region V5: DTS_SHOW_REBAR_LINK (Hiển thị Star Topology)

        /// <summary>
        /// V5: Hiển thị Star Topology của nhóm dầm được chọn.
        /// </summary>
        [CommandMethod("DTS_SHOW_REBAR_LINK")]
        public void DTS_SHOW_REBAR_LINK()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== HIỂN THỊ LIÊN KẾT NHÓM DẦM ===");
                WriteMessage("Chọn một hoặc nhiều dầm để xem liên kết:");

                var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
                if (ids.Count == 0) return;

                VisualUtils.ClearAll();

                var topologyBuilder = new Core.Algorithms.TopologyBuilder();
                int totalGroups = 0;

                UsingTransaction(tr =>
                {
                    // BuildGraph tự động expand selection theo links
                    var allTopologies = topologyBuilder.BuildGraph(ids, tr, autoEstablishLinks: false);

                    if (allTopologies.Count == 0)
                    {
                        WriteMessage("Không tìm thấy dầm có liên kết.");
                        return;
                    }

                    // Split into groups
                    var groups = topologyBuilder.SplitIntoGroups(allTopologies);

                    foreach (var group in groups)
                    {
                        if (group.Count == 0) continue;
                        totalGroups++;

                        // Mother = first (left-most)
                        var mother = group[0];
                        var children = group.Skip(1).ToList();

                        WriteMessage($"\nNhóm {totalGroups}: {group.Count} dầm");
                        WriteMessage($"  Mother: {mother.Handle} (X={mother.StartPoint.X:F0})");

                        foreach (var child in children)
                        {
                            WriteMessage($"  → {child.SpanId}: {child.Handle} (X={child.StartPoint.X:F0})");
                        }

                        // Highlight
                        var motherObjId = mother.ObjectId;
                        var childObjIds = children.Select(c => c.ObjectId).ToList();

                        int colorIndex = 3 + (totalGroups % 5); // Cycle colors: 3,4,5,6,7
                        VisualUtils.HighlightObject(motherObjId, 4); // Cyan for Mother
                        VisualUtils.HighlightObjects(childObjIds, colorIndex);
                        VisualUtils.DrawLinkLines(motherObjId, childObjIds, colorIndex);
                    }
                });

                if (totalGroups == 0)
                {
                    WriteMessage("Không tìm thấy nhóm dầm liên kết nào.");
                }
                else
                {
                    WriteSuccess($"Đã hiển thị {totalGroups} nhóm dầm.");
                }

                WriteMessage("\n(Sử dụng DTS_CLEAR_VISUAL để xóa hiển thị tạm thời)");
            });
        }

        #endregion

        // V5: DTS_CLEANUP_LEGACY removed - NOD no longer supported

        #region V5: DTS_VALIDATE_TOPOLOGY (Kiểm tra tính toàn vẹn)

        /// <summary>
        /// V5: Kiểm tra và báo cáo tình trạng Star Topology của tất cả nhóm dầm.
        /// </summary>
        [CommandMethod("DTS_VALIDATE_TOPOLOGY")]
        public void DTS_VALIDATE_TOPOLOGY()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== KIỂM TRA STAR TOPOLOGY ===");

                var topologyBuilder = new Core.Algorithms.TopologyBuilder();
                int totalGroups = 0;
                int validGroups = 0;
                int invalidGroups = 0;
                int orphanBeams = 0;

                UsingTransaction(tr =>
                {
                    // Scan all beams
                    var allIds = new List<ObjectId>();
                    var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;

                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased) continue;
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        if (XDataUtils.HasAppXData(ent))
                        {
                            var rebarData = XDataUtils.ReadRebarData(ent);
                            if (rebarData != null)
                            {
                                allIds.Add(id);
                            }
                        }
                    }

                    if (allIds.Count == 0)
                    {
                        WriteMessage("Không tìm thấy dầm có dữ liệu DTS.");
                        return;
                    }

                    WriteMessage($"Tìm thấy {allIds.Count} dầm, đang phân tích...");

                    var allTopologies = topologyBuilder.BuildGraph(allIds, tr, autoEstablishLinks: false);
                    var groups = topologyBuilder.SplitIntoGroups(allTopologies);

                    foreach (var group in groups)
                    {
                        totalGroups++;

                        if (group.Count == 1)
                        {
                            // Single beam - check if it has orphan link
                            var beam = group[0];
                            if (!string.IsNullOrEmpty(beam.OriginHandle))
                            {
                                // Has link but alone in group - orphan
                                orphanBeams++;
                                WriteMessage($"  ⚠️ Dầm đơn có link mồ côi: {beam.Handle}");
                            }
                            else
                            {
                                validGroups++;
                            }
                        }
                        else
                        {
                            // Multi-beam group - validate Star Topology
                            var mother = group[0];
                            bool isValid = true;

                            for (int i = 1; i < group.Count; i++)
                            {
                                if (group[i].OriginHandle != mother.Handle)
                                {
                                    isValid = false;
                                    WriteMessage($"  ❌ Nhóm [{mother.Handle}]: Beam {group[i].Handle} không link đúng về Mother.");
                                    break;
                                }
                            }

                            if (isValid)
                            {
                                validGroups++;
                                WriteMessage($"  ✅ Nhóm [{mother.Handle}]: {group.Count} dầm, Star Topology OK");
                            }
                            else
                            {
                                invalidGroups++;
                            }
                        }
                    }
                });

                // Summary
                WriteMessage("\n--- TỔNG KẾT ---");
                WriteMessage($"  Tổng số nhóm: {totalGroups}");
                WriteSuccess($"  Nhóm hợp lệ: {validGroups}");
                if (invalidGroups > 0)
                    WriteError($"  Nhóm không hợp lệ: {invalidGroups}");
                if (orphanBeams > 0)
                    WriteMessage($"  Dầm mồ côi: {orphanBeams}");

                if (invalidGroups > 0 || orphanBeams > 0)
                {
                    WriteMessage("\nChạy DTS_CLEANUP_LEGACY để sửa chữa tự động.");
                }
            });
        }

        #endregion

        #region V5: DTS_REBAR_GROUP_AUTO (Tự động gom nhóm dầm)

        /// <summary>
        /// V5: Tự động quét chọn và gom nhóm dầm dựa trên collinearity.
        /// Các dầm thẳng hàng (cùng trục Y hoặc X trong phạm vi tolerance) 
        /// và liên tục (khoảng cách đầu-đuôi nhỏ) sẽ được gom thành 1 nhóm.
        /// Mỗi nhóm sử dụng Star Topology: dầm trái nhất là Mother.
        /// </summary>
        [CommandMethod("DTS_REBAR_GROUP_AUTO")]
        public void DTS_REBAR_GROUP_AUTO()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== TỰ ĐỘNG GOM NHÓM DẦM (STAR TOPOLOGY) ===");
                WriteMessage("Chọn vùng chứa các dầm cần gom nhóm:");

                var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
                if (ids.Count < 2)
                {
                    WriteMessage("Cần chọn ít nhất 2 dầm để gom nhóm.");
                    return;
                }

                VisualUtils.ClearAll();

                const double AXIS_TOLERANCE = 500; // mm - tolerance để coi là cùng trục
                const double GAP_TOLERANCE = 1000; // mm - khoảng cách tối đa giữa 2 dầm liên tiếp

                int groupCount = 0;
                int totalLinks = 0;
                var detectedGroups = new List<Core.Data.BeamGroup>();

                UsingTransaction(tr =>
                {
                    // 1. Harvest BeamGeometry với đầy đủ thông tin từ XData
                    var beamGeometries = new List<Core.Data.BeamGeometry>();
                    var handleToObjectId = new Dictionary<string, ObjectId>();

                    foreach (ObjectId id in ids)
                    {
                        if (id.IsErased) continue;
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        // Chỉ xử lý entity có XData là BEAM
                        var elemData = XDataUtils.ReadElementData(ent);
                        if (elemData == null || elemData.ElementType != ElementType.Beam) continue;

                        var beamData = elemData as BeamData;

                        // Get geometry
                        double startX = 0, startY = 0, startZ = 0;
                        double endX = 0, endY = 0, endZ = 0;

                        if (ent is Autodesk.AutoCAD.DatabaseServices.Line line)
                        {
                            startX = line.StartPoint.X; startY = line.StartPoint.Y; startZ = line.StartPoint.Z;
                            endX = line.EndPoint.X; endY = line.EndPoint.Y; endZ = line.EndPoint.Z;
                        }
                        else if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline pline && pline.NumberOfVertices >= 2)
                        {
                            var p0 = pline.GetPoint3dAt(0);
                            var pN = pline.GetPoint3dAt(pline.NumberOfVertices - 1);
                            startX = p0.X; startY = p0.Y; startZ = p0.Z;
                            endX = pN.X; endY = pN.Y; endZ = pN.Z;
                        }
                        else continue;

                        // Get ResultData for A_req propagation
                        var rebarData = XDataUtils.ReadRebarData(ent);

                        var geom = new Core.Data.BeamGeometry
                        {
                            Handle = ent.Handle.ToString(),
                            Name = beamData?.SapFrameName ?? ent.Handle.ToString(),
                            StartX = startX,
                            StartY = startY,
                            StartZ = startZ,
                            EndX = endX,
                            EndY = endY,
                            EndZ = endZ,
                            Width = beamData?.Width ?? 200,
                            Height = beamData?.Depth ?? beamData?.Height ?? 400,
                            SupportI = beamData?.SupportI ?? 1,
                            SupportJ = beamData?.SupportJ ?? 1,
                            ResultData = rebarData,
                            // FIX: Store XData's BaseZ for correct story grouping (2D drawings have geometric Z=0)
                            BaseZ = beamData?.BaseZ
                        };

                        beamGeometries.Add(geom);
                        handleToObjectId[geom.Handle] = id;
                    }

                    if (beamGeometries.Count < 2)
                    {
                        WriteMessage("Không tìm thấy đủ dầm hợp lệ (cần có XData BEAM).");
                        return;
                    }

                    WriteMessage($"Tìm thấy {beamGeometries.Count} dầm. Đang phân tích nhóm...");

                    // 2. Populate AxisName and update SupportI/J in BeamGeometry từ XData
                    foreach (var geom in beamGeometries)
                    {
                        var objId = handleToObjectId[geom.Handle];
                        var obj = tr.GetObject(objId, OpenMode.ForRead);
                        var elemData = XDataUtils.ReadElementData(obj);
                        var beamData = elemData as BeamData;

                        // Set AxisName for Girder classification
                        geom.AxisName = beamData?.AxisName ?? "";

                        // Set SupportI/J for Girder classification
                        geom.SupportI = beamData?.SupportI ?? 1;
                        geom.SupportJ = beamData?.SupportJ ?? 1;
                    }

                    // 3. GROUP BY COLLINEARITY + CONNECTIVITY (NOT by AxisName!)
                    const double Z_TOLERANCE = 500; // mm - tolerance cho cùng tầng

                    // 3.1. Nhóm theo Story (Z elevation)
                    // FIX: Prioritize XData's BaseZ over geometric Z (2D drawings have geometric Z=0)
                    var storyGroups = beamGeometries
                        .GroupBy(b =>
                        {
                            // Use BaseZ from XData if available and non-zero
                            if (b.BaseZ.HasValue && b.BaseZ.Value != 0)
                                return System.Math.Round(b.BaseZ.Value / Z_TOLERANCE) * Z_TOLERANCE;
                            // Fallback to geometric Z
                            return System.Math.Round((b.StartZ + b.EndZ) / 2 / Z_TOLERANCE) * Z_TOLERANCE;
                        })
                        .OrderBy(g => g.Key)
                        .ToList();

                    WriteMessage($"Phát hiện {storyGroups.Count} tầng.");

                    var usedHandles = new HashSet<string>();
                    var allBeamGroups = new List<Core.Data.BeamGroup>();

                    foreach (var storyGroup in storyGroups)
                    {
                        double storyZ = storyGroup.Key;
                        var beamsInStory = storyGroup.ToList();

                        // 3.2. Group ALL beams by collinearity + connectivity
                        // (No AxisName-first grouping - group by geometry only!)
                        foreach (var beam in beamsInStory)
                        {
                            if (usedHandles.Contains(beam.Handle)) continue;

                            bool isXDirection = beam.IsXDirection;

                            // Start new group
                            var localGroup = new List<Core.Data.BeamGeometry> { beam };
                            usedHandles.Add(beam.Handle);

                            // Find ALL collinear + connected beams (regardless of AxisName)
                            bool foundNew;
                            do
                            {
                                foundNew = false;
                                foreach (var other in beamsInStory)
                                {
                                    if (usedHandles.Contains(other.Handle)) continue;

                                    // Must have same direction
                                    if (isXDirection != other.IsXDirection) continue;

                                    // Check collinearity
                                    bool collinear;
                                    if (isXDirection)
                                    {
                                        // X-direction beams: check Y-axis alignment
                                        collinear = localGroup.Any(g =>
                                            System.Math.Abs(g.CenterY - other.CenterY) < AXIS_TOLERANCE);
                                    }
                                    else
                                    {
                                        // Y-direction beams: check X-axis alignment
                                        collinear = localGroup.Any(g =>
                                            System.Math.Abs(g.CenterX - other.CenterX) < AXIS_TOLERANCE);
                                    }

                                    if (!collinear) continue;

                                    // Check connectivity (endpoints touch)
                                    bool connected;
                                    if (isXDirection)
                                    {
                                        connected = localGroup.Any(g =>
                                        {
                                            double gap1 = System.Math.Abs(g.EndX - other.StartX);
                                            double gap2 = System.Math.Abs(g.StartX - other.EndX);
                                            return gap1 < GAP_TOLERANCE || gap2 < GAP_TOLERANCE;
                                        });
                                    }
                                    else
                                    {
                                        connected = localGroup.Any(g =>
                                        {
                                            double gap1 = System.Math.Abs(g.EndY - other.StartY);
                                            double gap2 = System.Math.Abs(g.StartY - other.EndY);
                                            return gap1 < GAP_TOLERANCE || gap2 < GAP_TOLERANCE;
                                        });
                                    }

                                    if (connected)
                                    {
                                        localGroup.Add(other);
                                        usedHandles.Add(other.Handle);
                                        foundNew = true;
                                    }
                                }
                            } while (foundNew);

                            // Create group for 1+ beams (single beams also need calculation!)
                            if (localGroup.Count >= 1)
                            {
                                // Sort by primary coordinate
                                var sortedGroup = isXDirection
                                    ? localGroup.OrderBy(b => b.CenterX).ToList()
                                    : localGroup.OrderBy(b => b.CenterY).ToList();

                                // 3.3. Classify Girder/Beam by MAJORITY support type (using IsGirder property)
                                int girderCount = localGroup.Count(b => b.IsGirder);
                                string groupType = girderCount > localGroup.Count / 2 ? "Girder" : "Beam";

                                // Get AxisName from majority (for display, not grouping)
                                var axisNames = localGroup
                                    .Where(b => !string.IsNullOrEmpty(b.AxisName))
                                    .GroupBy(b => b.AxisName)
                                    .OrderByDescending(g => g.Count())
                                    .FirstOrDefault();
                                string majorityAxisName = axisNames?.Key ?? "";

                                // Create BeamGroup
                                var beamGroup = new Core.Data.BeamGroup
                                {
                                    AxisName = majorityAxisName,
                                    Direction = isXDirection ? "X" : "Y",
                                    GroupType = groupType,
                                    LevelZ = storyZ,
                                    Width = sortedGroup.Average(b => b.Width),
                                    Height = sortedGroup.Average(b => b.Height),
                                    EntityHandles = sortedGroup.Select(b => b.Handle).ToList(),
                                    Source = "Auto",
                                    IsSingleBeam = sortedGroup.Count == 1, // Mark single-beam groups
                                    // Store geometry center for NamingEngine sorting
                                    GeometryCenterX = sortedGroup.Average(b => b.CenterX),
                                    GeometryCenterY = sortedGroup.Average(b => b.CenterY),
                                    // Generate axis-based GroupName for display (Phase 3)
                                    GroupName = Core.Utils.GridUtils.GenerateGroupDisplayName(
                                        groupType,
                                        isXDirection ? "X" : "Y",
                                        majorityAxisName,
                                        sortedGroup.Count)
                                };

                                allBeamGroups.Add(beamGroup);

                                // Create Star Topology links
                                var motherObjId = handleToObjectId[sortedGroup[0].Handle];
                                var motherObj = tr.GetObject(motherObjId, OpenMode.ForWrite);

                                for (int i = 1; i < sortedGroup.Count; i++)
                                {
                                    var childObjId = handleToObjectId[sortedGroup[i].Handle];
                                    var childObj = tr.GetObject(childObjId, OpenMode.ForWrite);

                                    // Clear existing parent link first
                                    var childData = XDataUtils.ReadElementData(childObj);
                                    if (childData != null && !string.IsNullOrEmpty(childData.OriginHandle))
                                    {
                                        XDataUtils.UnregisterLink(childObj, childData.OriginHandle, tr);
                                    }

                                    // Register new link to Mother
                                    var result = XDataUtils.RegisterLink(childObj, motherObj, isReference: false, tr);
                                    if (result == LinkRegistrationResult.Primary || result == LinkRegistrationResult.AlreadyLinked)
                                    {
                                        totalLinks++;
                                    }
                                }

                                // Visual feedback
                                var childObjIds = sortedGroup.Skip(1)
                                    .Select(b => handleToObjectId[b.Handle])
                                    .ToList();

                                VisualUtils.HighlightObject(motherObjId, 4); // Cyan
                                VisualUtils.HighlightObjects(childObjIds, 3); // Green
                                VisualUtils.DrawLinkLines(motherObjId, childObjIds, 3);

                                // Display with axis info if available
                                string axisInfo = string.IsNullOrEmpty(majorityAxisName) ? "" : $" [{majorityAxisName}]";
                                WriteMessage($"  Nhóm {groupCount + 1}: {sortedGroup.Count} dầm ({groupType}{axisInfo} @Z={storyZ:F0})");
                                groupCount++;
                            }
                        }
                    }

                    // 4. Auto Labeling với NamingEngine
                    if (allBeamGroups.Count > 0)
                    {
                        var settings = DtsSettings.Instance;
                        Core.Algorithms.NamingEngine.AutoLabeling(allBeamGroups, settings);
                        WriteMessage($"  → Đã đặt tên cho {allBeamGroups.Count} nhóm với NamingEngine.");

                        // 5. Persist group names to mother beam XData
                        foreach (var group in allBeamGroups)
                        {
                            if (group.EntityHandles == null || group.EntityHandles.Count == 0) continue;
                            if (string.IsNullOrEmpty(group.Name)) continue;

                            // Get mother beam (first in EntityHandles)
                            string motherHandle = group.EntityHandles[0];
                            if (handleToObjectId.TryGetValue(motherHandle, out var motherObjId))
                            {
                                var motherObj = tr.GetObject(motherObjId, OpenMode.ForWrite);
                                var beamData = XDataUtils.ReadElementData(motherObj) as BeamData;
                                if (beamData != null)
                                {
                                    beamData.GroupLabel = group.Name;
                                    beamData.GroupType = group.GroupType;
                                    XDataUtils.WriteElementData(motherObj, beamData, tr);
                                }
                            }

                            // === DUAL-WRITE: Sync to NOD Registry ===
                            // This provides redundant storage for self-healing capabilities
                            Core.Engines.RegistryEngine.RegisterBeamGroup(
                                motherHandle: motherHandle,
                                groupName: group.GroupName,
                                name: group.Name,
                                groupType: group.GroupType,
                                direction: group.Direction,
                                axisName: group.AxisName,
                                levelZ: group.LevelZ,
                                width: group.Width,
                                height: group.Height,
                                childHandles: group.EntityHandles.Skip(1).ToList(),
                                tr: tr);
                        }
                        WriteMessage($"  → Đã lưu tên nhóm vào XData + NOD Registry.");
                    }
                });

                if (groupCount > 0)
                {
                    WriteSuccess($"✅ Đã tạo {groupCount} nhóm với tổng {totalLinks} liên kết Star Topology.");
                    WriteMessage("Chạy DTS_REBAR_CALCULATE hoặc DTS_REBAR_VIEWER để làm việc với các nhóm này.");
                }
                else
                {
                    WriteMessage("Không tìm thấy nhóm dầm nào phù hợp để gom.");
                }

                WriteMessage("\n(Sử dụng DTS_CLEAR_VISUAL để xóa hiển thị tạm thời)");
            });
        }

        #endregion
    }
}
