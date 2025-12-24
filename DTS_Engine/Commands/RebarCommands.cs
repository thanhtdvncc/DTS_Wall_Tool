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
                                if (existingData != null)
                                {
                                    // Calculate AreaProv from RebarString if not in XData (new format)
                                    var topAreaProv = existingData.TopAreaProv;
                                    var botAreaProv = existingData.BotAreaProv;

                                    // Fallback: calculate from RebarString if TopAreaProv is null/empty
                                    if ((topAreaProv == null || topAreaProv.All(x => x == 0)) && existingData.TopRebarString != null)
                                    {
                                        topAreaProv = existingData.TopRebarString.Select(DTS_Engine.Core.Algorithms.RebarCalculator.ParseRebarArea).ToArray();
                                    }
                                    if ((botAreaProv == null || botAreaProv.All(x => x == 0)) && existingData.BotRebarString != null)
                                    {
                                        botAreaProv = existingData.BotRebarString.Select(DTS_Engine.Core.Algorithms.RebarCalculator.ParseRebarArea).ToArray();
                                    }

                                    if (topAreaProv != null && topAreaProv.Length >= 3 &&
                                        botAreaProv != null && botAreaProv.Length >= 3)
                                    {
                                        // Check if existing Aprov is insufficient for new Areq
                                        bool isInsufficient = false;
                                        for (int i = 0; i < 3; i++)
                                        {
                                            double areqTop = designData.TopArea[i] + designData.TorsionArea[i] * (dtsSettings.Beam?.TorsionDist_TopBar ?? 0.25);
                                            double areqBot = designData.BotArea[i] + designData.TorsionArea[i] * (dtsSettings.Beam?.TorsionDist_BotBar ?? 0.25);

                                            if (topAreaProv[i] < areqTop * 0.99 ||
                                                botAreaProv[i] < areqBot * 0.99)
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

        /// <summary>
        /// V5 REFACTOR: Use TopologyBuilder instead of NOD-based BeamGroup.
        /// Star Topology via DTS_LINK system.
        /// </summary>
        [CommandMethod("DTS_REBAR_CALCULATE")]
        public void DTS_REBAR_CALCULATE()
        {
            WriteMessage("=== REBAR: TÍNH TOÁN CỐT THÉP (V5 TOPOLOGY) ===");
            WriteMessage("\nChọn các đường Dầm cần tính thép: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // V5: Force reload settings
            DtsSettings.Reload();
            var dtsSettings = DtsSettings.Instance;

            if (dtsSettings.EnablePipelineLogging)
            {
                WriteMessage("🔍 DEBUG: Pipeline Logging ENABLED");
            }

            // === V5: USE TOPOLOGYBUILDER INSTEAD OF NOD ===
            var topologyBuilder = new TopologyBuilder();
            var allRuntimeGroups = new List<BeamGroup>();
            int singleCount = 0;
            int groupCount = 0;
            int lockedCount = 0;

            UsingTransaction(tr =>
            {
                // Step 1: Build topology graph (L->R sorted, Star Topology)
                // HOTFIX: autoEstablishLinks = false to prevent geometry algorithm from 
                // reconnecting beams that already have XData links (prevents Giant Group bug)
                var allTopologies = topologyBuilder.BuildGraph(selectedIds, tr, autoEstablishLinks: false);

                if (allTopologies.Count == 0)
                {
                    WriteMessage("Không tìm thấy dầm hợp lệ trong selection.");
                    return;
                }

                WriteMessage($"Đã tìm thấy {allTopologies.Count} dầm, đang phân nhóm...");

                // Step 2: Split into separate groups based on links
                var topologyGroups = topologyBuilder.SplitIntoGroups(allTopologies);
                WriteMessage($"Đã phân thành {topologyGroups.Count} nhóm dầm.");

                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (var topoGroup in topologyGroups)
                {
                    if (topoGroup.Count == 0) continue;

                    // Build runtime BeamGroup
                    var group = topologyBuilder.BuildBeamGroup(topoGroup, dtsSettings);
                    if (group == null) continue;

                    // Extract BeamResultData for calculation
                    var spanResults = new List<BeamResultData>();
                    var objIds = new List<ObjectId>();

                    foreach (var topo in topoGroup)
                    {
                        objIds.Add(topo.ObjectId);

                        // Get RebarData 
                        var data = topo.RebarData;
                        if (data != null)
                        {
                            // FIX 1.4: REMOVED FlipBeamResultData - TopologyBuilder already sorts L→R
                            // Double-flip was causing data scrambling
                            // Old code:
                            // if (topo.IsGeometryReversed)
                            // {
                            //     data = FlipBeamResultData(data);
                            // }
                            spanResults.Add(data);
                        }
                        else
                        {
                            spanResults.Add(null);
                        }
                    }

                    // Validate dimensions
                    var validSpanResults = spanResults.Where(d => d != null && d.Width > 0 && d.SectionHeight > 0).ToList();
                    if (validSpanResults.Count == 0)
                    {
                        WriteMessage($"  ⚠️ Nhóm {group.GroupName}: Thiếu tiết diện. Bỏ qua.");
                        continue;
                    }

                    // Generate proposals using V4 Engine
                    var proposals = RebarCalculator.CalculateProposalsForGroup(group, spanResults, dtsSettings);

                    if (proposals == null || proposals.Count == 0)
                    {
                        WriteMessage($"  ❌ {group.GroupName}: Không thể tạo phương án.");
                        continue;
                    }

                    // Update BackboneOptions
                    group.BackboneOptions = proposals;
                    group.SelectedBackboneIndex = 0;

                    // V5.0: Ensure all entities have GroupIdentity (handles orphan beams)
                    EnsureGroupIdentity(tr, topoGroup);

                    // V5.0: Write all 5 options to ALL entities (per spec Section 4.2)
                    WriteOptionsToAllEntities(tr, topoGroup, proposals);

                    // Check if already locked (from XData)
                    bool isLocked = CheckIfGroupLocked(topoGroup, tr);

                    if (isLocked)
                    {
                        lockedCount++;
                        WriteMessage($"  🔒 {group.GroupName}: Đã chốt. Proposals mới đã lưu nhưng giữ nguyên SelectedDesign.");
                    }
                    else
                    {
                        // Apply best solution
                        var bestSolution = proposals.FirstOrDefault(p => p.IsValid);

                        // FIX: Skip if no valid solution or if first solution is ERROR
                        if (bestSolution == null)
                        {
                            var firstSolution = proposals.FirstOrDefault();
                            if (firstSolution != null && firstSolution.OptionName == "ERROR")
                            {
                                // This is an ERROR solution - likely no SAP data
                                WriteMessage($"  ❌ {group.GroupName}: {firstSolution.ValidationMessage ?? "Thiếu dữ liệu SAP"}. Bỏ qua.");
                                continue; // Skip this group entirely
                            }
                            // No valid solutions but not an ERROR - use fallback
                            bestSolution = firstSolution;
                            if (bestSolution != null)
                            {
                                WriteMessage($"  ⚠️ {group.GroupName}: Không có phương án Valid, dùng fallback: {bestSolution.OptionName}");
                            }
                        }

                        if (bestSolution != null)
                        {
                            // Apply solution to XData of each entity
                            ApplyGroupSolutionToEntitiesV5(tr, topoGroup, group, bestSolution, dtsSettings);

                            // Update SpanData for Viewer
                            UpdateGroupSpansFromSolution(group, bestSolution);

                            // Store SelectedDesign for persistence
                            group.SelectedDesign = bestSolution;

                            if (topoGroup.Count == 1)
                                singleCount++;
                            else
                                groupCount++;

                            WriteMessage($"  ✅ {group.GroupName}: {bestSolution.OptionName} ({bestSolution.TotalSteelWeight:F1}kg)");
                        }
                    }

                    allRuntimeGroups.Add(group);
                }
            });

            // Summary
            WriteSuccess($"Hoàn thành: {singleCount} dầm đơn + {groupCount} nhóm. {lockedCount} nhóm đã chốt (giữ nguyên).");

            // V5: Persist SelectedDesign and BackboneOptions to XData
            // This ensures Viewer can reload the last calculated proposals
            if (allRuntimeGroups.Count > 0)
            {
                SyncGroupSpansToXData(allRuntimeGroups);
                WriteMessage($"  → Đã lưu {allRuntimeGroups.Count} nhóm vào XData.");
            }
        }

        /// <summary>
        /// V5: Flip BeamResultData for R->L geometry beams.
        /// </summary>
        private BeamResultData FlipBeamResultData(BeamResultData original)
        {
            if (original == null) return null;

            var flipped = new BeamResultData
            {
                Width = original.Width,
                SectionHeight = original.SectionHeight,
                SapElementName = original.SapElementName,
                MappingSource = original.MappingSource,
                DesignCombo = original.DesignCombo,
                SectionName = original.SectionName,
                TorsionFactorUsed = original.TorsionFactorUsed,
                BelongToGroup = original.BelongToGroup,
                BeamType = original.BeamType,
                BaseZ = original.BaseZ,
                SupportI = original.SupportJ, // Swap supports
                SupportJ = original.SupportI
            };

            // Flip arrays (reverse order)
            flipped.TopArea = FlipArray(original.TopArea);
            flipped.BotArea = FlipArray(original.BotArea);
            flipped.TorsionArea = FlipArray(original.TorsionArea);
            flipped.ShearArea = FlipArray(original.ShearArea);
            flipped.TTArea = FlipArray(original.TTArea);
            flipped.TopRebarString = FlipArray(original.TopRebarString);
            flipped.BotRebarString = FlipArray(original.BotRebarString);
            flipped.TopAreaProv = FlipArray(original.TopAreaProv);
            flipped.BotAreaProv = FlipArray(original.BotAreaProv);
            flipped.StirrupString = FlipArray(original.StirrupString);
            flipped.WebBarString = FlipArray(original.WebBarString);

            return flipped;
        }

        private T[] FlipArray<T>(T[] arr)
        {
            if (arr == null || arr.Length == 0) return arr;
            var flipped = new T[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                flipped[i] = arr[arr.Length - 1 - i];
            }
            return flipped;
        }

        /// <summary>
        /// V5: Check if group is locked by checking XData of any beam.
        /// </summary>
        private bool CheckIfGroupLocked(List<BeamTopology> topoGroup, Transaction tr)
        {
            foreach (var topo in topoGroup)
            {
                var obj = tr.GetObject(topo.ObjectId, OpenMode.ForRead);
                var rawData = XDataUtils.GetRawData(obj);
                if (rawData != null && rawData.TryGetValue("DesignLocked", out var locked))
                {
                    if (locked?.ToString() == "True" || locked?.ToString() == "1")
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// V5: Apply solution to XData of entities, handling geometry reversal.
        /// </summary>
        private void ApplyGroupSolutionToEntitiesV5(
            Transaction tr,
            List<BeamTopology> topoGroup,
            BeamGroup group,
            ContinuousBeamSolution sol,
            DtsSettings settings)
        {
            if (sol == null || !sol.IsValid) return;

            string backboneTop = $"{sol.BackboneCount_Top}D{sol.BackboneDiameter}";
            string backboneBot = $"{sol.BackboneCount_Bot}D{sol.BackboneDiameter}";

            for (int i = 0; i < topoGroup.Count; i++)
            {
                var topo = topoGroup[i];
                var obj = tr.GetObject(topo.ObjectId, OpenMode.ForWrite);
                if (obj == null) continue;

                string spanId = group?.Spans != null && i < group.Spans.Count
                    ? group.Spans[i].SpanId
                    : $"S{i + 1}";

                // Build rebar strings for 3 positions (Start/Mid/End)
                var topStrings = new string[3] { backboneTop, backboneTop, backboneTop };
                var botStrings = new string[3] { backboneBot, backboneBot, backboneBot };

                // Add addons if present
                if (sol.Reinforcements != null)
                {
                    // Left/Start (index 0)
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Left", out var tl))
                        topStrings[0] += $"+{tl.Count}D{tl.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Left", out var bl))
                        botStrings[0] += $"+{bl.Count}D{bl.Diameter}";

                    // Mid (index 1)
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Mid", out var tm))
                        topStrings[1] += $"+{tm.Count}D{tm.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Mid", out var bm))
                        botStrings[1] += $"+{bm.Count}D{bm.Diameter}";

                    // Right/End (index 2)
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Right", out var tr2))
                        topStrings[2] += $"+{tr2.Count}D{tr2.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Right", out var br))
                        botStrings[2] += $"+{br.Count}D{br.Diameter}";
                }

                // FIX 1.4: REMOVED flip logic - XData stores L→R canonical order
                // Old code:
                // if (topo.IsGeometryReversed)
                // {
                //     topStrings = FlipArray(topStrings);
                //     botStrings = FlipArray(botStrings);
                // }

                // Get stirrup strings
                var stirrupStrings = new string[3];
                if (sol.StirrupDesigns != null)
                {
                    sol.StirrupDesigns.TryGetValue($"{spanId}_Stirrup_Left", out stirrupStrings[0]);
                    sol.StirrupDesigns.TryGetValue($"{spanId}_Stirrup_Mid", out stirrupStrings[1]);
                    sol.StirrupDesigns.TryGetValue($"{spanId}_Stirrup_Right", out stirrupStrings[2]);

                    // FIX 1.4: REMOVED flip logic
                    // if (topo.IsGeometryReversed)
                    //     stirrupStrings = FlipArray(stirrupStrings);
                }

                // Update XData (legacy format - backward compatible)
                XDataUtils.UpdateBeamSolutionXData(
                    obj,
                    tr,
                    topStrings,
                    botStrings,
                    stirrupStrings,
                    null, // WebBarString - placeholder
                    group?.GroupName,
                    group?.GroupType);

                // V5.0: Write current state in separated layer format per spec Section 4.2
                // This stores backbone (L0) and addon (L1) separately for easier manipulation
                var topL0 = new string[3] { backboneTop, backboneTop, backboneTop };
                var botL0 = new string[3] { backboneBot, backboneBot, backboneBot };
                var topL1 = new string[3];
                var botL1 = new string[3];

                if (sol.Reinforcements != null)
                {
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Left", out var tl1))
                        topL1[0] = $"{tl1.Count}D{tl1.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Mid", out var tm1))
                        topL1[1] = $"{tm1.Count}D{tm1.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Right", out var tr1))
                        topL1[2] = $"{tr1.Count}D{tr1.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Left", out var bl1))
                        botL1[0] = $"{bl1.Count}D{bl1.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Mid", out var bm1))
                        botL1[1] = $"{bm1.Count}D{bm1.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Right", out var br1))
                        botL1[2] = $"{br1.Count}D{br1.Diameter}";
                }

                // FIX 1.4: REMOVED flip logic - L0/L1 stored in L→R order
                // Old code:
                // if (topo.IsGeometryReversed)
                // {
                //     topL0 = FlipArray(topL0);
                //     botL0 = FlipArray(botL0);
                //     topL1 = FlipArray(topL1);
                //     botL1 = FlipArray(botL1);
                // }

                XDataUtils.WriteCurrentRebar(obj, topL0, topL1, botL0, botL1, tr);

                // V5.0: Write GroupState to ALL entities for self-sufficiency
                // This ensures state redundancy - each entity knows the current selection
                int bestIdx = group?.BackboneOptions?.FindIndex(o => o == sol) ?? 0;
                if (bestIdx < 0) bestIdx = 0;
                XDataUtils.WriteGroupState(obj, bestIdx, isLocked: false, tr);

                // Reset color to ByLayer
                if (obj is Entity ent) ent.ColorIndex = 256;
            }
        }

        /// <summary>
        /// V5: Update SpanData from ContinuousBeamSolution.
        /// </summary>
        private void UpdateGroupSpansFromSolution(BeamGroup group, ContinuousBeamSolution sol)
        {
            if (group?.Spans == null || sol == null) return;

            // Use V4RebarCalculator's built-in method
            Core.Algorithms.Rebar.V4.V4RebarCalculator.ApplySolutionToGroup(group, sol);
        }

        /// <summary>
        /// [V5.0] Write all 5 rebar options to ALL entities in group.
        /// Per spec Section 4.2 - ensures options are available when Viewer reopens.
        /// </summary>
        private void WriteOptionsToAllEntities(
            Transaction tr,
            List<BeamTopology> topoGroup,
            List<ContinuousBeamSolution> proposals)
        {
            if (topoGroup == null || proposals == null || proposals.Count == 0) return;

            // Convert proposals to RebarOptionData format for each span
            for (int spanIdx = 0; spanIdx < topoGroup.Count; spanIdx++)
            {
                var topo = topoGroup[spanIdx];
                var obj = tr.GetObject(topo.ObjectId, OpenMode.ForWrite);
                string spanId = $"S{spanIdx + 1}";

                var options = new List<XDataUtils.RebarOptionData>();

                // Build up to 5 options
                for (int optIdx = 0; optIdx < Math.Min(5, proposals.Count); optIdx++)
                {
                    var sol = proposals[optIdx];
                    if (sol == null) continue;

                    var optData = new XDataUtils.RebarOptionData
                    {
                        TopL0 = $"{sol.BackboneCount_Top}D{sol.BackboneDiameter}",
                        BotL0 = $"{sol.BackboneCount_Bot}D{sol.BackboneDiameter}"
                    };

                    // Add addons if present - check all positions (Left, Mid, Right)
                    // Per spec, option stores ONE addon representative (backbone is continuous)
                    if (sol.Reinforcements != null)
                    {
                        // Try Left, then Mid, then Right to find top addon
                        if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Left", out var tl))
                            optData.TopL1 = $"{tl.Count}D{tl.Diameter}";
                        else if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Mid", out var tm))
                            optData.TopL1 = $"{tm.Count}D{tm.Diameter}";
                        else if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Right", out var trRight))
                            optData.TopL1 = $"{trRight.Count}D{trRight.Diameter}";

                        // Try Left, then Mid, then Right to find bot addon
                        if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Left", out var bl))
                            optData.BotL1 = $"{bl.Count}D{bl.Diameter}";
                        else if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Mid", out var bm))
                            optData.BotL1 = $"{bm.Count}D{bm.Diameter}";
                        else if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Right", out var br))
                            optData.BotL1 = $"{br.Count}D{br.Diameter}";
                    }

                    options.Add(optData);
                }

                // Write options to entity
                XDataUtils.WriteRebarOptions(obj, options, tr);
            }
        }

        /// <summary>
        /// [V5.0] Ensure all entities in group have GroupIdentity.
        /// Creates new GroupId for orphan beams that were never linked.
        /// </summary>
        private void EnsureGroupIdentity(Transaction tr, List<BeamTopology> topoGroup)
        {
            if (topoGroup == null || topoGroup.Count == 0) return;

            // Check if first entity has GroupIdentity
            var firstObj = tr.GetObject(topoGroup[0].ObjectId, OpenMode.ForRead);
            var (existingGroupId, _) = XDataUtils.ReadGroupIdentity(firstObj);

            // If no existing GroupId, create new one for this group
            if (string.IsNullOrEmpty(existingGroupId))
            {
                string newGroupId = Guid.NewGuid().ToString();

                for (int i = 0; i < topoGroup.Count; i++)
                {
                    var obj = tr.GetObject(topoGroup[i].ObjectId, OpenMode.ForWrite);
                    XDataUtils.WriteGroupIdentity(obj, newGroupId, i, tr);
                    XDataUtils.WriteGroupState(obj, selectedIdx: 0, isLocked: false, tr);
                }
            }
        }

        /// <summary>
        /// V5 PUBLIC: Sync BeamGroup spans to XData on entities.
        /// Called by Viewer after Apply/Save.
        /// 
        /// [V5.0] Now uses WriteGroupState for state redundancy,
        /// SetIsManual for edit flag, and removes JSON serialization.
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
                        if (group.Spans == null || group.EntityHandles == null) continue;

                        // V5.0: Get SelectedIdx from group
                        int selectedIdx = group.SelectedBackboneIndex >= 0
                            ? group.SelectedBackboneIndex
                            : 0;
                        bool isLocked = group.IsLocked;

                        for (int i = 0; i < group.Spans.Count && i < group.EntityHandles.Count; i++)
                        {
                            var span = group.Spans[i];
                            var handle = group.EntityHandles[i];

                            var objId = AcadUtils.GetObjectIdFromHandle(handle);
                            if (objId == ObjectId.Null || objId.IsErased) continue;

                            var obj = tr.GetObject(objId, OpenMode.ForWrite);
                            if (obj == null) continue;

                            // Build rebar strings from structured data
                            var topStrings = new string[3];
                            var botStrings = new string[3];
                            var stirrupStrings = span.Stirrup ?? new string[3];
                            var webStrings = span.WebBar ?? new string[3];

                            // Zone 0 = Left
                            topStrings[0] = BuildRebarString(span.TopBackbone, span.TopAddLeft);
                            botStrings[0] = BuildRebarString(span.BotBackbone, span.BotAddLeft);

                            // Zone 1 = Mid
                            topStrings[1] = BuildRebarString(span.TopBackbone, span.TopAddMid);
                            botStrings[1] = BuildRebarString(span.BotBackbone, span.BotAddMid);

                            // Zone 2 = Right
                            topStrings[2] = BuildRebarString(span.TopBackbone, span.TopAddRight);
                            botStrings[2] = BuildRebarString(span.BotBackbone, span.BotAddRight);

                            // Check if geometry is reversed (need to flip back)
                            bool isReversed = CheckIfEntityReversed(obj);
                            if (isReversed)
                            {
                                topStrings = FlipArrayStatic(topStrings);
                                botStrings = FlipArrayStatic(botStrings);
                                stirrupStrings = FlipArrayStatic(stirrupStrings);
                                webStrings = FlipArrayStatic(webStrings);
                            }

                            // Write to XData (legacy format - backward compatible)
                            XDataUtils.UpdateBeamSolutionXData(
                                obj,
                                tr,
                                topStrings,
                                botStrings,
                                stirrupStrings,
                                webStrings,
                                group.GroupName,
                                group.GroupType);

                            // V5.0: Write GroupState to ALL entities (state redundancy)
                            XDataUtils.WriteGroupState(obj, selectedIdx, isLocked, tr);

                            // V5.0: Write current state in separated layer format for consistency
                            // Convert RebarInfo to string format "nDd" (e.g., "2D16")
                            string backboneTopStr = span.TopBackbone != null && span.TopBackbone.Count > 0
                                ? $"{span.TopBackbone.Count}D{span.TopBackbone.Diameter}" : "";
                            string backboneBotStr = span.BotBackbone != null && span.BotBackbone.Count > 0
                                ? $"{span.BotBackbone.Count}D{span.BotBackbone.Diameter}" : "";

                            var topL0 = new string[3] { backboneTopStr, backboneTopStr, backboneTopStr };
                            var botL0 = new string[3] { backboneBotStr, backboneBotStr, backboneBotStr };
                            var topL1 = new string[3] {
                                span.TopAddLeft != null && span.TopAddLeft.Count > 0 ? $"{span.TopAddLeft.Count}D{span.TopAddLeft.Diameter}" : "",
                                span.TopAddMid != null && span.TopAddMid.Count > 0 ? $"{span.TopAddMid.Count}D{span.TopAddMid.Diameter}" : "",
                                span.TopAddRight != null && span.TopAddRight.Count > 0 ? $"{span.TopAddRight.Count}D{span.TopAddRight.Diameter}" : ""
                            };
                            var botL1 = new string[3] {
                                span.BotAddLeft != null && span.BotAddLeft.Count > 0 ? $"{span.BotAddLeft.Count}D{span.BotAddLeft.Diameter}" : "",
                                span.BotAddMid != null && span.BotAddMid.Count > 0 ? $"{span.BotAddMid.Count}D{span.BotAddMid.Diameter}" : "",
                                span.BotAddRight != null && span.BotAddRight.Count > 0 ? $"{span.BotAddRight.Count}D{span.BotAddRight.Diameter}" : ""
                            };

                            if (isReversed)
                            {
                                topL0 = FlipArrayStatic(topL0);
                                botL0 = FlipArrayStatic(botL0);
                                topL1 = FlipArrayStatic(topL1);
                                botL1 = FlipArrayStatic(botL1);
                            }

                            XDataUtils.WriteCurrentRebar(obj, topL0, topL1, botL0, botL1, tr);

                            // V5.0: Use SetIsManual instead of merge raw data
                            if (span.IsManualModified)
                            {
                                XDataUtils.SetIsManual(obj, true, tr);
                            }
                        }
                    }

                    tr.Commit();
                }
                catch
                {
                    tr.Abort();
                }
            }
        }

        #region V5: DTS_REBAR_VIEWER (Beam Group Viewer)

        /// <summary>
        /// V5 REFACTOR: Open Beam Viewer using TopologyBuilder.
        /// No NOD dependency - builds groups at runtime.
        /// </summary>
        [CommandMethod("DTS_REBAR_VIEWER")]
        public void DTS_REBAR_VIEWER()
        {
            WriteMessage("=== BEAM GROUP VIEWER (V5 TOPOLOGY) ===");
            WriteMessage("\nChọn dầm cần xem (hoặc Enter để xem tất cả liên kết):");

            try
            {
                var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE", true);

                var topologyBuilder = new TopologyBuilder();
                var resultGroups = new List<BeamGroup>();
                var dtsSettings = DtsSettings.Instance;

                UsingTransaction(tr =>
                {
                    List<BeamTopology> allTopologies;

                    if (selectedIds.Count == 0)
                    {
                        // User pressed Enter - scan all linked beams in current space
                        WriteMessage("Đang quét tất cả dầm có liên kết...");
                        allTopologies = ScanAllLinkedBeams(tr, topologyBuilder);
                    }
                    else
                    {
                        // Build graph from selection
                        allTopologies = topologyBuilder.BuildGraph(selectedIds, tr, autoEstablishLinks: false);
                    }

                    if (allTopologies.Count == 0)
                    {
                        WriteMessage("Không tìm thấy dầm hợp lệ.");
                        return;
                    }

                    // Split into groups
                    var topologyGroups = topologyBuilder.SplitIntoGroups(allTopologies);
                    WriteMessage($"Tìm thấy {topologyGroups.Count} nhóm dầm.");

                    foreach (var topoGroup in topologyGroups)
                    {
                        var group = topologyBuilder.BuildBeamGroup(topoGroup, dtsSettings);
                        if (group != null)
                        {
                            // Refresh requirements from XData
                            RefreshGroupFromXDataV5(group, topoGroup, tr, dtsSettings);

                            // V5.0: Heal group from NOD (lazy healing per spec Section 5.1)
                            HealGroupFromNOD(group, topoGroup, tr);

                            // Load existing solution from XData if available
                            LoadExistingSolutionFromXData(group, topoGroup, tr);

                            resultGroups.Add(group);
                        }
                    }
                });

                if (resultGroups.Count == 0)
                {
                    WriteMessage("Không có nhóm dầm để hiển thị.");
                    return;
                }

                // Show viewer dialog as MODELESS
                var dialog = new UI.Forms.BeamGroupViewerDialog(resultGroups, ApplyBeamGroupResultsV5);
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(dialog);
            }
            catch (System.Exception ex)
            {
                WriteError($"Lỗi mở Beam Viewer: {ex.Message}");
            }
        }

        /// <summary>
        /// V5: Scan all beams with DTS_LINK in current space.
        /// </summary>
        private List<BeamTopology> ScanAllLinkedBeams(Transaction tr, TopologyBuilder builder)
        {
            var allIds = new List<ObjectId>();
            var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;

            foreach (ObjectId id in btr)
            {
                if (id.IsErased) continue;
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                // Check if has DTS XData
                if (!XDataUtils.HasAppXData(ent)) continue;

                // Check if is a beam (has rebar data or xType = BEAM)
                var rebarData = XDataUtils.ReadRebarData(ent);
                if (rebarData != null)
                {
                    allIds.Add(id);
                    continue;
                }

                var elemData = XDataUtils.ReadElementData(ent);
                var xType = elemData?.XType?.ToUpperInvariant();
                if (xType == "BEAM")
                {
                    allIds.Add(id);
                }
            }

            return builder.BuildGraph(allIds, tr, autoEstablishLinks: false);
        }

        /// <summary>
        /// V5.0: Heal group from NOD per spec Section 5.1 (Lazy Healing).
        /// 1. Lookup NOD by GroupId
        /// 2. If NOD missing: Register from current topology
        /// 3. If NOD has zombies: Purge dead handles
        /// 4. Update NOD with alive members
        /// </summary>
        private void HealGroupFromNOD(BeamGroup group, List<BeamTopology> topoGroup, Transaction tr)
        {
            if (group == null || topoGroup == null || topoGroup.Count == 0) return;

            // Get GroupId from first entity's XData
            var firstObj = tr.GetObject(topoGroup[0].ObjectId, OpenMode.ForRead);
            var (groupId, _) = XDataUtils.ReadGroupIdentity(firstObj);

            if (string.IsNullOrEmpty(groupId))
            {
                // No GroupId - this is a legacy or newly created group
                // Generate new GroupId and register
                groupId = Guid.NewGuid().ToString().Substring(0, 8).ToUpperInvariant();

                // Write GroupIdentity to all entities
                for (int i = 0; i < topoGroup.Count; i++)
                {
                    var obj = tr.GetObject(topoGroup[i].ObjectId, OpenMode.ForWrite);
                    XDataUtils.WriteGroupIdentity(obj, groupId, i, tr);
                    XDataUtils.WriteGroupState(obj, 0, false, tr);
                }

                // Register with NOD - convert Handle to string
                var handleStrings = topoGroup.Select(t => t.ObjectId.Handle.ToString()).ToList();
                RegistryEngine.ResurrectGroup(groupId, handleStrings, tr);
                return;
            }

            // Check NOD for this GroupId
            bool nodExists = RegistryEngine.GroupIdExists(groupId, tr);

            if (!nodExists)
            {
                // NOD missing -> Resurrect (register current topology)
                var handleStrings = topoGroup.Select(t => t.ObjectId.Handle.ToString()).ToList();
                RegistryEngine.ResurrectGroup(groupId, handleStrings, tr);
                return;
            }

            // NOD exists -> Validate members and purge zombies
            var nodHandleStrings = RegistryEngine.GetMembersByGroupId(groupId, tr);
            if (nodHandleStrings == null || nodHandleStrings.Count == 0)
            {
                // NOD empty -> Register current
                var handleStrings = topoGroup.Select(t => t.ObjectId.Handle.ToString()).ToList();
                RegistryEngine.UpdateMembers(groupId, handleStrings, tr);
                return;
            }

            // Check for zombie handles (deleted entities)
            var aliveHandleStrings = new List<string>();
            var deadCount = 0;

            foreach (var handleStr in nodHandleStrings)
            {
                var objId = AcadUtils.GetObjectIdFromHandle(handleStr);
                if (objId == ObjectId.Null || objId.IsErased)
                {
                    deadCount++;
                    continue;
                }

                // Verify entity still exists and can be opened
                try
                {
                    var obj = tr.GetObject(objId, OpenMode.ForRead);
                    if (obj != null && !obj.IsErased)
                    {
                        aliveHandleStrings.Add(handleStr);
                    }
                    else
                    {
                        deadCount++;
                    }
                }
                catch
                {
                    deadCount++;
                }
            }

            // Update NOD if zombies found
            if (deadCount > 0)
            {
                RegistryEngine.UpdateMembers(groupId, aliveHandleStrings, tr);
            }

            // V5.0 Spec Section 2.1: Detect Duplicate GroupId (copied entities)
            // If current topology has handles NOT in NOD, they may be COPIES
            var currentHandleStrings = topoGroup.Select(t => t.ObjectId.Handle.ToString()).ToList();
            var orphanHandles = currentHandleStrings.Except(aliveHandleStrings).ToList();

            if (orphanHandles.Count > 0)
            {
                // These handles have same GroupId but are NOT in NOD
                // This means they are COPIES of original group -> Create NEW GroupId

                // Check if ALL current handles are orphans (entire group is a copy)
                bool isEntireGroupCopy = orphanHandles.Count == currentHandleStrings.Count;

                if (isEntireGroupCopy)
                {
                    // All entities in topology are copies -> Generate new GroupId for entire group
                    var newGroupId = Guid.NewGuid().ToString().Substring(0, 8).ToUpperInvariant();

                    for (int i = 0; i < topoGroup.Count; i++)
                    {
                        var obj = tr.GetObject(topoGroup[i].ObjectId, OpenMode.ForWrite);
                        XDataUtils.WriteGroupIdentity(obj, newGroupId, i, tr);
                        XDataUtils.WriteGroupState(obj, 0, false, tr);
                    }

                    // Register new group in NOD
                    RegistryEngine.ResurrectGroup(newGroupId, currentHandleStrings, tr);

                    // Update group object with new GroupId
                    group.GroupId = newGroupId;
                }
                else
                {
                    // Mixed case: some are copies, some are original
                    // Split orphans into their own new group
                    var newGroupId = Guid.NewGuid().ToString().Substring(0, 8).ToUpperInvariant();

                    for (int idx = 0; idx < orphanHandles.Count; idx++)
                    {
                        var objId = AcadUtils.GetObjectIdFromHandle(orphanHandles[idx]);
                        if (objId != ObjectId.Null && !objId.IsErased)
                        {
                            var obj = tr.GetObject(objId, OpenMode.ForWrite);
                            XDataUtils.WriteGroupIdentity(obj, newGroupId, idx, tr);
                            XDataUtils.WriteGroupState(obj, 0, false, tr);
                        }
                    }

                    // Register orphans as new group
                    RegistryEngine.ResurrectGroup(newGroupId, orphanHandles, tr);
                }
            }

            // V5.0: Check for stale geometry
            CheckStaleGeometry(group, topoGroup, tr);
        }

        /// <summary>
        /// V5.0: Check if geometry has changed since last calculation.
        /// Compares current entity length with stored span length.
        /// </summary>
        private void CheckStaleGeometry(BeamGroup group, List<BeamTopology> topoGroup, Transaction tr)
        {
            if (group?.Spans == null || topoGroup == null) return;

            const double tolerance = 50; // mm tolerance for geometry change

            for (int i = 0; i < group.Spans.Count && i < topoGroup.Count; i++)
            {
                var span = group.Spans[i];
                var topo = topoGroup[i];

                // Get current entity length
                double currentLength = topo.Length;

                // Compare with stored span length
                if (span.Length > 0 && Math.Abs(currentLength - span.Length) > tolerance)
                {
                    group.HasStaleGeometry = true;
                    return;
                }
            }

            // Also check if span count changed
            if (group.Spans.Count != topoGroup.Count)
            {
                group.HasStaleGeometry = true;
            }
        }

        /// <summary>
        /// V5: Refresh group requirements from XData.
        /// </summary>
        private void RefreshGroupFromXDataV5(BeamGroup group, List<BeamTopology> topoGroup, Transaction tr, DtsSettings settings)
        {
            if (group?.Spans == null) return;

            double torsTop = settings?.Beam?.TorsionDist_TopBar ?? 0.25;
            double torsBot = settings?.Beam?.TorsionDist_BotBar ?? 0.25;
            double torsSide = settings?.Beam?.TorsionDist_SideBar ?? 0.50;

            for (int i = 0; i < group.Spans.Count && i < topoGroup.Count; i++)
            {
                var span = group.Spans[i];
                var topo = topoGroup[i];

                var designData = topo.RebarData;
                if (designData == null) continue;

                // FIX 1.4: REMOVED FlipBeamResultData - TopologyBuilder already sorts L→R
                // Old code:
                // if (topo.IsGeometryReversed)
                // {
                //     designData = FlipBeamResultData(designData);
                // }

                // Update span dimensions from XData
                if (designData.Width > 0)
                    span.Width = designData.Width * 10; // cm -> mm
                if (designData.SectionHeight > 0)
                    span.Height = designData.SectionHeight * 10;

                // Ensure arrays exist
                if (span.As_Top == null || span.As_Top.Length < 6) span.As_Top = new double[6];
                if (span.As_Bot == null || span.As_Bot.Length < 6) span.As_Bot = new double[6];
                if (span.StirrupReq == null || span.StirrupReq.Length < 3) span.StirrupReq = new double[3];
                if (span.WebReq == null || span.WebReq.Length < 3) span.WebReq = new double[3];

                // Map 3 zones -> 6 positions
                for (int zi = 0; zi < 3; zi++)
                {
                    double asTopReq = (designData.TopArea?.ElementAtOrDefault(zi) ?? 0) +
                                     (designData.TorsionArea?.ElementAtOrDefault(zi) ?? 0) * torsTop;
                    double asBotReq = (designData.BotArea?.ElementAtOrDefault(zi) ?? 0) +
                                     (designData.TorsionArea?.ElementAtOrDefault(zi) ?? 0) * torsBot;

                    int p0 = zi == 0 ? 0 : (zi == 1 ? 2 : 4);
                    int p1 = p0 + 1;

                    span.As_Top[p0] = RebarCalculator.RoundRebarValue(asTopReq);
                    span.As_Top[p1] = RebarCalculator.RoundRebarValue(asTopReq);
                    span.As_Bot[p0] = RebarCalculator.RoundRebarValue(asBotReq);
                    span.As_Bot[p1] = RebarCalculator.RoundRebarValue(asBotReq);

                    span.StirrupReq[zi] = RebarCalculator.RoundRebarValue(designData.ShearArea?.ElementAtOrDefault(zi) ?? 0);
                    span.WebReq[zi] = RebarCalculator.RoundRebarValue((designData.TorsionArea?.ElementAtOrDefault(zi) ?? 0) * torsSide);
                }
            }
        }

        /// <summary>
        /// V5: Load existing rebar solution from XData into SpanData.
        /// </summary>
        private void LoadExistingSolutionFromXData(BeamGroup group, List<BeamTopology> topoGroup, Transaction tr)
        {
            if (group?.Spans == null) return;

            for (int i = 0; i < group.Spans.Count && i < topoGroup.Count; i++)
            {
                var span = group.Spans[i];
                var topo = topoGroup[i];

                var obj = tr.GetObject(topo.ObjectId, OpenMode.ForRead);
                var rawData = XDataUtils.GetRawData(obj);
                if (rawData == null) continue;

                // Read rebar strings
                string[] topStrings = null;
                string[] botStrings = null;
                string[] stirrupStrings = null;

                if (rawData.TryGetValue("TopRebarString", out var topObj) && topObj is object[] topArr)
                    topStrings = topArr.Select(x => x?.ToString()).ToArray();
                if (rawData.TryGetValue("BotRebarString", out var botObj) && botObj is object[] botArr)
                    botStrings = botArr.Select(x => x?.ToString()).ToArray();
                if (rawData.TryGetValue("StirrupString", out var stirObj) && stirObj is object[] stirArr)
                    stirrupStrings = stirArr.Select(x => x?.ToString()).ToArray();

                // FIX 1.4: REMOVED flip logic - XData is in L→R canonical order
                // Old code:
                // if (topo.IsGeometryReversed)
                // {
                //     topStrings = FlipArray(topStrings);
                //     botStrings = FlipArray(botStrings);
                //     stirrupStrings = FlipArray(stirrupStrings);
                // }

                // Parse strings into RebarInfo structures
                if (topStrings != null && topStrings.Length >= 3)
                {
                    span.TopBackbone = ParseRebarString(topStrings[1]); // Mid position = backbone
                    span.TopAddLeft = ParseAddonFromString(topStrings[0], span.TopBackbone);
                    span.TopAddMid = ParseAddonFromString(topStrings[1], span.TopBackbone);
                    span.TopAddRight = ParseAddonFromString(topStrings[2], span.TopBackbone);
                }

                if (botStrings != null && botStrings.Length >= 3)
                {
                    span.BotBackbone = ParseRebarString(botStrings[1]);
                    span.BotAddLeft = ParseAddonFromString(botStrings[0], span.BotBackbone);
                    span.BotAddMid = ParseAddonFromString(botStrings[1], span.BotBackbone);
                    span.BotAddRight = ParseAddonFromString(botStrings[2], span.BotBackbone);
                }

                if (stirrupStrings != null)
                {
                    span.Stirrup = stirrupStrings;
                }

                // FIX: Populate TopRebarInternal/BotRebarInternal for viewer display
                // These are what the viewer's TopRebar/BotRebar properties read from
                PopulateRebarInternalArrays(span, topStrings, botStrings);

                // V5: Restore SelectedDesign from first span (group-level data)
                if (i == 0 && rawData.TryGetValue("SelectedDesignJson", out var designJson) && designJson != null)
                {
                    try
                    {
                        string json = designJson.ToString();
                        if (!string.IsNullOrEmpty(json))
                        {
                            var design = Newtonsoft.Json.JsonConvert.DeserializeObject<ContinuousBeamSolution>(json);
                            if (design != null)
                            {
                                group.SelectedDesign = design;
                            }
                        }
                    }
                    catch { /* Ignore deserialization errors */ }
                }

                // FIX: Restore BackboneOptions from first span (group-level data)
                // This ensures Viewer shows calculation results when reopening
                if (i == 0 && rawData.TryGetValue("BackboneOptionsJson", out var optionsJson) && optionsJson != null)
                {
                    try
                    {
                        string json = optionsJson.ToString();
                        if (!string.IsNullOrEmpty(json))
                        {
                            var options = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ContinuousBeamSolution>>(json);
                            if (options != null && options.Count > 0)
                            {
                                group.BackboneOptions = options;
                            }
                        }
                    }
                    catch { /* Ignore deserialization errors */ }
                }

                // V5.0: Read GroupState for SelectedIdx and IsLocked
                if (i == 0)
                {
                    var (selectedIdx, isLocked) = XDataUtils.ReadGroupState(obj);
                    if (selectedIdx >= 0)
                    {
                        group.SelectedBackboneIndex = selectedIdx;
                        group.IsLocked = isLocked;
                    }
                }

                // V5.0: Read rebar options from Opt0-4 format if available
                // This is the new compact format that replaces BackboneOptionsJson
                if (i == 0 && group.BackboneOptions == null)
                {
                    var optionsV5 = XDataUtils.ReadRebarOptionsV5(obj);
                    if (optionsV5 != null && optionsV5.Count > 0 && optionsV5.Any(o => !string.IsNullOrEmpty(o.TopL0)))
                    {
                        // Convert RebarOptionData to ContinuousBeamSolution format
                        // Note: This is a simplified conversion - full solution data requires recalculation
                        group.BackboneOptions = new List<ContinuousBeamSolution>();
                        for (int optIdx = 0; optIdx < optionsV5.Count && optIdx < 5; optIdx++)
                        {
                            var optData = optionsV5[optIdx];
                            if (string.IsNullOrEmpty(optData.TopL0) && string.IsNullOrEmpty(optData.BotL0)) continue;

                            // Parse backbone count and diameter from "nDd" format
                            var topInfo = ParseRebarString(optData.TopL0);
                            var botInfo = ParseRebarString(optData.BotL0);

                            var sol = new ContinuousBeamSolution
                            {
                                OptionName = $"Option {optIdx + 1}",
                                BackboneCount_Top = topInfo?.Count ?? 0,
                                BackboneCount_Bot = botInfo?.Count ?? 0,
                                BackboneDiameter_Top = topInfo?.Diameter ?? 20,
                                BackboneDiameter_Bot = botInfo?.Diameter ?? 20
                            };
                            group.BackboneOptions.Add(sol);
                        }
                    }
                }

                // V5.0: Read IsManual flag (replaces legacy DesignLocked check)
                bool isManualV5 = XDataUtils.ReadIsManual(obj);
                if (isManualV5)
                {
                    span.IsManualModified = true;
                }

                // Legacy check for old DesignLocked key (backward compat)
                if (!span.IsManualModified && rawData.TryGetValue("DesignLocked", out var lockedObj))
                {
                    bool isLocked = lockedObj?.ToString() == "True" || lockedObj?.ToString() == "1";
                    if (isLocked)
                    {
                        span.IsManualModified = true;
                    }
                }
            }
        }

        /// <summary>
        /// FIX: Populate TopRebarInternal/BotRebarInternal from string arrays.
        /// Viewer reads TopRebar property which returns TopRebarInternal.
        /// </summary>
        private void PopulateRebarInternalArrays(SpanData span, string[] topStrings, string[] botStrings)
        {
            if (span == null) return;

            // Initialize if null
            if (span.TopRebarInternal == null) span.TopRebarInternal = new string[3, 6];
            if (span.BotRebarInternal == null) span.BotRebarInternal = new string[3, 6];

            // Map 3-element array [Left, Mid, Right] to 6-position array [0,1,2,3,4,5]
            // Positions: 0,1 = Left; 2,3 = Mid; 4,5 = Right
            if (topStrings != null && topStrings.Length >= 3)
            {
                // Layer 0 (primary layer - backbone + addon combined)
                span.TopRebarInternal[0, 0] = topStrings[0]; // Left
                span.TopRebarInternal[0, 1] = topStrings[0];
                span.TopRebarInternal[0, 2] = topStrings[1]; // Mid
                span.TopRebarInternal[0, 3] = topStrings[1];
                span.TopRebarInternal[0, 4] = topStrings[2]; // Right
                span.TopRebarInternal[0, 5] = topStrings[2];
            }

            if (botStrings != null && botStrings.Length >= 3)
            {
                span.BotRebarInternal[0, 0] = botStrings[0];
                span.BotRebarInternal[0, 1] = botStrings[0];
                span.BotRebarInternal[0, 2] = botStrings[1];
                span.BotRebarInternal[0, 3] = botStrings[1];
                span.BotRebarInternal[0, 4] = botStrings[2];
                span.BotRebarInternal[0, 5] = botStrings[2];
            }
        }

        /// <summary>
        /// Parse rebar string like "3D20" into RebarInfo.
        /// </summary>
        private RebarInfo ParseRebarString(string str)
        {
            if (string.IsNullOrWhiteSpace(str) || str == "-") return null;

            // Handle "3D20+2D16" format - extract first part as backbone
            string mainPart = str.Split('+')[0].Trim();

            var match = System.Text.RegularExpressions.Regex.Match(mainPart, @"(\d+)D(\d+)");
            if (match.Success)
            {
                return new RebarInfo
                {
                    Count = int.Parse(match.Groups[1].Value),
                    Diameter = int.Parse(match.Groups[2].Value)
                };
            }

            return null;
        }

        /// <summary>
        /// Parse addon part from rebar string.
        /// </summary>
        private RebarInfo ParseAddonFromString(string str, RebarInfo backbone)
        {
            if (string.IsNullOrWhiteSpace(str) || str == "-" || !str.Contains("+")) return null;

            var parts = str.Split('+');
            if (parts.Length < 2) return null;

            string addonPart = parts[1].Trim();
            var match = System.Text.RegularExpressions.Regex.Match(addonPart, @"(\d+)D(\d+)");
            if (match.Success)
            {
                return new RebarInfo
                {
                    Count = int.Parse(match.Groups[1].Value),
                    Diameter = int.Parse(match.Groups[2].Value)
                };
            }

            return null;
        }

        /// <summary>
        /// V5: Apply results from Viewer back to XData.
        /// </summary>
        private void ApplyBeamGroupResultsV5(List<BeamGroup> groups)
        {
            if (groups == null || groups.Count == 0) return;

            // Sync to XData
            SyncGroupSpansToXData(groups);

            WriteMessage($"Đã apply thép cho {groups.Sum(g => g.Spans?.Count ?? 0)} nhịp.");
        }

        #endregion

        /// <summary>
        /// Build rebar string from backbone + addon.
        /// </summary>
        private static string BuildRebarString(RebarInfo backbone, RebarInfo addon)
        {
            string result = "";

            if (backbone != null && backbone.Count > 0 && backbone.Diameter > 0)
            {
                result = $"{backbone.Count}D{backbone.Diameter}";
            }

            if (addon != null && addon.Count > 0 && addon.Diameter > 0)
            {
                if (!string.IsNullOrEmpty(result))
                    result += $"+{addon.Count}D{addon.Diameter}";
                else
                    result = $"{addon.Count}D{addon.Diameter}";
            }

            return string.IsNullOrEmpty(result) ? "-" : result;
        }

        /// <summary>
        /// Check if entity geometry is reversed (Start.X > End.X).
        /// </summary>
        private static bool CheckIfEntityReversed(DBObject obj)
        {
            var curve = obj as Curve;
            if (curve == null) return false;

            var start = curve.StartPoint;
            var end = curve.EndPoint;

            return start.X > end.X;
        }

        /// <summary>
        /// Static version of FlipArray for use in static methods.
        /// </summary>
        private static T[] FlipArrayStatic<T>(T[] arr)
        {
            if (arr == null || arr.Length == 0) return arr;
            var flipped = new T[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                flipped[i] = arr[arr.Length - 1 - i];
            }
            return flipped;
        }
    }
}

