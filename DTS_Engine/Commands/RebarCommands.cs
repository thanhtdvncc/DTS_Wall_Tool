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
using System.Linq;

namespace DTS_Engine.Commands
{
    public class RebarCommands : CommandBase
    {
        [CommandMethod("DTS_REBAR_SAP_RESULT")]
        public void DTS_REBAR_SAP_RESULT()
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

                            // Step 2: Write XData
                            try
                            {
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
                                        displayTopStr[i] = FormatArea(displayTop[i]);
                                        displayBotStr[i] = FormatArea(displayBot[i]);
                                        break;
                                    case 1: // Flex only (Thép dọc chịu uốn thuần)
                                        displayTopStr[i] = FormatArea(designData.TopArea[i]);
                                        displayBotStr[i] = FormatArea(designData.BotArea[i]);
                                        break;
                                    case 2: // Torsion (Top=At/s, Bot=Al)
                                        // Top: TTArea = At/s (Đai xoắn trên đơn vị dài)
                                        // Bot: TorsionArea = Al (Tổng thép dọc xoắn)
                                        displayTopStr[i] = FormatArea(designData.TTArea[i]);
                                        displayBotStr[i] = FormatArea(designData.TorsionArea[i]);
                                        break;
                                    case 3: // Shear & Web (Top=Av/s, Bot=Al×SideRatio)
                                        // Top: ShearArea = Av/s (Đai cắt trên đơn vị dài)
                                        // Bot: TorsionArea × SideRatio = Thép dọc xoắn phân bổ cho sườn
                                        displayTopStr[i] = FormatArea(designData.ShearArea[i]);
                                        displayBotStr[i] = FormatArea(designData.TorsionArea[i] * settings.TorsionRatioSide);
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
        /// Format số thép: Làm tròn LÊN 1 chữ số, bỏ số 0 thừa (2.0→2, 13.62→13.7)
        /// </summary>
        private string FormatArea(double val)
        {
            if (Math.Abs(val) < 0.05) return "0";
            // Làm tròn LÊN (Ceiling) 1 chữ số thập phân
            double ceiling = Math.Ceiling(val * 10) / 10;
            // Bỏ số 0 thừa: 2.0 → 2
            return ceiling == Math.Floor(ceiling) ? $"{(int)ceiling}" : $"{ceiling:F1}";
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

                    // Validate Dimensions
                    if (data.Width <= 0 || data.SectionHeight <= 0)
                    {
                        // Try fallback to defaults or user data?
                        // For safe fail, skip or assume 20x30
                        data.Width = 22; // Default fallback
                        data.SectionHeight = 30;
                    }

                    // Calculate Rebar and update directly into data object
                    for (int i = 0; i < 3; i++)
                    {
                        // === Longitudinal Rebar ===
                        double asTop = data.TopArea[i] + data.TorsionArea[i] * settings.TorsionRatioTop;
                        double asBot = data.BotArea[i] + data.TorsionArea[i] * settings.TorsionRatioBot;

                        string sTop = RebarCalculator.Calculate(asTop, data.Width * 10, data.SectionHeight * 10, settings);
                        string sBot = RebarCalculator.Calculate(asBot, data.Width * 10, data.SectionHeight * 10, settings);

                        data.TopRebarString[i] = sTop;
                        data.BotRebarString[i] = sBot;
                        data.TopAreaProv[i] = RebarStringParser.Parse(sTop);
                        data.BotAreaProv[i] = RebarStringParser.Parse(sBot);

                        // === Stirrup (Thép đai) - ACI 318-19: Av/s + 2*At/s ===
                        string sStirrup = RebarCalculator.CalculateStirrup(data.ShearArea[i], data.TTArea[i], settings);
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
        }

        [CommandMethod("DTS_REBAR_CALCULATE_SETTING")]
        public void DTS_REBAR_CALCULATE_SETTING()
        {
            var ed = AcadUtils.Ed;
            var settings = RebarSettings.Instance;

            // Simple Prompt UI
            // 1. ZONE RATIOS (Chia vùng chiều dài dầm để quét Max)
            double midRatio = 1.0 - settings.ZoneRatioStart - settings.ZoneRatioEnd;
            string currentZone = $"{settings.ZoneRatioStart} {midRatio:F2} {settings.ZoneRatioEnd}";
            var pZone = new PromptStringOptions($"\nNhập tỷ lệ chia vùng dầm [Start Mid End] (Hiện tại: {currentZone}): ");
            pZone.AllowSpaces = true;
            var resZone = ed.GetString(pZone);

            if (resZone.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(resZone.StringResult))
            {
                var parts = resZone.StringResult.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3)
                {
                    if (double.TryParse(parts[0], out double s) &&
                        double.TryParse(parts[1], out double m) &&
                        double.TryParse(parts[2], out double e))
                    {
                        if (Math.Abs(s + m + e - 1.0) > 0.01)
                            ed.WriteMessage("\nCảnh báo: Tổng tỷ lệ không bằng 1.0.");

                        settings.ZoneRatioStart = s;
                        settings.ZoneRatioEnd = e;
                        // Mid không lưu, tự động = 1 - Start - End
                    }
                }
            }

            // 2. TORSION FACTOR (Hệ số phân bổ xoắn vào tiết diện)
            var pTor = new PromptDoubleOptions($"\nNhập hệ số xoắn Top/Bot (Hiện tại: {settings.TorsionFactorTop}): ");
            pTor.AllowNone = true;
            var resTor = ed.GetDouble(pTor);
            if (resTor.Status == PromptStatus.OK)
            {
                settings.TorsionFactorTop = resTor.Value;
                settings.TorsionFactorBot = resTor.Value;
                settings.TorsionFactorSide = 1 - 2 * resTor.Value;
            }

            // 2. Cover
            var pCov = new PromptDoubleOptions($"\nNhập lớp bảo vệ (mm) (Hiện tại: {settings.CoverTop}): ");
            pCov.AllowNone = true;
            var resC = ed.GetDouble(pCov);
            if (resC.Status == PromptStatus.OK)
            {
                settings.CoverTop = resC.Value;
                settings.CoverBot = resC.Value;
            }

            // 3. Longitudinal Diameters
            var pStr = new PromptStringOptions($"\nNhập đường kính thép dọc (phân cách space, hiện tại: {string.Join(" ", settings.PreferredDiameters)}): ");
            pStr.AllowSpaces = true;
            var resS = ed.GetString(pStr);
            if (resS.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(resS.StringResult))
            {
                var nums = resS.StringResult.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s, out int v) ? v : 0)
                    .Where(v => v > 0).ToList();
                if (nums.Count > 0) settings.PreferredDiameters = nums;
            }

            // 4. Stirrup Diameter
            var pStir = new PromptIntegerOptions($"\nNhập đường kính Đai (mm) (Hiện tại: {settings.StirrupDiameter}): ");
            pStir.AllowNone = true;
            var resStir = ed.GetInteger(pStir);
            if (resStir.Status == PromptStatus.OK) settings.StirrupDiameter = resStir.Value;

            // 5. Stirrup Legs
            var pLegs = new PromptIntegerOptions($"\nNhập số nhánh Đai (Hiện tại: {settings.StirrupLegs}): ");
            pLegs.AllowNone = true;
            var resLegs = ed.GetInteger(pLegs);
            if (resLegs.Status == PromptStatus.OK) settings.StirrupLegs = resLegs.Value;

            // 6. Web Bar Diameter
            var pWeb = new PromptIntegerOptions($"\nNhập đường kính thép Sườn (mm) (Hiện tại: {settings.WebBarDiameter}): ");
            pWeb.AllowNone = true;
            var resWeb = ed.GetInteger(pWeb);
            if (resWeb.Status == PromptStatus.OK) settings.WebBarDiameter = resWeb.Value;

            WriteMessage("\nĐã cập nhật cài đặt tính toán.");
        }

        private bool IsSamePt(Core.Primitives.Point2D p2d, Point3d p3d, double tol = 200.0)
        {
            return Math.Abs(p2d.X - p3d.X) < tol && Math.Abs(p2d.Y - p3d.Y) < tol;
        }

        [CommandMethod("DTS_REBAR_BEAM_NAME")]
        public void DTS_REBAR_BEAM_NAME()
        {
            WriteMessage("=== REBAR: ĐẶT TÊN DẦM TỰ ĐỘNG ===");

            WriteMessage("=== REBAR: ĐẶT TÊN DẦM TỰ ĐỘNG ===");
            WriteMessage("\nChọn các đường Dầm cần đặt tên: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            // Lấy thông tin lưới trục từ bản vẽ
            // Đơn giản hóa: Lấy tất cả các đường trên layer "dts_grid" hoặc "GRID"
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
                        {
                            gridLines.Add(crv);
                        }
                    }
                }

                // Tìm các giao điểm lưới
                for (int i = 0; i < gridLines.Count; i++)
                {
                    for (int j = i + 1; j < gridLines.Count; j++)
                    {
                        var pts = new Point3dCollection();
                        // Dùng ExtendBoth để phòng đường Grid vẽ chưa chạm nhau
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

            // Phân loại Girder (trên lưới) / Beam (ngoài lưới)
            // Sort theo Y rồi X
            int girderCount = 1;
            int beamCount = 1;
            int currentStory = 1; // Có thể lấy từ Layer hoặc User Input

            UsingTransaction(tr =>
            {
                // Sort beams by Y then X (based on midpoint)
                var beamsData = new List<(ObjectId Id, Point3d Mid, bool IsGirder)>();

                foreach (ObjectId id in selectedIds)
                {
                    var curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (curve == null) continue;

                    Point3d mid = curve.StartPoint + (curve.EndPoint - curve.StartPoint) * 0.5;

                    // Check if Start or End is on Grid Intersection
                    bool onGridStart = gridIntersections.Any(g => g.DistanceTo(curve.StartPoint) < 200);
                    bool onGridEnd = gridIntersections.Any(g => g.DistanceTo(curve.EndPoint) < 200);

                    bool isGirder = onGridStart && onGridEnd; // Both ends on grid -> Girder

                    beamsData.Add((id, mid, isGirder));
                }

                // Sort: Girders first, then by Y (descending = từ trên xuống), then X
                var sortedBeams = beamsData
                    .OrderByDescending(b => b.IsGirder)
                    .ThenByDescending(b => Math.Round(b.Mid.Y / 500) * 500) // Round to 500mm grid for grouping
                    .ThenBy(b => b.Mid.X)
                    .ToList();

                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (var beam in sortedBeams)
                {
                    var curve = tr.GetObject(beam.Id, OpenMode.ForWrite) as Curve;
                    if (curve == null) continue;

                    string prefix = beam.IsGirder ? "G" : "B";
                    int number = beam.IsGirder ? girderCount++ : beamCount++;

                    string beamName = $"{currentStory}{prefix}{number}";

                    // Plot Name Label at Mid
                    Point3d pStart = curve.StartPoint;
                    Point3d pEnd = curve.EndPoint;
                    LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, beamName, LabelPosition.MiddleBottom);

                    // Store Name in XData? 
                    // Update existing BeamResultData or write new?
                    // For now, just plot label. Phase 4 will use label text or XData.
                }
            });

            WriteSuccess($"Đã đặt tên cho {selectedIds.Count} dầm ({girderCount - 1} Girder, {beamCount - 1} Beam).");
        }

        [CommandMethod("DTS_REBAR_UPDATE")]
        public void DTS_REBAR_UPDATE()
        {
            WriteMessage("=== REBAR: CẬP NHẬT THÉP VỀ SAP2000 ===");

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

                    // Check if calculation was done (TopAreaProv should be populated)
                    if (data.TopAreaProv == null || data.TopAreaProv[0] <= 0)
                    {
                        // Re-calculate if needed
                        for (int i = 0; i < 3; i++)
                        {
                            double asTop = data.TopArea[i] + data.TorsionArea[i] * settings.TorsionRatioTop;
                            double asBot = data.BotArea[i] + data.TorsionArea[i] * settings.TorsionRatioBot;

                            string sTop = RebarCalculator.Calculate(asTop, data.Width * 10, data.SectionHeight * 10, settings);
                            string sBot = RebarCalculator.Calculate(asBot, data.Width * 10, data.SectionHeight * 10, settings);

                            data.TopAreaProv[i] = RebarStringParser.Parse(sTop);
                            data.BotAreaProv[i] = RebarStringParser.Parse(sBot);
                        }
                    }

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
                        successCount++;
                    else
                        failCount++;
                }
            });

            if (failCount > 0)
                WriteMessage($"Cảnh báo: {failCount} dầm không thể cập nhật.");

            WriteSuccess($"Đã cập nhật {successCount} dầm về SAP2000.");
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
                                    topText = $"{FormatArea(asProvTop)}/{FormatArea(asReqTop)}\\P{topRebar}";
                                    botText = $"{FormatArea(asProvBot)}/{FormatArea(asReqBot)}\\P{botRebar}";
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
                                    topText = $"{FormatArea(stirrupProv)}/{FormatArea(stirrupReq)}({FormatArea(2 * ats)})\\P{stirrupStr}";

                                    // Bot: Web - Aprov/Areq (Areq = TorsionArea × SideRatio)
                                    double webReq = (data.TorsionArea?[i] ?? 0) * settings.TorsionRatioSide;
                                    string webStr = data.WebBarString?[i] ?? "-";
                                    // Parse Aprov từ web string (e.g., "2d12")
                                    double webProv = RebarCalculator.ParseRebarArea(webStr);
                                    botText = $"{FormatArea(webProv)}/{FormatArea(webReq)}\\P{webStr}";
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
    }
}
