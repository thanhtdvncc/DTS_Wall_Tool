using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
// KẾT NỐI VỚI THƯ VIỆN CỦA CHÚNG TA (Folder Core)
using DTS_Wall_Tool.Core;
using System;
using System.Collections.Generic;
using static DTS_Wall_Tool.Core.Geometry;

// Đăng ký class này chứa các lệnh cho AutoCAD
[assembly: CommandClass(typeof(DTS_Wall_Tool.MyCommands))]

namespace DTS_Wall_Tool
{
    public class MyCommands
    {
        // --- LỆNH 1: HELLO WORLD (Để test kết nối) ---
        [CommandMethod("DTS_HELLO")]
        public void HelloCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage("\nXin chào! C# Plugin đã chạy ngon lành.");
        }

        // --- LỆNH 2: QUÉT TƯỜNG (Dùng AcadUtils + Geometry) ---
        [CommandMethod("DTS_SCAN")]
        public void ScanWallsCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;

            ed.WriteMessage("\n--- Bắt đầu lệnh quét tường ---");

            // BƯỚC 1: Gọi hàm chọn đối tượng (từ AcadUtils.cs)
            // (Bạn không cần viết lại logic chọn lọc loằng ngoằng nữa)
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE");

            // Kiểm tra nếu không chọn được gì
            if (selectedIds.Count == 0)
            {
                ed.WriteMessage("\nBạn chưa chọn đối tượng nào.");
                return;
            }

            ed.WriteMessage($"\nĐã chọn được: {selectedIds.Count} đường line.");

            // BƯỚC 2: Mở Transaction để đọc dữ liệu
            AcadUtils.UsingTransaction(tr =>
            {
                int count = 0;
                ed.WriteMessage($"\n--- KẾT QUẢ QUÉT (Chỉ hiện tường có dữ liệu) ---");

                foreach (var id in selectedIds)
                {
                    Line lineEnt = tr.GetObject(id, OpenMode.ForRead) as Line;
                    if (lineEnt == null) continue;

                    count++;

                    // Đọc dữ liệu
                    WallData wData = XDataUtils.ReadWallData(lineEnt, tr);

                    // KIỂM TRA MỚI:
                    if (wData == null)
                    {
                        // Đây là line rác, bạn có thể chọn in ra hoặc bỏ qua
                        // ed.WriteMessage($"\nLine {count}: [Line thường - Không có dữ liệu]");
                    }
                    else
                    {
                        // Đây là tường xịn (có XData)
                        ed.WriteMessage($"\nLine {count}: L={lineEnt.Length:0.0} | {wData}");
                    }
                }
            });
        }

        // --- LỆNH 3: GÁN DỮ LIỆU (NÂNG CẤP: CHO PHÉP QUÉT CHỌN) ---
        // Thêm cờ 'UsePickSet' để cho phép bạn chọn đối tượng trước rồi mới gõ lệnh
        [CommandMethod("DTS_SET", CommandFlags.UsePickSet)]
        public void SetDataCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;

            ed.WriteMessage("\n--- Bắt đầu gán dữ liệu hàng loạt ---");

            // 1. Dùng bộ công cụ để chọn (Hỗ trợ quét chuột)
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE");

            // Nếu không chọn được gì thì thoát
            if (selectedIds.Count == 0)
            {
                ed.WriteMessage("\nBạn chưa chọn đối tượng nào.");
                return;
            }

            ed.WriteMessage($"\nĐang xử lý {selectedIds.Count} đối tượng...");

            // 2. Mở Transaction để ghi dữ liệu cho TẤT CẢ đối tượng được chọn
            AcadUtils.UsingTransaction(tr =>
            {
                int countSuccess = 0;

                // Tạo dữ liệu mẫu
                WallData demoData = new WallData
                {
                    Thickness = 220,
                    WallType = "W220",
                    LoadPattern = "SDL",
                    LoadValue = 8.5
                };

                // demoData.BaseZ vẫn là null, nghĩa là ta không muốn ghi đè Z cũ (nếu có)

                foreach (var id in selectedIds)
                {
                    Line lineEnt = tr.GetObject(id, OpenMode.ForWrite) as Line;
                    if (lineEnt != null)
                    {
                        // Dùng hàm SaveWallData mới (Cơ chế Merge)
                        XDataUtils.SaveWallData(lineEnt, demoData, tr);
                        countSuccess++;
                    }
                }
                // ...
            });
        }



        // --- LỆNH 4: KIỂM TRA KẾT NỐI SAP2000 ---
        [CommandMethod("DTS_TEST_SAP")]
        public void TestSapCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage("\n--- Đang kết nối SAP2000... ---");

            try
            {
                // Gọi hàm kết nối
                bool isConnected = SapUtils.Connect(out string msg);

                if (isConnected)
                {
                    ed.WriteMessage($"\n[OK] {msg}");
                    try
                    {
                        int frameCount = SapUtils.CountFrames();
                        ed.WriteMessage($"\nSố lượng thanh (Frame): {frameCount}");
                    }
                    catch (System.Exception exFrame)
                    {
                        ed.WriteMessage($"\n[Cảnh báo] Kết nối được nhưng không đếm được Frame: {exFrame.Message}");
                    }
                }
                else
                {
                    ed.WriteMessage($"\n[LỖI KẾT NỐI] {msg}");
                }
            }
            catch (System.IO.FileNotFoundException)
            {
                ed.WriteMessage($"\n[LỖI FILE] Không tìm thấy thư viện SAP2000v1.dll! Hãy kiểm tra Copy Local = True.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[LỖI NGHIÊM TRỌNG] {ex}"); // In toàn bộ lỗi
            }
        }



        // --- LỆNH 5: ĐỌC DỮ LIỆU DẦM TỪ SAP ---
        [CommandMethod("DTS_GET_FRAMES")]
        public void GetSapFramesCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;

            if (!SapUtils.Connect(out _)) return;

            ed.WriteMessage("\nĐang đọc dữ liệu từ SAP...");
            var frames = SapUtils.GetAllFramesGeometry();

            // Đếm số lượng dầm và cột
            int beamCount = 0;
            int colCount = 0;
            foreach (var f in frames)
            {
                if (f.IsVertical) colCount++; else beamCount++;
            }

            ed.WriteMessage($"\nTổng: {frames.Count} (Dầm: {beamCount}, Cột: {colCount})");
            ed.WriteMessage("\n--- CHI TIẾT 10 THANH ĐẦU TIÊN ---");

            int limit = 10;
            foreach (var fr in frames)
            {
                // In ra để kiểm tra (Sẽ thấy rõ đâu là Cột, đâu là Dầm)
                ed.WriteMessage($"\n{fr}");

                limit--;
                if (limit == 0) break;
            }
        }

        // --- LỆNH 6: TEST MAPPING TƯỜNG LÊN DẦM ---
        [CommandMethod("DTS_TEST_MAP")]
        public void TestMapCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;

            // 1. Chọn tường
            PromptEntityOptions opt = new PromptEntityOptions("\nChọn 1 đường tường để tìm dầm đỡ: ");
            opt.SetRejectMessage("\nChỉ chọn LINE.");
            opt.AddAllowedClass(typeof(Line), true);
            PromptEntityResult res = ed.GetEntity(opt);
            if (res.Status != PromptStatus.OK) return;

            // 2. Lấy dữ liệu Dầm từ SAP
            if (!SapUtils.Connect(out string msg))
            {
                ed.WriteMessage("\n[LỖI] Chưa bật SAP!");
                return;
            }

            ed.WriteMessage("\nĐang lấy dữ liệu dầm...");
            var frames = SapUtils.GetAllFramesGeometry();

            // 3. Tính toán Mapping
            AcadUtils.UsingTransaction(tr =>
            {
                // Lấy tọa độ tường
                AcadUtils.GetLinePoints(res.ObjectId, tr, out Point2D wStart, out Point2D wEnd);

                // Lấy cao độ Z của tường (Tạm thời lấy từ geometry của CAD, sau này lấy từ XData)
                // Vì Line 2D thường Z=0, bạn có thể phải nhập tay hoặc vẽ đúng cao độ
                Line l = tr.GetObject(res.ObjectId, OpenMode.ForRead) as Line;
                double wallZ = l.StartPoint.Z;

                // Nếu bạn vẽ 2D (Z=0) nhưng SAP ở cao độ 3600, hãy fix cứng để test:
                // wallZ = 3657.6; // Bỏ comment dòng này nếu muốn test ép Z

                ed.WriteMessage($"\nĐang tìm dầm đỡ cho tường tại Z={wallZ:0.0}...");

                var results = MappingEngine.FindSupportingFrames(wStart, wEnd, wallZ, frames);

                // 4. In kết quả
                if (results.Count == 0)
                {
                    ed.WriteMessage("\n[KHÔNG TÌM THẤY] Không có dầm nào đỡ tường này (hoặc lệch Z/vị trí).");
                }
                else
                {
                    ed.WriteMessage($"\n[KẾT QUẢ] Tìm thấy {results.Count} dầm đỡ:");
                    foreach (var r in results)
                    {
                        ed.WriteMessage($"\n + {r}");
                    }
                }
            });
        }


        // --- LỆNH: THIẾT LẬP GỐC TỌA ĐỘ TẦNG ---
        [CommandMethod("DTS_SET_ORIGIN")]
        public void SetOriginCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;

            // 1. Hỏi người dùng chọn điểm đặt gốc
            PromptPointOptions ptOpt = new PromptPointOptions("\nPick điểm đặt gốc tọa độ tầng (Ví dụ: Giao trục A-1): ");
            PromptPointResult ptRes = ed.GetPoint(ptOpt);
            if (ptRes.Status != PromptStatus.OK) return;

            // 2. Hỏi tên tầng
            PromptStringOptions strOpt = new PromptStringOptions("\nNhập tên tầng (VD: Tang 2): ")
            {
                AllowSpaces = true // Cho phép nhập dấu cách
            };
            PromptResult strRes = ed.GetString(strOpt);
            if (strRes.Status != PromptStatus.OK) return;

            // 3. Hỏi cao độ
            PromptDoubleOptions dblOpt = new PromptDoubleOptions("\nNhập cao độ Z của tầng này trong SAP (mm): ")
            {
                AllowNone = false
            };
            PromptDoubleResult dblRes = ed.GetDouble(dblOpt);
            if (dblRes.Status != PromptStatus.OK) return;

            // 4. Thực hiện vẽ và ghi dữ liệu
            AcadUtils.CreateLayer("dts_origin", 1); // Tạo layer màu đỏ (1)

            AcadUtils.UsingTransaction(tr =>
            {
                // Mở không gian vẽ (ModelSpace)
                BlockTable bt = (BlockTable)tr.GetObject(AcadUtils.Db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Vẽ vòng tròn
                Circle c = new Circle
                {
                    Center = ptRes.Value,
                    Radius = 500, // Bán kính 500mm cho dễ nhìn
                    Layer = "dts_origin"
                };

                // Thêm vào bản vẽ
                btr.AppendEntity(c);
                tr.AddNewlyCreatedDBObject(c, true);

                // Chuẩn bị dữ liệu Tầng
                StoryData sData = new StoryData
                {
                    StoryName = strRes.StringResult,
                    Elevation = dblRes.Value
                };

                // Ghi vào XData của vòng tròn
                XDataUtils.WriteStoryData(c, sData, tr);

                ed.WriteMessage($"\n[OK] Đã tạo gốc '{sData.StoryName}' tại cao độ Z={sData.Elevation}.");
            });
        }


        // --- LỆNH 2: LINK TƯỜNG VÀO GỐC (FIX LỖI eWasErased) ---
        [CommandMethod("DTS_LINK", CommandFlags.UsePickSet)]
        public void LinkStoryCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;

            // 1. Quét chọn (Hàm mới đã tự lọc bỏ layer rác)
            var selection = AcadUtils.SelectObjectsOnScreen("LINE,CIRCLE");
            if (selection.Count == 0) return;

            AcadUtils.CreateLayer("dts_linkmap", 2);

            AcadUtils.UsingTransaction(tr =>
            {
                string foundHandle = "";
                string storyInfo = "";
                Entity originEnt = null;
                Point2D pOrigin = new Point2D(0, 0);

                // 2. Tìm vòng tròn gốc
                foreach (var id in selection)
                {
                    // [QUAN TRỌNG] Kiểm tra sinh tồn trước khi sờ vào
                    if (id.IsErased) continue;

                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent is Circle)
                    {
                        StoryData sData = XDataUtils.ReadStoryData(ent, tr);
                        if (sData != null)
                        {
                            foundHandle = ent.Handle.ToString();
                            storyInfo = sData.ToString();
                            originEnt = ent;
                            pOrigin = AcadUtils.GetEntityCenter(ent);
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(foundHandle))
                {
                    ed.WriteMessage("\n[LỖI] Không tìm thấy Gốc (Circle) hợp lệ trong vùng chọn!");
                    return;
                }

                // 3. Link các tường
                int count = 0;
                int skipped = 0;

                WallData updateData = new WallData();
                updateData.OriginHandle = foundHandle;

                foreach (var id in selection)
                {
                    // [QUAN TRỌNG] Kiểm tra sinh tồn
                    if (id.IsErased) continue;
                    if (id == originEnt.ObjectId) continue;

                    Line line = tr.GetObject(id, OpenMode.ForRead) as Line;
                    if (line != null)
                    {
                        WallData existingData = XDataUtils.ReadWallData(line, tr);

                        // Nếu là line rác (không có dữ liệu DTS) -> Bỏ qua
                        if (existingData == null)
                        {
                            skipped++;
                            continue;
                        }

                        // Ghi và Vẽ
                        line.UpgradeOpen();
                        XDataUtils.SaveWallData(line, updateData, tr);

                        Point2D pWall = AcadUtils.GetEntityCenter(line);
                        AcadUtils.CreateVisualLine(pWall, pOrigin, "dts_linkmap", 2, tr);

                        count++;
                    }
                }

                ed.WriteMessage($"\n[OK] Đã liên kết {count} tường vào {storyInfo}.");
                if (skipped > 0) ed.WriteMessage($"\n(Đã bỏ qua {skipped} đường line thường).");
            });
        }

        // --- LỆNH 4: HIỂN THỊ LIÊN KẾT (BẢN FINAL - CHỐNG LẶP) ---
        [CommandMethod("DTS_SHOW_LINK", CommandFlags.UsePickSet)]
        public void ShowLinkCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;

            // 1. Quét chọn đối tượng
            var selection = AcadUtils.SelectObjectsOnScreen("LINE,CIRCLE");
            if (selection.Count == 0) return;

            // Xóa cũ & Tạo layer
            AcadUtils.ClearLayer("dts_linkmap");
            AcadUtils.ClearLayer("dts_highlight");
            AcadUtils.CreateLayer("dts_linkmap", 2);
            AcadUtils.CreateLayer("dts_highlight", 6);

            AcadUtils.UsingTransaction(tr =>
            {
                int linkCount = 0;

                // [BỘ NHỚ ĐỆM] Để chống lặp
                // HashSet lưu các cặp đã vẽ: "HandleCon_HandleMe"
                HashSet<string> drawnLinks = new HashSet<string>();

                // HashSet lưu các Mẹ đã Highlight
                HashSet<string> highlightedOrigins = new HashSet<string>();

                foreach (ObjectId id in selection)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    // --- TRƯỜNG HỢP A: TƯỜNG (CON) ---
                    WallData wData = XDataUtils.ReadWallData(ent, tr);
                    if (wData != null && !string.IsNullOrEmpty(wData.OriginHandle))
                    {
                        // Tìm ID Mẹ
                        ObjectId originId = AcadUtils.GetObjectIdFromHandle(wData.OriginHandle);

                        // Gọi hàm vẽ thông minh (có kiểm tra drawnLinks)
                        if (TryDrawLink(ent, originId, tr, drawnLinks))
                        {
                            linkCount++;
                            // Tiện tay Highlight Mẹ luôn (nếu chưa)
                            HighlightParent(originId, tr, highlightedOrigins);
                        }
                    }

                    // --- TRƯỜNG HỢP B: GỐC (MẸ) ---
                    StoryData sData = XDataUtils.ReadStoryData(ent, tr);
                    if (sData != null)
                    {
                        string parentHandle = ent.Handle.ToString();

                        // Highlight chính nó
                        HighlightParent(ent.ObjectId, tr, highlightedOrigins);

                        // Quét ngược tìm các Con
                        TypedValue[] filter = {
                            new TypedValue((int)DxfCode.Start, "LINE"),
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, "DTS_APP")
                        };

                        PromptSelectionResult allLines = ed.SelectAll(new SelectionFilter(filter));
                        if (allLines.Status == PromptStatus.OK)
                        {
                            foreach (ObjectId childId in allLines.Value.GetObjectIds())
                            {
                                Line childLine = tr.GetObject(childId, OpenMode.ForRead) as Line;
                                WallData childData = XDataUtils.ReadWallData(childLine, tr);

                                if (childData != null && childData.OriginHandle == parentHandle)
                                {
                                    // Gọi hàm vẽ thông minh (Nó sẽ tự bỏ qua nếu Trường hợp A đã vẽ rồi)
                                    if (TryDrawLink(childLine, ent.ObjectId, tr, drawnLinks))
                                    {
                                        linkCount++;
                                    }
                                }
                            }
                        }
                    }
                }
                ed.WriteMessage($"\n[OK] Đã hiển thị {linkCount} đường liên kết (Không trùng lặp).");
            });
        }

        // --- HÀM PHỤ TRỢ 1: Vẽ Link có kiểm tra ---
        private bool TryDrawLink(Entity child, ObjectId parentId, Transaction tr, HashSet<string> drawnSet)
        {
            if (parentId == ObjectId.Null || parentId.IsErased) return false;

            string parentHandle = parentId.Handle.ToString();
            string childHandle = child.Handle.ToString();

            // Tạo khóa duy nhất: "HandleCon_HandleMe"
            string linkKey = $"{childHandle}_{parentHandle}";

            // Kiểm tra xem đã vẽ chưa?
            if (drawnSet.Contains(linkKey)) return false; // Đã vẽ rồi -> Bỏ qua

            // Chưa vẽ -> Tiến hành vẽ
            Entity parent = tr.GetObject(parentId, OpenMode.ForRead) as Entity;
            Point2D pStart = AcadUtils.GetEntityCenter(child);
            Point2D pEnd = AcadUtils.GetEntityCenter(parent);

            AcadUtils.CreateVisualLine(pStart, pEnd, "dts_linkmap", 2, tr);

            // Đánh dấu là đã vẽ
            drawnSet.Add(linkKey);
            return true;
        }

        // --- HÀM PHỤ TRỢ 2: Highlight Mẹ có kiểm tra ---
        private void HighlightParent(ObjectId parentId, Transaction tr, HashSet<string> highlightedSet)
        {
            if (parentId == ObjectId.Null || parentId.IsErased) return;

            string key = parentId.Handle.ToString();
            if (highlightedSet.Contains(key)) return; // Đã highlight rồi

            Entity parent = tr.GetObject(parentId, OpenMode.ForRead) as Entity;
            Point2D center = AcadUtils.GetEntityCenter(parent);

            AcadUtils.CreateHighlightCircle(center, 600, "dts_highlight", 6, tr);

            highlightedSet.Add(key);
        }

        // --- LỆNH 5: XÓA HIỂN THỊ LINK (CLEAR LINK) ---
        [CommandMethod("DTS_CLEAR_LINK")]
        public void ClearLinkCommand()
        {
            AcadUtils.ClearLayer("dts_linkmap");
            AcadUtils.ClearLayer("dts_highlight");
            Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n[OK] Đã dọn sạch các đường liên kết.");
        }

        // --- LỆNH 6: NGẮT KẾT NỐI (BREAK LINK) ---
        [CommandMethod("DTS_BREAK_LINK", CommandFlags.UsePickSet)]
        public void BreakLinkCommand()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;

            // 1. Chọn các tường cần ngắt link
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE");
            if (selectedIds.Count == 0) return;

            AcadUtils.UsingTransaction(tr =>
            {
                int count = 0;

                // Tạo dữ liệu update: Gán OriginHandle thành chuỗi rỗng ""
                WallData breakData = new WallData();
                breakData.OriginHandle = ""; // Chuỗi rỗng sẽ xóa link cũ

                foreach (var id in selectedIds)
                {
                    Line wall = tr.GetObject(id, OpenMode.ForWrite) as Line;
                    if (wall != null)
                    {
                        // Gọi hàm Save (Merge) sẽ ghi đè OriginHandle thành rỗng
                        XDataUtils.SaveWallData(wall, breakData, tr);
                        count++;
                    }
                }

                ed.WriteMessage($"\n[OK] Đã ngắt kết nối cho {count} bức tường.");
            });
        }











    }
}