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
                WriteMessage("\n=== GỠ LIÊN KẾT CỤ THỂ ===");

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
    }
}
