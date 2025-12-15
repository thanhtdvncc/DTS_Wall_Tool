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
            var ed = AcadUtils.Editor;
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
            var selectedIds = AcadUtils.SelectObjects("Chọn các đường Dầm (Frame) để lấy nội lực: ");
            if (selectedIds.Count == 0) return;

            // 4. Clear old rebar labels on layer "dts_rebar_text"
            WriteMessage("Đang xóa label cũ...");
            ClearRebarLabels();

            // 5. Geometry Match Strategy
            WriteMessage("Đang đồng bộ hình học để tìm tên phần tử SAP...");
            
            var allSapFrames = SapUtils.GetAllFramesGeometry();
            
            List<string> matchedNames = new List<string>();
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
                        matchedNames.Add(match.Name);
                        cadToSap[id] = match.Name;
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
            double torFactor = RebarSettings.Instance.TorsionDistributionFactor;

            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (var kvp in cadToSap)
                {
                    ObjectId cadId = kvp.Key;
                    string sapName = kvp.Value;

                    if (results.TryGetValue(sapName, out var designData))
                    {
                        designData.TorsionFactorUsed = torFactor;
                        DBObject obj = tr.GetObject(cadId, OpenMode.ForWrite);
                        XDataUtils.UpdateElementData(obj, designData, tr);

                        // Calculate display values based on mode
                        var settings = RebarSettings.Instance;
                        double[] displayTop = new double[3];
                        double[] displayBot = new double[3];
                        string[] displayTopStr = new string[3];
                        string[] displayBotStr = new string[3];

                        for(int i=0; i<3; i++)
                        {
                            switch (displayMode)
                            {
                                case 0: // Combined (Flex + Torsion)
                                    displayTop[i] = designData.TopArea[i] + designData.TorsionArea[i] * torFactor;
                                    displayBot[i] = designData.BotArea[i] + designData.TorsionArea[i] * torFactor;
                                    displayTopStr[i] = $"{displayTop[i]:F1}";
                                    displayBotStr[i] = $"{displayBot[i]:F1}";
                                    break;
                                case 1: // Flex only (Thép dọc)
                                    displayTopStr[i] = $"{designData.TopArea[i]:F1}";
                                    displayBotStr[i] = $"{designData.BotArea[i]:F1}";
                                    break;
                                case 2: // Torsion only (Thép xoắn)
                                    displayTopStr[i] = $"{designData.TorsionArea[i]:F2}";
                                    displayBotStr[i] = $"{designData.TorsionArea[i]:F2}";
                                    break;
                                case 3: // Stirrup/Web (Thép đai/Sườn) - Tính tạm từ Raw data
                                    // Top: Thép đai
                                    displayTopStr[i] = RebarCalculator.CalculateStirrup(designData.ShearArea[i], settings);
                                    // Bot: Thép sườn
                                    double sideTor = designData.TorsionArea[i] * (1 - 2 * torFactor) / 2.0;
                                    displayBotStr[i] = RebarCalculator.CalculateWebBars(sideTor, designData.Height * 10, settings);
                                    break;
                            }
                        }

                        // Plot Labels - 6 positions (Start/Mid/End x Top/Bot)
                        var curve = obj as Curve;
                        Point3d pStart = curve.StartPoint;
                        Point3d pEnd = curve.EndPoint;

                        for (int i = 0; i < 3; i++)
                        {
                            LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, displayTopStr[i], i, true);
                            LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, displayBotStr[i], i, false);
                        }

                        successCount++;
                    }
                }
            });
            
            string[] modeNames = { "Tổng hợp", "Thép dọc", "Thép xoắn", "Thép Đai/Sườn" };
            WriteSuccess($"Đã cập nhật Label thép ({modeNames[displayMode]}) cho {successCount} dầm.");
        }

        /// <summary>
        /// Xóa tất cả label rebar trên layer "dts_rebar_text"
        /// </summary>
        private void ClearRebarLabels()
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
                        ent.UpgradeOpen();
                        ent.Erase();
                    }
                }
            });
        }


        [CommandMethod("DTS_REBAR_CALCULATE")]
        public void DTS_REBAR_CALCULATE()
        {
            WriteMessage("=== REBAR: TÍNH TOÁN CỐT THÉP ===");

            var selectedIds = AcadUtils.SelectObjects("Chọn các đường Dầm cần tính thép: ");
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
                    if (data.Width <= 0 || data.Height <= 0)
                    {
                        // Try fallback to defaults or user data?
                        // For safe fail, skip or assume 20x30
                        data.Width = 22; // Default fallback
                        data.Height = 30;
                    }

                    // Calculate Rebar and update directly into data object
                    double torFactor = settings.TorsionDistributionFactor;

                    for (int i = 0; i < 3; i++)
                    {
                        // === Longitudinal Rebar ===
                        double asTop = data.TopArea[i] + data.TorsionArea[i] * torFactor;
                        double asBot = data.BotArea[i] + data.TorsionArea[i] * torFactor;

                        string sTop = RebarCalculator.Calculate(asTop, data.Width * 10, data.Height * 10, settings);
                        string sBot = RebarCalculator.Calculate(asBot, data.Width * 10, data.Height * 10, settings);

                        data.TopRebarString[i] = sTop;
                        data.BotRebarString[i] = sBot;
                        data.TopAreaProv[i] = RebarStringParser.Parse(sTop);
                        data.BotAreaProv[i] = RebarStringParser.Parse(sBot);

                        // === Stirrup (Thép đai) ===
                        string sStirrup = RebarCalculator.CalculateStirrup(data.ShearArea[i], settings);
                        data.StirrupString[i] = sStirrup;

                        // === Web Bars (Thép sườn) ===
                        // Torsion phần dư sau khi đã phân bổ cho Top/Bot = (1 - 2*torFactor) * TorsionArea
                        double torsionSide = data.TorsionArea[i] * (1 - 2 * torFactor) / 2.0; // Chia 2 bên
                        string sWeb = RebarCalculator.CalculateWebBars(torsionSide, data.Height * 10, settings);
                        data.WebBarString[i] = sWeb;
                    }

                    // Save updated data back to XData (preserves raw areas)
                    XDataUtils.UpdateElementData(obj, data, tr);

                    // Update Labels on screen
                    // Format: Top line = Longitudinal + Stirrup, Bot line = Longitudinal + WebBar
                    var curve = obj as Curve;
                    if(curve != null)
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

                            LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, topText, i, true);
                            LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, botText, i, false);
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
            var ed = AcadUtils.Editor;
            var settings = RebarSettings.Instance;

            // Simple Prompt UI
            // 1. Torsion Factor
            var pOpt = new PromptDoubleOptions($"\nNhập hệ số phân bổ xoắn (Hiện tại: {settings.TorsionDistributionFactor}): ");
            pOpt.AllowNone = true;
            var res = ed.GetDouble(pOpt);
            if (res.Status == PromptStatus.OK) settings.TorsionDistributionFactor = res.Value;

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
            pStr.AllowNone = true;
            var resS = ed.GetString(pStr);
            if (resS.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(resS.StringResult))
            {
                var nums = resS.StringResult.Split(new[]{' ', ','}, StringSplitOptions.RemoveEmptyEntries)
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

        private bool IsSamePt(SapUtils.Point2D p2d, Point3d p3d, double tol = 200.0)
        {
            return Math.Abs(p2d.X - p3d.X) < tol && Math.Abs(p2d.Y - p3d.Y) < tol;
        }

        [CommandMethod("DTS_REBAR_BEAM_NAME")]
        public void DTS_REBAR_BEAM_NAME()
        {
            WriteMessage("=== REBAR: ĐẶT TÊN DẦM TỰ ĐỘNG ===");

            var selectedIds = AcadUtils.SelectObjects("Chọn các đường Dầm cần đặt tên: ");
            if (selectedIds.Count == 0) return;

            // Lấy thông tin lưới trục từ bản vẽ
            // Đơn giản hóa: Lấy tất cả các đường trên layer "dts_grid" hoặc "GRID"
            List<Point3d> gridIntersections = new List<Point3d>();
            List<Curve> gridLines = new List<Curve>();

            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                foreach(ObjectId id in btr)
                {
                    var obj = tr.GetObject(id, OpenMode.ForRead);
                    if(obj is Curve crv)
                    {
                        string layer = (obj as Entity)?.Layer ?? "";
                        if(layer.ToUpper().Contains("GRID") || layer.ToUpper().Contains("AXIS"))
                        {
                            gridLines.Add(crv);
                        }
                    }
                }

                // Tìm các giao điểm lưới
                for(int i=0; i<gridLines.Count; i++)
                {
                    for(int j=i+1; j<gridLines.Count; j++)
                    {
                        var pts = new Point3dCollection();
                        // Dùng ExtendBoth để phòng đường Grid vẽ chưa chạm nhau
                        gridLines[i].IntersectWith(gridLines[j], Intersect.ExtendBoth, pts, IntPtr.Zero, IntPtr.Zero);
                        foreach(Point3d p in pts)
                        {
                            if(!gridIntersections.Any(x => x.DistanceTo(p) < 100))
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

                foreach(ObjectId id in selectedIds)
                {
                    var curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if(curve == null) continue;

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

                foreach(var beam in sortedBeams)
                {
                    var curve = tr.GetObject(beam.Id, OpenMode.ForWrite) as Curve;
                    if(curve == null) continue;

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

            WriteSuccess($"Đã đặt tên cho {selectedIds.Count} dầm ({girderCount-1} Girder, {beamCount-1} Beam).");
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
            var selectedIds = AcadUtils.SelectObjects("Chọn các đường Dầm cần cập nhật về SAP: ");
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
                        double torFactor = settings.TorsionDistributionFactor;
                        for (int i = 0; i < 3; i++)
                        {
                            double asTop = data.TopArea[i] + data.TorsionArea[i] * torFactor;
                            double asBot = data.BotArea[i] + data.TorsionArea[i] * torFactor;

                            string sTop = RebarCalculator.Calculate(asTop, data.Width * 10, data.Height * 10, settings);
                            string sBot = RebarCalculator.Calculate(asBot, data.Width * 10, data.Height * 10, settings);

                            data.TopAreaProv[i] = RebarStringParser.Parse(sTop);
                            data.BotAreaProv[i] = RebarStringParser.Parse(sBot);
                        }
                    }

                    // Use calculated values from data
                    double[] topProv = data.TopAreaProv;
                    double[] botProv = data.BotAreaProv;

                    // Create Section Name based on convention
                    // Format: [SapName]_[WxH]_[Top0]_[Top2]_[Bot0]_[Bot2]
                    string newSectionName = $"{sapName}_{(int)data.Width}x{(int)data.Height}_{(int)topProv[0]}_{(int)topProv[2]}_{(int)botProv[0]}_{(int)botProv[2]}";
                    
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

            // Chọn chế độ hiển thị
            var ed = AcadUtils.Editor;
            var pIntOpt = new PromptIntegerOptions("\nChọn chế độ hiển thị [0=Diện tích | 1=Bố trí thép | 2=Cả hai | 3=Thép Đai/Sườn]: ");
            pIntOpt.AllowNone = true;
            pIntOpt.DefaultValue = 1;
            pIntOpt.AllowNegative = false;
            pIntOpt.LowerLimit = 0;
            pIntOpt.UpperLimit = 3;

            var pIntRes = ed.GetInteger(pIntOpt);
            int mode = 1; // Default = Rebar
            if (pIntRes.Status == PromptStatus.OK)
                mode = pIntRes.Value;
            else if (pIntRes.Status != PromptStatus.None)
                return;

            // Select Frames
            var selectedIds = AcadUtils.SelectObjects("Chọn các đường Dầm cần hiển thị: ");
            if (selectedIds.Count == 0) return;

            // Clear existing labels
            ClearRebarLabels();

            int count = 0;
            var settings = RebarSettings.Instance;
            double torFactor = settings.TorsionDistributionFactor;

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
                            case 0: // Area (Diện tích tổng hợp)
                                double asTop = data.TopArea[i] + data.TorsionArea[i] * torFactor;
                                double asBot = data.BotArea[i] + data.TorsionArea[i] * torFactor;
                                topText = $"{asTop:F1}";
                                botText = $"{asBot:F1}";
                                break;

                            case 1: // Rebar (Bố trí thép dọc) - Dùng \P xuống dòng
                                topText = data.TopRebarString[i] ?? "-";
                                if (!string.IsNullOrEmpty(data.StirrupString[i]) && data.StirrupString[i] != "-")
                                    topText += "\\P" + data.StirrupString[i];

                                botText = data.BotRebarString[i] ?? "-";
                                if (!string.IsNullOrEmpty(data.WebBarString[i]) && data.WebBarString[i] != "-")
                                    botText += "\\P" + data.WebBarString[i];
                                break;

                            case 2: // Both (Cả hai) - Dùng \P xuống dòng
                                double asTopB = data.TopArea[i] + data.TorsionArea[i] * torFactor;
                                double asBotB = data.BotArea[i] + data.TorsionArea[i] * torFactor;
                                string topRebar = data.TopRebarString[i] ?? "-";
                                string botRebar = data.BotRebarString[i] ?? "-";
                                topText = $"{asTopB:F1}\\P{topRebar}";
                                botText = $"{asBotB:F1}\\P{botRebar}";
                                break;

                            case 3: // Stirrup/Web Only (Thép Đai/Sườn độc lập)
                                // Top: Chỉ hiện Thép đai
                                if (!string.IsNullOrEmpty(data.StirrupString[i]) && data.StirrupString[i] != "-")
                                    topText = data.StirrupString[i];
                                else
                                    topText = RebarCalculator.CalculateStirrup(data.ShearArea[i], settings);

                                // Bot: Chỉ hiện Thép sườn
                                if (!string.IsNullOrEmpty(data.WebBarString[i]) && data.WebBarString[i] != "-")
                                    botText = data.WebBarString[i];
                                else
                                {
                                    double sideTor = data.TorsionArea[i] * (1 - 2 * torFactor) / 2.0;
                                    botText = RebarCalculator.CalculateWebBars(sideTor, data.Height * 10, settings);
                                }
                                break;
                        }

                        LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, topText, i, true);
                        LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, botText, i, false);
                    }

                    count++;
                }
            });

            string[] modeNames = { "Diện tích", "Bố trí thép", "Cả hai", "Thép Đai/Sườn" };
            WriteSuccess($"Đã hiển thị {count} dầm theo chế độ: {modeNames[mode]}.");
        }
    }
}
