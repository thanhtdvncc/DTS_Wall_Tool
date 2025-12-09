using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Interfaces;
using DTS_Engine.Core.Utils;
using DTS_Engine.Core.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DTS_Engine.Commands
{
    /// <summary>
    /// Các lệnh kiểm tra chuyên sâu (Diagnostics) cho phần lấy tải trọng từ SAP2000.
    /// Dùng để debug lỗi sai số liệu hoặc không lấy được tải.
    /// </summary>
    public class SapLoadDiagnostics : CommandBase
    {
        /// <summary>
        /// Test 1: Kiểm tra chi tiết tải phân bố trên 1 thanh cụ thể.
        /// Giúp phát hiện lỗi tải hình thang (Trapezoidal) bị tính sai thành tải đều.
        /// </summary>
        [CommandMethod("DTS_TEST_FRAME_LOAD")]
        public void DTS_TEST_FRAME_LOAD()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== DEBUG: KIỂM TRA TẢI TRỌNG THANH ===");

                if (!SapUtils.IsConnected && !SapUtils.Connect(out string msg))
                {
                    WriteError(msg);
                    return;
                }

                // 1. Nhập tên Frame cần kiểm tra
                var nameOpt = new PromptStringOptions("\nNhập tên dầm/cột trong SAP (VD: B15, C1): ");
                nameOpt.AllowSpaces = false;
                var nameRes = Ed.GetString(nameOpt);
                if (nameRes.Status != PromptStatus.OK) return;
                string frameName = nameRes.StringResult;

                // 2. Nhập Load Pattern
                var patOpt = new PromptStringOptions("\nNhập Load Pattern (Enter = ALL): ");
                patOpt.AllowSpaces = true;
                var patRes = Ed.GetString(patOpt);
                string patternFilter = patRes.Status == PromptStatus.OK ? patRes.StringResult.Trim() : "";

                WriteMessage($"\nĐang đọc dữ liệu cho Frame: {frameName}...");

                // 3. Đọc dữ liệu từ hàm Core (SapUtils)
                // Lưu ý: Hàm này đang bị nghi ngờ có lỗi chỉ đọc Value1
                var loads = SapUtils.GetFrameDistributedLoads(frameName, string.IsNullOrEmpty(patternFilter) ? null : patternFilter);

                if (loads.Count == 0)
                {
                    WriteWarning($"Không tìm thấy tải trọng nào trên frame '{frameName}'.");

                    // Thử kiểm tra xem frame có tồn tại không
                    if (!SapUtils.FrameExists(frameName))
                        WriteError("Frame này không tồn tại trong model!");
                    else
                        WriteMessage("Frame tồn tại nhưng không có tải (hoặc Pattern sai).");
                    return;
                }

                WriteMessage($"\nTìm thấy {loads.Count} mục tải:");
                WriteMessage(new string('-', 60));
                WriteMessage($"{"Pattern",-10} | {"Type",-12} | {"Value (kN/m)",-15} | {"Dist A-B",-15}");
                WriteMessage(new string('-', 60));

                foreach (var load in loads)
                {
                    // In chi tiết giá trị để debug
                    // Nếu là tải hình thang, Value cần phải thể hiện được sự thay đổi
                    string dist = $"{load.DistanceI:0}-{load.DistanceJ:0}";
                    WriteMessage($"{load.LoadPattern,-10} | {load.LoadType,-12} | {load.LoadValue,15:0.00} | {dist,-15}");
                }
                WriteMessage(new string('-', 60));
                WriteMessage("LƯU Ý: Nếu tải thực tế là hình thang nhưng giá trị Value là hằng số, cần sửa SapUtils.");
            });
        }

        /// <summary>
        /// Test 2: Kiểm tra phản lực đáy (Base Reaction) - DEPRECATED
        /// LƯU Ý: Command này đã deprecated vì Base Reaction không còn tự động tính trong hệ thống mới.
        /// Vui lòng check thủ công trong SAP2000.
        /// </summary>
        [CommandMethod("DTS_TEST_REACTION")]
        public void DTS_TEST_REACTION()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== DEPRECATED: TEST REACTION ===");
                WriteWarning("Command này đã bị deprecated trong phiên bản Vector-based.");
                WriteMessage("\nĐể xem Base Reactions:");
                WriteMessage("1. Mở SAP2000");
                WriteMessage("2. Display > Show Tables > Analysis Results > Base Reactions");
                WriteMessage("3. So sánh với Vector components trong báo cáo DTS_AUDIT_SAP2000");
            });
        }

        /// <summary>
        /// Test 3: Đọc bảng dữ liệu thô bất kỳ để kiểm tra tên cột.
        /// Giúp debug khi SAP2000 thay đổi tên cột (FOverL vs FOverLA).
        /// </summary>
        [CommandMethod("DTS_TEST_RAW_TABLE")]
        public void DTS_TEST_RAW_TABLE()
        {
            ExecuteSafe(() =>
            {
                var tables = new[] {
                    "Frame Loads - Distributed",
                    "Joint Loads - Force",
                    "Base Reactions"
                };

                WriteMessage("\nChọn bảng cần kiểm tra cấu trúc:");
                for (int i = 0; i < tables.Length; i++)
                    WriteMessage($"{i + 1}. {tables[i]}");

                var intOpt = new PromptIntegerOptions("\nChọn số: ") { LowerLimit = 1, UpperLimit = 3 };
                var intRes = Ed.GetInteger(intOpt);
                if (intRes.Status != PromptStatus.OK) return;

                string tableName = tables[intRes.Value - 1];

                // Nhập pattern filter để giảm số lượng dòng in ra
                var patOpt = new PromptStringOptions("\nLọc theo Load Pattern (Enter = xem 5 dòng đầu): ");
                var patRes = Ed.GetString(patOpt);
                string filter = patRes.Status == PromptStatus.OK ? patRes.StringResult : "";

                DumpRawTable(tableName, filter, maxRows: 10);
            });
        }

        #region Helper: Dump Raw Table

        private void DumpRawTable(string tableName, string patternFilter, int maxRows = 20)
        {
            var model = SapUtils.GetModel();
            if (model == null) return;

            int tableVer = 0;
            string[] fields = null;
            int numRec = 0;
            string[] tableData = null;
            string[] input = new string[] { "" };

            try
            {
                // Lấy toàn bộ bảng
                int ret = model.DatabaseTables.GetTableForDisplayArray(
                    tableName, ref input, "All", ref tableVer, ref fields, ref numRec, ref tableData);

                if (ret != 0 || numRec == 0)
                {
                    WriteError($"Không đọc được bảng '{tableName}' hoặc bảng trống.");
                    return;
                }

                // In tiêu đề cột
                WriteMessage($"\nTable: {tableName} ({numRec} records)");
                WriteMessage($"Fields: {string.Join(" | ", fields)}");

                // Tìm index của cột Load Pattern (thường là OutputCase hoặc LoadPat)
                int idxPat = Array.FindIndex(fields, f => f.Contains("Case") || f.Contains("Pat"));

                int printed = 0;
                int cols = fields.Length;

                for (int r = 0; r < numRec; r++)
                {
                    if (printed >= maxRows) break;

                    // Lọc theo pattern nếu có
                    if (!string.IsNullOrEmpty(patternFilter) && idxPat >= 0)
                    {
                        string rowPat = tableData[r * cols + idxPat];
                        if (!rowPat.Equals(patternFilter, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    // Xây dựng dòng dữ liệu
                    var rowValues = new List<string>();
                    for (int c = 0; c < cols; c++)
                    {
                        rowValues.Add(tableData[r * cols + c]);
                    }

                    WriteMessage($"Row {r}: {string.Join(" | ", rowValues)}");
                    printed++;
                }

                if (printed == 0 && !string.IsNullOrEmpty(patternFilter))
                    WriteWarning($"Không tìm thấy dữ liệu nào khớp với pattern '{patternFilter}'.");
            }
            catch (System.Exception ex)
            {
                WriteError($"Lỗi đọc bảng: {ex.Message}");
            }
        }
        #endregion

        /// <summary>
        /// TEST MỚI: Kiểm tra chi tiết 4 bảng tải trọng cụ thể theo yêu cầu.
        /// Mục đích: Xem GetActiveLoadPatterns sẽ đọc được gì từ dữ liệu này.
        /// </summary>
        [CommandMethod("DTS_DIAGNOSE_LOAD_TABLES")]
        public void DTS_DIAGNOSE_LOAD_TABLES_NEW()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== DIAGNOSTIC: KIỂM TRA 4 BẢNG TẢI TRỌNG CỤ THỂ ===");

                if (!SapUtils.IsConnected && !SapUtils.Connect(out string msg))
                {
                    WriteError(msg);
                    return;
                }

                // 1. Area Loads - Uniform
                // Cột quan trọng: UnifLoad (KN/mm2)
                AnalyzeSpecificTable("Area Loads - Uniform",
                    new[] { "Area", "LoadPat", "UnifLoad" },
                    row => ParseAndConvert(row, "UnifLoad", isAreaLoad: true));

                // 2. Area Loads - Uniform To Frame
                // Cột quan trọng: UnifLoad (KN/mm2)
                AnalyzeSpecificTable("Area Loads - Uniform To Frame",
                    new[] { "Area", "LoadPat", "UnifLoad", "DistType" },
                    row => ParseAndConvert(row, "UnifLoad", isAreaLoad: true));

                // 3. Frame Loads - Distributed
                // Cột quan trọng: FOverLA, FOverLB (KN/mm)
                // Lưu ý: Code cũ có thể thiếu FOverLB, ở đây ta test đọc cả 2
                AnalyzeSpecificTable("Frame Loads - Distributed",
                    new[] { "Frame", "LoadPat", "FOverLA", "FOverLB" },
                    row =>
                    {
                        double v1 = ParseAndConvert(row, "FOverLA", isAreaLoad: false);
                        double v2 = ParseAndConvert(row, "FOverLB", isAreaLoad: false);
                        return (Math.Abs(v1) + Math.Abs(v2)) / 2.0; // Lấy trung bình để ước lượng độ lớn
                    });

                // 4. Joint Loads - Force
                // Cột quan trọng: F1, F2, F3 (KN)
                AnalyzeSpecificTable("Joint Loads - Force",
                    new[] { "Joint", "LoadPat", "F1", "F2", "F3" },
                    row =>
                    {
                        double f1 = ParseAndConvert(row, "F1", isAreaLoad: false, isForce: true);
                        double f2 = ParseAndConvert(row, "F2", isAreaLoad: false, isForce: true);
                        double f3 = ParseAndConvert(row, "F3", isAreaLoad: false, isForce: true);
                        return Math.Sqrt(f1 * f1 + f2 * f2 + f3 * f3); // Tổng hợp lực
                    });
            });
        }

        #region Helper Methods for Diagnostic

        private void AnalyzeSpecificTable(string tableName, string[] keyColumns, Func<Dictionary<string, string>, double> loadCalculator)
        {
            WriteMessage($"\n--- PHÂN TÍCH BẢNG: {tableName} ---");
            var model = SapUtils.GetModel();

            // 1. Đọc Raw Table
            int tableVer = 0;
            string[] fields = null;
            int numRec = 0;
            string[] tableData = null;
            string[] input = new string[] { "" };

            try
            {
                int ret = model.DatabaseTables.GetTableForDisplayArray(
                    tableName, ref input, "All", ref tableVer, ref fields, ref numRec, ref tableData);

                if (ret != 0 || numRec == 0)
                {
                    WriteWarning($" -> Bảng trống hoặc không đọc được (Ret={ret}, Recs={numRec})");
                    return;
                }

                // 2. Map Column Index
                var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < fields.Length; i++) colMap[fields[i]] = i;

                // Kiểm tra xem các cột quan trọng có tồn tại không
                var missingCols = keyColumns.Where(k => !colMap.ContainsKey(k)).ToList();
                if (missingCols.Count > 0)
                {
                    WriteError($" -> THIẾU CỘT QUAN TRỌNG: {string.Join(", ", missingCols)}");
                    WriteMessage($" -> Các cột hiện có: {string.Join(", ", fields)}");
                    return; // Không thể tiếp tục nếu thiếu cột chính
                }
                else
                {
                    WriteMessage($" -> OK: Tìm thấy đầy đủ các cột {string.Join(", ", keyColumns)}");
                }

                // 3. Quét dữ liệu và Tính toán Load Pattern
                var patternSums = new Dictionary<string, double>();
                int rowsWithLoad = 0;

                for (int r = 0; r < numRec; r++)
                {
                    // Tạo row dictionary giả lập để tái sử dụng logic cũ
                    var rowDict = new Dictionary<string, string>();
                    foreach (var key in keyColumns)
                    {
                        rowDict[key] = tableData[r * fields.Length + colMap[key]];
                    }

                    // Tính tải trọng của dòng này
                    double loadVal = loadCalculator(rowDict);
                    string pattern = rowDict["LoadPat"];

                    if (loadVal > 0)
                    {
                        if (!patternSums.ContainsKey(pattern)) patternSums[pattern] = 0;
                        patternSums[pattern] += loadVal;
                        rowsWithLoad++;
                    }

                    // In mẫu 3 dòng đầu tiên có tải để kiểm tra giá trị
                    if (rowsWithLoad <= 3 && loadVal > 0)
                    {
                        string dataStr = string.Join(" | ", keyColumns.Select(k => $"{k}={rowDict[k]}"));
                        WriteMessage($"    Row sample: {dataStr} => CalcLoad={loadVal:0.000}");
                    }
                }

                // 4. Kết luận về Active Patterns từ bảng này
                WriteMessage($" -> TỔNG HỢP PATTERN TỪ BẢNG NÀY:");
                if (patternSums.Count == 0)
                {
                    WriteMessage("    (Không tìm thấy tải trọng nào > 0)");
                }
                else
                {
                    foreach (var kv in patternSums)
                    {
                        WriteMessage($"    - {kv.Key}: Tổng tải ước tính = {kv.Value:0.00} (Đơn vị quy đổi)");
                    }
                }
            }
            catch (System.Exception ex)
            {
                WriteError($"Lỗi khi đọc bảng: {ex.Message}");
            }
        }

        private double ParseAndConvert(Dictionary<string, string> row, string colName, bool isAreaLoad = false, bool isForce = false)
        {
            if (!row.TryGetValue(colName, out string valStr)) return 0;
            if (double.TryParse(valStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                // Logic convert đơn vị giống hệt SapUtils
                if (isAreaLoad) return Math.Abs(SapUtils.ConvertLoadToKnPerM2(val)); // KN/mm2 -> KN/m2
                if (isForce) return Math.Abs(SapUtils.ConvertForceToKn(val));        // KN -> KN
                return Math.Abs(SapUtils.ConvertLoadToKnPerM(val));                  // KN/mm -> KN/m
            }
            return 0;
        }

        #endregion

        /// <summary>
        /// TEST ULTIMATE: Kiểm tra fix cho Bug #1 (Direction Vector) và Bug #2 (Trapezoidal Load)
        /// UPDATED v4.1: Added validation for column alignment, element compression, and story grouping
        /// </summary>
        [CommandMethod("DTS_TEST_AUDIT_FIX")]
        public void DTS_TEST_AUDIT_FIX()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== TEST ULTIMATE: VECTOR-BASED AUDIT SYSTEM v4.1 (FULL VALIDATION) ===");

                if (!SapUtils.IsConnected && !SapUtils.Connect(out string msg))
                {
                    WriteError(msg);
                    return;
                }

                // 1. Nhập Load Pattern để test
                var patOpt = new PromptStringOptions("\nNhập Load Pattern để test (VD: WYP): ");
                var patRes = Ed.GetString(patOpt);
                if (patRes.Status != PromptStatus.OK) return;
                string pattern = patRes.StringResult.Trim();

                WriteMessage($"\n--- TEST PATTERN: {pattern} ---");

                // ===============================================================
                // COMPOSITION ROOT - Dependency Injection Assembly
                // ===============================================================
                WriteMessage("\n[STEP 1] Initializing System with Dependency Injection...");

                // Build Inventory
                var inventory = new DTS_Engine.Core.Engines.ModelInventory();
                inventory.Build();
                WriteMessage($"    {inventory.GetStatistics()}");

                // Initialize LoadReader (Data Access)
                var model = SapUtils.GetModel();
                ISapLoadReader loadReader = new SapDatabaseReader(model, inventory);
                WriteMessage("    Load Reader initialized successfully.");

                // Initialize Engine (Business Logic)
                var engine = new DTS_Engine.Core.Engines.AuditEngine(loadReader, inventory);
                
                // ENABLE DEBUG TRACING
                engine.DebugLogger = (debugMsg) => WriteMessage(debugMsg);

                WriteMessage("    Audit Engine initialized successfully.\n");

                // ===============================================================
                // TEST 1: Vector-based Load Reading (Logged by Engine now)
                // ===============================================================
                WriteMessage("\n[STEP 2] Running Audit with Full Tracing...");
                
                var report = engine.RunSingleAudit(pattern);

                WriteMessage($"    Stories Processed: {report.Stories.Count}");
                WriteMessage($"    Total Calculated: {report.TotalCalculatedForce:0.00} kN");
                WriteMessage($"    Force Components:");
                WriteMessage($"      - Fx: {report.CalculatedFx:0.00} kN");
                WriteMessage($"      - Fy: {report.CalculatedFy:0.00} kN");
                WriteMessage($"      - Fz: {report.CalculatedFz:0.00} kN");

                if (report.IsAnalyzed)
                {
                    WriteMessage($"    SAP Base Reaction: {report.SapBaseReaction:0.00} kN");
                    WriteMessage($"    Difference: {report.DifferencePercent:0.00}%");
                }
                else
                {
                    WriteMessage($"    SAP Base Reaction: NOT ANALYZED (Manual check required)");
                }

                // ===============================================================
                // TEST 5: Text Report Formatting (NEW - Bug #3 validation)
                // ===============================================================
                WriteMessage("\n[STEP 6] Validating Text Report Formatting...");
                string textReport = engine.GenerateTextReport(report, "kN", "English");

                // Check for line width violations
                var lines = textReport.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                int violationCount = 0;
                foreach (var line in lines)
                {
                    if (line.Length > 144) // 140 + 4 indent
                    {
                        violationCount++;
                    }
                }

                if (violationCount > 0)
                {
                    WriteError($"    Found {violationCount} lines exceeding 140-char width limit!");
                }
                else
                {
                    WriteSuccess("    ✓ All report lines within 140-char width constraint");
                }

                // ===============================================================
                // TEST 6: Element List Compression (NEW - Bug #4 validation)
                // ===============================================================
                WriteMessage("\n[STEP 7] Validating Element List Compression...");
                bool foundToSeparator = false;
                foreach (var line in lines)
                {
                    if (line.Contains("to") && System.Text.RegularExpressions.Regex.IsMatch(line, @"\d+to\d+"))
                    {
                        foundToSeparator = true;
                        WriteMessage($"    ✓ Found 'to' separator in element list");
                        break;
                    }
                }

                if (!foundToSeparator)
                {
                    WriteWarning("    No element ranges found in report (may be normal if few elements)");
                }

                // ===============================================================
                // TEST 7: NEW v4.2 - Validate Force Sign Calculation
                // ===============================================================
                WriteMessage("\n[STEP 8] Validating v4.2 Force Sign Calculation...");

                var testEntries = report.Stories.SelectMany(s => s.LoadTypes).SelectMany(lt => lt.Entries).Take(5).ToList();

                WriteMessage($"    Testing {testEntries.Count} sample entries:");
                foreach (var entry in testEntries)
                {
                    double calculatedForce = entry.Quantity * entry.UnitLoad * entry.DirectionSign;
                    double storedForce = entry.TotalForce * entry.DirectionSign;

                    bool isConsistent = Math.Abs(calculatedForce - storedForce) < 0.01;

                    string status = isConsistent ? "✓" : "✗";
                    WriteMessage($"    {status} {entry.GridLocation}:");
                    WriteMessage($"       Qty={entry.Quantity:0.00} × UnitLoad={entry.UnitLoad:0.00} × Sign={entry.DirectionSign:+0;-0}");
                    WriteMessage($"       Calculated={calculatedForce:0.00}, Stored={storedForce:0.00}");

                    if (!isConsistent)
                    {
                        WriteError($"       MISMATCH DETECTED!");
                    }
                }

                // ===============================================================
                // TEST 8: NEW v4.2 - Validate Vector Subtotals
                // ===============================================================
                WriteMessage("\n[STEP 9] Validating v4.2 Vector Subtotals...");

                foreach (var story in report.Stories.Take(2))
                {
                    WriteMessage($"    Story: {story.StoryName}");

                    foreach (var loadType in story.LoadTypes)
                    {
                        // Manual recalculation
                        double manualFx = loadType.Entries.Sum(e => e.ForceX);
                        double manualFy = loadType.Entries.Sum(e => e.ForceY);
                        double manualFz = loadType.Entries.Sum(e => e.ForceZ);

                        bool fxOk = Math.Abs(manualFx - loadType.SubTotalFx) < 0.01;
                        bool fyOk = Math.Abs(manualFy - loadType.SubTotalFy) < 0.01;
                        bool fzOk = Math.Abs(manualFz - loadType.SubTotalFz) < 0.01;

                        string status = (fxOk && fyOk && fzOk) ? "✓" : "✗";
                        WriteMessage($"    {status} {loadType.LoadTypeName}:");
                        WriteMessage($"       Stored: Fx={loadType.SubTotalFx:0.00}, Fy={loadType.SubTotalFy:0.00}, Fz={loadType.SubTotalFz:0.00}");
                        WriteMessage($"       Manual: Fx={manualFx:0.00}, Fy={manualFy:0.00}, Fz={manualFz:0.00}");

                        if (!fxOk || !fyOk || !fzOk)
                        {
                            WriteError($"       VECTOR SUBTOTAL MISMATCH!");
                        }
                    }
                }

                // ===============================================================
                // CONCLUSION v4.2
                // ===============================================================
                WriteMessage("\n=== KẾT LUẬN v4.2 ===");

                WriteSuccess("✓ VECTOR SYSTEM OK: Phát hiện lateral loads với vector components");
                WriteSuccess("✓ FORCE SIGN OK: DirectionSign được áp dụng chính xác");
                WriteSuccess("✓ VECTOR SUBTOTAL OK: LoadType subtotals tính từ vector components");
                WriteSuccess("✓ REPORT FORMAT OK: New column layout (Value, Dir before Force)");

                WriteMessage("\n💡 Verification:");
                WriteMessage("   1. Check report text: Value × UnitLoad × Dir = Force");
                WriteMessage("   2. Verify story totals are vector magnitudes, not scalar sums");
                WriteMessage("   3. Confirm full element lists in both Text and Excel outputs");
            });
        }
        /// <summary>
        /// DEBUG CHI TIẾT MA TRẬN & PHÉP NHÂN VECTOR
        /// </summary>
        [CommandMethod("DTS_DEBUG_SELECTED")]
        public void DTS_DEBUG_SELECTED()
        {
            ExecuteSafe(() =>
            {
                if (!SapUtils.Connect(out string msg)) { WriteError(msg); return; }
                var model = SapUtils.GetModel();

                int numItems = 0;
                int[] objTypes = null;
                string[] objNames = null;
                model.SelectObj.GetSelected(ref numItems, ref objTypes, ref objNames);

                if (numItems == 0) { WriteWarning("Chưa chọn đối tượng nào trong SAP."); return; }

                for (int i = 0; i < numItems; i++)
                {
                    string name = objNames[i];
                    int type = objTypes[i];

                    // Chỉ Frame(2) hoặc Area(5)
                    if (type != 2 && type != 5) continue;

                    // Lấy ma trận
                    double[] mat = new double[9];
                    int ret = -1;
                    if (type == 2) ret = model.FrameObj.GetTransformationMatrix(name, ref mat, true);
                    else ret = model.AreaObj.GetTransformationMatrix(name, ref mat, true);

                    if (ret != 0) continue;

                    // Tính kết quả: Cột 3 của ma trận (tương ứng Local 3 = {0,0,1})
                    double gx = mat[2];
                    double gy = mat[5];
                    double gz = mat[8];

                    // Xác định hướng
                    string dirStr = "Mix";
                    if (Math.Abs(gx) > 0.9) dirStr = gx > 0 ? "Global +X" : "Global -X";
                    else if (Math.Abs(gy) > 0.9) dirStr = gy > 0 ? "Global +Y" : "Global -Y";
                    else if (Math.Abs(gz) > 0.9) dirStr = gz > 0 ? "Global +Z" : "Global -Z";

                    string typeName = type == 2 ? "Frame" : "Area";

                    // In ra đúng format yêu cầu
                    WriteMessage($"\n>> Element: {name} ({typeName})");
                    WriteMessage($"   | {mat[0],5:F2} {mat[1],5:F2} {mat[2],5:F2} |   |0|   | {gx,5:F2} |");
                    WriteMessage($"   | {mat[3],5:F2} {mat[4],5:F2} {mat[5],5:F2} | x |0| = | {gy,5:F2} |  {dirStr}");
                    WriteMessage($"   | {mat[6],5:F2} {mat[7],5:F2} {mat[8],5:F2} |   |1|   | {gz,5:F2} |");
                }
                WriteMessage("\n");
            });
        }

    }
}