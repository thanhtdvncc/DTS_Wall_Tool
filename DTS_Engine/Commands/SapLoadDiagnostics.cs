using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;
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
        /// Test 2: Kiểm tra phản lực đáy (Base Reaction) với các trường hợp Envelope/Step.
        /// Giúp phát hiện lỗi lấy sai giá trị Max/Min trong tổ hợp bao.
        /// </summary>
        [CommandMethod("DTS_TEST_REACTION")]
        public void DTS_TEST_REACTION()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== DEBUG: KIỂM TRA PHẢN LỰC ĐÁY (BASE REACTION) ===");

                if (!SapUtils.IsConnected && !SapUtils.Connect(out _)) return;

                // 1. Nhập Load Pattern/Combo
                var patOpt = new PromptStringOptions("\nNhập tên Load Pattern hoặc Combo (VD: ENV_DL): ");
                var patRes = Ed.GetString(patOpt);
                if (patRes.Status != PromptStatus.OK) return;
                string loadCase = patRes.StringResult;

                WriteMessage($"\nĐang truy vấn Base Reaction cho: {loadCase}...");

                // Gọi hàm hiện tại trong SapUtils
                double fz = SapUtils.GetBaseReaction(loadCase, "Z");
                double fx = SapUtils.GetBaseReaction(loadCase, "X");
                double fy = SapUtils.GetBaseReaction(loadCase, "Y");

                WriteMessage($"\nKẾT QUẢ TỪ SAPUTILS:");
                WriteMessage($"FZ = {fz:0.00} kN");
                WriteMessage($"FX = {fx:0.00} kN");
                WriteMessage($"FY = {fy:0.00} kN");

                // Dump chi tiết bảng dữ liệu thô để đối chiếu
                WriteMessage("\n--- ĐỐI CHIẾU DỮ LIỆU THÔ TỪ BẢNG BASE REACTION ---");
                DumpRawTable("Base Reactions", loadCase);
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
                    row => {
                        double v1 = ParseAndConvert(row, "FOverLA", isAreaLoad: false);
                        double v2 = ParseAndConvert(row, "FOverLB", isAreaLoad: false);
                        return (Math.Abs(v1) + Math.Abs(v2)) / 2.0; // Lấy trung bình để ước lượng độ lớn
                    });

                // 4. Joint Loads - Force
                // Cột quan trọng: F1, F2, F3 (KN)
                AnalyzeSpecificTable("Joint Loads - Force",
                    new[] { "Joint", "LoadPat", "F1", "F2", "F3" },
                    row => {
                        double f1 = ParseAndConvert(row, "F1", isAreaLoad: false, isForce: true);
                        double f2 = ParseAndConvert(row, "F2", isAreaLoad: false, isForce: true);
                        double f3 = ParseAndConvert(row, "F3", isAreaLoad: false, isForce: true);
                        return Math.Sqrt(f1*f1 + f2*f2 + f3*f3); // Tổng hợp lực
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
        /// </summary>
        [CommandMethod("DTS_TEST_AUDIT_FIX")]
        public void DTS_TEST_AUDIT_FIX()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== TEST ULTIMATE: KIỂM TRA FIX BUG #1 + #2 ===");

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

                // 2. Test SapDatabaseReader (NEW)
                WriteMessage("\n[1] Testing SapDatabaseReader.ReadAllLoadsWithBaseReaction()...");
                var dbReader = new SapDatabaseReader(SapUtils.GetModel());
                var loads = dbReader.ReadAllLoadsWithBaseReaction(pattern, out double baseReaction);

                WriteMessage($"    Total Loads Read: {loads.Count}");
                WriteMessage($"    Base Reaction: {baseReaction:0.00} kN");

                // Phân tích Direction Components
                double sumX = loads.Sum(l => Math.Abs(l.DirectionX));
                double sumY = loads.Sum(l => Math.Abs(l.DirectionY));
                double sumZ = loads.Sum(l => Math.Abs(l.DirectionZ));

                WriteMessage($"    Direction Components:");
                WriteMessage($"      - Sum |DirectionX|: {sumX:0.00}");
                WriteMessage($"      - Sum |DirectionY|: {sumY:0.00}");
                WriteMessage($"      - Sum |DirectionZ|: {sumZ:0.00}");

                string dominantDir = sumX > sumY ? (sumX > sumZ ? "X" : "Z") : (sumY > sumZ ? "Y" : "Z");
                WriteMessage($"      - Dominant Direction: {dominantDir}");

                // 3. Kiểm tra tải hình thang (FIX BUG #2)
                WriteMessage("\n[2] Checking for Trapezoidal Loads (FIX #2)...");
                var frameDistLoads = loads.Where(l => l.LoadType == "FrameDistributed").ToList();
                if (frameDistLoads.Count > 0)
                {
                    WriteMessage($"    Found {frameDistLoads.Count} Frame Distributed Loads");
                    
                    // In 3 mẫu đầu tiên
                    int sampleCount = Math.Min(3, frameDistLoads.Count);
                    for (int i = 0; i < sampleCount; i++)
                    {
                        var load = frameDistLoads[i];
                        WriteMessage($"    Sample {i+1}: {load.ElementName} = {load.Value1:0.00} kN/m (Dir: {load.Direction})");
                    }
                }
                else
                {
                    WriteMessage("    (No Frame Distributed Loads)");
                }

                // 4. Kiểm tra tải ngang trên Area (FIX BUG #1)
                WriteMessage("\n[3] Checking Lateral Loads on Walls (FIX #1)...");
                var lateralLoads = loads.Where(l => 
                    (l.LoadType.Contains("Area") || l.LoadType.Contains("AreaUniform")) &&
                    (Math.Abs(l.DirectionX) > 0.01 || Math.Abs(l.DirectionY) > 0.01)
                ).ToList();

                if (lateralLoads.Count > 0)
                {
                    WriteMessage($"    Found {lateralLoads.Count} Lateral Wall Loads:");
                    
                    double totalLateralForce = 0;
                    foreach (var load in lateralLoads.Take(5))
                    {
                        WriteMessage($"      - {load.ElementName}: Value={load.Value1:0.00}, DirX={load.DirectionX:0.00}, DirY={load.DirectionY:0.00}");
                        totalLateralForce += load.Value1;
                    }
                    
                    WriteMessage($"    Total Lateral Force (first 5): {totalLateralForce:0.00}");
                }
                else
                {
                    WriteWarning("    (No Lateral Wall Loads found - This may indicate bug still exists!)");
                }

                // 5. So sánh với AuditEngine
                WriteMessage("\n[4] Testing AuditEngine.RunSingleAudit()...");
                var engine = new DTS_Engine.Core.Engines.AuditEngine();
                var report = engine.RunSingleAudit(pattern);

                WriteMessage($"    Stories Processed: {report.Stories.Count}");
                WriteMessage($"    Total Calculated: {report.TotalCalculatedForce:0.00} kN");
                WriteMessage($"    SAP Base Reaction: {report.SapBaseReaction:0.00} kN");
                WriteMessage($"    Difference: {report.DifferencePercent:0.00}%");

                // 6. Kết luận
                WriteMessage("\n=== KẾT LUẬN ===");
                if (Math.Abs(sumX) > 0.01 || Math.Abs(sumY) > 0.01)
                {
                    WriteSuccess("✓ FIX #1 OK: Direction Components đã được resolve (có lateral loads)");
                }
                else
                {
                    WriteWarning("⚠ FIX #1 CẦN KIỂM TRA: Không phát hiện lateral components");
                }

                if (Math.Abs(report.DifferencePercent) < 10.0)
                {
                    WriteSuccess("✓ FIX #2 OK: Sai số < 10% (có thể do rounding)");
                }
                else
                {
                    WriteWarning($"⚠ FIX #2 CẦN KIỂM TRA: Sai số {report.DifferencePercent:0.00}% vẫn còn cao");
                }
            });
        }
    }
}