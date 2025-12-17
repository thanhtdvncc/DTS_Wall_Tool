using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Algorithms;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Engines;
using DTS_Engine.Core.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DTS_Engine.Commands
{
    public class RebarCommands : CommandBase
    {
        /// <summary>
        /// [INTERNAL] Import kết quả từ SAP2000 - được gọi bởi DTS_REBAR_IMPORT_SAP
        /// </summary>
        private void ImportSapResultInternal()
        {
            WriteMessage("=== REBAR: LẤY KẾT QUẢ TỪ SAP2000 ===");

            // 1. Check Connection
            if (!SapUtils.IsConnected)
            {
                if (!SapUtils.Connect(out string msg))
                {
                    WriteError(msg);
                    return;
                }
            }

            SapDesignEngine engine = new SapDesignEngine();
            if (!engine.IsReady)
            {
                WriteError("Không thể khởi tạo SAP Design Engine.");
                return;
            }

            // 2. Ask Display Mode
            // 0 = Combined (Flex + Torsion) - Default
            // 1 = Flex only (Thép dọc chịu uốn)
            // 2 = Torsion only (Thép xoắn)
            // 3 = Stirrup/Web (Thép đai/Sườn)
            var ed = AcadUtils.Ed;
            var pIntOpt = new PromptIntegerOptions("\nChọn chế độ hiển thị [0=Tổng hợp | 1=Thép dọc | 2=Thép xoắn | 3=Thép Đai/Sườn]: ");
            pIntOpt.AllowNone = true;
            pIntOpt.DefaultValue = 0;
            pIntOpt.AllowNegative = false;
            pIntOpt.LowerLimit = 0;
            pIntOpt.UpperLimit = 3;

            var pIntRes = ed.GetInteger(pIntOpt);
            int displayMode = 0; // Default = Combined
            if (pIntRes.Status == PromptStatus.OK)
                displayMode = pIntRes.Value;
            else if (pIntRes.Status != PromptStatus.None)
                return; // User cancelled

            // 3. Select Frames on Screen
            // 3. Select Frames on Screen
            WriteMessage("\nChọn các đường Dầm (Frame) để lấy nội lực: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // 4. Clear old rebar labels on layer "dts_rebar_text"
            WriteMessage("Đang xóa label cũ...");
            // Clear existing labels for SELECTED beams only (refresh)
            var selectedHandles = selectedIds.Select(id => id.Handle.ToString()).ToList();
            ClearRebarLabels(selectedHandles);

            // 5. Smart Mapping Strategy:
            //    - Priority 1: XData-based (from DTS_PLOT_FROM_SAP / DTS_LINK)
            //    - Priority 2: Coordinate matching (legacy/hand-drawn beams)
            WriteMessage("Đang ánh xạ phần tử CAD → SAP ...");

            var allSapFrames = SapUtils.GetAllFramesGeometry();

            List<string> matchedNames = new List<string>();
            Dictionary<ObjectId, string> cadToSap = new Dictionary<ObjectId, string>();
            Dictionary<ObjectId, string> mappingSources = new Dictionary<ObjectId, string>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in selectedIds)
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    var curve = obj as Curve;
                    if (curve == null) continue;

                    string sapName = null;
                    string mappingSource = "Coordinate";

                    // === INFO LOGGING ===
                    // WriteMessage($"Processing Handle: {obj.Handle}...");

                    // === PRIORITY 1: Try SapFrameName from XData (set by DTS_PLOT_FROM_SAP) ===
                    var existingData = XDataUtils.ReadElementData(obj);

                    if (existingData != null && existingData.HasSapFrame)
                    {
                        sapName = existingData.SapFrameName;
                        mappingSource = "XData";
                        // WriteMessage($" -> Match via SapFrameName: {sapName}");
                    }
                    // === PRIORITY 2: BeamResultData already has SapElementName from previous run ===
                    else if (existingData is BeamResultData rebarData && !string.IsNullOrEmpty(rebarData.SapElementName))
                    {
                        sapName = rebarData.SapElementName;
                        mappingSource = "XData";
                        // WriteMessage($" -> Match via BeamResultData.SapElementName: {sapName}");
                    }
                    // === NO MATCH (missing XData or not plotted from SAP) ===
                    else
                    {
                        // Highlight this object as unmapped
                        WriteMessage($" -> Không tìm thấy SapFrameName trong XData. Cần chạy lại DTS_PLOT_FROM_SAP.");
                    }

                    if (!string.IsNullOrEmpty(sapName))
                    {
                        matchedNames.Add(sapName);
                        cadToSap[id] = sapName;
                        mappingSources[id] = mappingSource;
                    }
                }
            });

            if (matchedNames.Count == 0)
            {
                WriteError("Không tìm thấy dầm SAP nào khớp với lựa chọn trên CAD.");
                return;
            }

            WriteMessage($"Đã khớp {matchedNames.Count} dầm. Đang lấy kết quả thiết kế...");

            // 6. Call Engine to get Results
            var results = engine.GetBeamResults(matchedNames);

            if (results.Count == 0)
            {
                WriteError("Không lấy được kết quả thiết kế. Kiểm tra xem đã chạy Design Concrete chưa.");
                return;
            }

            // 7. Update XData and Plot Labels based on displayMode
            int successCount = 0;
            int insufficientCount = 0; // NEW: Track beams where Aprov < Areq
            var insufficientBeamIds = new List<ObjectId>(); // NEW: For highlighting
            var settings = RebarSettings.Instance;

            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (var kvp in cadToSap)
                {
                    ObjectId cadId = kvp.Key;
                    string sapName = kvp.Value;

                    if (results.TryGetValue(sapName, out var designData))
                    {
                        try
                        {
                            designData.TorsionFactorUsed = settings.TorsionFactorTop;

                            // Store mapping info for future use
                            designData.SapElementName = sapName;
                            designData.MappingSource = mappingSources.TryGetValue(cadId, out var src) ? src : "XData";

                            // Validate ObjectId before accessing
                            if (!cadId.IsValid || cadId.IsErased)
                            {
                                WriteMessage($" -> ObjectId không hợp lệ: {sapName}");
                                continue;
                            }

                            // Step 1: Get object
                            DBObject obj = null;
                            try
                            {
                                obj = tr.GetObject(cadId, OpenMode.ForWrite);
                            }
                            catch (System.Exception ex1)
                            {
                                WriteMessage($" -> Lỗi GetObject {sapName}: {ex1.Message}");
                                continue;
                            }

                            try
                            {
                                // === NEW: Sync Highlight - Compare Areq_new vs Aprov_old ===
                                var existingData = XDataUtils.ReadElementData(obj) as BeamResultData;
                                if (existingData != null && existingData.TopAreaProv != null)
                                {
                                    // Check if existing Aprov is insufficient for new Areq
                                    bool isInsufficient = false;
                                    for (int i = 0; i < 3; i++)
                                    {
                                        double areqTop = designData.TopArea[i] + designData.TorsionArea[i] * settings.TorsionRatioTop;
                                        double areqBot = designData.BotArea[i] + designData.TorsionArea[i] * settings.TorsionRatioBot;

                                        if (existingData.TopAreaProv[i] < areqTop * 0.99 ||
                                            existingData.BotAreaProv[i] < areqBot * 0.99)
                                        {
                                            isInsufficient = true;
                                            break;
                                        }
                                    }

                                    if (isInsufficient)
                                    {
                                        insufficientBeamIds.Add(cadId);
                                        insufficientCount++;
                                    }
                                }
                                // === END Sync Highlight ===

                                XDataUtils.WriteElementData(obj, designData, tr);
                            }
                            catch (System.Exception ex2)
                            {
                                WriteMessage($" -> Lỗi WriteElementData {sapName}: {ex2.Message}");
                                continue;
                            }

                            // Calculate display values based on mode
                            double[] displayTop = new double[3];
                            double[] displayBot = new double[3];
                            string[] displayTopStr = new string[3];
                            string[] displayBotStr = new string[3];

                            try
                            {
                                // Validate arrays before access
                                if (designData.TopArea == null || designData.BotArea == null ||
                                    designData.TorsionArea == null || designData.ShearArea == null ||
                                    designData.TTArea == null)
                                {
                                    WriteMessage($" -> Lỗi {sapName}: Dữ liệu thiết kế không đầy đủ (null arrays)");
                                    continue;
                                }

                                for (int i = 0; i < 3; i++)
                                {
                                    switch (displayMode)
                                    {
                                        case 0: // Combined (Flex + Torsion phân bổ)
                                            displayTop[i] = designData.TopArea[i] + designData.TorsionArea[i] * settings.TorsionRatioTop;
                                            displayBot[i] = designData.BotArea[i] + designData.TorsionArea[i] * settings.TorsionRatioBot;
                                            displayTopStr[i] = FormatValue(displayTop[i]);
                                            displayBotStr[i] = FormatValue(displayBot[i]);
                                            break;
                                        case 1: // Flex only (Thép dọc chịu uốn thuần)
                                            displayTopStr[i] = FormatValue(designData.TopArea[i]);
                                            displayBotStr[i] = FormatValue(designData.BotArea[i]);
                                            break;
                                        case 2: // Torsion (Top=At/s, Bot=Al)
                                                // Top: TTArea = At/s (Đai xoắn trên đơn vị dài)
                                                // Bot: TorsionArea = Al (Tổng thép dọc xoắn)
                                            displayTopStr[i] = FormatValue(designData.TTArea[i]);
                                            displayBotStr[i] = FormatValue(designData.TorsionArea[i]);
                                            break;
                                        case 3: // Shear & Web (Top=Av/s, Bot=Al×SideRatio)
                                                // Top: ShearArea = Av/s (Đai cắt trên đơn vị dài)
                                                // Bot: TorsionArea × SideRatio = Thép dọc xoắn phân bổ cho sườn
                                            displayTopStr[i] = FormatValue(designData.ShearArea[i]);
                                            displayBotStr[i] = FormatValue(designData.TorsionArea[i] * settings.TorsionRatioSide);
                                            break;
                                    }
                                }
                            }
                            catch (System.Exception exCalc)
                            {
                                WriteMessage($" -> Lỗi tính toán {sapName}: {exCalc.Message}");
                                continue;
                            }

                            // Plot Labels - 6 positions (Start/Mid/End x Top/Bot)
                            try
                            {
                                var curve = obj as Curve;
                                if (curve == null)
                                {
                                    WriteMessage($" -> Lỗi {sapName}: Object không phải Curve");
                                    continue;
                                }
                                Point3d pStart = curve.StartPoint;
                                Point3d pEnd = curve.EndPoint;

                                for (int i = 0; i < 3; i++)
                                {
                                    // Plot with owner handle
                                    string ownerH = obj.Handle.ToString();
                                    LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, displayTopStr[i], i, true, ownerH);
                                    LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, displayBotStr[i], i, false, ownerH);
                                }
                            }
                            catch (System.Exception exPlot)
                            {
                                WriteMessage($" -> Lỗi vẽ label {sapName}: {exPlot.Message}");
                                continue;
                            }

                            successCount++;
                        }
                        catch (System.Exception ex)
                        {
                            WriteMessage($" -> Lỗi xử lý {sapName}: {ex.Message}");
                        }
                    }
                }
            });

            string[] modeNames = { "Tổng hợp", "Thép dọc", "Thép xoắn", "Thép Đai/Sườn" };
            WriteSuccess($"Đã cập nhật Label thép ({modeNames[displayMode]}) cho {successCount} dầm.");

            // === NEW: Highlight insufficient beams in RED ===
            if (insufficientCount > 0)
            {
                WriteMessage($"\n⚠️ CẢNH BÁO: Phát hiện {insufficientCount} dầm thiếu khả năng chịu lực sau khi cập nhật từ SAP!");
                WriteMessage("   Các dầm này đã được đổi sang MÀU ĐỎ trên bản vẽ (persistent).");
                WriteMessage("   Sau khi sửa, chạy DTS_REBAR_UPDATE để trả về màu ByLayer.");

                // Set PERSISTENT color (survives Regen/Pan/Zoom)
                int changed = VisualUtils.SetPersistentColors(insufficientBeamIds, 1); // 1 = Red
                WriteMessage($"   Đã đổi màu {changed}/{insufficientCount} dầm.");
            }
            // === END Sync Highlight ===

            // === AUTO-GROUP: Tự động gom nhóm sau khi import ===
            // Ngăn user chạy Viewer với dầm rời rạc
            if (successCount > 0)
            {
                WriteMessage("\n→ Đang tự động gom nhóm dầm...");
                try
                {
                    DTS_AUTO_GROUP();
                }
                catch (System.Exception exGroup)
                {
                    WriteMessage($"   Lỗi gom nhóm: {exGroup.Message}");
                }
            }
        }

        /// <summary>
        /// WORKFLOW: Import dữ liệu SAP + Tự động gom nhóm
        /// Kết hợp DTS_REBAR_SAP_RESULT + DTS_REBAR_GROUP_AUTO
        /// Tránh trường hợp user quên gom nhóm sau khi import
        /// </summary>
        [CommandMethod("DTS_REBAR_IMPORT_SAP")]
        public void DTS_REBAR_IMPORT_SAP()
        {
            WriteMessage("=== IMPORT SAP + AUTO GROUP ===");

            // Bước 1: Import dữ liệu từ SAP (gọi internal method)
            ImportSapResultInternal();

            // Bước 2: Tự động gom nhóm các dầm vừa import
            WriteMessage("\n→ Đang tự động gom nhóm dầm...");
            DTS_AUTO_GROUP();

            WriteSuccess("✅ Đã import dữ liệu SAP và gom nhóm tự động!");
        }

        /// <summary>
        /// Xóa label rebar theo danh sách owner handles (nếu null -> xóa hết)
        /// </summary>
        private void ClearRebarLabels(List<string> ownerHandles = null)
        {
            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                foreach (ObjectId id in btr)
                {
                    if (id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent != null && ent.Layer == "dts_rebar_text")
                    {
                        bool shouldDelete = false;

                        if (ownerHandles == null || ownerHandles.Count == 0)
                        {
                            shouldDelete = true;
                        }
                        else
                        {
                            // Check XData "xOwnerHandle"
                            var data = XDataUtils.GetRawData(ent);
                            if (data != null && data.TryGetValue("xOwnerHandle", out var ownerH))
                            {
                                if (ownerHandles.Contains(ownerH.ToString()))
                                    shouldDelete = true;
                            }
                        }

                        if (shouldDelete)
                        {
                            ent.UpgradeOpen();
                            ent.Erase();
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Format thông minh: F3 cho số nhỏ (shear cm2/cm như 0.067), F1 ceiling cho số lớn (area cm2)
        /// </summary>
        private string FormatValue(double val)
        {
            if (Math.Abs(val) < 0.0001) return "0";

            if (Math.Abs(val) < 1.0)
            {
                // Hiển thị dạng 0.067 (cho Shear Area/cm, TTArea)
                return val.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                // Hiển thị dạng 2.1, 15 (cho Longitudinal Area)
                double ceiling = Math.Ceiling(val * 10) / 10.0;
                return (ceiling % 1 == 0)
                    ? ceiling.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                    : ceiling.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        [CommandMethod("DTS_REBAR_CALCULATE_SETTING")]
        public void DTS_REBAR_CALCULATE_SETTING()
        {
            try
            {
                // Sử dụng RebarConfigDialog với WebView2 Modern UI
                var dialog = new DTS_Engine.UI.Forms.RebarConfigDialog();

                // ShowModalDialog giúp khóa CAD lại cho đến khi tắt form
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(dialog);
            }
            catch (System.Exception ex)
            {
                WriteError("Lỗi hệ thống UI: " + ex.Message);
            }
        }

        [CommandMethod("DTS_REBAR_CALCULATE")]
        public void DTS_REBAR_CALCULATE()
        {
            WriteMessage("=== REBAR: TÍNH TOÁN CỐT THÉP ===");

            // 1. Select
            WriteMessage("\nChọn các đường Dầm cần tính thép: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            int count = 0;
            RebarSettings settings = RebarSettings.Instance;

            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (ObjectId id in selectedIds)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForWrite);
                    var data = XDataUtils.ReadElementData(obj) as BeamResultData;

                    if (data == null) continue;

                    // Validate Dimensions - NO LONGER USE HARDCODED FALLBACK
                    if (data.Width <= 0 || data.SectionHeight <= 0)
                    {
                        // CRITICAL: Do not use hardcoded values, report error and skip
                        WriteMessage($" -> Lỗi: Dầm {data.SapElementName ?? "?"} thiếu tiết diện (Width={data.Width}, Height={data.SectionHeight}). Bỏ qua.");
                        continue;
                    }

                    // Calculate Rebar and update directly into data object
                    for (int i = 0; i < 3; i++)
                    {
                        // === Longitudinal Rebar ===
                        double asTop = data.TopArea[i] + data.TorsionArea[i] * settings.TorsionRatioTop;
                        double asBot = data.BotArea[i] + data.TorsionArea[i] * settings.TorsionRatioBot;

                        // LEGACY: Using old RebarSettings-based Calculate (marked Obsolete)
                        // TODO: Migrate to DtsSettings version in future refactoring
#pragma warning disable CS0618
                        string sTop = RebarCalculator.Calculate(asTop, data.Width * 10, data.SectionHeight * 10, settings);
                        string sBot = RebarCalculator.Calculate(asBot, data.Width * 10, data.SectionHeight * 10, settings);
#pragma warning restore CS0618

                        data.TopRebarString[i] = sTop;
                        data.BotRebarString[i] = sBot;
                        data.TopAreaProv[i] = RebarStringParser.Parse(sTop);
                        data.BotAreaProv[i] = RebarStringParser.Parse(sBot);

                        // === Stirrup (Thép đai) - ACI 318-19: Av/s + 2*At/s ===
                        // beamWidth (mm) = data.Width (cm) * 10
                        string sStirrup = RebarCalculator.CalculateStirrup(data.ShearArea[i], data.TTArea[i], data.Width * 10, settings);
                        data.StirrupString[i] = sStirrup;

                        // === Web Bars (Thép sườn) ===
                        // Dùng TorsionTotal và RatioSide từ settings
                        string sWeb = RebarCalculator.CalculateWebBars(data.TorsionArea[i], settings.TorsionRatioSide, data.SectionHeight * 10, settings);
                        data.WebBarString[i] = sWeb;
                    }

                    // Save updated data back to XData (preserves raw areas)
                    XDataUtils.UpdateElementData(obj, data, tr);

                    // Update Labels on screen
                    // Format: Top line = Longitudinal + Stirrup, Bot line = Longitudinal + WebBar
                    var curve = obj as Curve;
                    if (curve != null)
                    {
                        Point3d pStart = curve.StartPoint;
                        Point3d pEnd = curve.EndPoint;

                        for (int i = 0; i < 3; i++)
                        {
                            // Top: Thép dọc Top (dòng 1) + Thép đai (dòng 2)
                            // Dùng \P cho xuống dòng trong MText
                            string topText = data.TopRebarString[i] ?? "-";
                            if (!string.IsNullOrEmpty(data.StirrupString[i]) && data.StirrupString[i] != "-")
                                topText += "\\P" + data.StirrupString[i];

                            // Bot: Thép dọc Bot (dòng 1) + Thép sườn (dòng 2)
                            string botText = data.BotRebarString[i] ?? "-";
                            if (!string.IsNullOrEmpty(data.WebBarString[i]) && data.WebBarString[i] != "-")
                                botText += "\\P" + data.WebBarString[i];

                            // Plot with owner handle
                            string ownerH = obj.Handle.ToString();
                            LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, topText, i, true, ownerH);
                            LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, botText, i, false, ownerH);
                        }
                    }

                    count++;
                }
            });

            WriteSuccess($"Đã tính toán và cập nhật cho {count} dầm.");

            // ===== SYNC: Populate BeamGroups với kết quả tính toán =====
            // Để DTS_REBAR_VIEWER có dữ liệu để hiển thị
            SyncRebarCalculationsToGroups(selectedIds);
        }


        private bool IsSamePt(Core.Primitives.Point2D p2d, Point3d p3d, double tol = 200.0)
        {
            return Math.Abs(p2d.X - p3d.X) < tol && Math.Abs(p2d.Y - p3d.Y) < tol;
        }

        /// <summary>
        /// [FIXED] Đặt tên dầm thông minh:
        /// 1. Phân tách theo tầng (Level Z).
        /// 2. Sort theo không gian tuyệt đối (Trên->Dưới, Trái->Phải) dùng Row-Binning.
        /// 3. Tự động gom nhóm các dầm giống nhau (Tiết diện + Thép) để dùng chung tên.
        /// </summary>
        [CommandMethod("DTS_REBAR_BEAM_NAME")]
        public void DTS_REBAR_BEAM_NAME()
        {
            WriteMessage("=== SMART BEAM NAMING (SCANLINE SORT) ===");
            WriteMessage("\nChọn các đường Dầm cần đặt tên: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            var settings = RebarSettings.Instance;

            // 1. Thu thập dữ liệu dầm
            var allBeams = new List<(ObjectId Id, Point3d Mid, bool IsGirder, bool IsXDir, BeamResultData Data, double LevelZ)>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in selectedIds)
                {
                    var curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (curve == null) continue;

                    Point3d mid = curve.StartPoint + (curve.EndPoint - curve.StartPoint) * 0.5;
                    Vector3d dir = curve.EndPoint - curve.StartPoint;
                    bool isXDir = Math.Abs(dir.X) > Math.Abs(dir.Y);

                    var xdata = XDataUtils.ReadElementData(curve) as BeamResultData;

                    // Fix: Làm tròn Z để phân tầng
                    double levelZ = Math.Round(mid.Z / 100.0) * 100.0;

                    bool isGirder = false;
                    if (xdata != null)
                    {
                        // Ưu tiên lấy từ XData nếu đã chạy detect
                        // Nếu chưa, dùng heuristic đơn giản: Width >= 300 là Girder
                        isGirder = xdata.Width >= 300;
                    }

                    allBeams.Add((id, mid, isGirder, isXDir, xdata, levelZ));
                }
            });

            // 2. Xử lý từng tầng (Level Z)
            var beamsByLevel = allBeams.GroupBy(b => b.LevelZ).OrderBy(g => g.Key);

            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (var levelGroup in beamsByLevel)
                {
                    double currentZ = levelGroup.Key;
                    WriteMessage($"\n--- Đang xử lý Tầng Z={currentZ:F0} ---");

                    // Config naming cho tầng này
                    var storyConfig = DtsSettings.Instance.GetStoryConfig(currentZ);
                    string beamPrefix = storyConfig?.BeamPrefix ?? "B";
                    string girderPrefix = storyConfig?.GirderPrefix ?? "G";
                    string suffix = storyConfig?.Suffix ?? "";
                    int startIndex = storyConfig?.StartIndex ?? 1;

                    // Tách Dầm chính / Dầm phụ để đặt tên riêng
                    var girders = levelGroup.Where(b => b.IsGirder).ToList();
                    var beams = levelGroup.Where(b => !b.IsGirder).ToList();

                    // Hàm xử lý đặt tên cho một danh sách dầm (Generic)
                    void ProcessList(List<(ObjectId Id, Point3d Mid, bool IsGirder, bool IsXDir, BeamResultData Data, double LevelZ)> list, string prefix)
                    {
                        if (list.Count == 0) return;

                        // BƯỚC QUAN TRỌNG: SORT KHÔNG GIAN (Scanline Sort)
                        // Để đơn giản và chuẩn xác, ta dùng thuật toán Row-Binning (gom hàng)
                        // Binning: Các dầm có Y chênh lệch < 500mm coi như cùng 1 hàng.

                        double binTolerance = 500.0;

                        var sortedList = list.OrderByDescending(b => Math.Round(b.Mid.Y / binTolerance)) // Gom hàng Y trước (từ trên xuống)
                                             .ThenBy(b => b.Mid.X) // Trong cùng hàng, sort X tăng dần (từ trái qua)
                                             .ToList();

                        // Danh sách Assigned Types để Filter Re-use (WxH + Steel)
                        // Key: Identifier String -> Value: Assigned Number
                        var assignedTypes = new Dictionary<string, int>();
                        int nextNumber = startIndex;

                        foreach (var item in sortedList)
                        {
                            // Tạo Key định danh để so sánh giống nhau
                            string w = item.Data?.Width.ToString("F0") ?? "0";
                            string h = item.Data?.SectionHeight.ToString("F0") ?? "0";

                            // Lấy string thép (nếu null thì là "-")
                            string top = (item.Data?.TopRebarString != null && item.Data.TopRebarString.Length > 1) ? item.Data.TopRebarString[1] ?? "-" : "-";
                            string bot = (item.Data?.BotRebarString != null && item.Data.BotRebarString.Length > 1) ? item.Data.BotRebarString[1] ?? "-" : "-";
                            string stir = (item.Data?.StirrupString != null && item.Data.StirrupString.Length > 1) ? item.Data.StirrupString[1] ?? "-" : "-";

                            // Key để gom nhóm: WxH_Top_Bot_Stir
                            string typeKey = $"{w}x{h}_{top.Trim()}_{bot.Trim()}_{stir.Trim()}";

                            int number;
                            if (assignedTypes.ContainsKey(typeKey))
                            {
                                // Đã có dầm giống hệt -> Dùng lại số cũ
                                number = assignedTypes[typeKey];
                            }
                            else
                            {
                                // Chưa có -> Cấp số mới
                                number = nextNumber++;
                                assignedTypes[typeKey] = number;
                            }

                            string fullName = $"{prefix}{number}{suffix}";

                            // Vẽ Label và Cập nhật XData
                            var curve = tr.GetObject(item.Id, OpenMode.ForWrite) as Curve;
                            if (curve != null)
                            {
                                if (item.Data != null)
                                {
                                    item.Data.SapElementName = fullName; // Update name in XData
                                    XDataUtils.UpdateElementData(curve, item.Data, tr);
                                }

                                Point3d pStart = curve.StartPoint;
                                Point3d pEnd = curve.EndPoint;

                                LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, fullName, LabelPosition.MiddleBottom);
                            }
                        }
                    }

                    // Chạy đặt tên cho Girder và Beam riêng
                    ProcessList(girders, girderPrefix);
                    ProcessList(beams, beamPrefix);
                }
            });

            WriteSuccess("✅ Đã đặt tên dầm theo thứ tự không gian và gom nhóm (Scanline Sort).");
        }

        [CommandMethod("DTS_REBAR_EXPORT_SAP")]
        public void DTS_REBAR_EXPORT_SAP()
        {
            WriteMessage("=== REBAR: XUẤT THÉP VỀ SAP2000 ===");

            // 1. Check Connection
            if (!SapUtils.IsConnected)
            {
                if (!SapUtils.Connect(out string msg))
                {
                    WriteError(msg);
                    return;
                }
            }

            SapDesignEngine engine = new SapDesignEngine();
            if (!engine.IsReady)
            {
                WriteError("Không thể khởi tạo SAP Design Engine.");
                return;
            }

            // 2. Select Frames
            // 2. Select Objects
            WriteMessage("\nChọn các đường Dầm cần cập nhật về SAP: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // 3. Get SAP Frame Mapping (same as DTS_REBAR_SAP_RESULT)
            var allSapFrames = SapUtils.GetAllFramesGeometry();
            Dictionary<ObjectId, string> cadToSap = new Dictionary<ObjectId, string>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in selectedIds)
                {
                    var curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (curve == null) continue;

                    Point3d start = curve.StartPoint;
                    Point3d end = curve.EndPoint;

                    var match = allSapFrames.FirstOrDefault(f =>
                        (IsSamePt(f.StartPt, start) && IsSamePt(f.EndPt, end)) ||
                        (IsSamePt(f.StartPt, end) && IsSamePt(f.EndPt, start))
                    );

                    if (match != null)
                    {
                        cadToSap[id] = match.Name;
                    }
                }
            });

            if (cadToSap.Count == 0)
            {
                WriteError("Không tìm thấy dầm SAP nào khớp.");
                return;
            }

            // 4. Read XData and Update SAP
            int successCount = 0;
            int failCount = 0;
            RebarSettings settings = RebarSettings.Instance;

            UsingTransaction(tr =>
            {
                foreach (var kvp in cadToSap)
                {
                    ObjectId cadId = kvp.Key;
                    string sapName = kvp.Value;

                    DBObject obj = tr.GetObject(cadId, OpenMode.ForRead);
                    var data = XDataUtils.ReadElementData(obj) as BeamResultData;

                    if (data == null)
                    {
                        failCount++;
                        continue;
                    }

                    // === FIX Issue 3: Protect user data - Check NOD first ===
                    // Priority: NOD_BEAM_GROUPS (user edited) > XData > Recalculate

                    // 1. Validate dimensions first
                    if (data.Width <= 0 || data.SectionHeight <= 0)
                    {
                        WriteMessage($" -> Lỗi: Dầm {sapName} thiếu tiết diện. Bỏ qua.");
                        failCount++;
                        continue;
                    }

                    // 2. Check if beam exists in NOD (user has edited in BeamGroupViewer)
                    bool hasNodData = false;
                    string nodJson = XDataUtils.LoadBeamGroupsFromNOD(AcadUtils.Db, tr);
                    if (!string.IsNullOrEmpty(nodJson))
                    {
                        try
                        {
                            var nodGroups = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BeamGroup>>(nodJson);
                            if (nodGroups != null)
                            {
                                foreach (var group in nodGroups)
                                {
                                    // Match by SpanId or by physical segment SAP name
                                    var matchingSpan = group.Spans?.FirstOrDefault(s =>
                                        s.SpanId == sapName ||
                                        s.Segments?.Any(seg => seg.SapFrameName == sapName) == true);
                                    if (matchingSpan != null)
                                    {
                                        // Found in NOD - use user's choice
                                        // TopRebar is [layer, position] - get layer 0, positions 0,1,2
                                        if (matchingSpan.TopRebar != null)
                                        {
                                            for (int i = 0; i < 3; i++)
                                            {
                                                string topStr = matchingSpan.TopRebar[0, i];
                                                string botStr = matchingSpan.BotRebar?[0, i];
                                                if (!string.IsNullOrEmpty(topStr))
                                                    data.TopAreaProv[i] = RebarStringParser.Parse(topStr);
                                                if (!string.IsNullOrEmpty(botStr))
                                                    data.BotAreaProv[i] = RebarStringParser.Parse(botStr);
                                            }
                                            hasNodData = true;
                                            WriteMessage($"   {sapName}: Sử dụng dữ liệu từ BeamGroupViewer (user edited)");
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        catch { /* JSON parse error - continue without NOD data */ }
                    }

                    // 3. Check XData (already calculated)
                    if (!hasNodData && (data.TopAreaProv == null || data.TopAreaProv[0] <= 0))
                    {
                        // 4. Last resort: Re-calculate (only if no user data exists)
                        WriteMessage($"   {sapName}: Không có dữ liệu user, tính toán lại...");
                        for (int i = 0; i < 3; i++)
                        {
                            double asTop = data.TopArea[i] + data.TorsionArea[i] * settings.TorsionRatioTop;
                            double asBot = data.BotArea[i] + data.TorsionArea[i] * settings.TorsionRatioBot;

#pragma warning disable CS0618 // Legacy Calculate - TODO: migrate to DtsSettings
                            string sTop = RebarCalculator.Calculate(asTop, data.Width * 10, data.SectionHeight * 10, settings);
                            string sBot = RebarCalculator.Calculate(asBot, data.Width * 10, data.SectionHeight * 10, settings);
#pragma warning restore CS0618

                            data.TopAreaProv[i] = RebarStringParser.Parse(sTop);
                            data.BotAreaProv[i] = RebarStringParser.Parse(sBot);
                        }
                    }
                    // === END Fix Issue 3 ===

                    // Use calculated values from data
                    double[] topProv = data.TopAreaProv;
                    double[] botProv = data.BotAreaProv;

                    // Create Section Name based on convention
                    // Format: [SapName]_[WxH]_[Top0]_[Top2]_[Bot0]_[Bot2]
                    string newSectionName = $"{sapName}_{(int)data.Width}x{(int)data.SectionHeight}_{(int)topProv[0]}_{(int)topProv[2]}_{(int)botProv[0]}_{(int)botProv[2]}";

                    // Limit length for SAP
                    if (newSectionName.Length > 31)
                    {
                        // Truncate to fit SAP limit
                        newSectionName = newSectionName.Substring(0, 31);
                    }

                    bool success = engine.UpdateBeamRebar(
                        sapName,
                        newSectionName,
                        topProv,
                        botProv,
                        settings.CoverTop,
                        settings.CoverBot
                    );

                    if (success)
                    {
                        successCount++;

                        // FIX DATA DESYNC: Save calculated data back to XData
                        // This ensures Sync Highlight works correctly next time
                        XDataUtils.UpdateElementData(obj, data, tr);
                    }
                    else
                        failCount++;
                }
            });

            if (failCount > 0)
                WriteMessage($"Cảnh báo: {failCount} dầm không thể cập nhật.");

            // Reset color to ByLayer for successfully updated beams
            // (they were marked RED by DTS_REBAR_SAP_RESULT if insufficient)
            if (successCount > 0)
            {
                var successIds = cadToSap.Keys.ToList();
                int resetCount = VisualUtils.ResetToByLayer(successIds);
                if (resetCount > 0)
                    WriteMessage($"   Đã reset màu {resetCount} dầm về ByLayer.");
            }

            WriteSuccess($"Đã cập nhật {successCount} dầm về SAP2000.");
        }

        /// <summary>
        /// SMART SECTION SYNC: Đồng bộ sections SAP2000 theo BeamGroup names.
        /// - Tạo sections mới nếu chưa có
        /// - Cập nhật dimensions nếu khác
        /// - Xóa sections rác không còn sử dụng
        /// </summary>
        [CommandMethod("DTS_SYNC_SAP_SECTIONS")]
        public void DTS_SYNC_SAP_SECTIONS()
        {
            WriteMessage("=== SMART SECTION SYNC: SAP2000 ===");

            // 1. Check SAP Connection
            if (!SapUtils.IsConnected)
            {
                if (!SapUtils.Connect(out string msg))
                {
                    WriteError(msg);
                    return;
                }
            }

            var engine = new SapDesignEngine();
            if (!engine.IsReady)
            {
                WriteError("Không thể khởi tạo SAP Design Engine.");
                return;
            }

            // 2. Get all BeamGroups from DWG
            var groups = GetOrCreateBeamGroups();
            if (groups.Count == 0)
            {
                WriteMessage("Không có BeamGroup nào trong bản vẽ.");
                return;
            }

            WriteMessage($"Tìm thấy {groups.Count} BeamGroups trong bản vẽ.");

            // 3. Get material (hardcoded for now, TODO: add to settings)
            string material = "C25";

            // 4. Sync sections
            int created = 0, updated = 0, noChange = 0, failed = 0;

            foreach (var group in groups)
            {
                // Skip unnamed groups
                if (string.IsNullOrEmpty(group.Name))
                {
                    failed++;
                    continue;
                }

                var result = engine.EnsureSection(group.Name, group.Width, group.Height, material);

                if (result.Success)
                {
                    switch (result.Action)
                    {
                        case SectionAction.Created:
                            created++;
                            WriteMessage($"  [+] {result.Message}");
                            break;
                        case SectionAction.Updated:
                            updated++;
                            WriteMessage($"  [~] {result.Message}");
                            break;
                        case SectionAction.NoChange:
                            noChange++;
                            break;
                    }
                }
                else
                {
                    failed++;
                    WriteError($"  [!] {group.Name}: {result.Message}");
                }
            }

            // 5. Ask about cleanup
            var ed = AcadUtils.Ed;
            var cleanupOpt = new PromptKeywordOptions("\nXóa sections không còn sử dụng? [Yes/No] <No>: ");
            cleanupOpt.Keywords.Add("Yes");
            cleanupOpt.Keywords.Add("No");
            cleanupOpt.Keywords.Default = "No";
            cleanupOpt.AllowNone = true;

            var cleanupRes = ed.GetKeywords(cleanupOpt);
            if (cleanupRes.Status == PromptStatus.OK && cleanupRes.StringResult == "Yes")
            {
                int deletedCount = engine.CleanupUnusedSections(null);
                if (deletedCount > 0)
                {
                    WriteMessage($"\n🗑️ Đã xóa {deletedCount} sections rác.");
                }
                else
                {
                    WriteMessage("Không có section rác cần xóa.");
                }
            }

            // 6. Summary
            WriteSuccess($"\n=== KẾT QUẢ SYNC ===");
            WriteMessage($"  ✅ Tạo mới: {created} sections");
            WriteMessage($"  🔄 Cập nhật: {updated} sections");
            WriteMessage($"  ⏭️ Không đổi: {noChange} sections");
            if (failed > 0)
                WriteError($"  ❌ Thất bại: {failed} sections");
        }

        [CommandMethod("DTS_REBAR_SHOW")]
        public void DTS_REBAR_SHOW()
        {
            WriteMessage("=== REBAR: CHUYỂN ĐỔI CHẾ ĐỘ HIỂN THỊ ===");

            // Chọn chế độ hiển thị (Updated per spec)
            var ed = AcadUtils.Ed;
            var pIntOpt = new PromptIntegerOptions("\nChọn chế độ hiển thị [0=Thép dọc | 1=Đai/Sườn | 2=Dọc+Area | 3=Đai/Sườn+Area]: ");
            pIntOpt.AllowNone = true;
            pIntOpt.DefaultValue = 0;
            pIntOpt.AllowNegative = false;
            pIntOpt.LowerLimit = 0;
            pIntOpt.UpperLimit = 3;

            var pIntRes = ed.GetInteger(pIntOpt);
            int mode = 0; // Default = Rebar Strings
            if (pIntRes.Status == PromptStatus.OK)
                mode = pIntRes.Value;
            else if (pIntRes.Status != PromptStatus.None)
                return;

            // Select Frames
            // 1. Select Objects
            WriteMessage("\nChọn các đường Dầm cần hiển thị: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // Clear existing labels for SELECTED beams only (refresh)
            var selectedHandles = selectedIds.Select(id => id.Handle.ToString()).ToList();
            ClearRebarLabels(selectedHandles);

            int count = 0;
            var settings = RebarSettings.Instance;

            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (ObjectId id in selectedIds)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    var data = XDataUtils.ReadElementData(obj) as BeamResultData;
                    if (data == null) continue;

                    var curve = obj as Curve;
                    if (curve == null) continue;

                    Point3d pStart = curve.StartPoint;
                    Point3d pEnd = curve.EndPoint;

                    for (int i = 0; i < 3; i++)
                    {
                        string topText = "-";
                        string botText = "-";

                        switch (mode)
                        {
                            case 0: // Bố trí thép dọc (Top/Bot Rebar Strings)
                                topText = data.TopRebarString[i] ?? "-";
                                botText = data.BotRebarString[i] ?? "-";
                                break;

                            case 1: // Bố trí thép đai/sườn
                                topText = data.StirrupString[i] ?? "-";
                                botText = data.WebBarString[i] ?? "-";
                                break;

                            case 2: // Thép dọc + Area so sánh (Aprov/Areq)
                                {
                                    double asReqTop = data.TopArea[i] + data.TorsionArea[i] * settings.TorsionRatioTop;
                                    double asReqBot = data.BotArea[i] + data.TorsionArea[i] * settings.TorsionRatioBot;
                                    string topRebar = data.TopRebarString?[i] ?? "-";
                                    string botRebar = data.BotRebarString?[i] ?? "-";
                                    // Parse Aprov từ rebar string thay vì dùng TopAreaProv
                                    double asProvTop = RebarCalculator.ParseRebarArea(topRebar);
                                    double asProvBot = RebarCalculator.ParseRebarArea(botRebar);
                                    // Format: Aprov/Areq \n RebarString
                                    topText = $"{FormatValue(asProvTop)}/{FormatValue(asReqTop)}\\P{topRebar}";
                                    botText = $"{FormatValue(asProvBot)}/{FormatValue(asReqBot)}\\P{botRebar}";
                                }
                                break;

                            case 3: // Thép đai/sườn + Area so sánh
                                {
                                    // Top: Stirrup - Aprov/Areq(2At/s)
                                    // Null-safe access
                                    double avs = data.ShearArea?[i] ?? 0;
                                    double ats = data.TTArea?[i] ?? 0;
                                    double stirrupReq = avs + 2 * ats; // Atotal/s
                                    string stirrupStr = data.StirrupString?[i] ?? "-";
                                    // Parse Aprov từ stirrup string (e.g., "d10a150")
                                    double stirrupProv = RebarCalculator.ParseStirrupAreaPerLen(stirrupStr);
                                    topText = $"{FormatValue(stirrupProv)}/{FormatValue(stirrupReq)}({FormatValue(2 * ats)})\\P{stirrupStr}";

                                    // Bot: Web - Aprov/Areq (Areq = TorsionArea × SideRatio)
                                    double webReq = (data.TorsionArea?[i] ?? 0) * settings.TorsionRatioSide;
                                    string webStr = data.WebBarString?[i] ?? "-";
                                    // Parse Aprov từ web string (e.g., "2d12")
                                    double webProv = RebarCalculator.ParseRebarArea(webStr);
                                    botText = $"{FormatValue(webProv)}/{FormatValue(webReq)}\\P{webStr}";
                                }
                                break;
                        }

                        string ownerH = obj.Handle.ToString();
                        LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, topText, i, true, ownerH);
                        LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, botText, i, false, ownerH);
                    }

                    count++;
                }
            });

            string[] modeNames = { "Thép dọc", "Đai/Sườn", "Dọc+Area", "Đai/Sườn+Area" };
            WriteSuccess($"Đã hiển thị {count} dầm theo chế độ: {modeNames[mode]}.");
        }

        /// <summary>
        /// Mở BeamGroupViewer để xem/chỉnh sửa nhóm dầm liên tục
        /// </summary>
        [CommandMethod("DTS_REBAR_VIEWER")]
        public void DTS_BEAM_VIEWER()
        {
            WriteMessage("Loading Beam Group Viewer...");

            try
            {
                // Get cached beam groups or create empty list
                var groups = GetOrCreateBeamGroups();

                // Show viewer dialog as MODELESS to allow CAD interaction
                var dialog = new UI.Forms.BeamGroupViewerDialog(groups, ApplyBeamGroupResults);
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(dialog);
            }
            catch (System.Exception ex)
            {
                WriteError($"Lỗi mở Beam Viewer: {ex.Message}");
            }
        }

        /// <summary>
        /// Command cho phép User chọn dầm và tạo nhóm thủ công
        /// </summary>
        [CommandMethod("DTS_REBAR_GROUP_MANUAL")]
        public void DTS_SET_BEAM()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            WriteMessage("Chọn các dầm để tạo nhóm liên tục...");

            // Prompt selection
            var opts = new PromptSelectionOptions()
            {
                MessageForAdding = "\nChọn các dầm (LINE/POLYLINE):"
            };

            var result = ed.GetSelection(opts);
            if (result.Status != PromptStatus.OK)
            {
                WriteMessage("Đã hủy chọn.");
                return;
            }

            // Prompt for group name
            var nameOpts = new PromptStringOptions("\nNhập tên nhóm:")
            {
                AllowSpaces = true,
                DefaultValue = "NewGroup"
            };
            var nameResult = ed.GetString(nameOpts);
            if (nameResult.Status != PromptStatus.OK)
            {
                WriteMessage("Đã hủy.");
                return;
            }

            string groupName = nameResult.StringResult;
            // Use full namespace to avoid ambiguity with Core.Data.BeamGeometry
            var beamDataList = new List<Core.Data.BeamGeometry>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in result.Value.GetObjectIds())
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    // Extract geometry from LINE or POLYLINE
                    double sx = 0, sy = 0, ex = 0, ey = 0;
                    double w = 300, h = 500; // Default dimensions

                    if (ent is Line line)
                    {
                        sx = line.StartPoint.X; sy = line.StartPoint.Y;
                        ex = line.EndPoint.X; ey = line.EndPoint.Y;
                    }
                    else if (ent is Polyline poly && poly.NumberOfVertices >= 2)
                    {
                        var p0 = poly.GetPoint2dAt(0);
                        var p1 = poly.GetPoint2dAt(poly.NumberOfVertices - 1);
                        sx = p0.X; sy = p0.Y;
                        ex = p1.X; ey = p1.Y;
                    }
                    else
                    {
                        continue; // Skip unsupported entities
                    }

                    // Try to read dimensions from XData if available
                    var beamXData = XDataUtils.ReadBeamData(ent);
                    if (beamXData != null)
                    {
                        w = beamXData.Width ?? w;
                        h = beamXData.Height ?? h;
                    }

                    beamDataList.Add(new Core.Data.BeamGeometry
                    {
                        Handle = ent.Handle.ToString(),
                        Name = ent.Handle.ToString(),
                        StartX = sx,
                        StartY = sy,
                        EndX = ex,
                        EndY = ey,
                        Width = w,
                        Height = h
                    });
                }
                tr.Commit();
            }

            if (beamDataList.Count == 0)
            {
                WriteMessage("Không có đối tượng hợp lệ.");
                return;
            }

            // CONFLICT HANDLING: Remove beams from old groups (Steal Ownership)
            var groups = GetOrCreateBeamGroups();
            var newHandles = beamDataList.Select(b => b.Handle).ToList();
            StealOwnership(groups, newHandles);

            // Tạo nhóm thủ công và chạy detection
            var group = CreateManualBeamGroup(groupName, beamDataList);

            // Add to cache
            groups.Add(group);

            // Save to cache
            SaveBeamGroupsToNOD(groups);

            WriteMessage($"Đã tạo nhóm '{groupName}' với {beamDataList.Count} dầm, {group.Spans.Count} nhịp.");

            // Show viewer
            using (var dialog = new UI.Forms.BeamGroupViewerDialog(groups, ApplyBeamGroupResults))
            {
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(dialog);
            }
        }

        /// <summary>
        /// Tạo BeamGroup thủ công từ danh sách BeamData, bao gồm detection logic
        /// Sorting theo NamingConfig.SortCorner và SortDirection
        /// </summary>
        private BeamGroup CreateManualBeamGroup(string name, List<Core.Data.BeamGeometry> beamDataList)
        {
            var settings = DtsSettings.Instance;
            var namingCfg = settings.Naming ?? new NamingConfig();

            // Sort beams theo NamingConfig.SortCorner và SortDirection
            // SortCorner: 0=TopLeft, 1=TopRight, 2=BottomLeft, 3=BottomRight
            // SortDirection: 0=Horizontal(X first), 1=Vertical(Y first)
            var sortedBeams = SortBeamsByNamingConfig(beamDataList, namingCfg);

            var group = new BeamGroup
            {
                GroupName = name,
                GroupType = "Beam",
                Source = "Manual",
                EntityHandles = sortedBeams.Select(b => b.Handle).ToList(),
                Width = sortedBeams.Average(b => b.Width),
                Height = sortedBeams.Average(b => b.Height),
                TotalLength = sortedBeams.Sum(b => b.Length) / 1000.0
            };

            // Xác định hướng
            var first = sortedBeams.First();
            var last = sortedBeams.Last();
            double dx = Math.Abs(last.EndX - first.StartX);
            double dy = Math.Abs(last.EndY - first.StartY);
            group.Direction = dy > dx ? "Y" : "X";

            // === SMART NAMING: Populate LevelZ for story matching ===
            group.LevelZ = first.StartZ;

            // Check splice requirement
            double standardLength = settings.Beam?.StandardBarLength ?? 11700;
            group.RequiresSplice = group.TotalLength * 1000 > standardLength;

            // Query supports from drawing database (Columns, Walls on designated layers)
            var supports = QuerySupportsFromDrawing(sortedBeams);

            // Use proper support detection instead of hardcoded Column/300mm
            BeamGroupDetector.DetectSupports(group, sortedBeams, supports);

            // FIX: Check if we only have FreeEnd supports (no real columns/walls found)
            // Also check for maximum reasonable span length
            bool onlyFreeEnds = group.Supports.All(s => s.Type == SupportType.FreeEnd);
            int realSupportCount = group.Supports.Count(s => s.Type == SupportType.Column || s.Type == SupportType.Wall);

            // FIX: If total length is too long (>15m) and only 2 or fewer real supports,
            // force per-element split to avoid 90m single spans
            double maxReasonableSpan = 15.0; // 15 meters max span
            bool forceSplitByElement = (group.TotalLength > maxReasonableSpan * 2) && (realSupportCount < 2);

            double prevHeight = group.Height;

            if (onlyFreeEnds || group.Supports.Count < 2 || forceSplitByElement)
            {
                // NO REAL SUPPORTS FOUND: Create 1 span per beam
                // This prevents 30-50m spans when columns are not detected
                double cumPosition = 0;
                for (int i = 0; i < sortedBeams.Count; i++)
                {
                    var beam = sortedBeams[i];
                    double beamLen = beam.Length / 1000.0; // mm to m

                    var span = new SpanData
                    {
                        SpanId = $"S{i + 1}",
                        SpanIndex = i,
                        Length = beamLen,
                        ClearLength = beamLen,
                        Width = beam.Width,
                        Height = beam.Height,
                        LeftSupportId = i == 0 ? "FE_Start" : $"J{i}",
                        RightSupportId = i == sortedBeams.Count - 1 ? "FE_End" : $"J{i + 1}",
                        IsStepChange = Math.Abs(beam.Height - prevHeight) > 50,
                        HeightDifference = beam.Height - prevHeight,
                        IsConsole = (i == 0 || i == sortedBeams.Count - 1)
                    };

                    span.Segments.Add(new PhysicalSegment
                    {
                        EntityHandle = beam.Handle,
                        Length = beamLen,
                        StartPoint = new[] { beam.StartX, beam.StartY },
                        EndPoint = new[] { beam.EndX, beam.EndY }
                    });

                    group.Spans.Add(span);
                    prevHeight = beam.Height;
                    cumPosition += beamLen;
                }
            }
            else
            {
                // SUPPORTS DETECTED: Create spans between supports
                for (int i = 0; i < group.Supports.Count - 1; i++)
                {
                    var left = group.Supports[i];
                    var right = group.Supports[i + 1];

                    // Find beams that fall within this span's position range
                    double cumPos = 0;
                    var spanBeams = new List<Core.Data.BeamGeometry>();
                    foreach (var b in sortedBeams)
                    {
                        double beamMidPos = cumPos + (b.Length / 1000.0) / 2;
                        if (beamMidPos >= left.Position && beamMidPos <= right.Position)
                        {
                            spanBeams.Add(b);
                        }
                        cumPos += b.Length / 1000.0;
                    }

                    // Fallback: if no beams found, take at least one
                    if (spanBeams.Count == 0 && sortedBeams.Count > i)
                    {
                        spanBeams.Add(sortedBeams[Math.Min(i, sortedBeams.Count - 1)]);
                    }

                    double spanHeight = spanBeams.Count > 0 ? spanBeams.Average(b => b.Height) : group.Height;
                    bool isStep = Math.Abs(spanHeight - prevHeight) > 50;

                    var span = new SpanData
                    {
                        SpanId = $"S{i + 1}",
                        SpanIndex = i,
                        Length = right.Position - left.Position,
                        ClearLength = right.Position - left.Position - (left.Width + right.Width) / 2000.0,
                        Width = spanBeams.Count > 0 ? spanBeams.Average(b => b.Width) : group.Width,
                        Height = spanHeight,
                        LeftSupportId = left.SupportId,
                        RightSupportId = right.SupportId,
                        IsStepChange = isStep,
                        HeightDifference = spanHeight - prevHeight,
                        IsConsole = left.IsFreeEnd || right.IsFreeEnd
                    };

                    // Add physical segments
                    foreach (var b in spanBeams)
                    {
                        span.Segments.Add(new PhysicalSegment
                        {
                            EntityHandle = b.Handle,
                            Length = b.Length / 1000.0,
                            StartPoint = new[] { b.StartX, b.StartY },
                            EndPoint = new[] { b.EndX, b.EndY }
                        });
                    }

                    group.Spans.Add(span);
                    if (isStep) group.HasStepChange = true;
                    prevHeight = spanHeight;
                }
            }

            // ===== INTEGRATE RebarCuttingAlgorithm =====
            // Tính toán các đoạn thép cắt + nối + hook
            CalculateBarSegmentsForGroup(group, settings);

            return group;
        }

        /// <summary>
        /// Tính toán và populate TopBarSegments/BotBarSegments cho BeamGroup
        /// Sử dụng RebarCuttingAlgorithm từ C# (không để JS tính)
        /// </summary>
        private void CalculateBarSegmentsForGroup(BeamGroup group, DtsSettings settings)
        {
            try
            {
                var algorithm = new Core.Algorithms.RebarCuttingAlgorithm(settings);

                // Convert spans to SpanInfo for algorithm
                var spanInfos = new List<Core.Algorithms.SpanInfo>();
                double cumPos = 0;
                foreach (var span in group.Spans)
                {
                    spanInfos.Add(new Core.Algorithms.SpanInfo
                    {
                        SpanId = span.SpanId,
                        Length = span.Length * 1000, // Convert m to mm
                        StartPos = cumPos * 1000
                    });
                    cumPos += span.Length;
                }

                double totalLengthMm = group.TotalLength * 1000;
                string groupType = group.GroupType?.ToUpperInvariant() ?? "BEAM";

                // Get actual bar diameter from settings (NOT hardcode 20!)
                var availableDiameters = settings.General?.AvailableDiameters;
                int barDiameter = (availableDiameters != null && availableDiameters.Count > 0)
                    ? availableDiameters.Max()  // Use largest available diameter
                    : 20;  // Fallback only if no settings

                // Calculate TOP bar segments
                var topResult = algorithm.AutoCutBars(totalLengthMm, spanInfos, true, groupType);

                // Determine support types for hooks
                var firstSupport = group.Supports?.FirstOrDefault();
                var lastSupport = group.Supports?.LastOrDefault();
                string startSupportType = SupportTypeToString(firstSupport?.Type ?? SupportType.FreeEnd);
                string endSupportType = SupportTypeToString(lastSupport?.Type ?? SupportType.FreeEnd);

                // Apply staggering and end anchorage WITH ACTUAL DIAMETER
                algorithm.ApplyStaggering(topResult, barDiameter, 2);
                algorithm.ApplyEndAnchorage(topResult, startSupportType, endSupportType, barDiameter);

                // Convert to DTO for JSON
                group.TopBarSegments = topResult.Segments.Select(s => new BarSegmentDto
                {
                    StartPos = s.StartPos / 1000.0,  // Convert back to meters for JS
                    EndPos = s.EndPos / 1000.0,
                    SpliceAtStart = s.SpliceAtStart,
                    SpliceAtEnd = s.SpliceAtEnd,
                    SplicePosition = s.SpliceAtEnd ? s.SplicePosition / 1000.0 : (double?)null,
                    IsStaggered = s.IsStaggered,
                    BarIndex = s.BarIndex,
                    HookAtStart = s.HookAtStart,
                    HookAtEnd = s.HookAtEnd,
                    HookAngle = s.HookAngle,
                    HookLength = s.HookLength / 1000.0
                }).ToList();

                // Calculate BOT bar segments WITH ACTUAL DIAMETER
                var botResult = algorithm.AutoCutBars(totalLengthMm, spanInfos, false, groupType);
                algorithm.ApplyStaggering(botResult, barDiameter, 2);
                algorithm.ApplyEndAnchorage(botResult, startSupportType, endSupportType, barDiameter);

                group.BotBarSegments = botResult.Segments.Select(s => new BarSegmentDto
                {
                    StartPos = s.StartPos / 1000.0,
                    EndPos = s.EndPos / 1000.0,
                    SpliceAtStart = s.SpliceAtStart,
                    SpliceAtEnd = s.SpliceAtEnd,
                    SplicePosition = s.SpliceAtEnd ? s.SplicePosition / 1000.0 : (double?)null,
                    IsStaggered = s.IsStaggered,
                    BarIndex = s.BarIndex,
                    HookAtStart = s.HookAtStart,
                    HookAtEnd = s.HookAtEnd,
                    HookAngle = s.HookAngle,
                    HookLength = s.HookLength / 1000.0
                }).ToList();

                WriteMessage($"   Đã tính {group.TopBarSegments.Count} đoạn thép TOP, {group.BotBarSegments.Count} đoạn thép BOT");
            }
            catch (System.Exception ex)
            {
                WriteMessage($"   Lỗi tính toán bar segments: {ex.Message}");
            }
        }

        private string SupportTypeToString(SupportType type)
        {
            switch (type)
            {
                case SupportType.Column: return "COLUMN";
                case SupportType.Wall: return "WALL";
                case SupportType.Beam: return "BEAM";
                default: return "FREEEND";
            }
        }

        /// <summary>
        /// Sync dữ liệu từ XData (BeamResultData) sang BeamGroup.
        /// Tạo 3 BackboneOptions và populate SpanData.TopRebar/BotRebar/Stirrup.
        /// </summary>
        private void SyncRebarCalculationsToGroups(ICollection<ObjectId> calculatedIds)
        {
            WriteMessage("   Syncing rebar data to BeamGroups...");

            var groups = GetOrCreateBeamGroups();

            // Nếu không có groups → tự tạo 1 group từ các dầm đã calculate
            if (groups.Count == 0)
            {
                WriteMessage("   Auto-creating BeamGroup from calculated beams...");
                var newGroup = AutoCreateGroupFromCalculatedBeams(calculatedIds);
                if (newGroup != null)
                {
                    groups.Add(newGroup);
                }
                else
                {
                    WriteMessage("   (Failed to create BeamGroup - skipping sync)");
                    return;
                }
            }

            var settings = DtsSettings.Instance;
            int synced = 0;

            // Build handle lookup from calculated beams
            var handleToData = new Dictionary<string, BeamResultData>();
            UsingTransaction(tr =>
            {
                foreach (ObjectId id in calculatedIds)
                {
                    try
                    {
                        var obj = tr.GetObject(id, OpenMode.ForRead);
                        var data = XDataUtils.ReadElementData(obj) as BeamResultData;
                        if (data != null)
                        {
                            handleToData[obj.Handle.ToString()] = data;
                        }
                    }
                    catch { }
                }
            });

            if (handleToData.Count == 0)
            {
                WriteMessage("   (No rebar data found - skipping sync)");
                return;
            }

            foreach (var group in groups)
            {
                // Skip if user has manually edited - only sync "best" option, not overwrite
                bool hasUserData = group.IsManuallyEdited && group.BackboneOptions.Count > 0;

                // ===== CRITICAL: BẢO VỆ SELECTED DESIGN =====
                // Nếu đã chốt phương án, KHÔNG ĐƯỢC ghi đè SelectedDesign
                // Chỉ tính lại ProposedDesigns và ValidateSafety
                bool isLocked = group.IsDesignLocked && group.SelectedDesign != null;

                // Collect all BeamResultData for this group
                var groupRebarData = new List<BeamResultData>();
                foreach (var handle in group.EntityHandles)
                {
                    if (handleToData.TryGetValue(handle, out var data))
                    {
                        groupRebarData.Add(data);
                    }
                }

                if (groupRebarData.Count == 0) continue;

                // ===== CREATE 3 BACKBONE OPTIONS (Always regenerate for comparison) =====
                // Luôn tạo lại ProposedDesigns để user có thể so sánh với SelectedDesign
                group.BackboneOptions = GenerateBackboneOptions(groupRebarData, settings, group.Width, group.Height);

                if (!isLocked)
                {
                    // Chưa chốt → Chọn option 0 làm mặc định
                    group.SelectedBackboneIndex = 0;
                }
                else
                {
                    // ===== VALIDATE SAFETY: Kiểm tra SelectedDesign còn đủ thép không =====
                    // Tính As_required mới từ nội lực mới
                    double maxAsReqTop = 0, maxAsReqBot = 0;
                    foreach (var data in groupRebarData)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            maxAsReqTop = Math.Max(maxAsReqTop, data.TopArea[i]);
                            maxAsReqBot = Math.Max(maxAsReqBot, data.BotArea[i]);
                        }
                    }

                    // Lưu As_required mới vào SelectedDesign để hiển thị cảnh báo
                    group.SelectedDesign.As_Required_Top_Max = maxAsReqTop;
                    group.SelectedDesign.As_Required_Bot_Max = maxAsReqBot;

                    // So sánh As_provided (trong SelectedDesign) vs As_required (mới)
                    double asProvTop = group.SelectedDesign.As_Backbone_Top;
                    double asProvBot = group.SelectedDesign.As_Backbone_Bot;

                    bool isSafe = asProvTop >= maxAsReqTop && asProvBot >= maxAsReqBot;
                    group.SelectedDesign.IsValid = isSafe;

                    if (!isSafe)
                    {
                        double deficitTop = maxAsReqTop - asProvTop;
                        double deficitBot = maxAsReqBot - asProvBot;
                        group.SelectedDesign.ValidationMessage =
                            $"UNSAFE: Thiếu Top {deficitTop:F2}cm², Bot {deficitBot:F2}cm²";
                        WriteMessage($"   ⚠️ WARNING: Nhóm {group.GroupName} - {group.SelectedDesign.ValidationMessage}");
                    }
                    else
                    {
                        group.SelectedDesign.ValidationMessage = null;
                    }
                }

                // ===== APPLY UNIFIED BACKBONE TO ALL SPANS =====
                // Instead of individual XData values, use backbone option for uniformity
                if (group.BackboneOptions.Count > 0)
                {
                    var selectedOpt = group.BackboneOptions[group.SelectedBackboneIndex];
                    string topBackbone = $"{selectedOpt.BackboneCount_Top}D{selectedOpt.BackboneDiameter}";
                    string botBackbone = $"{selectedOpt.BackboneCount_Bot}D{selectedOpt.BackboneDiameter}";

                    for (int i = 0; i < group.Spans.Count; i++)
                    {
                        var span = group.Spans[i];
                        var data = i < groupRebarData.Count ? groupRebarData[i] : null;

                        // Apply UNIFIED backbone to all positions
                        if (!hasUserData || span.TopRebar == null || string.IsNullOrEmpty(span.TopRebar[0, 0]))
                        {
                            span.TopRebar[0, 0] = topBackbone;  // L1
                            span.TopRebar[0, 2] = topBackbone;  // Mid
                            span.TopRebar[0, 4] = topBackbone;  // L2

                            span.BotRebar[0, 0] = botBackbone;
                            span.BotRebar[0, 2] = botBackbone;
                            span.BotRebar[0, 4] = botBackbone;

                            // Stirrup from XData (can vary per span)
                            if (data != null)
                            {
                                span.Stirrup[0] = data.StirrupString[0] ?? "";
                                span.Stirrup[1] = data.StirrupString[1] ?? "";
                                span.Stirrup[2] = data.StirrupString[2] ?? "";
                                span.SideBar = data.WebBarString[1] ?? "";

                                span.As_Top[0] = data.TopArea[0];
                                span.As_Top[2] = data.TopArea[1];
                                span.As_Top[4] = data.TopArea[2];
                                span.As_Bot[0] = data.BotArea[0];
                                span.As_Bot[2] = data.BotArea[1];
                                span.As_Bot[4] = data.BotArea[2];
                            }
                        }
                    }
                }

                synced++;
            }

            // Save updated groups back to NOD
            if (synced > 0)
            {
                // Generate Signature for each group
                foreach (var g in groups)
                {
                    g.UpdateSignature();
                }

                // Auto-naming: Assign names based on story and signature
                Core.Algorithms.NamingEngine.AutoLabeling(groups, DtsSettings.Instance);

                SaveBeamGroupsToNOD(groups);
                WriteMessage($"   Synced data to {synced} groups.");
            }
        }

        /// <summary>
        /// Generate 3 backbone options cho group dựa trên calculated rebar.
        /// Option 1: Đường kính lớn nhất, ít thanh (ưu tiên D25, D22)
        /// Option 2: Đường kính trung bình, cân bằng
        /// Option 3: Đường kính nhỏ, nhiều thanh (ưu tiên D20, D18)
        /// </summary>
        private List<ContinuousBeamSolution> GenerateBackboneOptions(List<BeamResultData> rebarData, DtsSettings settings, double widthMm, double heightMm)
        {
            var options = new List<ContinuousBeamSolution>();
            var availableDiameters = settings.General?.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };

            // Tính tổng As yêu cầu max
            double maxAsTop = 0, maxAsBot = 0;
            foreach (var data in rebarData)
            {
                for (int i = 0; i < 3; i++)
                {
                    maxAsTop = Math.Max(maxAsTop, data.TopArea[i] + data.TorsionArea[i] * 0.16);
                    maxAsBot = Math.Max(maxAsBot, data.BotArea[i] + data.TorsionArea[i] * 0.16);
                }
            }

            // Backbone diameters to try
            var backboneDias = availableDiameters.Where(d => d >= 16 && d <= 25).OrderByDescending(d => d).ToList();
            if (backboneDias.Count < 3) backboneDias = new List<int> { 22, 20, 18 };

            // Generate 3 options with different diameters
            for (int opt = 0; opt < 3 && opt < backboneDias.Count; opt++)
            {
                int dia = backboneDias[opt];
                double asPerBar = Math.PI * dia * dia / 4.0; // mm² per bar

                int nTop = Math.Max(2, (int)Math.Ceiling(maxAsTop * 100 / asPerBar)); // As in cm², convert
                int nBot = Math.Max(2, (int)Math.Ceiling(maxAsBot * 100 / asPerBar));

                // Cap at reasonable count
                nTop = Math.Min(nTop, 6);
                nBot = Math.Min(nBot, 6);

                var solution = new ContinuousBeamSolution
                {
                    OptionName = $"{nTop}D{dia} / {nBot}D{dia}",
                    BackboneDiameter = dia,
                    BackboneCount_Top = nTop,
                    BackboneCount_Bot = nBot,
                    As_Backbone_Top = nTop * asPerBar / 100.0, // cm²
                    As_Backbone_Bot = nBot * asPerBar / 100.0,
                    Description = opt == 0 ? "Phương án tối ưu" : (opt == 1 ? "Cân bằng" : "Tiết kiệm"),
                    EfficiencyScore = 100 - opt * 15,
                    WastePercentage = 5 + opt * 3,
                    TotalSteelWeight = (nTop + nBot) * dia * dia * 0.00617 * (rebarData.Count * 6) // rough estimate
                };

                options.Add(solution);
            }

            return options;
        }

        /// <summary>
        /// Tự động tạo BeamGroup từ các dầm đã tính toán khi chưa có group nào.
        /// </summary>
        private BeamGroup AutoCreateGroupFromCalculatedBeams(ICollection<ObjectId> calculatedIds)
        {
            var beamDataList = new List<Core.Data.BeamGeometry>();
            var settings = DtsSettings.Instance;

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in calculatedIds)
                {
                    try
                    {
                        var obj = tr.GetObject(id, OpenMode.ForRead);
                        var data = XDataUtils.ReadElementData(obj) as BeamResultData;
                        if (data == null) continue;

                        double sx = 0, sy = 0, ex = 0, ey = 0;
                        if (obj is Line line)
                        {
                            sx = line.StartPoint.X; sy = line.StartPoint.Y;
                            ex = line.EndPoint.X; ey = line.EndPoint.Y;
                        }
                        else if (obj is Polyline poly && poly.NumberOfVertices >= 2)
                        {
                            var p0 = poly.GetPoint2dAt(0);
                            var p1 = poly.GetPoint2dAt(poly.NumberOfVertices - 1);
                            sx = p0.X; sy = p0.Y;
                            ex = p1.X; ey = p1.Y;
                        }
                        else continue;

                        beamDataList.Add(new Core.Data.BeamGeometry
                        {
                            Handle = obj.Handle.ToString(),
                            Name = data.SapElementName ?? obj.Handle.ToString(),
                            StartX = sx,
                            StartY = sy,
                            EndX = ex,
                            EndY = ey,
                            Width = data.Width > 0 ? data.Width * 10 : 300, // cm -> mm
                            Height = data.SectionHeight > 0 ? data.SectionHeight * 10 : 500
                        });
                    }
                    catch { }
                }
            });

            if (beamDataList.Count == 0) return null;

            // Create the group using existing logic
            return CreateManualBeamGroup("Auto-Group", beamDataList);
        }

        /// <summary>
        /// Lấy danh sách BeamGroup từ NOD của bản vẽ hiện tại.
        /// Data đi theo file DWG, không dùng file cache bên ngoài.
        /// DEFENSIVE: Auto-validate và cleanup zombie data (dầm đã bị xóa).
        /// </summary>
        private List<BeamGroup> GetOrCreateBeamGroups()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return new List<BeamGroup>();

            var db = doc.Database;

            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    string json = XDataUtils.LoadBeamGroupsFromNOD(db, tr);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var groups = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BeamGroup>>(json);
                        if (groups != null)
                        {
                            // DEFENSIVE LOGIC: Validate and cleanup zombie data
                            return ValidateAndCleanupGroups(groups);
                        }
                    }
                }
            }
            catch { }

            return new List<BeamGroup>();
        }

        /// <summary>
        /// Lưu danh sách BeamGroup vào NOD của bản vẽ hiện tại.
        /// </summary>
        private void SaveBeamGroupsToNOD(List<BeamGroup> groups)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null || groups == null) return;

            var db = doc.Database;

            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    string json = Newtonsoft.Json.JsonConvert.SerializeObject(groups);
                    XDataUtils.SaveBeamGroupsToNOD(db, tr, json);
                    tr.Commit();
                }
            }
            catch { }
        }

        #region Defensive Logic - Beam Group Protection

        /// <summary>
        /// DEFENSIVE LOGIC 1: Validate và cleanup groups - Remove erased beams
        /// Gọi khi Load dữ liệu từ NOD để tránh crash khi dầm đã bị xóa trên CAD.
        /// </summary>
        private List<BeamGroup> ValidateAndCleanupGroups(List<BeamGroup> groups)
        {
            if (groups == null || groups.Count == 0) return groups;

            var validGroups = new List<BeamGroup>();
            bool needsUpdate = false;

            UsingTransaction(tr =>
            {
                foreach (var group in groups)
                {
                    if (group.EntityHandles == null || group.EntityHandles.Count == 0) continue;

                    var validHandles = new List<string>();
                    foreach (var handle in group.EntityHandles)
                    {
                        try
                        {
                            var h = new Handle(long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
                            var objId = AcadUtils.Db.GetObjectId(false, h, 0);
                            if (objId != ObjectId.Null && !objId.IsErased)
                            {
                                var obj = tr.GetObject(objId, OpenMode.ForRead, true);
                                if (obj != null && !obj.IsErased)
                                {
                                    validHandles.Add(handle);
                                }
                            }
                        }
                        catch
                        {
                            // Handle invalid or erased - skip it
                            needsUpdate = true;
                        }
                    }

                    if (validHandles.Count != group.EntityHandles.Count)
                    {
                        needsUpdate = true;
                        group.EntityHandles = validHandles;
                    }

                    // Keep group if it still has members
                    if (validHandles.Count > 0)
                    {
                        // === BACKWARD COMPATIBILITY ===
                        // Bản vẽ cũ chưa có trường Name → gán từ GroupName hoặc "UNNAMED"
                        if (string.IsNullOrEmpty(group.Name))
                        {
                            group.Name = !string.IsNullOrEmpty(group.GroupName) ? group.GroupName : "UNNAMED";
                            needsUpdate = true;
                        }

                        validGroups.Add(group);
                    }
                }
            });

            // Auto-save if we cleaned up any zombie data
            if (needsUpdate)
            {
                WriteMessage("   Đã tự động xóa các dầm không còn tồn tại khỏi dữ liệu nhóm.");
                SaveBeamGroupsToNOD(validGroups);
            }

            return validGroups;
        }

        /// <summary>
        /// DEFENSIVE LOGIC 2: Get all beam handles that are already in groups
        /// Dùng để check conflict khi tạo nhóm mới.
        /// </summary>
        private HashSet<string> GetBeamsAlreadyInGroups(List<BeamGroup> groups)
        {
            var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (groups == null) return handles;

            foreach (var group in groups)
            {
                if (group.EntityHandles != null)
                {
                    foreach (var h in group.EntityHandles)
                        handles.Add(h);
                }
            }
            return handles;
        }

        /// <summary>
        /// DEFENSIVE LOGIC 3: Remove beam from all groups (Steal Ownership)
        /// Gọi trước khi add beam vào group mới để tránh 1 dầm nằm trong 2 nhóm.
        /// Returns true if any group was modified.
        /// </summary>
        private bool RemoveBeamFromAllGroups(List<BeamGroup> groups, string beamHandle)
        {
            if (groups == null || string.IsNullOrEmpty(beamHandle)) return false;

            bool modified = false;
            var groupsToRemove = new List<BeamGroup>();

            foreach (var group in groups)
            {
                if (group.EntityHandles != null && group.EntityHandles.Contains(beamHandle))
                {
                    group.EntityHandles.Remove(beamHandle);
                    modified = true;

                    // If group becomes empty, mark for removal
                    if (group.EntityHandles.Count == 0)
                    {
                        groupsToRemove.Add(group);
                    }
                }
            }

            // Remove empty groups
            foreach (var g in groupsToRemove)
            {
                groups.Remove(g);
            }

            return modified;
        }

        /// <summary>
        /// DEFENSIVE LOGIC 4: Steal ownership for multiple beams
        /// Dùng khi tạo nhóm mới - đảm bảo mỗi dầm chỉ thuộc 1 nhóm.
        /// </summary>
        private void StealOwnership(List<BeamGroup> existingGroups, List<string> newBeamHandles)
        {
            if (existingGroups == null || newBeamHandles == null) return;

            foreach (var handle in newBeamHandles)
            {
                if (RemoveBeamFromAllGroups(existingGroups, handle))
                {
                    WriteMessage($"   Dầm {handle} đã được chuyển từ nhóm cũ sang nhóm mới.");
                }
            }
        }

        #endregion

        /// <summary>
        /// Apply kết quả từ BeamGroupViewer vào bản vẽ
        /// </summary>
        private void ApplyBeamGroupResults(List<BeamGroup> groups)
        {
            if (groups == null || groups.Count == 0) return;

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;

            int count = 0;

            // Lưu groups vào cache
            SaveBeamGroupsToNOD(groups);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var group in groups)
                {
                    foreach (var span in group.Spans)
                    {
                        // Apply rebar data to each segment
                        foreach (var seg in span.Segments)
                        {
                            if (string.IsNullOrEmpty(seg.EntityHandle)) continue;

                            try
                            {
                                Handle handle = new Handle(Convert.ToInt64(seg.EntityHandle, 16));
                                ObjectId objId;

                                if (db.TryGetObjectId(handle, out objId) && objId != ObjectId.Null)
                                {
                                    var ent = tr.GetObject(objId, OpenMode.ForWrite) as Entity;
                                    if (ent != null)
                                    {
                                        // Build rebar strings from Span data
                                        string topRebar = BuildRebarString(span.TopRebar, 0);
                                        string botRebar = BuildRebarString(span.BotRebar, 0);
                                        string stirrup = span.Stirrup != null && span.Stirrup.Length > 1
                                            ? span.Stirrup[1] ?? "" : "";
                                        string sideBar = span.SideBar ?? "";

                                        // Write XData to entity
                                        XDataUtils.WriteRebarXData(ent, tr,
                                            topRebar, botRebar, stirrup, sideBar,
                                            group.GroupName);

                                        count++;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                tr.Commit();
            }

            WriteMessage($"Đã apply thép cho {count} đoạn dầm và lưu cache.");
        }

        /// <summary>
        /// Build rebar string từ mảng 2D [layer, position]
        /// </summary>
        private string BuildRebarString(string[,] rebarArray, int position)
        {
            if (rebarArray == null) return "";

            var parts = new List<string>();
            for (int layer = 0; layer < 3; layer++)
            {
                if (position < rebarArray.GetLength(1))
                {
                    var val = rebarArray[layer, position];
                    if (!string.IsNullOrEmpty(val))
                        parts.Add(val);
                }
            }
            return string.Join("+", parts);
        }

        /// <summary>
        /// Sort beams theo NamingConfig.SortCorner và SortDirection
        /// SortCorner: 0=TopLeft, 1=TopRight, 2=BottomLeft, 3=BottomRight
        /// SortDirection: 0=Horizontal(X first), 1=Vertical(Y first)
        /// </summary>
        private List<Core.Data.BeamGeometry> SortBeamsByNamingConfig(
            List<Core.Data.BeamGeometry> beams, NamingConfig cfg)
        {
            if (beams == null || beams.Count == 0)
                return new List<Core.Data.BeamGeometry>();

            int corner = cfg?.SortCorner ?? 0;
            int direction = cfg?.SortDirection ?? 0;

            // Xác định hệ số nhân để đảo chiều sort
            // Corner: 0=TL(-X, +Y), 1=TR(+X, +Y), 2=BL(-X, -Y), 3=BR(+X, -Y)
            double xMultiplier = (corner == 0 || corner == 2) ? 1 : -1;  // TL/BL: X tăng, TR/BR: X giảm
            double yMultiplier = (corner == 0 || corner == 1) ? -1 : 1;  // TL/TR: Y giảm (top=max), BL/BR: Y tăng

            // SortDirection: 0=Horizontal(X ưu tiên), 1=Vertical(Y ưu tiên)
            if (direction == 0) // Horizontal: sort X first, then Y
            {
                return beams
                    .OrderBy(b => (b.StartX + b.EndX) / 2 * xMultiplier)
                    .ThenBy(b => (b.StartY + b.EndY) / 2 * yMultiplier)
                    .ToList();
            }
            else // Vertical: sort Y first, then X
            {
                return beams
                    .OrderBy(b => (b.StartY + b.EndY) / 2 * yMultiplier)
                    .ThenBy(b => (b.StartX + b.EndX) / 2 * xMultiplier)
                    .ToList();
            }
        }

        /// <summary>
        /// Query hỗ trợ (Column, Wall) từ database dựa trên khu vực dầm.
        /// OPTIMIZED: Dùng SelectCrossingWindow + XData filter thay vì duyệt toàn bộ ModelSpace.
        /// </summary>
        private List<SupportGeometry> QuerySupportsFromDrawing(List<Core.Data.BeamGeometry> beams)
        {
            var supports = new List<SupportGeometry>();
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null || beams == null || beams.Count == 0) return supports;

            var db = doc.Database;
            var ed = doc.Editor;

            // Tính bounding box của chain + buffer
            double minX = beams.Min(b => Math.Min(b.StartX, b.EndX)) - 1000;
            double maxX = beams.Max(b => Math.Max(b.StartX, b.EndX)) + 1000;
            double minY = beams.Min(b => Math.Min(b.StartY, b.EndY)) - 1000;
            double maxY = beams.Max(b => Math.Max(b.StartY, b.EndY)) + 1000;

            try
            {
                // SelectionFilter: chỉ lấy entity có XData của DTS_APP
                var filter = new SelectionFilter(new TypedValue[]
                {
                    new TypedValue((int)DxfCode.Start, "*"), // Mọi entity type
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, "DTS_APP") // Có XData DTS_APP
                });

                // SelectCrossingWindow trong bounding box - NHANH hơn duyệt toàn bộ
                var pt1 = new Point3d(minX, minY, 0);
                var pt2 = new Point3d(maxX, maxY, 0);
                var result = ed.SelectCrossingWindow(pt1, pt2, filter);

                if (result.Status != PromptStatus.OK || result.Value == null)
                    return supports;

                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (ObjectId id in result.Value.GetObjectIds())
                    {
                        try
                        {
                            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null) continue;

                            // Đọc XData để xác định type
                            var elemData = XDataUtils.ReadElementData(ent);
                            if (elemData == null) continue;

                            string xType = elemData.XType?.ToUpperInvariant();
                            bool isColumn = xType == "COLUMN";
                            bool isWall = xType == "WALL";

                            if (!isColumn && !isWall) continue;

                            // Lấy center point
                            double cx = 0, cy = 0, w = 300, d = 300;

                            // Handle Circle (column markers from PlotFramesAt)
                            if (ent is Circle circle)
                            {
                                cx = circle.Center.X;
                                cy = circle.Center.Y;
                                w = circle.Radius * 2; // Diameter as width
                                d = circle.Radius * 2;
                            }
                            else if (ent is Line line)
                            {
                                cx = (line.StartPoint.X + line.EndPoint.X) / 2;
                                cy = (line.StartPoint.Y + line.EndPoint.Y) / 2;
                            }
                            else if (ent is Polyline poly && poly.NumberOfVertices >= 2)
                            {
                                var p0 = poly.GetPoint2dAt(0);
                                var p1 = poly.GetPoint2dAt(poly.NumberOfVertices > 1 ? 1 : 0);
                                cx = (p0.X + p1.X) / 2;
                                cy = (p0.Y + p1.Y) / 2;
                            }
                            else if (ent.Bounds.HasValue)
                            {
                                var bounds = ent.Bounds.Value;
                                cx = (bounds.MinPoint.X + bounds.MaxPoint.X) / 2;
                                cy = (bounds.MinPoint.Y + bounds.MaxPoint.Y) / 2;
                                w = bounds.MaxPoint.X - bounds.MinPoint.X;
                                d = bounds.MaxPoint.Y - bounds.MinPoint.Y;
                            }
                            else
                            {
                                continue;
                            }

                            // Lấy kích thước từ typed data
                            if (elemData is ColumnData colData)
                            {
                                w = colData.Width ?? w;
                                d = colData.Height ?? d;
                            }
                            else if (elemData is WallData wallData)
                            {
                                w = wallData.Thickness ?? w;
                            }

                            supports.Add(new SupportGeometry
                            {
                                Handle = ent.Handle.ToString(),
                                Name = ent.Handle.ToString(),
                                Type = isColumn ? "Column" : "Wall",
                                CenterX = cx,
                                CenterY = cy,
                                Width = w,
                                Depth = d,
                                // Capture Z elevation for story filtering
                                Elevation = ent.Bounds.HasValue ? ent.Bounds.Value.MinPoint.Z : 0
                            });
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return supports;
        }

        /// <summary>
        /// Tự động gom nhóm TẤT CẢ dầm trong bản vẽ theo trục.
        /// Tính toán bar segments và lưu vào NOD để Viewer có thể mở ngay.
        /// Giải quyết bottleneck phải chọn từng dầm.
        /// [UPDATED] Fixed issue where beams on different levels were grouped together.
        /// </summary>
        [CommandMethod("DTS_REBAR_GROUP_AUTO")]
        public void DTS_AUTO_GROUP()
        {
            WriteMessage("=== AUTO GROUP: GOM NHÓM TỰ ĐỘNG TẤT CẢ DẦM ===");

            var settings = DtsSettings.Instance;
            var rebarSettings = RebarSettings.Instance;

            // 1. Lấy thông tin lưới trục
            List<Point3d> gridIntersections = new List<Point3d>();
            List<Curve> gridLines = new List<Curve>();

            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                foreach (ObjectId id in btr)
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    if (obj is Curve crv)
                    {
                        string layer = (obj as Entity)?.Layer ?? "";
                        if (layer.ToUpper().Contains("GRID") || layer.ToUpper().Contains("AXIS"))
                            gridLines.Add(crv);
                    }
                }

                for (int i = 0; i < gridLines.Count; i++)
                {
                    for (int j = i + 1; j < gridLines.Count; j++)
                    {
                        var pts = new Point3dCollection();
                        gridLines[i].IntersectWith(gridLines[j], Intersect.ExtendBoth, pts, IntPtr.Zero, IntPtr.Zero);
                        foreach (Point3d p in pts)
                        {
                            if (!gridIntersections.Any(x => x.DistanceTo(p) < 100))
                                gridIntersections.Add(p);
                        }
                    }
                }
            });

            WriteMessage($"Tìm thấy {gridIntersections.Count} giao điểm lưới trục.");

            // DEFENSIVE LOGIC: Load existing groups and get beams already assigned
            var existingGroups = GetOrCreateBeamGroups();
            var beamsAlreadyInGroups = GetBeamsAlreadyInGroups(existingGroups);
            int skippedCount = 0;

            // 2. Thu thập dầm CHƯA thuộc nhóm nào (INCREMENTAL mode - không đè data user)
            var freeBeamIds = new List<ObjectId>();
            // Key map: ObjectId -> (MidPoint, IsGirder, IsXDir, AxisKey, Handle, LevelZ)
            var beamsDataMap = new Dictionary<ObjectId, (Point3d Mid, bool IsGirder, bool IsXDir, string AxisKey, string Handle, double LevelZ)>();

            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                foreach (ObjectId id in btr)
                {
                    if (id.IsErased) continue;
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    if (obj is Curve curve)
                    {
                        string handle = curve.Handle.ToString();

                        // SAFE AUTO-GROUP: Skip beams already in a group
                        if (beamsAlreadyInGroups.Contains(handle))
                        {
                            skippedCount++;
                            continue;
                        }

                        // Check if this is a beam (has SAP data)
                        var xdata = XDataUtils.ReadElementData(curve) as BeamResultData;
                        if (xdata != null && !string.IsNullOrEmpty(xdata.SapElementName))
                        {
                            freeBeamIds.Add(id);

                            Point3d mid = curve.StartPoint + (curve.EndPoint - curve.StartPoint) * 0.5;
                            Vector3d dir = curve.EndPoint - curve.StartPoint;
                            bool isXDir = Math.Abs(dir.X) > Math.Abs(dir.Y);

                            bool onGridStart = gridIntersections.Any(g => g.DistanceTo(curve.StartPoint) < 200);
                            bool onGridEnd = gridIntersections.Any(g => g.DistanceTo(curve.EndPoint) < 200);
                            bool isGirder = onGridStart && onGridEnd;

                            // === FIX: Include Z-Level in grouping key ===
                            // Round Z to nearest 100mm to tolerate small modeling errors
                            double levelZ = Math.Round(mid.Z / 100.0) * 100.0;

                            // AxisKey để nhóm dầm cùng trục VÀ cùng tầng
                            double axisCoord = isXDir ? Math.Round(mid.Y / 100) * 100 : Math.Round(mid.X / 100) * 100;

                            // New Key Format: L[Z]_G/B_X/Y_[Coord]
                            string axisKey = $"L{levelZ:F0}_{(isGirder ? "G" : "B")}_{(isXDir ? "X" : "Y")}_{axisCoord:F0}";

                            beamsDataMap[id] = (mid, isGirder, isXDir, axisKey, handle, levelZ);
                        }
                    }
                }
            });

            if (skippedCount > 0)
            {
                WriteMessage($"   Bỏ qua {skippedCount} dầm đã thuộc nhóm (bảo toàn dữ liệu user).");
            }

            if (freeBeamIds.Count == 0)
            {
                if (skippedCount > 0)
                    WriteSuccess("Tất cả dầm đã được gom nhóm. Không có dầm mới cần xử lý.");
                else
                    WriteError("Không tìm thấy dầm nào có dữ liệu SAP. Hãy chạy DTS_REBAR_SAP_RESULT trước.");
                return;
            }

            WriteMessage($"Tìm thấy {freeBeamIds.Count} dầm chưa thuộc nhóm.");

            // 3. Nhóm dầm theo AxisKey
            // Sort groups by LevelZ first, then by Girder/Beam, then by Coordinate
            var groups = beamsDataMap.GroupBy(b => b.Value.AxisKey)
                                     .OrderBy(g => g.First().Value.LevelZ) // Sort by Level first
                                     .ThenBy(g => g.First().Value.IsGirder ? 0 : 1)
                                     .ThenBy(g => g.Key)
                                     .ToList();

            WriteMessage($"Đã gom thành {groups.Count} nhóm dầm.");

            // 4. Tạo BeamGroup cho mỗi nhóm (with GAP DETECTION / Chain Splitting)
            const double GAP_TOLERANCE = 500; // mm - Max gap before splitting chain
            var beamGroups = new List<BeamGroup>();

            // Dictionary to track group index per Level
            // Key: LevelZ, Value: Current Index
            var levelIndices = new Dictionary<double, int>();

            foreach (var group in groups)
            {
                var firstItem = group.First().Value;
                double z = firstItem.LevelZ;
                bool isXDir = firstItem.IsXDir;
                string prefix = firstItem.IsGirder ? "G" : "B";
                string direction = isXDir ? "X" : "Y";

                // Sort members by position (along beam axis)
                var sortedMembers = group
                    .OrderBy(m => isXDir ? m.Value.Mid.X : m.Value.Mid.Y)
                    .ToList();

                // Collect all BeamGeometry with Transaction
                var allBeamGeos = new List<(ObjectId Id, Core.Data.BeamGeometry Geo)>();
                UsingTransaction(tr =>
                {
                    foreach (var member in sortedMembers)
                    {
                        var curve = tr.GetObject(member.Key, OpenMode.ForRead) as Curve;
                        if (curve == null) continue;

                        var xdata = XDataUtils.ReadElementData(curve) as BeamResultData;
                        var geo = new Core.Data.BeamGeometry
                        {
                            Handle = curve.Handle.ToString(),
                            Name = xdata?.SapElementName ?? curve.Handle.ToString(),
                            StartX = curve.StartPoint.X,
                            StartY = curve.StartPoint.Y,
                            EndX = curve.EndPoint.X,
                            EndY = curve.EndPoint.Y,
                            StartZ = curve.StartPoint.Z,
                            EndZ = curve.EndPoint.Z,
                            Width = xdata?.Width ?? 0,
                            Height = xdata?.SectionHeight ?? 0
                        };
                        allBeamGeos.Add((member.Key, geo));
                    }
                });

                if (allBeamGeos.Count == 0) continue;

                // === CHAIN SPLITTING LOGIC ===
                var chains = new List<List<Core.Data.BeamGeometry>>();
                var currentChain = new List<Core.Data.BeamGeometry>();
                Core.Data.BeamGeometry prevBeam = null;

                foreach (var (id, geo) in allBeamGeos)
                {
                    if (prevBeam != null)
                    {
                        // Calculate gap between prevBeam End and current beam Start
                        // For X-Dir: compare X coordinate; for Y-Dir: compare Y coordinate
                        double prevEnd = isXDir ? Math.Max(prevBeam.StartX, prevBeam.EndX) : Math.Max(prevBeam.StartY, prevBeam.EndY);
                        double currStart = isXDir ? Math.Min(geo.StartX, geo.EndX) : Math.Min(geo.StartY, geo.EndY);
                        double gap = currStart - prevEnd;

                        // If gap > tolerance, start new chain
                        if (gap > GAP_TOLERANCE)
                        {
                            if (currentChain.Count > 0)
                            {
                                chains.Add(currentChain);
                                currentChain = new List<Core.Data.BeamGeometry>();
                            }
                        }
                    }

                    currentChain.Add(geo);
                    prevBeam = geo;
                }

                // Add last chain
                if (currentChain.Count > 0)
                {
                    chains.Add(currentChain);
                }

                // === CREATE BEAM GROUP FOR EACH CHAIN ===
                foreach (var chain in chains)
                {
                    if (!levelIndices.ContainsKey(z)) levelIndices[z] = 1;
                    int currentIndex = levelIndices[z]++;

                    string groupName = $"{prefix}{currentIndex}_{direction}";

                    // Create BeamGroup (this also calls CalculateBarSegmentsForGroup)
                    var beamGroup = CreateManualBeamGroup(groupName, chain);

                    // Explicitly set LevelZ for the group
                    beamGroup.LevelZ = z;

                    beamGroups.Add(beamGroup);
                }
            }

            // 5. Merge với existing groups và lưu vào NOD (INCREMENTAL - không xóa data cũ)
            if (beamGroups.Count > 0)
            {
                // Merge: existing groups + new groups
                existingGroups.AddRange(beamGroups);
                SaveBeamGroupsToNOD(existingGroups);
                WriteSuccess($"Đã tạo {beamGroups.Count} nhóm dầm mới. Tổng: {existingGroups.Count} nhóm.");
                WriteMessage("Giờ bạn có thể mở DTS_BEAM_VIEWER để xem tất cả các nhóm!");
            }
            else
            {
                WriteError("Không tạo được nhóm dầm nào.");
            }
        }

        /// <summary>
        /// Tách dầm ra khỏi nhóm hiện tại.
        /// User có thể tạo nhóm riêng hoặc để dầm đứng độc lập.
        /// </summary>
        [CommandMethod("DTS_REBAR_UNGROUP")]
        public void DTS_UNGROUP()
        {
            WriteMessage("=== UNGROUP: TÁCH DẦM RA KHỎI NHÓM ===");
            WriteMessage("\nChọn các dầm cần tách ra khỏi nhóm: ");

            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0)
            {
                WriteMessage("Không có dầm nào được chọn.");
                return;
            }

            // Load existing groups
            var existingGroups = GetOrCreateBeamGroups();
            if (existingGroups.Count == 0)
            {
                WriteMessage("Không có nhóm dầm nào trong bản vẽ.");
                return;
            }

            // Collect handles of selected beams
            var selectedHandles = new List<string>();
            UsingTransaction(tr =>
            {
                foreach (var id in selectedIds)
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (obj != null)
                    {
                        selectedHandles.Add(obj.Handle.ToString());
                    }
                }
            });

            // Remove beams from all groups
            int removedCount = 0;
            var groupsToRemove = new List<BeamGroup>();

            foreach (var handle in selectedHandles)
            {
                foreach (var group in existingGroups)
                {
                    if (group.EntityHandles != null && group.EntityHandles.Contains(handle))
                    {
                        group.EntityHandles.Remove(handle);
                        removedCount++;
                        WriteMessage($"   Đã tách dầm {handle} khỏi nhóm {group.GroupName}");

                        // ===== HARD RESET: Clear design data when structure changes =====
                        // Khi cấu trúc nhóm thay đổi, dữ liệu thiết kế cũ không còn valid
                        if (group.SelectedDesign != null)
                        {
                            WriteMessage($"   ⚠️ Reset phương án đã chốt của nhóm {group.GroupName}");
                            group.SelectedDesign = null;
                            group.LockedAt = null;
                            group.LockedBy = null;
                        }
                        // Clear proposed designs too (will be regenerated on next calculate)
                        group.BackboneOptions.Clear();
                        group.IsManuallyEdited = false;

                        // Mark empty groups for removal
                        if (group.EntityHandles.Count == 0)
                        {
                            groupsToRemove.Add(group);
                        }
                    }
                }
            }

            // Remove empty groups
            int deletedGroups = 0;
            foreach (var g in groupsToRemove)
            {
                existingGroups.Remove(g);
                deletedGroups++;
                WriteMessage($"   Đã xóa nhóm rỗng: {g.GroupName}");
            }

            if (removedCount > 0)
            {
                SaveBeamGroupsToNOD(existingGroups);
                WriteSuccess($"Đã tách {removedCount} dầm ra khỏi nhóm. Đã xóa {deletedGroups} nhóm rỗng.");
                WriteMessage("Bạn có thể chạy DTS_SET_BEAM để tạo nhóm mới cho dầm này.");
            }
            else
            {
                WriteMessage("Không có dầm nào đang thuộc nhóm.");
            }
        }

        /// <summary>
        /// Hiển thị Dashboard Mini-Toolbar
        /// </summary>
        [CommandMethod("DTS_DASHBOARD")]
        public void DTS_DASHBOARD()
        {
            DTS_Engine.UI.Forms.DashboardPalette.ShowPalette();
        }
    }
}

