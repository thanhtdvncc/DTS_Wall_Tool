using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Algorithms;
using DTS_Engine.Core.Algorithms.Rebar;
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

            // 2. Select Frames on Screen FIRST (cho phép chọn trước khi hỏi chế độ)
            var ed = AcadUtils.Ed;
            WriteMessage("\nChọn các đường Dầm (Frame) để lấy nội lực: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // 3. Ask Display Mode AFTER selection
            // 0 = Combined (Flex + Torsion) - Default
            // 1 = Flex only (Thép dọc chịu uốn)
            // 2 = Torsion only (Thép xoắn)
            // 3 = Stirrup/Web (Thép đai/Sườn)
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
                    // === PRIORITY 2: Raw XData key SapElementName (independent of xType) ===
                    else
                    {
                        var raw = XDataUtils.GetRawData(obj);
                        if (raw != null && raw.TryGetValue("SapElementName", out var sapObj))
                        {
                            var sapFromRaw = sapObj?.ToString();
                            if (!string.IsNullOrEmpty(sapFromRaw))
                            {
                                sapName = sapFromRaw;
                                mappingSource = "XData";
                            }
                        }
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
            var dtsSettings = DtsSettings.Instance;

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
                            designData.TorsionFactorUsed = dtsSettings.Beam?.TorsionDist_TopBar ?? 0.25;

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
                                var existingData = XDataUtils.ReadRebarData(obj);
                                if (existingData != null && existingData.TopAreaProv != null)
                                {
                                    // Check if existing Aprov is insufficient for new Areq
                                    bool isInsufficient = false;
                                    for (int i = 0; i < 3; i++)
                                    {
                                        double areqTop = designData.TopArea[i] + designData.TorsionArea[i] * (dtsSettings.Beam?.TorsionDist_TopBar ?? 0.25);
                                        double areqBot = designData.BotArea[i] + designData.TorsionArea[i] * (dtsSettings.Beam?.TorsionDist_BotBar ?? 0.25);

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

                                // XData-first: update REQUIRED data only (do NOT overwrite provided layout/solution)
                                XDataUtils.UpdateBeamRequiredXData(
                                    obj,
                                    tr,
                                    topArea: designData.TopArea,
                                    botArea: designData.BotArea,
                                    torsionArea: designData.TorsionArea,
                                    shearArea: designData.ShearArea,
                                    ttArea: designData.TTArea,
                                    designCombo: designData.DesignCombo,
                                    sectionName: designData.SectionName,
                                    width: designData.Width,
                                    sectionHeight: designData.SectionHeight,
                                    torsionFactorUsed: designData.TorsionFactorUsed,
                                    sapElementName: designData.SapElementName,
                                    mappingSource: designData.MappingSource);
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
                                            displayTop[i] = designData.TopArea[i] + designData.TorsionArea[i] * (dtsSettings.Beam?.TorsionDist_TopBar ?? 0.25);
                                            displayBot[i] = designData.BotArea[i] + designData.TorsionArea[i] * (dtsSettings.Beam?.TorsionDist_BotBar ?? 0.25);
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
                                            displayBotStr[i] = FormatValue(designData.TorsionArea[i] * (dtsSettings.Beam?.TorsionDist_SideBar ?? 0.50));
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
        }

        /// <summary>
        /// WORKFLOW: Import dữ liệu SAP + Tự động gom nhóm
        /// Kết hợp DTS_REBAR_SAP_RESULT + DTS_REBAR_GROUP_AUTO
        /// Tránh trường hợp user quên gom nhóm sau khi import
        /// </summary>
        [CommandMethod("DTS_REBAR_IMPORT_SAP")]
        public void DTS_REBAR_IMPORT_SAP()
        {
            WriteMessage("=== IMPORT KẾT QUẢ THIẾT KẾ TỪ SAP2000 ===");

            // Chỉ import dữ liệu từ SAP, KHÔNG auto group
            ImportSapResultInternal();

            WriteSuccess("✅ Đã import dữ liệu SAP!");
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
                    if (ent != null && ent.Layer == "dts_labels")
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
        /// UNIFIED ROUNDING: &lt;1 → 4 decimals, ≥1 → 2 decimals
        /// Used across DTS_REBAR for consistent display
        /// </summary>
        private string FormatValue(double val)
        {
            return Core.Algorithms.RebarCalculator.FormatRebarValue(val);
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
            WriteMessage("\nChọn các đường Dầm cần tính thép: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // V3.5.2: Force reload settings from file to ensure UI changes are reflected
            DtsSettings.Reload();
            var dtsSettings = DtsSettings.Instance;

            // V3.5.2: Debug - Show if logging is enabled
            if (dtsSettings.EnablePipelineLogging)
            {
                WriteMessage("🔍 DEBUG: Pipeline Logging ENABLED - Log sẽ được tạo sau khi tính toán");
            }

            // Load existing groups để check dầm thuộc group nào
            var allGroups = GetOrCreateBeamGroups();

            // Tạo map: EntityHandle -> BeamGroup
            var handleToGroup = new Dictionary<string, BeamGroup>();
            foreach (var group in allGroups)
            {
                foreach (var handle in group.EntityHandles)
                {
                    handleToGroup[handle] = group;
                }
            }

            // Phân loại dầm: trong group hoặc dầm đơn
            var groupedBeams = new Dictionary<BeamGroup, List<(ObjectId Id, BeamResultData Data)>>();
            var singleBeams = new List<(ObjectId Id, BeamResultData Data)>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in selectedIds)
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    var data = XDataUtils.ReadRebarData(obj);
                    if (data == null) continue;

                    // Validate dimensions
                    if (data.Width <= 0 || data.SectionHeight <= 0)
                    {
                        WriteMessage($"  ⚠️ Dầm {data.SapElementName ?? "?"} thiếu tiết diện. Bỏ qua.");
                        continue;
                    }

                    string handle = obj.Handle.ToString();
                    if (handleToGroup.TryGetValue(handle, out var group))
                    {
                        // Dầm thuộc group
                        if (!groupedBeams.ContainsKey(group))
                            groupedBeams[group] = new List<(ObjectId, BeamResultData)>();
                        groupedBeams[group].Add((id, data));
                    }
                    else
                    {
                        // Dầm đơn
                        singleBeams.Add((id, data));
                    }
                }
            });

            int singleCount = 0;
            int groupCount = 0;
            int lockedCount = 0;

            // ========== XỬ LÝ DẦM ĐƠN (Dùng DtsSettings - không dùng Legacy) ==========
            if (singleBeams.Count > 0)
            {
                WriteMessage($"\n--- Tính thép dầm đơn: {singleBeams.Count} dầm ---");
                UsingTransaction(tr =>
                {
                    var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                    foreach (var (id, data) in singleBeams)
                    {
                        var obj = tr.GetObject(id, OpenMode.ForWrite);

                        // Lấy torsion ratio từ DtsSettings (không phải RebarSettings)
                        double torsionRatioTop = dtsSettings.Beam?.TorsionDist_TopBar ?? 0.25;
                        double torsionRatioBot = dtsSettings.Beam?.TorsionDist_BotBar ?? 0.25;
                        double torsionRatioSide = dtsSettings.Beam?.TorsionDist_SideBar ?? 0.50;

                        for (int i = 0; i < 3; i++)
                        {
                            double asTop = data.TopArea[i] + data.TorsionArea[i] * torsionRatioTop;
                            double asBot = data.BotArea[i] + data.TorsionArea[i] * torsionRatioBot;

                            // [FIX] Sử dụng DtsSettings thay vì RebarSettings
                            string sTop = RebarCalculator.Calculate(asTop, data.Width * 10, data.SectionHeight * 10, dtsSettings);
                            string sBot = RebarCalculator.Calculate(asBot, data.Width * 10, data.SectionHeight * 10, dtsSettings);

                            data.TopRebarString[i] = sTop;
                            data.BotRebarString[i] = sBot;
                            data.TopAreaProv[i] = RebarStringParser.Parse(sTop);
                            data.BotAreaProv[i] = RebarStringParser.Parse(sBot);

                            // [FIX] Dùng DtsSettings thay vì RebarSettings cho Stirrup và Web
                            string sStirrup = RebarCalculator.CalculateStirrup(data.ShearArea[i], data.TTArea[i], data.Width * 10, dtsSettings);
                            data.StirrupString[i] = sStirrup;

                            string sWeb = RebarCalculator.CalculateWebBars(data.TorsionArea[i], torsionRatioSide, data.SectionHeight * 10, dtsSettings);
                            data.WebBarString[i] = sWeb;
                        }

                        // XData-first: only update solution keys; do NOT overwrite xType/other schemas
                        XDataUtils.UpdateBeamSolutionXData(
                            obj,
                            tr,
                            data.TopRebarString,
                            data.BotRebarString,
                            data.StirrupString,
                            data.WebBarString,
                            data.BelongToGroup,
                            data.BeamType);
                        singleCount++;
                    }
                });
            }

            // ========== XỬ LÝ DẦM TRONG GROUP (Out-Perform) ==========
            if (groupedBeams.Count > 0)
            {
                WriteMessage($"\n--- Tính thép theo nhóm: {groupedBeams.Count} nhóm ---");
                UsingTransaction(tr =>
                {
                    foreach (var kvp in groupedBeams)
                    {
                        var group = kvp.Key;
                        var beamList = kvp.Value;

                        // Generate proposals using Out-Perform (ALWAYS, even if locked)
                        var spanResults = beamList.Select(b => b.Data).ToList();
                        var objIds = beamList.Select(b => b.Id).ToList();

                        var proposals = RebarCalculator.CalculateProposalsForGroup(group, spanResults, dtsSettings);

                        if (proposals == null || proposals.Count == 0)
                        {
                            WriteMessage($"  ❌ {group.GroupName}: Không thể tạo phương án.");
                            continue;
                        }

                        // [FIX] Luôn cập nhật BackboneOptions với proposals mới (kể cả Invalid)
                        group.BackboneOptions = proposals;
                        group.SelectedBackboneIndex = 0;

                        // [FIX] CHỈ apply khi CHƯA chốt
                        if (group.IsDesignLocked)
                        {
                            // Đã chốt: Giữ nguyên SelectedDesign, KHÔNG apply proposals mới
                            lockedCount++;
                            WriteMessage($"  🔒 {group.GroupName}: Đã chốt. Proposals mới đã lưu nhưng giữ nguyên SelectedDesign.");
                        }
                        else
                        {
                            // Chưa chốt: Apply best solution
                            var bestSolution = proposals.FirstOrDefault(p => p.IsValid);

                            // [FIX] Fallback: Nếu không có giải pháp hợp lệ, lấy giải pháp có điểm cao nhất
                            if (bestSolution == null && proposals.Count > 0)
                            {
                                bestSolution = proposals.OrderByDescending(p => p.TotalScore).First();
                                WriteMessage($"  ⚠️ {group.GroupName}: Không có phương án Valid, dùng fallback: {bestSolution.OptionName}");
                            }

                            if (bestSolution != null)
                            {
                                // 1. Cập nhật XData (Logic cũ)
                                ApplyGroupSolutionToEntities(tr, group, objIds, spanResults, bestSolution, dtsSettings);

                                // 2. [MỚI - QUAN TRỌNG] Cập nhật SpanData để Viewer hiển thị được
                                UpdateGroupSpansFromSolution(group, bestSolution);

                                groupCount++;
                                WriteMessage($"  ✅ {group.GroupName}: {bestSolution.OptionName} ({bestSolution.TotalSteelWeight:F1}kg)");
                            }
                        }
                    }

                    SaveBeamGroupsToNOD(allGroups);
                });
            }

            // Summary
            WriteSuccess($"Hoàn thành: {singleCount} dầm đơn + {groupCount} nhóm. {lockedCount} nhóm đã chốt (giữ nguyên).");
        }

        /// <summary>
        /// [DEPRECATED] Đã merge vào DTS_REBAR_CALCULATE.
        /// Giữ lại cho backward compatibility, redirect sang DTS_REBAR_CALCULATE.
        /// </summary>
        [Obsolete("Use DTS_REBAR_CALCULATE instead - logic merged.")]
        [CommandMethod("DTS_REBAR_CALCULATE_GROUP")]
        public void DTS_REBAR_CALCULATE_GROUP()
        {
            WriteMessage("⚠️ Command đã được merge vào DTS_REBAR_CALCULATE. Tự động chuyển...\n");
            DTS_REBAR_CALCULATE();
        }

        /// <summary>
        /// Chốt phương án thép cho BeamGroup đang chọn.
        /// Phương án chốt sẽ KHÔNG bị ghi đè khi recalculate.
        /// </summary>
        [CommandMethod("DTS_REBAR_LOCK")]
        public void DTS_REBAR_LOCK()
        {
            WriteMessage("=== REBAR: CHỐT PHƯƠNG ÁN THÉP ===");

            // 1. Select dầm
            WriteMessage("\nChọn dầm trong nhóm cần chốt: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // 2. Tìm group chứa dầm đã chọn
            var groups = GetOrCreateBeamGroups();
            string selectedHandle = null;

            UsingTransaction(tr =>
            {
                var firstObj = tr.GetObject(selectedIds[0], OpenMode.ForRead);
                selectedHandle = firstObj?.Handle.ToString();
            });

            if (selectedHandle == null)
            {
                WriteError("Không thể đọc handle của đối tượng.");
                return;
            }

            var targetGroup = groups.FirstOrDefault(g => g.EntityHandles.Contains(selectedHandle));
            if (targetGroup == null)
            {
                WriteError("Dầm này chưa thuộc BeamGroup nào. Chạy DTS_AUTO_GROUP trước.");
                return;
            }

            // 3. Check có proposals chưa
            if (targetGroup.BackboneOptions == null || targetGroup.BackboneOptions.Count == 0)
            {
                WriteError($"{targetGroup.GroupName}: Chưa có phương án. Chạy DTS_REBAR_CALCULATE_GROUP trước.");
                return;
            }

            // 4. Lock solution
            int selectedIdx = Math.Min(targetGroup.SelectedBackboneIndex, targetGroup.BackboneOptions.Count - 1);
            selectedIdx = Math.Max(0, selectedIdx);

            var solutionToLock = targetGroup.BackboneOptions[selectedIdx];
            if (!solutionToLock.IsValid)
            {
                WriteError($"Phương án [{selectedIdx}] không hợp lệ: {solutionToLock.ValidationMessage}");
                return;
            }

            targetGroup.SelectedDesign = solutionToLock;
            targetGroup.LockedAt = DateTime.UtcNow;
            targetGroup.LockedBy = Environment.UserName;

            // 5. Save to NOD
            SaveBeamGroupsToNOD(groups);

            WriteSuccess($"✅ Đã chốt phương án cho {targetGroup.GroupName}:");
            WriteMessage($"   - Backbone: {solutionToLock.OptionName}");
            WriteMessage($"   - Khối lượng: {solutionToLock.TotalSteelWeight:F2} kg");
            WriteMessage($"   - Thời gian: {targetGroup.LockedAt:HH:mm dd/MM/yyyy}");
        }

        /// <summary>
        /// Mở khóa (unlock) phương án đã chốt cho BeamGroup.
        /// </summary>
        [CommandMethod("DTS_REBAR_UNLOCK")]
        public void DTS_REBAR_UNLOCK()
        {
            WriteMessage("=== REBAR: MỞ KHÓA PHƯƠNG ÁN ===");

            WriteMessage("\nChọn dầm trong nhóm cần mở khóa: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            var groups = GetOrCreateBeamGroups();
            string selectedHandle = null;

            UsingTransaction(tr =>
            {
                var firstObj = tr.GetObject(selectedIds[0], OpenMode.ForRead);
                selectedHandle = firstObj?.Handle.ToString();
            });

            var targetGroup = groups.FirstOrDefault(g => g.EntityHandles.Contains(selectedHandle));
            if (targetGroup == null)
            {
                WriteError("Dầm này chưa thuộc BeamGroup nào.");
                return;
            }

            if (!targetGroup.IsDesignLocked)
            {
                WriteMessage($"{targetGroup.GroupName}: Chưa chốt phương án nào.");
                return;
            }

            // Unlock
            targetGroup.SelectedDesign = null;
            targetGroup.LockedAt = null;
            targetGroup.LockedBy = null;

            SaveBeamGroupsToNOD(groups);
            WriteSuccess($"✅ Đã mở khóa phương án cho {targetGroup.GroupName}. Chạy DTS_REBAR_CALCULATE_GROUP để tính lại.");
        }

        /// <summary>
        /// Hiển thị danh sách các BeamGroup đã chốt phương án.
        /// </summary>
        [CommandMethod("DTS_REBAR_LOCKED_LIST")]
        public void DTS_REBAR_LOCKED_LIST()
        {
            WriteMessage("=== DANH SÁCH NHÓM DẦM ĐÃ CHỐT ===\n");

            var groups = GetOrCreateBeamGroups();
            var lockedGroups = groups.Where(g => g.IsDesignLocked).ToList();

            if (lockedGroups.Count == 0)
            {
                WriteMessage("Chưa có nhóm dầm nào được chốt.\n");
                WriteMessage("Sử dụng DTS_REBAR_CALCULATE_GROUP để tạo phương án, sau đó DTS_REBAR_LOCK để chốt.");
                return;
            }

            WriteMessage($"Tổng: {lockedGroups.Count} nhóm đã chốt\n");
            WriteMessage("─────────────────────────────────────────────────────");

            foreach (var g in lockedGroups.OrderBy(x => x.GroupName))
            {
                var sol = g.SelectedDesign;
                WriteMessage($"  {g.GroupName,-20} | {sol?.OptionName,-10} | {sol?.TotalSteelWeight:F1} kg | {g.LockedAt:dd/MM/yyyy HH:mm}");
            }

            WriteMessage("─────────────────────────────────────────────────────");
            WriteMessage("\nDùng DTS_REBAR_UNLOCK để mở khóa nếu cần tính lại.");
        }


        private bool IsSamePt(Core.Primitives.Point2D p2d, Point3d p3d, double tol = 200.0)
        {
            return Math.Abs(p2d.X - p3d.X) < tol && Math.Abs(p2d.Y - p3d.Y) < tol;
        }

        /// <summary>
        /// Sắp xếp dầm thông minh dựa trên Setting (Góc bắt đầu + Hướng quét)
        /// Hỗ trợ Scanline (Row-Binning) linh hoạt cho cả 4 góc và 2 hướng.
        /// </summary>
        private List<(ObjectId Id, Point3d Mid, bool IsGirder, bool IsXDir, BeamResultData Data, double LevelZ)>
            GetSmartSortedBeams(
                List<(ObjectId Id, Point3d Mid, bool IsGirder, bool IsXDir, BeamResultData Data, double LevelZ)> inputList,
                NamingConfig config)
        {
            if (inputList == null || inputList.Count == 0) return inputList;

            // 1. Lấy Config (hoặc default)
            int direction = config?.SortDirection ?? 0; // 0: Horiz (Row), 1: Vert (Col)
            int corner = config?.SortCorner ?? 0;       // 0:TL, 1:TR, 2:BL, 3:BR
            double tol = config?.RowTolerance ?? 500.0;

            // 2. Xác định chiều Sort (Ascending hay Descending) dựa trên Corner
            // Corner 0 (TL): X tăng, Y giảm
            // Corner 1 (TR): X giảm, Y giảm
            // Corner 2 (BL): X tăng, Y tăng
            // Corner 3 (BR): X giảm, Y tăng

            int xSign = (corner == 1 || corner == 3) ? -1 : 1; // 1: Tăng dần, -1: Giảm dần
            int ySign = (corner == 0 || corner == 1) ? -1 : 1; // 1: Tăng dần, -1: Giảm dần

            // Logic Scanline:
            // - Primary Axis: Trục dùng để "Gom hàng" (Binning)
            // - Secondary Axis: Trục dùng để sort các phần tử trong cùng 1 hàng

            if (direction == 0) // HORIZONTAL (Quét theo hàng ngang - Ưu tiên Y)
            {
                // Primary: Y (chia bin), Secondary: X
                return inputList
                    .OrderBy(b => Math.Round(b.Mid.Y / tol) * ySign) // Sort các "Hàng" trước
                    .ThenBy(b => b.Mid.X * xSign)                    // Sort các phần tử trong hàng
                    .ToList();
            }
            else // VERTICAL (Quét theo cột dọc - Ưu tiên X)
            {
                // Primary: X (chia bin), Secondary: Y
                return inputList
                    .OrderBy(b => Math.Round(b.Mid.X / tol) * xSign) // Sort các "Cột" trước
                    .ThenBy(b => b.Mid.Y * ySign)                    // Sort các phần tử trong cột
                    .ToList();
            }
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
            WriteMessage("=== SMART BEAM NAMING (CONFIGURABLE) ===");
            WriteMessage("\nChọn các đường Dầm cần đặt tên: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // Load Settings (DtsSettings chứa NamingConfig)
            var settings = DtsSettings.Instance;
            var namingCfg = settings.Naming ?? new NamingConfig();

            // Lấy GirderMinWidth từ config (default 300)
            double girderThreshold = namingCfg.GirderMinWidth > 0 ? namingCfg.GirderMinWidth : 300.0;

            // 1. Thu thập dữ liệu dầm
            var allBeams = new List<(ObjectId Id, Point3d Mid, bool IsGirder, bool IsXDir, BeamResultData Data, double LevelZ)>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in selectedIds)
                {
                    var curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (curve == null) continue;

                    // [FIX] Chỉ xử lý phần tử đã đăng ký DTS_APP
                    if (!XDataUtils.HasAppXData(curve)) continue;

                    Point3d mid = curve.StartPoint + (curve.EndPoint - curve.StartPoint) * 0.5;
                    Vector3d dir = curve.EndPoint - curve.StartPoint;
                    bool isXDir = Math.Abs(dir.X) > Math.Abs(dir.Y);

                    var xdata = XDataUtils.ReadRebarData(curve);

                    // [FIX] Nếu có XData BaseZ (logical elevation), dùng nó thay vì geometric Z (thường là 0 trong 2D)
                    double levelZ;
                    if (xdata != null && xdata.BaseZ.HasValue)
                    {
                        levelZ = xdata.BaseZ.Value;
                    }
                    else
                    {
                        // Fallback: Làm tròn Z hình học để phân tầng (Tolerance 100mm)
                        levelZ = Math.Round(mid.Z / 100.0) * 100.0;
                    }

                    // === GIRDER DETECTION (COLUMN + AXIS BASED) ===
                    // Rule 1: Dầm có cột ở 2 đầu => Girder (chắc chắn)
                    // Rule 2: Dầm có cột ở 1 đầu + nằm trên trục => Girder
                    // Rule 3: Còn lại => Beam (dầm phụ)
                    bool isGirder = false;
                    if (xdata != null)
                    {
                        int columnCount = (xdata.SupportI == 1 ? 1 : 0) + (xdata.SupportJ == 1 ? 1 : 0);

                        if (columnCount == 2)
                        {
                            // 2 cột ở 2 đầu => chắc chắn Girder
                            isGirder = true;
                        }
                        else if (columnCount == 1 && !string.IsNullOrEmpty(xdata.AxisName))
                        {
                            // 1 cột + nằm trên trục (có tên trục) => Girder
                            isGirder = true;
                        }
                        else
                        {
                            // 0 cột, hoặc 1 cột nhưng không có tên trục => Beam
                            isGirder = false;
                        }
                    }
                    else
                    {
                        // Không có XData => default Beam
                        isGirder = false;
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
                    WriteMessage($"\n--- Tầng Z={currentZ:F0} ---");

                    // Config naming cho tầng này
                    // Config naming cho tầng này
                    var storyConfig = settings.GetStoryConfig(currentZ);

                    // [STRICT] Kiểm tra Config tồn tại. Nếu không có -> Báo lỗi & Bỏ qua
                    if (storyConfig == null)
                    {
                        WriteMessage($"   ⚠️ [SKIP] Không tìm thấy cấu hình cho tầng Z={currentZ}. Vui lòng kiểm tra lại Setting > Naming.");
                        continue; // Bỏ qua tầng này
                    }

                    WriteMessage($"   [INFO] Áp dụng Config: {storyConfig.StoryName}, Elev={storyConfig.Elevation}, StartIndex={storyConfig.StartIndex}");

                    // Lấy thông tin từ Config (Không dùng fallback mặc định)
                    string beamPrefix = storyConfig.BeamPrefix;   // VD: "B"
                    string girderPrefix = storyConfig.GirderPrefix; // VD: "G"
                    string suffix = storyConfig.Suffix ?? "";
                    int startIndex = storyConfig.StartIndex;

                    // [FIX] StoryIndex = StartIndex trực tiếp từ StoryConfig
                    // VD: xBaseZ=11700 khớp với StoryConfig có StartIndex=3 => storyIndex="3"
                    string storyIndex = startIndex.ToString();

                    // Tách Dầm chính / Dầm phụ
                    var girders = levelGroup.Where(b => b.IsGirder).ToList();
                    var beams = levelGroup.Where(b => !b.IsGirder).ToList();

                    // === PROCESS FUNCTION MỚI (Dùng GetSmartSortedBeams) ===
                    void ProcessList(List<(ObjectId Id, Point3d Mid, bool IsGirder, bool IsXDir, BeamResultData Data, double LevelZ)> list, string prefix)
                    {
                        if (list.Count == 0) return;

                        // [CONFIGURABLE] Gọi hàm sort thông minh với NamingConfig
                        var sortedList = GetSmartSortedBeams(list, namingCfg);

                        // Danh sách Assigned Types để gom nhóm (WxH + Steel + Direction)
                        var assignedTypes = new Dictionary<string, int>();

                        // [FIX] Bộ đếm riêng cho từng hướng (key="X" hoặc "Y")
                        // Reset về 1 cho mỗi hướng
                        var counters = new Dictionary<string, int> { { "X", 1 }, { "Y", 1 } };

                        foreach (var item in sortedList)
                        {
                            // Tạo Key định danh để so sánh giống nhau
                            string w = item.Data?.Width.ToString("F0") ?? "0";
                            string h = item.Data?.SectionHeight.ToString("F0") ?? "0";

                            // Lấy string thép
                            string top = (item.Data?.TopRebarString != null && item.Data.TopRebarString.Length > 1) ? item.Data.TopRebarString[1] ?? "-" : "-";
                            string bot = (item.Data?.BotRebarString != null && item.Data.BotRebarString.Length > 1) ? item.Data.BotRebarString[1] ?? "-" : "-";
                            string stir = (item.Data?.StirrupString != null && item.Data.StirrupString.Length > 1) ? item.Data.StirrupString[1] ?? "-" : "-";

                            // [FIX] Lấy Direction từ item.IsXDir
                            string direction = item.IsXDir ? "X" : "Y";

                            // Key để gom nhóm (bao gồm direction)
                            string typeKey = $"{direction}_{w}x{h}_{top.Trim()}_{bot.Trim()}_{stir.Trim()}";

                            int number;
                            if (assignedTypes.ContainsKey(typeKey))
                            {
                                number = assignedTypes[typeKey];
                            }
                            else
                            {
                                // Get current counter for this direction
                                number = counters[direction];
                                // Increment counter for this direction
                                counters[direction]++;

                                assignedTypes[typeKey] = number;
                            }

                            // [FIX] Format đầy đủ: {StoryIndex}{Prefix}{Direction}{Number}{Suffix}
                            // VD: 3GX12 = Tầng 3, Girder, Hướng X, Số 12
                            string fullName = $"{storyIndex}{prefix}{direction}{number}{suffix}";

                            // Update CAD & XData
                            var curve = tr.GetObject(item.Id, OpenMode.ForWrite) as Curve;
                            if (curve != null)
                            {
                                if (item.Data != null)
                                {
                                    // Set BeamName (display name) - NOT SapElementName (SAP frame ID)
                                    XDataUtils.MergeRawData(curve, tr, new Dictionary<string, object>
                                    {
                                        ["BeamName"] = fullName
                                    });
                                }
                                LabelPlotter.PlotLabel(btr, tr, curve.StartPoint, curve.EndPoint, fullName, LabelPosition.MiddleBottom);
                            }
                        }
                    }

                    ProcessList(girders, girderPrefix);
                    ProcessList(beams, beamPrefix);
                }
            });

            // Log config info
            WriteSuccess($"✅ Đã đặt tên theo Cấu hình Naming.");
            WriteMessage($"   - Direction: {(namingCfg.SortDirection == 0 ? "Horizontal" : "Vertical")}");
            WriteMessage($"   - Corner: {new[] { "TopLeft", "TopRight", "BottomLeft", "BottomRight" }[namingCfg.SortCorner % 4]}");
            WriteMessage($"   - RowTolerance: {namingCfg.RowTolerance}mm, GirderMinWidth: {girderThreshold}mm");
        }

        /// <summary>
        /// Xuất kết quả bố trí thép thực tế (As Provided) từ CAD cập nhật ngược lại vào SAP2000.
        /// [UPDATE] Format: {BeamName}_{Section}_{TopStart}_{TopEnd}_{BotStart}_{BotEnd}
        /// Ví dụ: 1GX1_40x60_8.6_13.2_8.3_8.6
        /// </summary>
        [CommandMethod("DTS_REBAR_EXPORT_SAP")]
        public void DTS_REBAR_EXPORT_SAP()
        {
            WriteMessage("=== REBAR: XUẤT THÉP VỀ SAP2000 (FORMATTED) ===");

            // 1. Check Connection
            if (!SapUtils.IsConnected)
            {
                if (!SapUtils.Connect(out string msg))
                {
                    WriteError(msg);
                    return;
                }
            }

            WriteMessage("\n⚠️ LƯU Ý: Hãy đảm bảo mô hình SAP2000 ĐÃ ĐƯỢC MỞ KHÓA (Unlock).");

            SapDesignEngine engine = new SapDesignEngine();
            if (!engine.IsReady)
            {
                WriteError("Không thể khởi tạo SAP Design Engine.");
                return;
            }

            // 2. Select Objects
            WriteMessage("Chọn các đường Dầm cần cập nhật về SAP: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // 3. Mapping
            var allSapFrames = SapUtils.GetAllFramesGeometry();
            Dictionary<ObjectId, string> cadToSap = new Dictionary<ObjectId, string>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in selectedIds)
                {
                    var curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (curve == null) continue;

                    // [FIX] Chỉ xử lý phần tử đã đăng ký DTS_APP
                    if (!XDataUtils.HasAppXData(curve)) continue;

                    var xData = XDataUtils.ReadRebarData(curve);
                    if (xData != null && !string.IsNullOrEmpty(xData.SapElementName))
                    {
                        cadToSap[id] = xData.SapElementName;
                        continue;
                    }

                    // Fallback mapping
                    Point3d start = curve.StartPoint;
                    Point3d end = curve.EndPoint;
                    var match = allSapFrames.FirstOrDefault(f =>
                        (IsSamePt(f.StartPt, start) && IsSamePt(f.EndPt, end)) ||
                        (IsSamePt(f.StartPt, end) && IsSamePt(f.EndPt, start))
                    );

                    if (match != null) cadToSap[id] = match.Name;
                }
            });

            if (cadToSap.Count == 0)
            {
                WriteError("Không tìm thấy dầm SAP nào khớp.");
                return;
            }

            // 4. Update SAP
            int successCount = 0;
            int failCount = 0;
            var dtsSettings = DtsSettings.Instance;

            UsingTransaction(tr =>
            {
                foreach (var kvp in cadToSap)
                {
                    ObjectId cadId = kvp.Key;
                    string sapID = kvp.Value;

                    DBObject obj = tr.GetObject(cadId, OpenMode.ForRead);
                    var data = XDataUtils.ReadRebarData(obj);

                    if (data == null) continue;
                    if (data.Width <= 0 || data.SectionHeight <= 0) continue;

                    // Ensure Data (Recalculate logic if needed)
                    if (data.TopAreaProv == null || data.TopAreaProv.Length < 3 || data.TopAreaProv[0] <= 0)
                    {
                        // Tự động tính toán lại nếu thiếu dữ liệu
                        if (data.TopAreaProv == null) data.TopAreaProv = new double[6];
                        if (data.BotAreaProv == null) data.BotAreaProv = new double[6];
                        if (data.TopArea == null) data.TopArea = new double[6];
                        if (data.BotArea == null) data.BotArea = new double[6];
                        if (data.TorsionArea == null) data.TorsionArea = new double[6];

                        double torsionRatioTop = dtsSettings.Beam?.TorsionDist_TopBar ?? 0.25;
                        double torsionRatioBot = dtsSettings.Beam?.TorsionDist_BotBar ?? 0.25;

                        for (int i = 0; i < 3; i++)
                        {
                            double asTop = data.TopArea[i] + data.TorsionArea[i] * torsionRatioTop;
                            double asBot = data.BotArea[i] + data.TorsionArea[i] * torsionRatioBot;

                            if (asTop == 0) asTop = data.Width * data.SectionHeight * 0.0015;
                            if (asBot == 0) asBot = data.Width * data.SectionHeight * 0.0015;

                            string sTop = RebarCalculator.Calculate(asTop, data.Width * 10, data.SectionHeight * 10, dtsSettings);
                            string sBot = RebarCalculator.Calculate(asBot, data.Width * 10, data.SectionHeight * 10, dtsSettings);

                            data.TopAreaProv[i] = RebarStringParser.Parse(sTop);
                            data.BotAreaProv[i] = RebarStringParser.Parse(sBot);
                        }
                    }

                    double[] topProv = data.TopAreaProv ?? new double[6];
                    double[] botProv = data.BotAreaProv ?? new double[6];

                    // === [NAMING LOGIC - STRICT: NO FALLBACK] ===

                    // [STRICT] Bỏ qua nếu thiếu BeamName - KHÔNG CÓ FALLBACK
                    if (string.IsNullOrEmpty(data.BeamName))
                    {
                        WriteMessage($" ❌ [{sapID}] Lỗi: Chưa có BeamName. Vui lòng chạy DTS_REBAR_BEAM_NAME trước.");
                        continue;
                    }
                    string baseName = data.BeamName.Replace(" ", "").Replace("/", "_");

                    // === APPLY EXPORT CONFIG ===
                    var exportCfg = dtsSettings.Export ?? new ExportConfig();
                    string sep = exportCfg.Separator ?? "_";
                    string fmt = $"F{exportCfg.RebarDecimalPlaces}";

                    // 2. Section: "30x40" (bật/tắt theo ExportConfig)
                    string dimStr = exportCfg.IncludeSection
                        ? $"{sep}{data.Width:F0}x{data.SectionHeight:F0}"
                        : "";

                    // 3. Rebar: dùng RebarFormat để user tùy chỉnh thứ tự
                    string rebarStr = "";
                    if (exportCfg.IncludeRebar)
                    {
                        rebarStr = (exportCfg.RebarFormat ?? "{TS}_{TE}_{BS}_{BE}")
                            .Replace("{TS}", topProv[0].ToString(fmt))
                            .Replace("{TM}", topProv[1].ToString(fmt))
                            .Replace("{TE}", topProv[2].ToString(fmt))
                            .Replace("{BS}", botProv[0].ToString(fmt))
                            .Replace("{BM}", botProv[1].ToString(fmt))
                            .Replace("{BE}", botProv[2].ToString(fmt));
                        rebarStr = sep + rebarStr;
                    }

                    // 4. Combine: {BeamName}{Section}{Rebar}
                    string newSectionName = $"{baseName}{dimStr}{rebarStr}";

                    // [STRICT] Kiểm tra độ dài - báo lỗi thay vì rút gọn
                    int maxLen = exportCfg.MaxSectionNameLength > 0 ? exportCfg.MaxSectionNameLength : 49;
                    if (newSectionName.Length > maxLen)
                    {
                        WriteMessage($" ⚠️ [{sapID}] Tên quá dài ({newSectionName.Length}/{maxLen} ký tự): {newSectionName}");
                        continue;
                    }

                    // 5. Call Engine
                    try
                    {
                        bool success = engine.UpdateBeamRebar(
                            sapID,
                            newSectionName,
                            topProv,
                            botProv,
                            dtsSettings.Beam?.CoverTop ?? 35,
                            dtsSettings.Beam?.CoverBot ?? 35
                        );

                        if (success) successCount++;
                        else
                        {
                            failCount++;
                            WriteMessage($" -> [{sapID}] Thất bại. Name: {newSectionName}");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        WriteMessage($" -> [{sapID}] Exception: {ex.Message}");
                        failCount++;
                    }
                }
            });

            if (failCount > 0)
                WriteError($"Thất bại: {failCount} dầm.");

            if (successCount > 0)
            {
                var successIds = cadToSap.Keys.ToList();
                VisualUtils.ResetToByLayer(successIds);
                WriteSuccess($"Đã cập nhật {successCount} dầm về SAP với định dạng mới.");
            }
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

            // 1. Select Objects FIRST
            var ed = AcadUtils.Ed;
            WriteMessage("\nChọn các đường Dầm cần hiển thị: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // 2. Chọn chế độ hiển thị AFTER selection
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

            // Clear existing labels for SELECTED beams only (refresh)
            var selectedHandles = selectedIds.Select(id => id.Handle.ToString()).ToList();
            ClearRebarLabels(selectedHandles);

            // int count = 0; // Previously for counting plotted labels - not currently used
            var dtsSettings = DtsSettings.Instance;

            UsingTransaction(tr =>
            {
                // Ensure the layer exists before creating labels
                AcadUtils.EnsureLayerExists("dts_labels", tr);

                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (ObjectId id in selectedIds)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    var data = XDataUtils.ReadRebarData(obj);
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
                                    double torsionTop = dtsSettings.Beam?.TorsionDist_TopBar ?? 0.25;
                                    double torsionBot = dtsSettings.Beam?.TorsionDist_BotBar ?? 0.25;
                                    double asReqTop = data.TopArea[i] + data.TorsionArea[i] * torsionTop;
                                    double asReqBot = data.BotArea[i] + data.TorsionArea[i] * torsionBot;
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
                                    double torsionSide = dtsSettings.Beam?.TorsionDist_SideBar ?? 0.50;
                                    double webReq = data.TorsionArea?[i] * torsionSide ?? 0;
                                    string webStr = data.WebBarString?[i] ?? "-";
                                    double webProv = RebarCalculator.ParseRebarArea(webStr);
                                    botText = $"{FormatValue(webProv)}/{FormatValue(webReq)}\\P{webStr}";
                                }
                                break;
                        }

                        // Plot labels
                        Point3d labelPos1, labelPos2;
                        if (i == 0)
                        {
                            labelPos1 = pStart;
                            labelPos2 = pStart;
                        }
                        else if (i == 1)
                        {
                            labelPos1 = new Point3d((pStart.X + pEnd.X) / 2, (pStart.Y + pEnd.Y) / 2, 0);
                            labelPos2 = labelPos1;
                        }
                        else
                        {
                            labelPos1 = pEnd;
                            labelPos2 = pEnd;
                        }

                        // Create MText for Top
                        var mtextTop = new MText();
                        mtextTop.Contents = topText;
                        mtextTop.TextHeight = dtsSettings.General.TextHeight;
                        mtextTop.Location = new Point3d(labelPos1.X, labelPos1.Y + 2.5, 0);
                        mtextTop.Layer = "dts_labels";
                        mtextTop.ColorIndex = 1; // Red

                        var xDataTop = new Dictionary<string, object>();
                        xDataTop["xOwnerHandle"] = id.Handle.ToString();
                        xDataTop["xType"] = "RebarLabel";
                        XDataUtils.SetRawData(mtextTop, xDataTop, tr);

                        btr.AppendEntity(mtextTop);
                        tr.AddNewlyCreatedDBObject(mtextTop, true);

                        // Create MText for Bottom
                        var mtextBot = new MText();
                        mtextBot.Contents = botText;
                        mtextBot.TextHeight = dtsSettings.General.TextHeight;
                        mtextBot.Location = new Point3d(labelPos2.X, labelPos2.Y - 2.5, 0);
                        mtextBot.Layer = "dts_labels";
                        mtextBot.ColorIndex = 5; // Blue

                        var xDataBot = new Dictionary<string, object>();
                        xDataBot["xOwnerHandle"] = id.Handle.ToString();
                        xDataBot["xType"] = "RebarLabel";
                        XDataUtils.SetRawData(mtextBot, xDataBot, tr);

                        btr.AppendEntity(mtextBot);
                        tr.AddNewlyCreatedDBObject(mtextBot, true);
                    }
                }
            });

            WriteSuccess($"Đã hiển thị thép cho {selectedIds.Count} dầm (Mode {mode}).");
        }

        [CommandMethod("DTS_REBAR_VIEWER")]
        public void DTS_BEAM_VIEWER()
        {
            WriteMessage("=== BEAM GROUP VIEWER ===");
            WriteMessage("\nChọn dầm cần xem (hoặc Enter để xem tất cả nhóm):");

            try
            {
                // [FIX] Cho phép user chọn hoặc skip (xem tất cả)
                var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE", true); // allowEmpty = true

                var allGroups = GetOrCreateBeamGroups();
                var resultGroups = new List<BeamGroup>();

                if (selectedIds.Count == 0)
                {
                    // User nhấn Enter -> Xem tất cả groups
                    resultGroups = allGroups;
                    WriteMessage($"Hiển thị tất cả {allGroups.Count} nhóm dầm.");
                }
                else
                {
                    // Get selected handles
                    var selectedHandles = new HashSet<string>();
                    UsingTransaction(tr =>
                    {
                        foreach (var id in selectedIds)
                        {
                            var obj = tr.GetObject(id, OpenMode.ForRead);
                            if (obj != null)
                                selectedHandles.Add(obj.Handle.ToString());
                        }
                    });

                    // Find handles that are already in groups (UPPERCASE for case-insensitive comparison)
                    var handlesInGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in allGroups)
                    {
                        foreach (var h in g.EntityHandles)
                            handlesInGroups.Add(h?.ToUpperInvariant() ?? h);
                    }

                    // Normalize selected handles to uppercase
                    var selectedHandlesNormalized = new HashSet<string>(
                        selectedHandles.Select(h => h?.ToUpperInvariant() ?? h),
                        StringComparer.OrdinalIgnoreCase);

                    // === 1. GET GROUPS that contain selected beams ===
                    var matchedGroups = allGroups
                        .Where(g => g.EntityHandles.Any(h => selectedHandlesNormalized.Contains(h?.ToUpperInvariant() ?? h)))
                        .ToList();
                    resultGroups.AddRange(matchedGroups);

                    // === 2. CREATE TEMP GROUPS for ungrouped beams (filter out already in groups) ===
                    var ungroupedHandles = selectedHandles
                        .Where(h => !handlesInGroups.Contains(h?.ToUpperInvariant() ?? h))
                        .ToList();

                    if (ungroupedHandles.Count > 0)
                    {
                        UsingTransaction(tr =>
                        {
                            foreach (var handle in ungroupedHandles)
                            {
                                try
                                {
                                    var objId = AcadUtils.GetObjectIdFromHandle(handle);
                                    if (objId == ObjectId.Null) continue;

                                    var ent = tr.GetObject(objId, OpenMode.ForRead);
                                    var curve = ent as Curve;
                                    if (curve == null) continue;

                                    var length = curve.GetDistanceAtParameter(curve.EndParam) / 1000.0; // mm to m
                                    var start = curve.StartPoint;
                                    var end = curve.EndPoint;

                                    // === READ REAL SECTION DATA FROM XDATA ===
                                    var beamData = XDataUtils.ReadElementData<BeamData>(ent);
                                    double width = beamData?.Width ?? 220;   // mm
                                    double height = beamData?.Height ?? 400; // mm (Depth alias)
                                    string sectionName = beamData?.SectionName ?? $"B{width}x{height}";

                                    // === READ DESIGN + SOLUTION DATA FROM XDATA (XData-first) ===
                                    var designData = XDataUtils.ReadRebarData(ent);
                                    var rebarInfo = XDataUtils.ReadRebarXData(ent); // legacy fallback

                                    // Parse rebar strings to populate SpanData arrays
                                    // Format: "3D18" or "2D16+2D18" at 6 positions (Left, L/4, Mid, R/4, Right, Reserve)
                                    var topRebar = new string[3, 6]; // 3 layers × 6 positions
                                    var botRebar = new string[3, 6];
                                    var stirrup = new string[3];     // 3 positions: Left, Mid, Right
                                    var webBar = new string[3];

                                    var asTopReq6 = new double[6];
                                    var asBotReq6 = new double[6];
                                    var stirrupReq3 = new double[3];
                                    var webReq3 = new double[3];

                                    if (designData != null)
                                    {
                                        double torsTop = DtsSettings.Instance.Beam?.TorsionDist_TopBar ?? 0.25;
                                        double torsBot = DtsSettings.Instance.Beam?.TorsionDist_BotBar ?? 0.25;
                                        double torsSide = DtsSettings.Instance.Beam?.TorsionDist_SideBar ?? 0.50;
                                        for (int zi = 0; zi < 3; zi++)
                                        {
                                            double asTopReq = (designData.TopArea?[zi] ?? 0) + (designData.TorsionArea?[zi] ?? 0) * torsTop;
                                            double asBotReq = (designData.BotArea?[zi] ?? 0) + (designData.TorsionArea?[zi] ?? 0) * torsBot;
                                            int p0 = zi == 0 ? 0 : (zi == 1 ? 2 : 4);
                                            int p1 = p0 + 1;
                                            asTopReq6[p0] = asTopReq;
                                            asTopReq6[p1] = asTopReq;
                                            asBotReq6[p0] = asBotReq;
                                            asBotReq6[p1] = asBotReq;

                                            stirrupReq3[zi] = (designData.ShearArea?[zi] ?? 0);
                                            webReq3[zi] = (designData.TorsionArea?[zi] ?? 0) * torsSide;
                                        }
                                    }

                                    // Prefer BeamResultData 3-zone solution arrays (Start/Mid/End)
                                    var topZones = (designData?.TopRebarString != null && designData.TopRebarString.Length >= 3)
                                        ? designData.TopRebarString
                                        : new string[3];
                                    var botZones = (designData?.BotRebarString != null && designData.BotRebarString.Length >= 3)
                                        ? designData.BotRebarString
                                        : new string[3];
                                    var stirZones = (designData?.StirrupString != null && designData.StirrupString.Length >= 3)
                                        ? designData.StirrupString
                                        : new string[3];
                                    var webZones = (designData?.WebBarString != null && designData.WebBarString.Length >= 3)
                                        ? designData.WebBarString
                                        : new string[3];

                                    // Map 3 zones -> 6 positions: (0,1)=Start, (2,3)=Mid, (4,5)=End
                                    for (int zi = 0; zi < 3; zi++)
                                    {
                                        int p0 = zi == 0 ? 0 : (zi == 1 ? 2 : 4);
                                        int p1 = p0 + 1;
                                        if (!string.IsNullOrEmpty(topZones[zi])) { topRebar[0, p0] = topZones[zi]; topRebar[0, p1] = topZones[zi]; }
                                        if (!string.IsNullOrEmpty(botZones[zi])) { botRebar[0, p0] = botZones[zi]; botRebar[0, p1] = botZones[zi]; }
                                        stirrup[zi] = stirZones[zi] ?? "";
                                        webBar[zi] = webZones[zi] ?? "";
                                    }

                                    // Legacy fallback: fill if XData zones are empty
                                    if (topZones.All(string.IsNullOrEmpty) && !string.IsNullOrEmpty(rebarInfo?.TopRebar))
                                        for (int i = 0; i < 6; i++) topRebar[0, i] = rebarInfo.TopRebar;

                                    if (botZones.All(string.IsNullOrEmpty) && !string.IsNullOrEmpty(rebarInfo?.BotRebar))
                                        for (int i = 0; i < 6; i++) botRebar[0, i] = rebarInfo.BotRebar;

                                    if (stirZones.All(string.IsNullOrEmpty) && !string.IsNullOrEmpty(rebarInfo?.Stirrup))
                                        for (int i = 0; i < 3; i++) stirrup[i] = rebarInfo.Stirrup;

                                    if (webZones.All(string.IsNullOrEmpty) && !string.IsNullOrEmpty(rebarInfo?.SideBar))
                                        webBar[1] = rebarInfo.SideBar;

                                    // Create real single-span BeamGroup with calculated rebar
                                    var singleGroup = new BeamGroup
                                    {
                                        GroupName = $"[Đơn] {sectionName}",
                                        Name = $"SINGLE_{handle}",
                                        IsSingleBeam = true, // Mark as single beam (1 span)
                                        EntityHandles = new List<string> { handle },
                                        Width = width,
                                        Height = height,
                                        TotalLength = length,
                                        Spans = new List<SpanData>
                                        {
                                            new SpanData
                                            {
                                                SpanId = "S1",
                                                SpanIndex = 0,
                                                Length = length,
                                                ClearLength = Math.Max(0, length - 0.3), // ~30cm for supports
                                                Width = width,
                                                Height = height,
                                                IsActive = true,
                                                TopRebarInternal = topRebar,
                                                BotRebarInternal = botRebar,
                                                Stirrup = stirrup,
                                                WebBar = webBar,
                                                SideBar = rebarInfo?.SideBar,
                                                As_Top = asTopReq6,
                                                As_Bot = asBotReq6,
                                                StirrupReq = stirrupReq3,
                                                WebReq = webReq3,
                                                Segments = new List<PhysicalSegment>
                                                {
                                                    new PhysicalSegment
                                                    {
                                                        EntityHandle = handle,
                                                        Length = length,
                                                        StartPoint = new double[] { start.X, start.Y },
                                                        EndPoint = new double[] { end.X, end.Y },
                                                        TopRebar = (topRebar[0,2] ?? rebarInfo?.TopRebar),
                                                        BotRebar = (botRebar[0,2] ?? rebarInfo?.BotRebar),
                                                        Stirrup = (stirrup.Length > 1 ? stirrup[1] : rebarInfo?.Stirrup)
                                                    }
                                                }
                                            }
                                        },
                                        Supports = new List<SupportData>
                                        {
                                            new SupportData { SupportId = "C1", SupportIndex = 0, Type = SupportType.Column, Width = 300 },
                                            new SupportData { SupportId = "C2", SupportIndex = 1, Type = SupportType.Column, Width = 300 }
                                        }
                                    };

                                    resultGroups.Add(singleGroup);
                                }
                                catch { }
                            }
                        });

                        WriteMessage($"Đã tạo {ungroupedHandles.Count} nhóm dầm đơn từ XData.");
                    }

                    WriteMessage($"Tổng cộng: {matchedGroups.Count} nhóm có sẵn + {ungroupedHandles.Count} dầm đơn = {resultGroups.Count} items.");
                }

                // === REFRESH XData before displaying (ensure As_Top/As_Bot are current) ===
                RefreshGroupsFromXData(resultGroups);

                // Show viewer dialog as MODELESS
                var dialog = new UI.Forms.BeamGroupViewerDialog(resultGroups, ApplyBeamGroupResults);
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

                    // Try to read ResultData from XData
                    var resultData = XDataUtils.ReadElementData(ent) as Core.Data.BeamResultData;
                    if (resultData != null)
                    {
                        w = resultData.Width > 0 ? resultData.Width * 10 : w;
                        h = resultData.SectionHeight > 0 ? resultData.SectionHeight * 10 : h;
                    }
                    else
                    {
                        // Fallback to basic BeamData
                        var beamXData = XDataUtils.ReadBeamData(ent);
                        if (beamXData != null)
                        {
                            w = beamXData.Width ?? w;
                            h = beamXData.Height ?? h;
                        }
                    }

                    beamDataList.Add(new Core.Data.BeamGeometry
                    {
                        Handle = ent.Handle.ToString(),
                        Name = resultData?.SapElementName ?? ent.Handle.ToString(),
                        ResultData = resultData,
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
                    // Propagate A_req
                    BeamGroupDetector.AggregateRebarAreas(span, new List<Core.Data.BeamGeometry> { beam }, settings);

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
                    // Propagate A_req
                    BeamGroupDetector.AggregateRebarAreas(span, spanBeams, settings);

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

                double totalLengthMm = group.TotalLength > 0
                    ? group.TotalLength * 1000
                    : spanInfos.Sum(s => s.Length);
                string groupType = group.GroupType?.ToUpperInvariant() ?? "BEAM";

                // Resolve bar diameter + bars per layer from current design context
                var design = group.SelectedDesign ?? group.BackboneOptions?.ElementAtOrDefault(group.SelectedBackboneIndex);
                int barDiameter = design?.BackboneDiameter
                    ?? settings.General?.AvailableDiameters?.DefaultIfEmpty().Max()
                    ?? 0;
                int barsPerLayerTop = Math.Max(2, design?.BackboneCount_Top ?? (settings.Beam?.MinBarsPerLayer ?? 2));
                int barsPerLayerBot = Math.Max(2, design?.BackboneCount_Bot ?? (settings.Beam?.MinBarsPerLayer ?? 2));

                // Material grades for anchorage/splice tables
                string concreteGrade = !string.IsNullOrWhiteSpace(group.ConcreteGrade)
                    ? group.ConcreteGrade
                    : (settings.Anchorage?.ConcreteGrades?.FirstOrDefault() ?? settings.General?.ConcreteGradeName);
                string steelGrade = !string.IsNullOrWhiteSpace(group.SteelGrade)
                    ? group.SteelGrade
                    : (settings.Anchorage?.SteelGrades?.FirstOrDefault() ?? settings.General?.SteelGradeName);

                // Determine support types for hooks
                var firstSupport = group.Supports?.FirstOrDefault();
                var lastSupport = group.Supports?.LastOrDefault();
                string startSupportType = SupportTypeToString(firstSupport?.Type ?? SupportType.FreeEnd);
                string endSupportType = SupportTypeToString(lastSupport?.Type ?? SupportType.FreeEnd);

                // Ensure bar diameter has a sensible fallback
                if (barDiameter <= 0)
                    barDiameter = settings.General?.AvailableDiameters?.DefaultIfEmpty().Max() ?? 0;

                // Calculate TOP bar segments
                var topResult = algorithm.ProcessComplete(
                    totalLengthMm,
                    spanInfos,
                    isTopBar: true,
                    groupType: groupType,
                    startSupportType: startSupportType,
                    endSupportType: endSupportType,
                    barDiameter: barDiameter,
                    barsPerLayer: barsPerLayerTop,
                    concreteGrade: concreteGrade,
                    steelGrade: steelGrade);

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
                var botResult = algorithm.ProcessComplete(
                    totalLengthMm,
                    spanInfos,
                    isTopBar: false,
                    groupType: groupType,
                    startSupportType: startSupportType,
                    endSupportType: endSupportType,
                    barDiameter: barDiameter,
                    barsPerLayer: barsPerLayerBot,
                    concreteGrade: concreteGrade,
                    steelGrade: steelGrade);

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
        /// CRITICAL FIX: Refresh As_Top/As_Bot/StirrupReq/WebReq from XData before viewer display.
        /// This ensures data is current even if groups were created before SAP import.
        /// </summary>
        private void RefreshGroupsFromXData(List<BeamGroup> groups)
        {
            if (groups == null || groups.Count == 0) return;

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var db = doc.Database;
            var settings = DtsSettings.Instance;

            UsingTransaction(tr =>
            {
                foreach (var group in groups)
                {
                    if (group.Spans == null) continue;

                    foreach (var span in group.Spans)
                    {
                        // Get entity handle(s) for this span
                        var handle = span.Segments?.FirstOrDefault()?.EntityHandle;
                        if (string.IsNullOrWhiteSpace(handle)) continue;

                        var objId = AcadUtils.GetObjectIdFromHandle(handle);
                        if (objId == ObjectId.Null || objId.IsErased) continue;

                        try
                        {
                            var obj = tr.GetObject(objId, OpenMode.ForRead);
                            var designData = XDataUtils.ReadRebarData(obj);
                            if (designData == null) continue;

                            // Torsion distribution settings
                            double torsTop = settings?.Beam?.TorsionDist_TopBar ?? 0.25;
                            double torsBot = settings?.Beam?.TorsionDist_BotBar ?? 0.25;
                            double torsSide = settings?.Beam?.TorsionDist_SideBar ?? 0.50;

                            // Ensure arrays exist
                            if (span.As_Top == null || span.As_Top.Length < 5) span.As_Top = new double[6];
                            if (span.As_Bot == null || span.As_Bot.Length < 5) span.As_Bot = new double[6];
                            if (span.StirrupReq == null || span.StirrupReq.Length < 3) span.StirrupReq = new double[3];
                            if (span.WebReq == null || span.WebReq.Length < 3) span.WebReq = new double[3];

                            // Map 3 zones (Start/Mid/End) -> 6 positions (0,1)=Start, (2,3)=Mid, (4,5)=End
                            for (int zi = 0; zi < 3; zi++)
                            {
                                double asTopReq = (designData.TopArea?[zi] ?? 0) + (designData.TorsionArea?[zi] ?? 0) * torsTop;
                                double asBotReq = (designData.BotArea?[zi] ?? 0) + (designData.TorsionArea?[zi] ?? 0) * torsBot;
                                int p0 = zi == 0 ? 0 : (zi == 1 ? 2 : 4);
                                int p1 = p0 + 1;

                                // Apply unified rounding
                                asTopReq = Core.Algorithms.RebarCalculator.RoundRebarValue(asTopReq);
                                asBotReq = Core.Algorithms.RebarCalculator.RoundRebarValue(asBotReq);

                                span.As_Top[p0] = asTopReq;
                                span.As_Top[p1] = asTopReq;
                                span.As_Bot[p0] = asBotReq;
                                span.As_Bot[p1] = asBotReq;

                                span.StirrupReq[zi] = Core.Algorithms.RebarCalculator.RoundRebarValue(designData.ShearArea?[zi] ?? 0);
                                span.WebReq[zi] = Core.Algorithms.RebarCalculator.RoundRebarValue((designData.TorsionArea?[zi] ?? 0) * torsSide);
                            }
                        }
                        catch { /* Ignore individual beam failures */ }
                    }
                }
            });
        }

        /// <summary>
        /// XDATA-FIRST: Sync rebar solution strings từ SpanData back to XData của beam entities.
        /// Gọi hàm này khi SAVE/APPLY trong Viewer để đảm bảo XData là Source of Truth.
        /// </summary>
        public static void SyncGroupSpansToXData(List<BeamGroup> groups)
        {
            if (groups == null || groups.Count == 0) return;

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                try
                {
                    foreach (var group in groups)
                    {
                        if (group?.Spans == null) continue;

                        foreach (var span in group.Spans)
                        {
                            if (span?.Segments == null) continue;

                            // Get rebar strings from SpanData (3-zone: L, M, R -> indices 0, 2, 4)
                            string[] topRebarZone = ExtractZoneStrings(span.TopRebarInternal, 0);
                            string[] botRebarZone = ExtractZoneStrings(span.BotRebarInternal, 0);
                            string[] stirrupZone = span.Stirrup ?? new string[3];
                            string[] webBarZone = span.WebBar ?? new string[3];

                            foreach (var seg in span.Segments)
                            {
                                if (string.IsNullOrWhiteSpace(seg?.EntityHandle)) continue;

                                var objId = AcadUtils.GetObjectIdFromHandle(seg.EntityHandle);
                                if (objId == ObjectId.Null || objId.IsErased) continue;

                                var obj = tr.GetObject(objId, OpenMode.ForWrite);
                                if (obj == null) continue;

                                XDataUtils.UpdateBeamSolutionXData(
                                    obj, tr,
                                    topRebarZone,
                                    botRebarZone,
                                    stirrupZone,
                                    webBarZone,
                                    belongToGroup: group.GroupName,
                                    beamType: group.GroupType);
                            }
                        }
                    }
                    tr.Commit();
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SyncGroupSpansToXData] Error: {ex.Message}");
                    tr.Abort();
                }
            }
        }

        /// <summary>
        /// Extract 3-zone strings from 2D TopRebar/BotRebar array [layer, position].
        /// Returns [L, M, R] = positions [0, 2, 4] from layer 0.
        /// </summary>
        private static string[] ExtractZoneStrings(string[,] rebarArray, int layer = 0)
        {
            if (rebarArray == null || rebarArray.GetLength(0) <= layer || rebarArray.GetLength(1) < 5)
                return new string[3];

            return new[]
            {
                rebarArray[layer, 0] ?? "", // Left (position 0)
                rebarArray[layer, 2] ?? "", // Mid (position 2)
                rebarArray[layer, 4] ?? ""  // Right (position 4)
            };
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
                        var data = XDataUtils.ReadRebarData(obj);
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
                group.BackboneOptions = GenerateBackboneOptions(group, groupRebarData, settings);

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
                        if (!hasUserData || span.TopRebarInternal == null || string.IsNullOrEmpty(span.TopRebarInternal[0, 0]))
                        {
                            // 6 positions: (0,1)=Start, (2,3)=Mid, (4,5)=End
                            span.TopRebarInternal[0, 0] = topBackbone;
                            span.TopRebarInternal[0, 1] = topBackbone;
                            span.TopRebarInternal[0, 2] = topBackbone;
                            span.TopRebarInternal[0, 3] = topBackbone;
                            span.TopRebarInternal[0, 4] = topBackbone;
                            span.TopRebarInternal[0, 5] = topBackbone;

                            span.BotRebarInternal[0, 0] = botBackbone;
                            span.BotRebarInternal[0, 1] = botBackbone;
                            span.BotRebarInternal[0, 2] = botBackbone;
                            span.BotRebarInternal[0, 3] = botBackbone;
                            span.BotRebarInternal[0, 4] = botBackbone;
                            span.BotRebarInternal[0, 5] = botBackbone;
                        }

                        // ALWAYS sync REQUIRED values from XData (independent of user/provided layouts)
                        if (data != null)
                        {
                            // Fill stirrup/web provided from XData if empty (can vary per span)
                            if (span.Stirrup == null || span.Stirrup.Length < 3) span.Stirrup = new string[3];
                            if (span.WebBar == null || span.WebBar.Length < 3) span.WebBar = new string[3];

                            if (string.IsNullOrEmpty(span.Stirrup[0])) span.Stirrup[0] = data.StirrupString?.ElementAtOrDefault(0) ?? "";
                            if (string.IsNullOrEmpty(span.Stirrup[1])) span.Stirrup[1] = data.StirrupString?.ElementAtOrDefault(1) ?? "";
                            if (string.IsNullOrEmpty(span.Stirrup[2])) span.Stirrup[2] = data.StirrupString?.ElementAtOrDefault(2) ?? "";

                            if (string.IsNullOrEmpty(span.WebBar[0])) span.WebBar[0] = data.WebBarString?.ElementAtOrDefault(0) ?? "";
                            if (string.IsNullOrEmpty(span.WebBar[1])) span.WebBar[1] = data.WebBarString?.ElementAtOrDefault(1) ?? "";
                            if (string.IsNullOrEmpty(span.WebBar[2])) span.WebBar[2] = data.WebBarString?.ElementAtOrDefault(2) ?? "";

                            if (span.As_Top == null || span.As_Top.Length < 6) span.As_Top = new double[6];
                            if (span.As_Bot == null || span.As_Bot.Length < 6) span.As_Bot = new double[6];
                            if (span.StirrupReq == null || span.StirrupReq.Length < 3) span.StirrupReq = new double[3];
                            if (span.WebReq == null || span.WebReq.Length < 3) span.WebReq = new double[3];

                            double torsTop = settings?.Beam?.TorsionDist_TopBar ?? 0.25;
                            double torsBot = settings?.Beam?.TorsionDist_BotBar ?? 0.25;
                            double torsSide = settings?.Beam?.TorsionDist_SideBar ?? 0.50;

                            // 3 zones -> fill 6 positions
                            for (int zi = 0; zi < 3; zi++)
                            {
                                double asTopReq = (data.TopArea?.ElementAtOrDefault(zi) ?? 0) + (data.TorsionArea?.ElementAtOrDefault(zi) ?? 0) * torsTop;
                                double asBotReq = (data.BotArea?.ElementAtOrDefault(zi) ?? 0) + (data.TorsionArea?.ElementAtOrDefault(zi) ?? 0) * torsBot;
                                int p0 = zi == 0 ? 0 : (zi == 1 ? 2 : 4);
                                int p1 = p0 + 1;
                                span.As_Top[p0] = asTopReq;
                                span.As_Top[p1] = asTopReq;
                                span.As_Bot[p0] = asBotReq;
                                span.As_Bot[p1] = asBotReq;

                                // Shear/Web required
                                span.StirrupReq[zi] = (data.ShearArea?.ElementAtOrDefault(zi) ?? 0);
                                span.WebReq[zi] = (data.TorsionArea?.ElementAtOrDefault(zi) ?? 0) * torsSide;
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
        private List<ContinuousBeamSolution> GenerateBackboneOptions(BeamGroup group, List<BeamResultData> rebarData, DtsSettings settings)
        {
            var options = new List<ContinuousBeamSolution>();
            var inventory = settings.General?.AvailableDiameters ?? new List<int>();
            var availableDiameters = DiameterParser.ParseRange(settings.Beam?.MainBarRange ?? "", inventory);
            if (settings.Beam?.PreferEvenDiameter == true)
                availableDiameters = DiameterParser.FilterEvenDiameters(availableDiameters);
            if (availableDiameters.Count == 0)
                availableDiameters = inventory;

            // Tính tổng As yêu cầu max
            double maxAsTop = 0, maxAsBot = 0;
            double torsionTopFactor = settings.Beam?.TorsionDist_TopBar ?? 0;
            double torsionBotFactor = settings.Beam?.TorsionDist_BotBar ?? 0;
            foreach (var data in rebarData)
            {
                for (int i = 0; i < 3; i++)
                {
                    maxAsTop = Math.Max(maxAsTop, data.TopArea[i] + data.TorsionArea[i] * torsionTopFactor);
                    maxAsBot = Math.Max(maxAsBot, data.BotArea[i] + data.TorsionArea[i] * torsionBotFactor);
                }
            }

            // Backbone diameters to try
            var backboneDias = availableDiameters.OrderByDescending(d => d).ToList();
            if (backboneDias.Count == 0) return options;

            // Total length (m) from group data (no hardcode)
            double totalLengthM = 0;
            if (group != null)
            {
                if (group.TotalLength > 0)
                    totalLengthM = group.TotalLength;
                else if (group.Spans != null && group.Spans.Count > 0)
                    totalLengthM = group.Spans.Sum(s => s.Length) / 1000.0;
            }

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
                    BackboneDiameter_Top = dia,
                    BackboneDiameter_Bot = dia,
                    BackboneCount_Top = nTop,
                    BackboneCount_Bot = nBot,
                    As_Backbone_Top = nTop * asPerBar / 100.0, // cm²
                    As_Backbone_Bot = nBot * asPerBar / 100.0,
                    Description = opt == 0 ? "Phương án tối ưu" : (opt == 1 ? "Cân bằng" : "Tiết kiệm"),
                    TotalSteelWeight = totalLengthM > 0
                        ? (nTop + nBot) * (0.00617 * dia * dia) * totalLengthM
                        : 0
                };

                // Waste/Efficiency score (0-100) based on required/provided As proxy
                double reqAvg = (maxAsTop + maxAsBot) / 2.0;
                double provAvg = (solution.As_Backbone_Top + solution.As_Backbone_Bot) / 2.0;
                solution.WastePercentage = reqAvg > 0 ? Math.Max(0, (provAvg - reqAvg) / reqAvg * 100.0) : 0;
                solution.EfficiencyScore = Math.Max(0, 100 - solution.WastePercentage);

                // Constructability + TotalScore (0-100)
                solution.ConstructabilityScore = ConstructabilityScoring.CalculateScore(solution, group, settings);
                solution.TotalScore = 0.6 * solution.EfficiencyScore + 0.4 * solution.ConstructabilityScore;

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
                        var data = XDataUtils.ReadRebarData(obj);
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
                            ResultData = data,
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
        /// Áp dụng phương án bố trí thép (ContinuousBeamSolution) vào các CAD entities.
        /// Cập nhật XData: TopRebarString, BotRebarString, TopAreaProv, BotAreaProv.
        /// </summary>
        private void ApplyGroupSolutionToEntities(
            Transaction tr,
            BeamGroup group,
            List<ObjectId> objIds,
            List<BeamResultData> datas,
            ContinuousBeamSolution sol,
            DtsSettings settings)
        {
            if (sol == null || !sol.IsValid || datas == null || objIds == null) return;

            // Chuỗi Backbone cơ sở (Lớp 1)
            string backboneTop = $"{sol.BackboneCount_Top}D{sol.BackboneDiameter}";
            string backboneBot = $"{sol.BackboneCount_Bot}D{sol.BackboneDiameter}";

            for (int i = 0; i < Math.Min(datas.Count, objIds.Count); i++)
            {
                var data = datas[i];
                if (data == null) continue;

                var obj = tr.GetObject(objIds[i], OpenMode.ForWrite);
                if (obj == null) continue;

                string spanId = group?.Spans != null && i < group.Spans.Count ? group.Spans[i].SpanId : $"S{i + 1}";

                // Make sure data arrays are initialized
                if (data.TopRebarString == null || data.TopRebarString.Length < 3) data.TopRebarString = new string[3];
                if (data.BotRebarString == null || data.BotRebarString.Length < 3) data.BotRebarString = new string[3];
                if (data.StirrupString == null || data.StirrupString.Length < 3) data.StirrupString = new string[3];
                if (data.WebBarString == null || data.WebBarString.Length < 3) data.WebBarString = new string[3]; // Placeholder for SideBars

                if (data.TopAreaProv == null || data.TopAreaProv.Length < 3) data.TopAreaProv = new double[3];
                if (data.BotAreaProv == null || data.BotAreaProv.Length < 3) data.BotAreaProv = new double[3];

                // Xử lý 3 vị trí: 0=Left/Start, 1=Mid, 2=Right/End
                for (int pos = 0; pos < 3; pos++)
                {
                    string posName = pos == 0 ? "Left" : (pos == 1 ? "Mid" : "Right");

                    // --- XỬ LÝ TOP ---
                    // Top sử dụng keys: _Top_Left (pos=0), _Top_Mid (pos=1), _Top_Right (pos=2)
                    string keyTop = $"{spanId}_Top_{posName}";
                    string topStr = backboneTop;

                    if (sol.Reinforcements != null && sol.Reinforcements.TryGetValue(keyTop, out var specTop))
                    {
                        topStr += $"+{specTop.Count}D{specTop.Diameter}";
                    }

                    data.TopRebarString[pos] = topStr;
                    data.TopAreaProv[pos] = RebarCalculator.ParseRebarArea(topStr);

                    // --- XỬ LÝ BOT ---
                    // FIX: Bot Mid-span rebar kéo suốt nhịp -> dùng _Bot_Mid cho tất cả vị trí
                    // SolveScenario chỉ tạo _Bot_Mid (không tạo _Bot_Left/_Bot_Right riêng)
                    string keyBot = $"{spanId}_Bot_Mid"; // ALWAYS use Mid key for Bot
                    string botStr = backboneBot;

                    if (sol.Reinforcements != null && sol.Reinforcements.TryGetValue(keyBot, out var specBot))
                    {
                        botStr += $"+{specBot.Count}D{specBot.Diameter}";
                    }

                    data.BotRebarString[pos] = botStr;
                    data.BotAreaProv[pos] = RebarCalculator.ParseRebarArea(botStr);

                    // --- XỬ LÝ STIRRUP [NEW] ---
                    // Keys: _Stirrup_Left, _Stirrup_Mid, _Stirrup_Right
                    // Fallback to _Governing or default logic if needed
                    string keyStir = $"{spanId}_Stirrup_{posName}";
                    string stirStr = "";

                    if (sol.StirrupDesigns != null)
                    {
                        if (sol.StirrupDesigns.TryGetValue(keyStir, out var s))
                            stirStr = s;
                        else if (sol.StirrupDesigns.TryGetValue($"{spanId}_Stirrup_Governing", out var gov))
                            stirStr = gov;
                    }

                    data.StirrupString[pos] = stirStr;
                    // WebBarString logic pending (Torsion stage unimplemented) - leaving existing or empty
                    // data.WebBarString[pos] = ""; 
                }

                // Update XData (FULL SYNC)
                XDataUtils.UpdateBeamSolutionXData(
                    obj,
                    tr,
                    data.TopRebarString,
                    data.BotRebarString,
                    data.StirrupString, // Pass updated StirrupString
                    data.WebBarString,  // Pass WebBarString (even if empty)
                    group?.GroupName,
                    group?.GroupType);

                // RESET COLOR to ByLayer (256) to indicate processed
                if (obj is Entity ent) ent.ColorIndex = 256;
            }
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
                                        var zones = RebarXDataBridge.BuildSolutionZonesFromSpan(span);

                                        // XData-first: update only solution keys; preserve existing xType/other data
                                        XDataUtils.UpdateBeamSolutionXData(
                                            ent, tr,
                                            zones.TopZones, zones.BotZones,
                                            zones.StirrupZones, zones.WebZones,
                                            group.GroupName, group.GroupType);

                                        // RESET COLOR to ByLayer (256)
                                        ent.ColorIndex = 256;

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
            WriteMessage("=== AUTO GROUP: GOM NHÓM DẦM THEO VÙNG CHỌN ===");

            // [FIX] Yêu cầu user chọn vùng thay vì tự động quét toàn bộ
            WriteMessage("\nChọn các dầm cần gom nhóm:");
            var userSelectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (userSelectedIds.Count == 0)
            {
                WriteMessage("Không có dầm nào được chọn. Hủy.");
                return;
            }

            var settings = DtsSettings.Instance;
            // [FIX] RebarSettings not needed - removed unused variable

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

            // 2. Thu thập dầm từ vùng chọn CHƯA thuộc nhóm nào
            var freeBeamIds = new List<ObjectId>();
            var beamsDataMap = new Dictionary<ObjectId, (Point3d Mid, bool IsGirder, bool IsXDir, string AxisKey, string Handle, double LevelZ)>();

            UsingTransaction(tr =>
            {
                // [FIX] Chỉ xử lý userSelectedIds thay vì toàn bộ bản vẽ
                foreach (ObjectId id in userSelectedIds)
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
                        var xdata = XDataUtils.ReadRebarData(curve);
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

                        // FIX: Handle both BeamData (from DTS_PLOT_FROM_SAP) and BeamResultData
                        var elementData = XDataUtils.ReadElementData(curve);

                        double width = 0, height = 0;
                        int supportI = 1, supportJ = 1; // Default = has support
                        string sapName = curve.Handle.ToString();

                        if (elementData is BeamData beamData)
                        {
                            // BeamData từ DTS_PLOT_FROM_SAP: Width/Depth in mm
                            width = beamData.Width ?? 0;
                            height = beamData.Depth ?? 0;
                            sapName = beamData.SapFrameName ?? sapName;
                            // SOURCE-BASED SUPPORT: Read from XData
                            supportI = beamData.SupportI;
                            supportJ = beamData.SupportJ;
                        }
                        else if (elementData is BeamResultData resultData)
                        {
                            // BeamResultData từ DTS_REBAR_IMPORT_SAP: Width/SectionHeight in cm -> convert to mm
                            width = resultData.Width > 0 ? resultData.Width * 10 : 0;
                            height = resultData.SectionHeight > 0 ? resultData.SectionHeight * 10 : 0;
                            sapName = resultData.SapElementName ?? sapName;
                            // Default support for BeamResultData (can be extended to store support info)
                            supportI = 1;
                            supportJ = 1;
                        }

                        var geo = new Core.Data.BeamGeometry
                        {
                            Handle = curve.Handle.ToString(),
                            Name = sapName,
                            StartX = curve.StartPoint.X,
                            StartY = curve.StartPoint.Y,
                            EndX = curve.EndPoint.X,
                            EndY = curve.EndPoint.Y,
                            StartZ = curve.StartPoint.Z,
                            EndZ = curve.EndPoint.Z,
                            Width = width,
                            Height = height,
                            SupportI = supportI,
                            SupportJ = supportJ
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

        /// <summary>
        /// CẬP NHẬT QUAN TRỌNG: Map kết quả tính toán vào cấu trúc SpanData của BeamGroup.
        /// Giúp Viewer hiển thị được ngay lập tức mà không cần tính lại.
        /// </summary>
        private void UpdateGroupSpansFromSolution(BeamGroup group, ContinuousBeamSolution sol)
        {
            if (group == null || sol == null || group.Spans == null) return;

            // 1. Tạo thông tin Backbone chung
            var bbTop = new RebarInfo
            {
                Count = sol.BackboneCount_Top,
                Diameter = sol.BackboneDiameter_Top > 0 ? sol.BackboneDiameter_Top : sol.BackboneDiameter
            };
            var bbBot = new RebarInfo
            {
                Count = sol.BackboneCount_Bot,
                Diameter = sol.BackboneDiameter_Bot > 0 ? sol.BackboneDiameter_Bot : sol.BackboneDiameter
            };

            // 2. Duyệt từng nhịp để gán thép gia cường (Addons)
            foreach (var span in group.Spans)
            {
                string spanId = span.SpanId;

                // Gán Backbone
                span.TopBackbone = bbTop;
                span.BotBackbone = bbBot;

                // Helper để lấy RebarInfo từ Dictionary kết quả
                RebarInfo GetSpec(string key)
                {
                    if (sol.Reinforcements != null && sol.Reinforcements.TryGetValue(key, out var spec))
                    {
                        return new RebarInfo
                        {
                            Count = spec.Count,
                            Diameter = spec.Diameter,
                            LayerCounts = spec.LayerBreakdown
                        };
                    }
                    return null;
                }

                // Gán thép gia cường (Top)
                span.TopAddLeft = GetSpec($"{spanId}_Top_Left");
                span.TopAddMid = GetSpec($"{spanId}_Top_Mid"); // Thường null
                span.TopAddRight = GetSpec($"{spanId}_Top_Right");

                // Gán thép gia cường (Bot)
                span.BotAddLeft = GetSpec($"{spanId}_Bot_Left");
                span.BotAddMid = GetSpec($"{spanId}_Bot_Mid");
                span.BotAddRight = GetSpec($"{spanId}_Bot_Right");

                // Gán đai (Stirrup) - Lấy đại diện gối/nhịp
                if (sol.StirrupDesigns != null)
                {
                    // Map string đai vào mảng Stirrup[] của SpanData
                    // Index 0=Left, 1=Mid, 2=Right
                    if (span.Stirrup == null || span.Stirrup.Length < 3) span.Stirrup = new string[3];

                    if (sol.StirrupDesigns.TryGetValue($"{spanId}_Stirrup_Left", out var sL)) span.Stirrup[0] = sL;
                    if (sol.StirrupDesigns.TryGetValue($"{spanId}_Stirrup_Mid", out var sM)) span.Stirrup[1] = sM;
                    if (sol.StirrupDesigns.TryGetValue($"{spanId}_Stirrup_Right", out var sR)) span.Stirrup[2] = sR;
                }
            }
        }
    }
}

