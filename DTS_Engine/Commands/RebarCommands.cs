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

            // 2. Select Frames on Screen
            var ed = AcadUtils.Ed;
            WriteMessage("\nChọn các đường Dầm (Frame) để lấy nội lực: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // 3. Ask Display Mode
            var pIntOpt = new PromptIntegerOptions("\nChọn chế độ hiển thị [0=Tổng hợp | 1=Thép dọc | 2=Thép xoắn | 3=Thép Đai/Sườn]: ");
            pIntOpt.AllowNone = true;
            pIntOpt.DefaultValue = 0;
            pIntOpt.AllowNegative = false;
            pIntOpt.LowerLimit = 0;
            pIntOpt.UpperLimit = 3;

            var pIntRes = ed.GetInteger(pIntOpt);
            int displayMode = 0;
            if (pIntRes.Status == PromptStatus.OK)
                displayMode = pIntRes.Value;
            else if (pIntRes.Status != PromptStatus.None)
                return;

            // 4. Mapping CAD -> SAP
            WriteMessage("Đang ánh xạ phần tử CAD → SAP ...");
            var matchedNames = new List<string>();
            var cadToSap = new Dictionary<ObjectId, string>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in selectedIds)
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    var raw = XDataUtils.GetRawData(obj);
                    string sapName = null;
                    if (raw != null && raw.TryGetValue("xSapFrameName", out var sapObj)) sapName = sapObj?.ToString();

                    if (string.IsNullOrEmpty(sapName))
                    {
                        var existing = XDataUtils.ReadElementData(obj);
                        if (existing != null && existing.HasSapFrame) sapName = existing.SapFrameName;
                    }

                    if (!string.IsNullOrEmpty(sapName))
                    {
                        matchedNames.Add(sapName);
                        cadToSap[id] = sapName;
                    }
                }
            });

            if (matchedNames.Count == 0)
            {
                WriteError("Không tìm thấy dầm SAP nào được ánh xạ. Hãy chạy DTS_LINK hoặc DTS_PLOT_FROM_SAP trước.");
                return;
            }

            // 5. Get Results
            var results = engine.GetBeamResults(matchedNames);
            if (results.Count == 0)
            {
                WriteError("Không lấy được kết quả từ SAP. Hãy đảm bảo đã chạy Design Concrete.");
                return;
            }

            // 6. Update Display
            UpdateRebarDisplayLabels(selectedIds, displayMode, results);
        }

        /// <summary>
        /// Hiển thị diện tích thép yêu cầu từ dữ liệu XData hiện có.
        /// </summary>
        [CommandMethod("DTS_REBAR_SHOW")]
        public void DTS_REBAR_SHOW()
        {
            WriteMessage("=== REBAR: HIỂN THỊ DIỆN TÍCH THÉP YÊU CẦU ===");

            var ed = AcadUtils.Ed;
            WriteMessage("\nChọn dầm để hiển thị (hoặc Enter để chọn tất cả dầm có dữ liệu): ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE", true);

            // Nếu không chọn gì, tự động lấy tất cả dầm có dữ liệu DTS trong Current Space
            if (selectedIds.Count == 0)
            {
                WriteMessage("Đang quét toàn bộ dầm có dữ liệu...");
                UsingTransaction(tr =>
                {
                    var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased) continue;
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        var rebarData = XDataUtils.ReadRebarData(ent);
                        if (rebarData != null)
                        {
                            selectedIds.Add(id);
                        }
                    }
                });
            }

            if (selectedIds.Count == 0)
            {
                WriteMessage("Không tìm thấy dầm nào có dữ liệu thiết kế.");
                return;
            }

            // Ask Display Mode
            var pIntOpt = new PromptIntegerOptions("\nChọn chế độ hiển thị [0=Tổng hợp | 1=Thép dọc | 2=Thép xoắn | 3=Thép Đai/Sườn]: ");
            pIntOpt.AllowNone = true;
            pIntOpt.DefaultValue = 0;
            pIntOpt.AllowNegative = false;
            pIntOpt.LowerLimit = 0;
            pIntOpt.UpperLimit = 3;

            var pIntRes = ed.GetInteger(pIntOpt);
            int displayMode = 0;
            if (pIntRes.Status == PromptStatus.OK)
                displayMode = pIntRes.Value;
            else if (pIntRes.Status != PromptStatus.None)
                return;

            UpdateRebarDisplayLabels(selectedIds, displayMode);
        }

        /// <summary>
        /// Cập nhật hiển thị label thép cho danh sách ObjectId.
        /// Nếu results != null, sẽ cập nhật XData trước khi hiển thị.
        /// </summary>
        private void UpdateRebarDisplayLabels(IEnumerable<ObjectId> ids, int displayMode, Dictionary<string, BeamResultData> results = null)
        {
            int successCount = 0;
            int insufficientCount = 0;
            var insufficientBeamIds = new List<ObjectId>();
            var dtsSettings = DtsSettings.Instance;

            // 1. Clear old labels
            var handles = ids.Select(id => id.Handle.ToString()).ToList();
            ClearRebarLabels(handles);

            // 2. Plot new labels
            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (ObjectId cadId in ids)
                {
                    try
                    {
                        var obj = tr.GetObject(cadId, OpenMode.ForWrite);
                        BeamResultData designData = null;

                        // Case A: Đang Import (results != null) -> Cập nhật XData từ results
                        if (results != null)
                        {
                            // Tìm SAP Name để map
                            string sapName = null;
                            var raw = XDataUtils.GetRawData(obj);
                            if (raw.TryGetValue("xSapFrameName", out var sapObj)) sapName = sapObj?.ToString();

                            if (string.IsNullOrEmpty(sapName))
                            {
                                var existing = XDataUtils.ReadElementData(obj);
                                if (existing != null && existing.HasSapFrame) sapName = existing.SapFrameName;
                            }

                            if (!string.IsNullOrEmpty(sapName) && results.TryGetValue(sapName, out var newData))
                            {
                                designData = newData;
                                designData.TorsionFactorUsed = dtsSettings.Beam?.TorsionDist_TopBar ?? 0.25;
                                designData.SapElementName = sapName;

                                // Cập nhật XData REQUIRED
                                XDataUtils.UpdateBeamRequiredXData(
                                    obj, tr,
                                    topArea: designData.TopArea,
                                    botArea: designData.BotArea,
                                    torsionArea: designData.TorsionArea,
                                    shearArea: designData.ShearArea,
                                    ttArea: designData.TTArea,
                                    sapElementName: designData.SapElementName);
                            }
                        }

                        // Case B: Đọc trực tiếp từ XData
                        if (designData == null)
                        {
                            designData = XDataUtils.ReadRebarData(obj);
                        }

                        if (designData == null || !designData.HasValidData()) continue;

                        // Calculate display values
                        string[] displayTopStr = new string[3];
                        string[] displayBotStr = new string[3];

                        for (int i = 0; i < 3; i++)
                        {
                            switch (displayMode)
                            {
                                case 0:
                                    double top = (designData.TopArea?[i] ?? 0) + (designData.TorsionArea?[i] ?? 0) * (dtsSettings.Beam?.TorsionDist_TopBar ?? 0.25);
                                    double bot = (designData.BotArea?[i] ?? 0) + (designData.TorsionArea?[i] ?? 0) * (dtsSettings.Beam?.TorsionDist_BotBar ?? 0.25);
                                    displayTopStr[i] = FormatValue(top);
                                    displayBotStr[i] = FormatValue(bot);
                                    break;
                                case 1:
                                    displayTopStr[i] = FormatValue(designData.TopArea?[i] ?? 0);
                                    displayBotStr[i] = FormatValue(designData.BotArea?[i] ?? 0);
                                    break;
                                case 2:
                                    displayTopStr[i] = FormatValue(designData.TTArea?[i] ?? 0);
                                    displayBotStr[i] = FormatValue(designData.TorsionArea?[i] ?? 0);
                                    break;
                                case 3:
                                    displayTopStr[i] = FormatValue(designData.ShearArea?[i] ?? 0);
                                    displayBotStr[i] = FormatValue((designData.TorsionArea?[i] ?? 0) * (dtsSettings.Beam?.TorsionDist_SideBar ?? 0.50));
                                    break;
                            }
                        }

                        // Plot
                        var curve = obj as Curve;
                        if (curve != null)
                        {
                            string ownerH = obj.Handle.ToString();
                            for (int i = 0; i < 3; i++)
                            {
                                LabelPlotter.PlotRebarLabel(btr, tr, curve.StartPoint, curve.EndPoint, displayTopStr[i], i, true, ownerH);
                                LabelPlotter.PlotRebarLabel(btr, tr, curve.StartPoint, curve.EndPoint, displayBotStr[i], i, false, ownerH);
                            }
                            successCount++;
                        }
                    }
                    catch { }
                }
            });

            // HIGHLIGHT insufficient beams
            if (insufficientCount > 0)
            {
                WriteMessage($"\n⚠️ CẢNH BÁO: Phát hiện {insufficientCount} dầm thiếu diện tích thép!");
                VisualUtils.SetPersistentColors(insufficientBeamIds, 1); // Red
            }

            string[] modeNames = { "Tổng hợp", "Thép dọc", "Thép xoắn", "Thép Đai/Sườn" };
            WriteSuccess($"Đã hiển thị Label thép ({modeNames[displayMode]}) cho {successCount} dầm.");
        }

        [CommandMethod("DTS_REBAR_IMPORT_SAP")]
        public void DTS_REBAR_IMPORT_SAP()
        {
            WriteMessage("=== IMPORT KẾT QUẢ THIẾT KẾ TỪ SAP2000 ===");
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
                    if (ent != null && (ent.Layer == "dts_labels" || ent.Layer == "dts_rebar_text"))
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

                WriteMessage($"Đã tìm thấy {allTopologies.Count} dầm, đang xử lý...");

                // Step 2: Split into separate groups based on existing links
                var topologyGroups = topologyBuilder.SplitIntoGroups(allTopologies);
                // NOTE: Không in message "phân nhóm" vì lệnh này không phân nhóm - chỉ tính toán

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

                    // NOTE: Không gọi EnsureGroupIdentity - lệnh Calculate không tạo GroupIdentity mới
                    // GroupIdentity chỉ được tạo bởi lệnh Group beam

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
                // NOTE: MappingSource đã deprecated - không copy
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

                string spanId = $"S{i + 1}";



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

                // NOTE: UpdateBeamSolutionXData (legacy TopRebarString/BotRebarString) đã xóa
                // V6.0: OptUser là Single Source of Truth
                // V6.0: Write OptUser (Single Source of Truth)
                var optUser = new XDataUtils.RebarOptionData
                {
                    TopL0 = backboneTop,
                    BotL0 = backboneBot,
                    Stirrup = stirrupStrings?[1] ?? "", // Use Mid zone
                    Web = "" // No web bar for now
                };
                XDataUtils.RebarOptionData.PopulateAddons(optUser, sol, spanId, "Top");
                XDataUtils.RebarOptionData.PopulateAddons(optUser, sol, spanId, "Bot");
                XDataUtils.WriteOptUser(obj, optUser, tr);

                // V6.0: Write IsLocked (always false after initial calculation)
                XDataUtils.WriteIsLocked(obj, isLocked: false, tr);

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

                    XDataUtils.RebarOptionData.PopulateAddons(optData, sol, spanId, "Top");
                    XDataUtils.RebarOptionData.PopulateAddons(optData, sol, spanId, "Bot");

                    options.Add(optData);
                }

                // Write options to entity
                XDataUtils.WriteRebarOptions(obj, options, tr);
            }
        }



        // NOTE: EnsureGroupIdentity đã xóa - việc tạo GroupIdentity là của lệnh Group beam

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

                            // V6.1: Extract directly from internal rebar arrays (Single Source of Truth)
                            // Layers: 0 = Backbone, 1 = Addon
                            // Positions: 0 = Left, 2 = Mid, 4 = Right

                            // Check reversed geometry
                            bool isReversed = CheckIfEntityReversed(obj);
                            int idxL = isReversed ? 4 : 0;
                            int idxM = 2;
                            int idxR = isReversed ? 0 : 4;

                            var optUser = new XDataUtils.RebarOptionData
                            {
                                TopL0 = span.TopRebarInternal[0, idxM] ?? "", // Backbone (typically same across all)
                                BotL0 = span.BotRebarInternal[0, idxM] ?? "",
                                Stirrup = span.Stirrup != null && span.Stirrup.Length > idxM ? span.Stirrup[idxM] : "",
                                Web = span.WebBar != null && span.WebBar.Length > idxM ? span.WebBar[idxM] : ""
                            };

                            // Addons (Layers 1 to 7)
                            for (int l = 1; l < 8; l++)
                            {
                                string tL = span.TopRebarInternal[l, idxL] ?? "";
                                string tM = span.TopRebarInternal[l, idxM] ?? "";
                                string tR = span.TopRebarInternal[l, idxR] ?? "";

                                if (!string.IsNullOrEmpty(tL) || !string.IsNullOrEmpty(tM) || !string.IsNullOrEmpty(tR))
                                {
                                    optUser.TopAddons.Add(new string[] { tL, tM, tR });
                                }

                                string bL = span.BotRebarInternal[l, idxL] ?? "";
                                string bM = span.BotRebarInternal[l, idxM] ?? "";
                                string bR = span.BotRebarInternal[l, idxR] ?? "";

                                if (!string.IsNullOrEmpty(bL) || !string.IsNullOrEmpty(bM) || !string.IsNullOrEmpty(bR))
                                {
                                    optUser.BotAddons.Add(new string[] { bL, bM, bR });
                                }
                            }

                            XDataUtils.WriteOptUser(obj, optUser, tr);
                            XDataUtils.WriteIsLocked(obj, group.IsLocked, tr);

                            // V6.0: Manual modification flag (sycned with IsLocked)
                            if (span.IsManualModified)
                            {
                                XDataUtils.WriteIsLocked(obj, true, tr);
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
                    XDataUtils.WriteIsLocked(obj, false, tr);
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
                        XDataUtils.WriteIsLocked(obj, false, tr);
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
                            XDataUtils.WriteIsLocked(obj, false, tr);
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

                // V6.0: Read IsLocked flag
                if (i == 0)
                {
                    bool isLocked = XDataUtils.ReadIsLocked(obj);
                    group.IsLocked = isLocked;
                    // NOTE: SelectedBackboneIndex no longer stored in XData - use first option
                    group.SelectedBackboneIndex = 0;
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

                // V5.0: Read IsLocked flag
                span.IsManualModified = XDataUtils.ReadIsLocked(obj);
                // NOTE: DesignLocked legacy key fallback đã xóa
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

        #region DTS_REBAR_BEAM_NAME - Re-name dầm sau khi tính toán

        /// <summary>
        /// [V6.0] Đặt lại tên dầm (xSectionLabel) dựa trên bố trí thép thực tế.
        /// Gọi NamingEngine.AutoLabeling() để đặt tên theo rules trong DtsSettings.
        /// Workflow: GROUP (tên tạm) → CALCULATE → BEAM_NAME (tên chính thức)
        /// </summary>
        [CommandMethod("DTS_REBAR_BEAM_NAME")]
        public void DTS_REBAR_BEAM_NAME()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== ĐẶT TÊN SECTION DẦM ===");
                WriteMessage("Lệnh này đặt tên cho từng dầm dựa trên tiết diện + thép.");

                var allBeams = new List<BeamData>();
                var handleToBeam = new Dictionary<string, BeamData>(); // Track handles

                UsingTransaction(tr =>
                {
                    var db = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Database;
                    var ed = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Editor;

                    // Yêu cầu user chọn entities
                    WriteMessage("Chọn các dầm cần đặt tên (hoặc Enter để chọn tất cả):");
                    var selectionResult = ed.GetSelection();

                    ObjectId[] selectedIds;

                    if (selectionResult.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK)
                    {
                        // User chọn entities
                        selectedIds = selectionResult.Value.GetObjectIds();
                        WriteMessage($"Đã chọn {selectedIds.Length} entities.");
                    }
                    else
                    {
                        // Enter - quét tất cả entities có XData DTS_APP
                        WriteMessage("Quét tất cả entities có DTS_APP XData...");
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                        var tempList = new List<ObjectId>();
                        foreach (ObjectId id in btr)
                        {
                            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null) continue;

                            // Kiểm tra XData DTS_APP
                            var xdata = ent.GetXDataForApplication("DTS_APP");
                            if (xdata != null)
                                tempList.Add(id);
                        }
                        selectedIds = tempList.ToArray();
                        WriteMessage($"Tìm thấy {selectedIds.Length} entities có DTS_APP.");
                    }

                    if (selectedIds.Length == 0)
                    {
                        WriteMessage("Không có entities nào để xử lý.");
                        return;
                    }

                    // Xử lý từng entity đã chọn
                    int scannedCount = 0;
                    foreach (var id in selectedIds)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        scannedCount++;

                        // Kiểm tra XData DTS_APP
                        var xdata = ent.GetXDataForApplication("DTS_APP");
                        if (xdata == null) continue;

                        // Đọc XData
                        var beamData = XDataUtils.ReadElementData(ent) as BeamData;
                        if (beamData == null) continue;

                        // 3. Đọc OptUser từ XData (dùng cho Signature)
                        var optUserDict = XDataUtils.ReadOptUser(ent);
                        if (optUserDict != null && optUserDict.TopL0 != null)
                        {
                            // Format OptUser string (Skin không có trong RebarOptionData)
                            beamData.OptUser = $"T:{optUserDict.TopL0};B:{optUserDict.BotL0};S:{optUserDict.Stirrup};W:";
                        }

                        // 4. Đọc geometry từ Entity (cho Direction và Sort)
                        if (ent is Autodesk.AutoCAD.DatabaseServices.Line line)
                        {
                            beamData.StartPoint = new[] { line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z };
                            beamData.EndPoint = new[] { line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z };
                            beamData.CenterX = (line.StartPoint.X + line.EndPoint.X) / 2.0;
                            beamData.CenterY = (line.StartPoint.Y + line.EndPoint.Y) / 2.0;
                        }
                        else if (ent is Autodesk.AutoCAD.DatabaseServices.Polyline pline && pline.NumberOfVertices >= 2)
                        {
                            var pt0 = pline.GetPoint3dAt(0);
                            var pt1 = pline.GetPoint3dAt(pline.NumberOfVertices - 1);
                            beamData.StartPoint = new[] { pt0.X, pt0.Y, pt0.Z };
                            beamData.EndPoint = new[] { pt1.X, pt1.Y, pt1.Z };
                            beamData.CenterX = (pt0.X + pt1.X) / 2.0;
                            beamData.CenterY = (pt0.Y + pt1.Y) / 2.0;
                        }

                        // 5. Validation: Phải có Support data
                        // (Nếu không có, đây là dầm chưa phân tích topology)
                        if (!beamData.BaseZ.HasValue)
                        {
                            WriteMessage($"⚠ Dầm {ent.Handle} thiếu BaseZ - skip.");
                            continue;
                        }

                        // Track handle for later update
                        string handle = ent.Handle.ToString();
                        handleToBeam[handle] = beamData;
                        allBeams.Add(beamData);
                    }

                    WriteMessage($"Đã quét {scannedCount} entities, tìm thấy {allBeams.Count} dầm hợp lệ.");

                    if (allBeams.Count == 0)
                    {
                        WriteMessage("Không tìm thấy dầm nào để đặt tên.");
                        return;
                    }

                    // 4. Validation: Kiểm tra StoryConfig
                    var settings = DtsSettings.Instance;
                    if (settings.StoryConfigs == null || settings.StoryConfigs.Count == 0)
                    {
                        WriteMessage("❌ LỖI: Chưa có cấu hình StoryNamingConfig trong DtsSettings.");
                        WriteMessage("Vui lòng mở Rebar Config để thêm cấu hình tầng.");
                        return;
                    }

                    // 5. Gọi NamingEngine.AutoLabelBeams()
                    WriteMessage("Đang đặt tên cho các dầm...");
                    Core.Algorithms.NamingEngine.AutoLabelBeams(allBeams, settings);

                    // 6. Cập nhật xSectionLabel vào XData
                    int updatedCount = 0;
                    int lockedCount = 0;

                    foreach (var kvp in handleToBeam)
                    {
                        var handle = kvp.Key;
                        var beam = kvp.Value;
                        if (string.IsNullOrEmpty(beam.SectionLabel)) continue;

                        var objId = AcadUtils.GetObjectIdFromHandle(handle);
                        if (objId.IsNull) continue;

                        var obj = tr.GetObject(objId, OpenMode.ForWrite);
                        XDataUtils.UpdateElementData(obj, beam, tr);

                        updatedCount++;
                        if (beam.SectionLabelLocked) lockedCount++;
                    }

                    WriteSuccess($"✅ Đã đặt tên cho {updatedCount} dầm ({lockedCount} locked).");

                    // 7. Hiển thị settings trước
                    WriteMessage("\n=== CẤU HÌNH ĐẶT TÊN ===");
                    WriteMessage($"Sort Corner: {settings.Naming.SortCorner} (0=TL, 1=TR, 2=BL, 3=BR)");
                    WriteMessage($"Sort Direction: {settings.Naming.SortDirection} (0=X first, 1=Y first)");
                    WriteMessage($"Merge Same Section: {settings.Naming.MergeSameSection}");

                    // 8. Hiển thị kết quả chi tiết theo tầng
                    WriteMessage("\n=== KẾT QUẢ ĐẶT TÊN CHI TIẾT ===");
                    var byStory = allBeams.GroupBy(b => b.StoryName ?? "Unknown").OrderBy(g => g.Key);

                    foreach (var storyGroup in byStory)
                    {
                        WriteMessage($"\n{storyGroup.Key}:");

                        // Group by SectionLabel để hiển thị từng nhóm
                        var labelGroups = storyGroup.GroupBy(b => b.SectionLabel).OrderBy(g => g.Key);

                        foreach (var labelGroup in labelGroups)
                        {
                            var sectionLabel = labelGroup.Key;
                            var beamsInGroup = labelGroup.ToList();
                            var count = beamsInGroup.Count;

                            // Lấy signature từ beam đầu tiên
                            var firstBeam = beamsInGroup.First();
                            string signature = firstBeam.OptUser ?? "N/A";
                            if (firstBeam.Width.HasValue && firstBeam.Depth.HasValue)
                            {
                                signature = $"{(int)firstBeam.Width.Value}x{(int)firstBeam.Depth.Value}|{signature}";
                            }

                            // Check lock status
                            int lockedInGroup = beamsInGroup.Count(b => b.SectionLabelLocked);
                            int unlockedInGroup = count - lockedInGroup;

                            if (lockedInGroup > 0 && unlockedInGroup > 0)
                            {
                                // Mixed locked/unlocked
                                WriteMessage($"  {sectionLabel}: {lockedInGroup} dầm - {signature} - LOCKED");
                                WriteMessage($"    => Tìm thấy {unlockedInGroup} dầm cùng thông số, đã gộp với {sectionLabel}");
                            }
                            else if (lockedInGroup > 0)
                            {
                                // All locked
                                WriteMessage($"  {sectionLabel}: {count} dầm - {signature} - LOCKED - Skipped");
                                WriteMessage($"    => Tên đã được khóa, không thay đổi");
                            }
                            else
                            {
                                // All unlocked
                                WriteMessage($"  {sectionLabel}: {count} dầm - {signature} - Unlocked");
                            }
                        }
                    }
                });
            });
        }

        #endregion
    }
}

