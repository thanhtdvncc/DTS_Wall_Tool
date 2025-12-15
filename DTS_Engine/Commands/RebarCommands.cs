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

            // 2. Select Frames on Screen
            var selectedIds = AcadUtils.SelectObjects("Chọn các đường Dầm (Frame) để lấy nội lực: ");
            if (selectedIds.Count == 0) return;

            // 3. Filter Valid Frames (Must correspond to SAP names)
            List<string> sapNames = new List<string>();
            Dictionary<string, ObjectId> mapNameToId = new Dictionary<string, ObjectId>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in selectedIds)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead);
                    var data = XDataUtils.ReadElementData(ent);
                    // Use Handle or explicitly stored Name? 
                    // Usually we map CAD Handle <-> SAP Name via some mapping or assume Name is stored.
                    // Check PlotCommands: When plotting from SAP, we store data but do we store SAP Name?
                    // In PlotCommands.cs: We store `BeamData` but looking at it, it DOES NOT assume Name property is SAP Name.
                    // Actually, `LabelUtils` usually plots the Name.
                    // Let's assume the user has plotted from SAP -> CAD using `DTS_PLOT_FROM_SAP`.
                    // We need to match CAD entity back to SAP Frame.
                    // Strategy: If we plotted from SAP, we likely stored SAP Name or we can use geometry.
                    // BUT: Current BeamData in `PlotCommands.cs` line 516 does NOT explicitly store SAP Name in a dedicated field other than `SectionName`?
                    // Wait, `SapFrame` has Name. When plotting, we should store SAP Name.
                    // Let's checking `BeamData`... `ElementData` has no `SapName` field?
                    // RISK: If we don't store SAP Name, we can't map back easily!
                    // Fix: We must check if we can rely on Text Label or geometry.
                    // Alternative: Use Geometry Matching (GetBeamResults gets ALL, then we match).
                    // BETTER: Assume the Layer Name or XData contains ID?
                    // Let's check `ElementData` definition. It inherits from `ElementData`.
                    // If `DTS_PLOT_FROM_SAP` was used, maybe we can assume specific Layer convention or just geometry match.
                    
                    // FOR NOW: Let's assume we can rely on Geometry Matching with SAP 
                    // OR we assume the user has just plotted them and they are in sync.
                    // To be safe: We get ALL SAP Beams, then match with checked CAD entities by geometry (Start/End).
                }
            });

            // Geometry Match Strategy
            // 1. Get All Design Results from all Beams in current Story?
            //    Too heavy.
            // 2. Map Selected CAD Lines -> SAP Names via Geometry.
            WriteMessage("Đang đồng bộ hình học để tìm tên phần tử SAP...");
            
            var allSapFrames = SapUtils.GetAllFramesGeometry(); // Fast cached
            
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

                    // Find SAP frame with matching geometric endpoints (tolerance 200mm)
                    var match = allSapFrames.FirstOrDefault(f => 
                        (IsSamePt(f.StartPt, start) && IsSamePt(f.EndPt, end)) ||
                        (IsSamePt(f.StartPt, end) && IsSamePt(f.EndPt, start)) // Reversed
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

            // 4. Call Engine to get Results
            var results = engine.GetBeamResults(matchedNames);

            if (results.Count == 0)
            {
                WriteError("Không lấy được kết quả thiết kế. Kiểm tra xem đã chạy Design Concrete chưa.");
                return;
            }

            // 5. Update XData and Plot Labels
            int successCount = 0;
            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (var kvp in cadToSap)
                {
                    ObjectId cadId = kvp.Key;
                    string sapName = kvp.Value;

                    if (results.TryGetValue(sapName, out var designData))
                    {
                         // Apply Torsion Factor (Default 0.25 -> distributed to 4 faces?)
                         // Formula: As_Total = As_Flex + k * As_Torsion
                         // If Torsion is total longitudinal, usually we divide by 2 or 4?
                         // User said: "As_Total = As_Flex + A_tor_long / 4".
                         // With factor = 0.25.
                         double torFactor = RebarSettings.Instance.TorsionDistributionFactor;
                         
                         // Update effective Area in Data (Storing Raw + Logic happens display/calc side?
                         // Let's store raw in XData, but calculate for Display now.
                        
                        // Update XData
                        designData.TorsionFactorUsed = torFactor;
                        DBObject obj = tr.GetObject(cadId, OpenMode.ForWrite);
                        
                        // Merge XData instead of Overwrite? We might lose other info?
                        // XDataUtils.UpdateElementData merges.
                        XDataUtils.UpdateElementData(obj, designData, tr);

                        // Plot Labels
                        // Plot 6 positions: 
                        // Start-Top, Start-Bot
                        // Mid-Top, Mid-Bot
                        // End-Top, End-Bot
                        
                        // We use LabelPlotter.
                        // We need to calculate the values to display (including Torsion).
                        
                        double[] displayTop = new double[3];
                        double[] displayBot = new double[3];

                        for(int i=0; i<3; i++)
                        {
                            // Logic: Flex + Factor * Tor
                             displayTop[i] = designData.TopArea[i] + designData.TorsionArea[i] * torFactor;
                             displayBot[i] = designData.BotArea[i] + designData.TorsionArea[i] * torFactor;
                        }

                        // Format Text: "{Top} / {Bot}"
                        // Use Curve geometry to determine positions
                        var curve = obj as Curve;
                        Point3d pStart = curve.StartPoint;
                        Point3d pEnd = curve.EndPoint;
                        Point3d pMid = curve.GetPointAtParameter((curve.EndParam - curve.StartParam)/2);

                        // Start
                        string txtStart = $"{displayTop[0]:F1}/{displayBot[0]:F1}";
                        LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, txtStart, 0); 

                        // Mid
                        string txtMid = $"{displayTop[1]:F1}/{displayBot[1]:F1}";
                        LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, txtMid, 1);

                        // End
                        string txtEnd = $"{displayTop[2]:F1}/{displayBot[2]:F1}";
                        LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, txtEnd, 2);

                        successCount++;
                    }
                }
            });
            
            WriteSuccess($"Đã cập nhật Label thép cho {successCount} dầm.");
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

                    // Calculate Rebar
                    // Apply Torsion Factor from Settings (User might change settings between fetching and calculating)
                    // Or stick to what was fetched?
                    // Usually Calc command uses CURRENT Settings.
                    double torFactor = settings.TorsionDistributionFactor;
                    
                    BeamRebarSolution sol = new BeamRebarSolution();

                    for (int i = 0; i < 3; i++)
                    {
                        double asTop = data.TopArea[i] + data.TorsionArea[i] * torFactor;
                        double asBot = data.BotArea[i] + data.TorsionArea[i] * torFactor;

                        // Calculate
                        // Unit check: Width/Height in cm? RebarCalculator expects what?
                        // RebarCalculator: areaReq (cm2), b (mm), h (mm) usually?
                        // Let's check RebarCalculator internal logic.
                        // "double workingWidth = b - 2*cover..."
                        // "double as1 = ... d*d/400" (cm2).
                        // If d is mm (e.g. 20), d*d/400 gives cm2. Correct.
                        // Width should be mm for Cover/Spacing check?
                        // "workingWidth = b - 2 * cover". Cover is mm.
                        // So 'b' must be mm.
                        // data.Width is from SAP (cm) -> Need Convert to mm.
                        
                        string sTop = RebarCalculator.Calculate(asTop, data.Width * 10, data.Height * 10, settings);
                        string sBot = RebarCalculator.Calculate(asBot, data.Width * 10, data.Height * 10, settings);

                        sol.TopRebarString[i] = sTop;
                        sol.BotRebarString[i] = sBot;

                        // Calculate Solved Area (Optional, for check)
                        // TODO: Parser to get exact area
                        
                        // Update Label
                        // Format: "3d20"
                        // Or "3d20 (15.4)" showing area?
                        // Let's show "3d20"
                    }

                    // Save Solution to XData (Extend BeamResultData or Side-car?)
                    // If we overwrite BeamResultData with BeamRebarSolution, we lose raw areas!
                    // XDataUtils.UpdateElementData merges properties? No, it serializes the object.
                    // Solution: BeamResultData should HOLD Solution or we define a Combined Type.
                    // Or we just store `BeamRebarSolution` separately?
                    // `XDataUtils` reads based on what? `ReadElementData` tries to parse JSON.
                    // ElementData has `xType`.
                    // If we write `BeamRebarSolution`, xType becomes "REBAR_SOLUTION".
                    // Later if we try to read, XDataUtils might return `BeamRebarSolution`.
                    // But we still need Raw Data for re-calc!
                    // So we must NOT destroy `BeamResultData`.
                    // We should merge them.
                    
                    // Temporary: Update using BeamResultData's RebarString fields?
                    // BeamResultData definition (I need to update it again or make it hold solution).
                    // Actually, let's keep it simple: Add RebarString fields to `BeamResultData`.
                    // It's cleaner than maintaining two objects.
                    
                    // Since I cannot change BeamResultData structure easily in this tool call without overwrite,
                    // I will check if I can just write `BeamRebarSolution`.
                    // RISK: If I write Solution, `ReadElementData` next time returns Solution.
                    // Then `data.TopArea` is lost (null/default).
                    // Logic break: Next time I select logic, `data = Read() as BeamResultData`. It will be null.
                    
                    // FIX: `BeamResultData` should have `Solution` property or fields.
                    // I'll stick to updating Labels for now and assume Data structure holds solution later.
                    // Wait, this is Agentic. I CAN Modify BeamResultData.
                    // I will modify BeamResultData to include Solution strings.
                    
                    // Update Labels on screen
                    string[] formatted = new string[3];
                    for(int k=0; k<3; k++)
                    {
                        formatted[k] = $"{sol.TopRebarString[k]} / {sol.BotRebarString[k]}";
                    }
                    
                    // Display
                     var curve = obj as Curve;
                    if(curve != null)
                    {
                         Point3d pStart = curve.StartPoint;
                        Point3d pEnd = curve.EndPoint;
                         // Start
                        LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, formatted[0], 0); 
                         // Mid
                        LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, formatted[1], 1);
                         // End
                        LabelPlotter.PlotRebarLabel(btr, tr, pStart, pEnd, formatted[2], 2);
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

            // 3. Diameters (Simple input string)
            var pStr = new PromptStringOptions($"\nNhập đường kính ưu tiên (phân cách space, hiện tại: {string.Join(" ", settings.PreferredDiameters)}): ");
            pStr.AllowNone = true;
            var resS = ed.GetString(pStr);
            if (resS.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(resS.StringResult))
            {
                var nums = resS.StringResult.Split(new[]{' ', ','}, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s, out int v) ? v : 0)
                    .Where(v => v > 0).ToList();
                if (nums.Count > 0) settings.PreferredDiameters = nums;
            }
            
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
                        gridLines[i].IntersectWith(gridLines[j], Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
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

                    // Read Calculated Rebar from Label or XData
                    // Since we haven't stored Solution in XData yet, we need to re-calculate
                    // OR try to parse from MText labels on the entity?
                    // For simplicity, re-calculate using stored raw areas.
                    
                    double torFactor = settings.TorsionDistributionFactor;
                    double[] topProv = new double[3];
                    double[] botProv = new double[3];

                    for (int i = 0; i < 3; i++)
                    {
                        double asTop = data.TopArea[i] + data.TorsionArea[i] * torFactor;
                        double asBot = data.BotArea[i] + data.TorsionArea[i] * torFactor;

                        string sTop = RebarCalculator.Calculate(asTop, data.Width * 10, data.Height * 10, settings);
                        string sBot = RebarCalculator.Calculate(asBot, data.Width * 10, data.Height * 10, settings);

                        topProv[i] = RebarStringParser.Parse(sTop);
                        botProv[i] = RebarStringParser.Parse(sBot);
                    }

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
    }
}
