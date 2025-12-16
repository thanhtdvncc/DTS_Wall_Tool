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
        }


        private bool IsSamePt(Core.Primitives.Point2D p2d, Point3d p3d, double tol = 200.0)
        {
            return Math.Abs(p2d.X - p3d.X) < tol && Math.Abs(p2d.Y - p3d.Y) < tol;
        }

        [CommandMethod("DTS_REBAR_BEAM_NAME")]
        public void DTS_REBAR_BEAM_NAME()
        {
            WriteMessage("=== SMART BEAM NAMING ===");
            WriteMessage("\nChọn các đường Dầm cần đặt tên: ");
            var selectedIds = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE");
            if (selectedIds.Count == 0) return;

            var settings = RebarSettings.Instance;

            // Lấy thông tin lưới trục 
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

            // Thu thập dữ liệu dầm
            var beamsData = new List<(ObjectId Id, Point3d Mid, bool IsGirder, bool IsXDir, string GroupKey,
                                      double Width, double Height, string TopRebar, string BotRebar, string Stirrup)>();

            UsingTransaction(tr =>
            {
                foreach (ObjectId id in selectedIds)
                {
                    var curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                    if (curve == null) continue;

                    Point3d mid = curve.StartPoint + (curve.EndPoint - curve.StartPoint) * 0.5;
                    Vector3d dir = curve.EndPoint - curve.StartPoint;
                    bool isXDir = Math.Abs(dir.X) > Math.Abs(dir.Y);

                    bool onGridStart = gridIntersections.Any(g => g.DistanceTo(curve.StartPoint) < 200);
                    bool onGridEnd = gridIntersections.Any(g => g.DistanceTo(curve.EndPoint) < 200);
                    bool isGirder = onGridStart && onGridEnd;

                    // Đọc XData để lấy rebar strings
                    var xdata = XDataUtils.ReadElementData(curve) as BeamResultData;
                    string topRebar = "-", botRebar = "-", stirrup = "-";
                    double width = 0, height = 0;

                    if (xdata != null)
                    {
                        topRebar = (xdata.TopRebarString != null && xdata.TopRebarString.Length > 1) ? xdata.TopRebarString[1] ?? "-" : "-";
                        botRebar = (xdata.BotRebarString != null && xdata.BotRebarString.Length > 1) ? xdata.BotRebarString[1] ?? "-" : "-";
                        stirrup = (xdata.StirrupString != null && xdata.StirrupString.Length > 1) ? xdata.StirrupString[1] ?? "-" : "-";
                        width = xdata.Width;
                        height = xdata.SectionHeight;
                    }

                    // GroupKey = [IsGirder]_[Dir]_[WxH]_[Top]_[Bot]_[Stirrup]
                    string groupKey = $"{(isGirder ? "G" : "B")}_{(isXDir ? "X" : "Y")}_{width:F0}x{height:F0}_{topRebar}_{botRebar}_{stirrup}";

                    beamsData.Add((id, mid, isGirder, isXDir, groupKey, width, height, topRebar, botRebar, stirrup));
                }
            });

            // Gom nhóm theo GroupKey
            var groups = beamsData.GroupBy(b => b.GroupKey).ToList();
            WriteMessage($"Gom được {groups.Count} nhóm dầm.");

            // Xác định "dầm đại diện" cho mỗi nhóm (theo góc bắt đầu)
            // SortCorner: 0=TL, 1=TR, 2=BL, 3=BR
            // SortDirection: 0=Horizontal (X first), 1=Vertical (Y first)
            bool sortYDesc = settings.SortCorner <= 1; // Top = Y lớn trước
            bool sortXDesc = settings.SortCorner == 1 || settings.SortCorner == 3; // Right = X lớn trước
            bool priorityX = settings.SortDirection == 0;

            Func<Point3d, Point3d, int> comparePoints = (a, b) =>
            {
                double tolerance = 500; // tolerance để nhóm thành hàng/cột

                if (priorityX)
                {
                    // Horizontal: so sánh Y trước (để phân hàng), nếu cùng hàng thì so sánh X
                    double yA = Math.Round(a.Y / tolerance);
                    double yB = Math.Round(b.Y / tolerance);
                    if (Math.Abs(yA - yB) > 0.1)
                        return sortYDesc ? -yA.CompareTo(yB) : yA.CompareTo(yB);
                    return sortXDesc ? -a.X.CompareTo(b.X) : a.X.CompareTo(b.X);
                }
                else
                {
                    // Vertical: so sánh X trước (để phân cột), nếu cùng cột thì so sánh Y
                    double xA = Math.Round(a.X / tolerance);
                    double xB = Math.Round(b.X / tolerance);
                    if (Math.Abs(xA - xB) > 0.1)
                        return sortXDesc ? -xA.CompareTo(xB) : xA.CompareTo(xB);
                    return sortYDesc ? -a.Y.CompareTo(b.Y) : a.Y.CompareTo(b.Y);
                }
            };

            // Sắp xếp nhóm theo dầm đại diện (dầm có tọa độ ưu tiên nhất trong nhóm)
            var sortedGroups = groups
                .Select(g => new
                {
                    Group = g,
                    Representative = g.OrderBy(b => 0, Comparer<int>.Create((_, __) => 0))
                                      .First() // Tìm dầm đứng "đầu tiên" theo thứ tự sort
                })
                .OrderBy(x => x.Group.First().IsGirder ? 0 : 1) // Girder trước Beam
                .ThenBy(x => 0, Comparer<int>.Create((_, __) => 0))
                .ToList();

            // Re-sort groups properly using the representative
            sortedGroups = groups
                .Select(g =>
                {
                    var sorted = g.ToList();
                    sorted.Sort((a, b) => comparePoints(a.Mid, b.Mid));
                    return new { Group = g, Rep = sorted.First().Mid, IsGirder = g.First().IsGirder };
                })
                .OrderBy(x => x.IsGirder ? 0 : 1)
                .ThenBy(x => x.Rep, Comparer<Point3d>.Create((a, b) => comparePoints(a, b)))
                .Select(x => new { Group = x.Group, Representative = x.Group.First() })
                .ToList();

            // Đặt tên
            int girderCount = 1, beamCount = 1;
            string girderPrefix = settings.GirderPrefix ?? "G";
            string beamPrefix = settings.BeamPrefix ?? "B";
            string suffix = settings.BeamSuffix ?? "";

            UsingTransaction(tr =>
            {
                var btr = tr.GetObject(AcadUtils.Db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;

                foreach (var group in sortedGroups)
                {
                    bool isGirder = group.Group.First().IsGirder;
                    string prefix = isGirder ? girderPrefix : beamPrefix;
                    int number = isGirder ? girderCount++ : beamCount++;
                    string beamName = $"{prefix}{number}{suffix}";

                    // Sort beams within group theo thứ tự
                    var sortedMembers = group.Group.ToList();
                    sortedMembers.Sort((a, b) => comparePoints(a.Mid, b.Mid));

                    foreach (var beam in sortedMembers)
                    {
                        var curve = tr.GetObject(beam.Id, OpenMode.ForWrite) as Curve;
                        if (curve == null) continue;

                        Point3d pStart = curve.StartPoint;
                        Point3d pEnd = curve.EndPoint;
                        LabelPlotter.PlotLabel(btr, tr, pStart, pEnd, beamName, LabelPosition.MiddleBottom);
                    }
                }
            });

            WriteSuccess($"Đã đặt tên: {girderCount - 1} Girder ({girderPrefix}), {beamCount - 1} Beam ({beamPrefix}).");
            WriteMessage($"Quy tắc: Corner={settings.SortCorner}, Direction={(settings.SortDirection == 0 ? "Horizontal" : "Vertical")}");
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
        [CommandMethod("DTS_BEAM_VIEWER")]
        public void DTS_BEAM_VIEWER()
        {
            WriteMessage("Loading Beam Group Viewer...");

            try
            {
                // Get cached beam groups or create empty list
                var groups = GetOrCreateBeamGroups();

                // Show viewer dialog
                using (var dialog = new UI.Forms.BeamGroupViewerDialog(groups, ApplyBeamGroupResults))
                {
                    Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(dialog);
                }
            }
            catch (System.Exception ex)
            {
                WriteError($"Lỗi mở Beam Viewer: {ex.Message}");
            }
        }

        /// <summary>
        /// Command cho phép User chọn dầm và tạo nhóm thủ công
        /// </summary>
        [CommandMethod("DTS_SET_BEAM")]
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
            // Use full namespace to avoid ambiguity with Core.Algorithms.BeamData
            var beamDataList = new List<Core.Algorithms.BeamData>();

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

                    beamDataList.Add(new Core.Algorithms.BeamData
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

            // Tạo nhóm thủ công và chạy detection
            var group = CreateManualBeamGroup(groupName, beamDataList);

            // Add to cache
            var groups = GetOrCreateBeamGroups();
            groups.Add(group);

            // Save to cache
            SaveBeamGroupsToCache(groups);

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
        private BeamGroup CreateManualBeamGroup(string name, List<Core.Algorithms.BeamData> beamDataList)
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

            // Check splice requirement
            double standardLength = settings.Beam?.StandardBarLength ?? 11700;
            group.RequiresSplice = group.TotalLength * 1000 > standardLength;

            // For manual groups, create spans based on beam segments
            // (No column detection - user manually selected the beams)
            double pos = 0;
            group.Supports.Add(new SupportData
            {
                SupportId = "Start",
                SupportIndex = 0,
                Type = SupportType.FreeEnd,
                Position = 0,
                Width = 0
            });

            foreach (var beam in sortedBeams)
            {
                pos += beam.Length / 1000.0;
                group.Supports.Add(new SupportData
                {
                    SupportId = $"J{group.Supports.Count}",
                    SupportIndex = group.Supports.Count,
                    Type = SupportType.Column, // Assume joint at each beam end
                    Position = pos,
                    Width = 300
                });
            }

            // Create spans between supports
            double prevHeight = group.Height;
            for (int i = 0; i < group.Supports.Count - 1; i++)
            {
                var left = group.Supports[i];
                var right = group.Supports[i + 1];

                // Find beam(s) in this span
                var spanBeams = sortedBeams.Skip(i).Take(1).ToList();
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

            return group;
        }

        // Cache file path
        private static string BeamGroupsCachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DTS_Engine", "BeamGroupsCache.json");

        /// <summary>
        /// Lấy hoặc tạo danh sách BeamGroup từ file cache
        /// </summary>
        private List<BeamGroup> GetOrCreateBeamGroups()
        {
            try
            {
                if (File.Exists(BeamGroupsCachePath))
                {
                    string json = File.ReadAllText(BeamGroupsCachePath);
                    var groups = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BeamGroup>>(json);
                    if (groups != null) return groups;
                }
            }
            catch { }

            return new List<BeamGroup>();
        }

        /// <summary>
        /// Lưu danh sách BeamGroup vào file cache
        /// </summary>
        private void SaveBeamGroupsToCache(List<BeamGroup> groups)
        {
            try
            {
                string dir = Path.GetDirectoryName(BeamGroupsCachePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(groups, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(BeamGroupsCachePath, json);
            }
            catch { }
        }

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
            SaveBeamGroupsToCache(groups);

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
        private List<Core.Algorithms.BeamData> SortBeamsByNamingConfig(
            List<Core.Algorithms.BeamData> beams, NamingConfig cfg)
        {
            if (beams == null || beams.Count == 0)
                return new List<Core.Algorithms.BeamData>();

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
    }
}
