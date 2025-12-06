using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using SAP2000v1;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// Tiện ích kết nối và làm việc với SAP2000.
    /// Hỗ trợ đồng bộ 2 chiều: Đọc/Ghi tải trọng.
    /// 
    /// ⚠️ QUAN TRỌNG - ĐƠN VỊ:
    /// - Sử dụng UnitManager để đồng bộ đơn vị giữa CAD và SAP
    /// - Mọi giá trị chiều dài từ CAD đều qua UnitManager quy đổi
    /// - Tải trọng luôn xuất sang SAP theo đơn vị của UnitManager
    /// </summary>
    public static partial class SapUtils    
    {
        #region Connection

        private static cOAPI _sapObject = null;
        private static cSapModel _sapModel = null;

        /// <summary>
        /// Kết nối đến SAP2000 đang chạy (Sử dụng Helper chuẩn)
        /// 
        /// ⚠️ QUAN TRỌNG - KHÔNG SỬA HÀM NÀY:
        /// - PHẢI dùng cHelper.GetObject() - Cách DUY NHẤT ổn định cho SAP v26+
        /// - KHÔNG dùng Marshal.GetActiveObject() - Sẽ KHÔNG hoạt động với v26
        /// - KHÔNG thay đổi chuỗi "CSI.SAP2000.API.SapObject" (không có dấu cách)
        /// - Sau khi kết nối, tự động gọi SyncUnits() để đồng bộ đơn vị
        /// </summary>
        public static bool Connect(out string message)
        {
            _sapObject = null;
            _sapModel = null;
            message = "";

            try
            {
                // 1. Dùng Helper - Cách duy nhất ổn định cho SAP v26+
                cHelper myHelper = new Helper();

                // 2. Lấy object đang chạy (KHÔNG có dấu cách trong chuỗi)
                _sapObject = myHelper.GetObject("CSI.SAP2000.API.SapObject");

                if (_sapObject != null)
                {
                    // 3. Lấy Model
                    _sapModel = _sapObject.SapModel;

                    // 4. Đồng bộ đơn vị với UnitManager (THAY ĐỔI QUAN TRỌNG)
                    if (SyncUnits())
                    {
                        // Lấy tên file để confirm
                        string modelName = "Unknown";
                        try { modelName = System.IO.Path.GetFileName(_sapModel.GetModelFilename()); } catch { }

                        message = $"Kết nối thành công: {modelName} | Đơn vị: {UnitManager.Info}";
                    }
                    else
                    {
                        message = "Kết nối OK nhưng không thể đồng bộ đơn vị SAP.";
                    }

                    return true;
                }
                else
                {
                    message = "Không tìm thấy SAP2000 đang chạy. Hãy mở SAP2000 trước.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = $"Lỗi kết nối SAP: {ex.Message}";

                // Gợi ý fix lỗi COM phổ biến
                if (ex.Message.Contains("cast") || ex.Message.Contains("COM"))
                {
                    message += "\n(Gợi ý: Chạy RegisterSAP2000.exe trong thư mục cài đặt SAP bằng quyền Admin)";
                }

                return false;
            }
        }

        /// <summary>
        /// Đồng bộ đơn vị: Set đơn vị của SAP model theo cài đặt trong UnitManager.
        /// 
        /// ⚠️ QUAN TRỌNG - LOGIC HOẠT ĐỘNG:
        /// - Ép kiểu enum DtsUnit sang SAP2000v1.eUnits (giá trị int giống nhau)
        /// - SAP sẽ sử dụng đơn vị này cho mọi thao tác tiếp theo
        /// - Đảm bảo tải trọng gán vào SAP có đơn vị đúng
        /// </summary>
        public static bool SyncUnits()
        {
            var model = GetModel();
            if (model == null) return false;

            try
            {
                // Ép kiểu Enum DTS sang Enum SAP
                // ⚠️ GIÁ TRỊ INT PHẢI KHỚP - xem DtsUnit enum trong UnitManager.cs
                eUnits sapUnit = (eUnits)(int)UnitManager.CurrentUnit;

                int ret = model.SetPresentUnits(sapUnit);
                return ret == 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SyncUnits failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Lấy đơn vị hiện tại của SAP model
        /// </summary>
        public static eUnits GetSapCurrentUnit()
        {
            var model = GetModel();
            if (model == null) return eUnits.kN_mm_C;

            try
            {
                return model.GetPresentUnits();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetSapCurrentUnit failed: {ex}");
                return eUnits.kN_mm_C;
            }
        }

        public static void Disconnect()
        {
            _sapModel = null;
            _sapObject = null;
        }

        public static bool IsConnected => _sapModel != null;

        public static cSapModel GetModel()
        {
            if (_sapModel == null) Connect(out _);
            return _sapModel;
        }

        #endregion

        #region Frame Geometry

        public static int CountFrames()
        {
            var model = GetModel();
            if (model == null) return -1;

            int count = 0;
            string[] names = null;
            int ret = model.FrameObj.GetNameList(ref count, ref names);
            return (ret == 0) ? count : 0;
        }

        public static List<SapFrame> GetAllFramesGeometry()
        {
            var listFrames = new List<SapFrame>();
            var model = GetModel();
            if (model == null) return listFrames;

            int count = 0;
            string[] frameNames = null;
            model.FrameObj.GetNameList(ref count, ref frameNames);

            if (count == 0 || frameNames == null) return listFrames;

            foreach (var name in frameNames)
            {
                var frame = GetFrameGeometry(name);
                if (frame != null) listFrames.Add(frame);
            }

            return listFrames;
        }

        public static SapFrame GetFrameGeometry(string frameName)
        {
            var model = GetModel();
            if (model == null) return null;

            string p1Name = "", p2Name = "";
            int ret = model.FrameObj.GetPoints(frameName, ref p1Name, ref p2Name);
            if (ret != 0) return null;

            double x1 = 0, y1 = 0, z1 = 0;
            ret = model.PointObj.GetCoordCartesian(p1Name, ref x1, ref y1, ref z1, "Global");
            if (ret != 0) return null;

            double x2 = 0, y2 = 0, z2 = 0;
            ret = model.PointObj.GetCoordCartesian(p2Name, ref x2, ref y2, ref z2, "Global");
            if (ret != 0) return null;

            return new SapFrame
            {
                Name = frameName,
                StartPt = new Point2D(x1, y1),
                EndPt = new Point2D(x2, y2),
                Z1 = z1,
                Z2 = z2
            };
        }

        public static List<SapFrame> GetBeamsAtElevation(double elevation, double tolerance = 200)
        {
            var result = new List<SapFrame>();
            var allFrames = GetAllFramesGeometry();

            foreach (var f in allFrames)
            {
                if (f.IsVertical) continue;

                double avgZ = (f.Z1 + f.Z2) / 2.0;
                if (Math.Abs(avgZ - elevation) <= tolerance)
                {
                    result.Add(f);
                }
            }
            return result;
        }

        #endregion

        #region Load Reading - ĐỌC TẢI TRỌNG TỪ SAP

        /// <summary>
        /// Đọc tất cả tải phân bố trên một frame.
        /// 
        /// ⚠️ QUY ĐỔI ĐƠN VỊ:
        /// - SAP trả về tải theo đơn vị hiện tại (thường là kN/mm nếu dùng kN_mm_C)
        /// - Chuyển đổi sang kN/m để thống nhất hiển thị
        /// </summary>
        public static List<SapLoadInfo> GetFrameDistributedLoads(string frameName, string loadPattern = null)
        {
            var loads = new List<SapLoadInfo>();
            var model = GetModel();
            if (model == null) return loads;

            try
            {
                var rows = GetSapTableData("Frame Loads - Distributed", loadPattern);
                if (rows == null || rows.Count == 0) return loads;

                foreach (var row in rows)
                {
                    if (!row.ContainsKey("Frame")) continue;
                    var fName = row["Frame"];
                    if (!string.Equals(fName, frameName, StringComparison.OrdinalIgnoreCase)) continue;

                    double val = 0;
                    val = ParseDouble(TryGetRowValue(row, "FOverLA") ?? TryGetRowValue(row, "FOverLB") ?? TryGetRowValue(row, "FOverL"));

                    val = ConvertLoadToKnPerM(val);

                    double distI = ParseDouble(TryGetRowValue(row, "AbsDistA") ?? TryGetRowValue(row, "RelDistA") ?? "0");
                    double distJ = ParseDouble(TryGetRowValue(row, "AbsDistB") ?? TryGetRowValue(row, "RelDistB") ?? "0");

                    string pattern = TryGetRowValue(row, "LoadPat") ?? TryGetRowValue(row, "OutputCase") ?? string.Empty;
                    string dir = TryGetRowValue(row, "Dir") ?? TryGetRowValue(row, "Direction") ?? "Gravity";

                    loads.Add(new SapLoadInfo
                    {
                        FrameName = fName,
                        LoadPattern = pattern,
                        LoadValue = val,
                        DistanceI = distI,
                        DistanceJ = distJ,
                        Direction = dir,
                        LoadType = "Distributed"
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetFrameDistributedLoads failed: {ex}");
            }

            return loads;
        }

        /// <summary>
        /// Chuyển đổi tải từ đơn vị SAP sang kN/m.
        /// ⚠️ LOGIC QUY ĐỔI:
        /// - Nếu SAP dùng mm: val (kN/mm) * 1000 = kN/m
        /// - Nếu SAP dùng m: val đã là kN/m
        /// </summary>
        private static double ConvertLoadToKnPerM(double sapValue)
        {
            switch (UnitManager.Info.LengthUnit.ToLowerInvariant())
            {
                case "mm":
                    return sapValue * 1000.0; // kN/mm -> kN/m
                case "cm":
                    return sapValue * 100.0;// kN/cm -> kN/m
                case "m":
                    return sapValue;      // kN/m (no conversion)
                case "in":
                    return sapValue * 39.37;  // kN/in -> kN/m (approx)
                case "ft":
                    return sapValue * 3.281;  // kN/ft -> kN/m (approx)
                default:
                    return sapValue * 1000.0; // Default: assume mm
            }
        }

        /// <summary>
        /// Chuyển đổi tải từ kN/m sang đơn vị SAP.
        /// Đảo ngược của ConvertLoadToKnPerM.
        /// </summary>
        private static double ConvertLoadFromKnPerM(double knPerMValue)
        {
            switch (UnitManager.Info.LengthUnit.ToLowerInvariant())
            {
                case "mm":
                    return knPerMValue / 1000.0; // kN/m -> kN/mm
                case "cm":
                    return knPerMValue / 100.0;  // kN/m -> kN/cm
                case "m":
                    return knPerMValue;          // kN/m (no conversion)
                case "in":
                    return knPerMValue / 39.37;  // kN/m -> kN/in (approx)
                case "ft":
                    return knPerMValue / 3.281;  // kN/m -> kN/ft (approx)
                default:
                    return knPerMValue / 1000.0; // Default: assume mm
            }
        }

        /// <summary>
        /// Đọc chi tiết các tải phân bố trên frame, nhóm theo pattern và kèm segments
        /// </summary>
        public static Dictionary<string, List<LoadEntry>> GetFrameDistributedLoadsDetailed(string frameName)
        {
            var result = new Dictionary<string, List<LoadEntry>>();
            var model = GetModel();
            if (model == null) return result;

            try
            {
                var rows = GetSapTableData("Frame Loads - Distributed");
                if (rows == null || rows.Count == 0) return result;

                foreach (var row in rows)
                {
                    if (!row.ContainsKey("Frame")) continue;
                    if (!string.Equals(row["Frame"], frameName, StringComparison.OrdinalIgnoreCase)) continue;
                    string pattern = TryGetRowValue(row, "LoadPat") ?? TryGetRowValue(row, "OutputCase") ?? string.Empty;
                    double val = ParseDouble(TryGetRowValue(row, "FOverLA") ?? TryGetRowValue(row, "FOverLB") ?? TryGetRowValue(row, "FOverL") ?? "0");
                    val = ConvertLoadToKnPerM(val);

                    double iPos = ParseDouble(TryGetRowValue(row, "AbsDistA") ?? TryGetRowValue(row, "RelDistA") ?? "0");
                    double jPos = ParseDouble(TryGetRowValue(row, "AbsDistB") ?? TryGetRowValue(row, "RelDistB") ?? "0");
                    string dir = TryGetRowValue(row, "Dir") ?? TryGetRowValue(row, "Direction") ?? "Gravity";

                    var entry = new LoadEntry
                    {
                        Pattern = pattern,
                        Value = val,
                        Direction = dir,
                        LoadType = "Distributed",
                        Segments = new List<LoadSegment> { new LoadSegment { I = iPos, J = jPos } }
                    };

                    if (!result.ContainsKey(pattern)) result[pattern] = new List<LoadEntry>();
                    result[pattern].Add(entry);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetFrameDistributedLoadsDetailed failed: {ex}");
            }

            return result;
        }

        /// <summary>
        /// Đọc tổng tải trọng trên frame theo pattern
        /// </summary>
        public static double GetFrameTotalLoad(string frameName, string loadPattern)
        {
            var loads = GetFrameDistributedLoads(frameName, loadPattern);
            if (loads.Count == 0) return 0;

            double total = 0;
            foreach (var load in loads)
            {
                total += load.LoadValue;
            }
            return total;
        }

        /// <summary>
        /// Kiểm tra frame có tải trọng không
        /// </summary>
        public static bool FrameHasLoad(string frameName, string loadPattern)
        {
            var loads = GetFrameDistributedLoads(frameName, loadPattern);
            return loads.Count > 0;
        }

        /// <summary>
        /// Lấy danh sách frame đã có tải theo pattern
        /// </summary>
        public static Dictionary<string, List<SapLoadInfo>> GetAllFrameLoads(string loadPattern)
        {
            var result = new Dictionary<string, List<SapLoadInfo>>();
            var model = GetModel();
            if (model == null) return result;

            int count = 0;
            string[] frameNames = null;
            model.FrameObj.GetNameList(ref count, ref frameNames);

            if (frameNames == null) return result;

            foreach (var frameName in frameNames)
            {
                var loads = GetFrameDistributedLoads(frameName, loadPattern);
                if (loads.Count > 0)
                {
                    result[frameName] = loads;
                }
            }

            return result;
        }

        private static string GetDirectionName(int dir)
        {
            switch (dir)
            {
                case 1: return "Local 1";
                case 2: return "Local 2";
                case 3: return "Local 3";
                case 4: return "X";
                case 5: return "Y";
                case 6: return "Z";
                case 7: return "X Projected";
                case 8: return "Y Projected";
                case 9: return "Z Projected";
                case 10: return "Gravity";
                case 11: return "Gravity Projected";
                default: return "Unknown";
            }
        }

        #endregion

        #region Load Writing - GHI TẢI TRỌNG VÀO SAP

        /// <summary>
        /// Gán tải phân bố lên frame.
        /// 
        /// ⚠️ XỬ LÝ ĐƠN VỊ:
        /// - loadValue phải là kN/m (đã chuẩn hóa)
        /// - Tự động quy đổi sang đơn vị SAP (kN/mm nếu dùng mm)
        /// </summary>
        public static bool AssignDistributedLoad(string frameName, string loadPattern,
    double loadValue, double distI = 0, double distJ = 0, bool isRelative = false)
        {
            var model = GetModel();
            if (model == null) return false;

            try
            {
                // Quy đổi từ kN/m sang đơn vị SAP
                double sapLoadValue = ConvertLoadFromKnPerM(loadValue);

                int ret = model.FrameObj.SetLoadDistributed(
                     frameName,
                         loadPattern,
                        1,          // Force/Length
                    10,         // Gravity Direction
                  distI, distJ,
                       sapLoadValue, sapLoadValue,
                         "Global",
                  isRelative,
                       true, // Replace existing
                         eItemType.Objects
                               );

                return ret == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gán tải từ MappingRecord
        /// </summary>
        public static bool AssignLoadFromMapping(MappingRecord mapping, string loadPattern, double loadValue)
        {
            if (string.IsNullOrEmpty(mapping.TargetFrame) || mapping.TargetFrame == "New")
                return false;

            return AssignDistributedLoad(
                mapping.TargetFrame,
           loadPattern,
             loadValue,
      mapping.DistI,
  mapping.DistJ,
       false
            );
        }

        /// <summary>
        /// Xóa tất cả tải trên frame theo pattern
        /// </summary>
        public static bool DeleteFrameLoads(string frameName, string loadPattern)
        {
            var model = GetModel();
            if (model == null) return false;

            try
            {
                int ret = model.FrameObj.DeleteLoadDistributed(frameName, loadPattern);
                return ret == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Xóa và gán lại tải (đảm bảo clean)
        /// </summary>
        public static bool ReplaceFrameLoad(string frameName, string loadPattern,
    double loadValue, double distI = 0, double distJ = 0)
        {
            // Xóa tải cũ
            DeleteFrameLoads(frameName, loadPattern);

            // Gán tải mới
            return AssignDistributedLoad(frameName, loadPattern, loadValue, distI, distJ, false);
        }

        #endregion

        #region Frame Change Detection - PHÁT HIỆN THAY ĐỔI

        /// <summary>
        /// Tạo hash từ geometry frame để phát hiện thay đổi
        /// </summary>
        public static string GetFrameGeometryHash(string frameName)
        {
            var frame = GetFrameGeometry(frameName);
            if (frame == null) return null;

            string data = $"{frame.StartPt.X:0.0},{frame.StartPt.Y:0.0},{frame.Z1:0.0}|" +
          $"{frame.EndPt.X:0.0},{frame.EndPt.Y:0.0},{frame.Z2:0.0}";

            return ComputeHash(data);
        }

        /// <summary>
        /// Tạo hash từ tải trọng frame
        /// </summary>
        public static string GetFrameLoadHash(string frameName, string loadPattern)
        {
            var loads = GetFrameDistributedLoads(frameName, loadPattern);
            if (loads.Count == 0) return "NOLOAD";

            var sortedLoads = loads.OrderBy(l => l.DistanceI).ToList();
            string data = string.Join("|", sortedLoads.Select(l =>
            $"{l.LoadValue:0.00},{l.DistanceI:0},{l.DistanceJ:0}"));

            return ComputeHash(data);
        }

        /// <summary>
        /// Kiểm tra frame có tồn tại trong SAP không
        /// </summary>
        public static bool FrameExists(string frameName)
        {
            var model = GetModel();
            if (model == null) return false;

            int count = 0;
            string[] names = null;
            model.FrameObj.GetNameList(ref count, ref names);

            if (names == null) return false;
            return names.Contains(frameName);
        }

        /// <summary>
        /// Tìm frame mới được tạo/merge gần vị trí cũ
        /// </summary>
        public static string FindReplacementFrame(Point2D oldStart, Point2D oldEnd, double elevation, double tolerance = 500)
        {
            var beams = GetBeamsAtElevation(elevation, 200);

            foreach (var beam in beams)
            {
                double dist1 = Math.Min(
           oldStart.DistanceTo(beam.StartPt),
            oldStart.DistanceTo(beam.EndPt)
           );
                double dist2 = Math.Min(
              oldEnd.DistanceTo(beam.StartPt),
                        oldEnd.DistanceTo(beam.EndPt)
                );

                if (dist1 < tolerance || dist2 < tolerance)
                {
                    return beam.Name;
                }
            }

            return null;
        }

        private static string ComputeHash(string input)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = md5.ComputeHash(inputBytes);
                return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
            }
        }

        #endregion

        #region ROBUST DATABASE TABLE READER (NEW CORE)

        /// <summary>
        /// Helper class để đọc bảng SAP an toàn, không sợ sai index cột.
        /// </summary>
        private class SapTableReader
        {
            private string[] _fields;
            private string[] _data;
            private int _numRecs;
            private int _colCount;
            private Dictionary<string, int> _colMap;

            public int RecordCount => _numRecs;

            public SapTableReader(cSapModel model, string tableName, string loadPatternFilter = null)
            {
                _colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int tableVer = 0;
                string[] input = new string[] { };
                
                // Reset arrays
                _fields = null;
                _data = null;
                _numRecs = 0;

                // Cố gắng đọc bảng
                try
                {
                    // Group "All" lấy toàn bộ, hoặc lọc theo Selection nếu cần
                    // Ở đây ta dùng chiến lược: Đọc hết rồi lọc bằng C# để kiểm soát tốt hơn
                    int ret = model.DatabaseTables.GetTableForDisplayArray(
                        tableName, ref input, "All", ref tableVer, ref _fields, ref _numRecs, ref _data);
                    if (ret == 0 && _numRecs > 0 && _fields != null && _data != null)
                    {
                        _colCount = _fields.Length;
                        // Map tên cột sang index (trim và bỏ rỗng)
                        for (int i = 0; i < _colCount; i++)
                        {
                            var f = _fields[i];
                            if (!string.IsNullOrEmpty(f))
                            {
                                f = f.Trim();
                                if (!_colMap.ContainsKey(f))
                                    _colMap[f] = i;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"SapTableReader: failed reading table '{tableName}': {ex.Message}");
                }
            }

            /// <summary>
            /// Kiểm tra xem cột có tồn tại không
            /// </summary>
            public bool HasColumn(string colName) => _colMap.ContainsKey(colName);

            /// <summary>
            /// Lấy giá trị chuỗi tại dòng row, cột colName
            /// </summary>
            public string GetString(int row, string colName)
            {
                if (row < 0 || row >= _numRecs) return null;
                if (_data == null || _colCount <= 0) return null;
                if (string.IsNullOrEmpty(colName)) return null;
                if (!_colMap.TryGetValue(colName, out int colIdx)) return null;
                int idx = row * _colCount + colIdx;
                if (idx < 0 || idx >= _data.Length) return null;
                return _data[idx];
            }

            /// <summary>
            /// Lấy giá trị double tại dòng row, cột colName. Trả về 0 nếu lỗi/rỗng.
            /// </summary>
            public double GetDouble(int row, string colName)
            {
                string val = GetString(row, colName);
                if (string.IsNullOrEmpty(val)) return 0.0;
                if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double result))
                    return result;
                return 0.0;
            }
        }

        #endregion

        #region AUDIT: Load Pattern Detection (FIXED)

        public class PatternSummary
        {
            public string Name { get; set; }
            public double TotalEstimatedLoad { get; set; }
        }

        /// <summary>
        /// Quét toàn bộ tải trọng (dọc, ngang, momen) để xác định Pattern nào đang hoạt động.
        /// Fix lỗi: Đã bao gồm F1, F2 (ngang) và M1, M2, M3 (momen).
        /// </summary>
        public static List<PatternSummary> GetActiveLoadPatterns()
        {
            var result = new List<PatternSummary>();
            var model = GetModel();
            if (model == null) return result;

            var loadSums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // 1. Quét bảng Joint Loads (Quan trọng nhất cho tải ngang/gió/động đất gán vào tâm)
            // Cột cần quét: F1, F2, F3, M1, M2, M3
            ScanTableSums(model, "Joint Loads - Force", new[] { "F1", "F2", "F3", "M1", "M2", "M3" }, loadSums);

            // 2. Quét bảng Frame Loads - Distributed
            // Cột cần quét: FOverLA, FOverLB (và các biến thể tên cột nếu SAP đổi version)
            ScanTableSums(model, "Frame Loads - Distributed", new[] { "FOverLA", "FOverLB", "FOverL" }, loadSums);

            // 3. Quét bảng Frame Loads - Point
            // Cột cần quét: Force, F1, F2...
            ScanTableSums(model, "Frame Loads - Point", new[] { "Force", "F1", "F2", "F3" }, loadSums);

            // 4. Quét bảng Area Loads
            ScanTableSums(model, "Area Loads - Uniform", new[] { "UnifLoad" }, loadSums);
            ScanTableSums(model, "Area Loads - Uniform To Frame", new[] { "UnifLoad" }, loadSums);

            // Chuyển dictionary thành list kết quả
            foreach (var kvp in loadSums)
            {
                if (kvp.Value > 0.001) // Chỉ lấy pattern có tải đáng kể
                    result.Add(new PatternSummary { Name = kvp.Key, TotalEstimatedLoad = kvp.Value });
            }

            return result.OrderByDescending(p => p.TotalEstimatedLoad).ToList();
        }

        private static void ScanTableSums(cSapModel model, string tableName, string[] valueCols, Dictionary<string, double> accumulator)
        {
            var reader = new SapTableReader(model, tableName);
            if (reader.RecordCount == 0) return;

            // Xác định tên cột chứa Load Pattern (thường là "LoadPat" hoặc "OutputCase")
            string patCol = reader.HasColumn("LoadPat") ? "LoadPat" : (reader.HasColumn("OutputCase") ? "OutputCase" : null);
            if (patCol == null) return;

            for (int r = 0; r < reader.RecordCount; r++)
            {
                string pat = reader.GetString(r, patCol);
                if (string.IsNullOrEmpty(pat)) continue;

                double rowSum = 0;
                foreach (var col in valueCols)
                {
                    // Lấy trị tuyệt đối để cộng dồn mức độ "hoạt động"
                    rowSum += Math.Abs(reader.GetDouble(r, col));
                }

                if (rowSum > 0)
                {
                    if (!accumulator.ContainsKey(pat)) accumulator[pat] = 0;
                    accumulator[pat] += rowSum;
                }
            }
        }

        #endregion

        #region Load Patterns & Stories

        public static bool LoadPatternExists(string patternName)
        {
            var model = GetModel();
            if (model == null) return false;

            int count = 0;
            string[] names = null;
            model.LoadPatterns.GetNameList(ref count, ref names);

            if (names == null) return false;
            foreach (var n in names)
            {
                if (n.Equals(patternName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static List<string> GetLoadPatterns()
        {
            var patterns = new List<string>();
            var model = GetModel();
            if (model == null) return patterns;

            int count = 0;
            string[] names = null;
            model.LoadPatterns.GetNameList(ref count, ref names);
            if (names != null) patterns.AddRange(names);
            return patterns;
        }

        // ...existing code for GridLines, Points, Stories...

        public class GridLineRecord
        {
            public string Name { get; set; }
            public string Orientation { get; set; }
            public double Coordinate { get; set; }
            public override string ToString() => $"{Name}: {Orientation}={Coordinate}";
        }

        public static List<GridLineRecord> GetGridLines()
        {
            var result = new List<GridLineRecord>();
            var model = GetModel();
            if (model == null) return result;

            string[] candidateTableKeys = new[] { "Grid Lines" };

            foreach (var tableKey in candidateTableKeys)
            {
                try
                {
                    int tableVersion = 0;
                    string[] fieldNames = null;
                    string[] tableData = null;
                    int numberRecords = 0;
                    string[] fieldsKeysIncluded = null;

                    string[] fieldKeyListInput = new string[] { "" };
                    int ret = model.DatabaseTables.GetTableForDisplayArray(
                     tableKey,
                 ref fieldKeyListInput, "All", ref tableVersion, ref fieldsKeysIncluded,
                     ref numberRecords, ref tableData
                     );

                    if (ret != 0 || tableData == null || fieldsKeysIncluded == null || numberRecords == 0)
                        continue;

                    int colCount = fieldsKeysIncluded.Length;
                    if (colCount == 0) continue;

                    int axisDirIdx = Array.IndexOf(fieldsKeysIncluded, "AxisDir");
                    if (axisDirIdx < 0) axisDirIdx = Array.FindIndex(fieldsKeysIncluded, f => f != null && f.ToLowerInvariant().Contains("axis"));

                    int gridIdIdx = Array.IndexOf(fieldsKeysIncluded, "GridID");
                    if (gridIdIdx < 0) gridIdIdx = Array.FindIndex(fieldsKeysIncluded, f => f != null && (f.ToLowerInvariant().Contains("gridid") || f.ToLowerInvariant().Contains("grid")));

                    int coordIdx = Array.IndexOf(fieldsKeysIncluded, "XRYZCoord");
                    if (coordIdx < 0) coordIdx = Array.FindIndex(fieldsKeysIncluded, f => f != null && f.ToLowerInvariant().Contains("coord"));

                    for (int r = 0; r < numberRecords; r++)
                    {
                        try
                        {
                            string axisDir = axisDirIdx >= 0 ? tableData[r * colCount + axisDirIdx]?.Trim() : null;
                            string gridId = gridIdIdx >= 0 ? tableData[r * colCount + gridIdIdx]?.Trim() : null;
                            string coordStr = coordIdx >= 0 ? tableData[r * colCount + coordIdx]?.Trim() : null;

                            double coord = 0;
                            if (!string.IsNullOrEmpty(coordStr))
                            {
                                var parts = coordStr.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length > 0)
                                {
                                    var tryS = parts[0];
                                    if (!double.TryParse(tryS, NumberStyles.Any, CultureInfo.InvariantCulture, out coord))
                                    {
                                        double.TryParse(tryS, out coord);
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(axisDir))
                                axisDir = axisDir.Split(new[] { ' ', '_' }, StringSplitOptions.RemoveEmptyEntries)[0];

                            result.Add(new GridLineRecord
                            {
                                Name = gridId ?? string.Empty,
                                Orientation = axisDir ?? string.Empty,
                                Coordinate = coord
                            });
                        }
                        catch { }
                    }

                    if (result.Count > 0) return result;
                }
                catch { }
            }

            return result;
        }

        public static void RefreshView()
        {
            GetModel()?.View.RefreshView();
        }

        #endregion

        #region Points

        public class SapPoint
        {
            public string Name { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
        }

        public static List<SapPoint> GetAllPoints()
        {
            var result = new List<SapPoint>();
            var model = GetModel();
            if (model == null) return result;

            try
            {
                int tableVersion = 0;
                string[] tableData = null;
                int numberRecords = 0;
                string[] fieldsKeysIncluded = null;

                string[] fieldKeyListInput = new string[] { "" };

                int ret = model.DatabaseTables.GetTableForDisplayArray(
                     "Joint Coordinates",
                    ref fieldKeyListInput, "All", ref tableVersion, ref fieldsKeysIncluded,
                ref numberRecords, ref tableData
                     );

                if (ret == 0 && numberRecords > 0 && fieldsKeysIncluded != null && tableData != null)
                {
                    int idxName = Array.IndexOf(fieldsKeysIncluded, "Joint");
                    int idxX = -1, idxY = -1, idxZ = -1;

                    for (int i = 0; i < fieldsKeysIncluded.Length; i++)
                    {
                        var f = fieldsKeysIncluded[i] ?? string.Empty;
                        var fl = f.ToLowerInvariant();
                        if (fl.Contains("x") || fl.Contains("coord1") || fl.Contains("globalx") || fl.Contains("xor")) idxX = i;
                        if (fl.Contains("y") || fl.Contains("coord2") || fl.Contains("globaly")) idxY = i;
                        if (fl.Contains("z") || fl.Contains("coord3") || fl.Contains("globalz")) idxZ = i;
                    }

                    if (idxName >= 0 && idxX >= 0 && idxY >= 0 && idxZ >= 0)
                    {
                        int cols = fieldsKeysIncluded.Length;
                        for (int r = 0; r < numberRecords; r++)
                        {
                            try
                            {
                                string name = tableData[r * cols + idxName] ?? string.Empty;
                                double x = 0, y = 0, z = 0;
                                double.TryParse(tableData[r * cols + idxX], NumberStyles.Any, CultureInfo.InvariantCulture, out x);
                                double.TryParse(tableData[r * cols + idxY], NumberStyles.Any, CultureInfo.InvariantCulture, out y);
                                double.TryParse(tableData[r * cols + idxZ], NumberStyles.Any, CultureInfo.InvariantCulture, out z);

                                result.Add(new SapPoint { Name = name, X = x, Y = y, Z = z });
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"GetAllPoints: row parse failed: {ex}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetAllPoints failed: {ex}");
            }

            return result;
        }

        #endregion

        #region Stories

        public class GridStoryItem
        {
            public string AxisDir { get; set; }
            public string Name { get; set; }
            public double Coordinate { get; set; }
            public bool IsElevation => !string.IsNullOrEmpty(AxisDir) && AxisDir.Trim().StartsWith("Z", StringComparison.OrdinalIgnoreCase);

            public string StoryName
            {
                get => Name;
                set => Name = value;
            }

            public double Elevation
            {
                get => Coordinate;
                set => Coordinate = value;
            }

            public StoryData ToStoryData()
            {
                return new StoryData
                {
                    StoryName = this.StoryName,
                    Elevation = this.Elevation,
                    StoryHeight = 3300
                };
            }

            public override string ToString() => $"{AxisDir}\t{Name}\t{Coordinate}";
        }

        public static List<GridStoryItem> GetStories()
        {
            var result = new List<GridStoryItem>();
            try
            {
                var grids = GetGridLines();
                foreach (var g in grids)
                {
                    result.Add(new GridStoryItem
                    {
                        AxisDir = g.Orientation ?? string.Empty,
                        Name = g.Name ?? string.Empty,
                        Coordinate = g.Coordinate
                    });
                }

                result = result.OrderBy(r => r.AxisDir).ThenBy(r => r.Coordinate).ToList();
            }
            catch { }
            return result;
        }

        #endregion

        #region DATABASE TABLE HELPERS (CORE FIX)

        /// <summary>
        /// Generic helper to read any SAP2000 Database Table safely.
        /// Returns a list of dictionaries (Row -> [ColumnName: Value])
        /// </summary>
        private static List<Dictionary<string, string>> GetSapTableData(string tableName, string loadPatternFilter = null)
        {
            var results = new List<Dictionary<string, string>>();
            var model = GetModel();
            if (model == null) return results;

            try
            {
                int tableVersion = 0;
                string[] fields = null;
                int numRecords = 0;
                string[] tableData = null;
                string[] inputKeys = new string[] { }; // Empty for all

                // Use Group "All" to get everything
                int ret = model.DatabaseTables.GetTableForDisplayArray(
                    tableName,
                    ref inputKeys,
                    "All",
                    ref tableVersion,
                    ref fields,
                    ref numRecords,
                    ref tableData);

                if (ret != 0 || numRecords == 0 || fields == null || tableData == null)
                    return results;

                int colCount = fields.Length;

                // Support multi-pattern filter: split by comma/semicolon/space and use HashSet for fast lookup
                HashSet<string> patternSet = null;
                if (!string.IsNullOrWhiteSpace(loadPatternFilter) && loadPatternFilter != "*")
                {
                    var parts = loadPatternFilter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (parts.Length > 0)
                        patternSet = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
                }

                // Pre-calculate index of LoadPat to filter early if needed
                int loadPatIdx = Array.IndexOf(fields, "LoadPat");
                if (loadPatIdx < 0) loadPatIdx = Array.IndexOf(fields, "OutputCase"); // Some tables use OutputCase

                for (int r = 0; r < numRecords; r++)
                {
                    // Filter by Pattern if requested and column exists
                    if (patternSet != null && loadPatIdx >= 0)
                    {
                        string rowPat = tableData[r * colCount + loadPatIdx]?.Trim();
                        if (string.IsNullOrEmpty(rowPat) || !patternSet.Contains(rowPat))
                            continue;
                    }

                    var rowDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int c = 0; c < colCount; c++)
                    {
                        string key = fields[c]?.Trim();
                        string val = tableData[r * colCount + c];
                        if (key == null) key = string.Empty;
                        rowDict[key] = val;
                    }
                    results.Add(rowDict);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetSapTableData: failed reading table '{tableName}': {ex}");
            }

            return results;
        }

        private static double ParseDouble(string val)
        {
            if (string.IsNullOrEmpty(val)) return 0.0;
            if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double res))
                return res;
            return 0.0;
        }

        /// <summary>
        /// Safe helper to try multiple keys from a row dictionary.
        /// </summary>
        private static string TryGetRowValue(Dictionary<string, string> row, params string[] keys)
        {
            if (row == null || keys == null) return null;
            foreach (var k in keys)
            {
                if (string.IsNullOrEmpty(k)) continue;
                if (row.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v)) return v;
                // fallback: case-insensitive search
                var match = row.Keys.FirstOrDefault(x => !string.IsNullOrEmpty(x) && x.Equals(k, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    var vv = row[match];
                    if (!string.IsNullOrEmpty(vv)) return vv;
                }
            }
            return null;
        }

        #endregion

        #region Extended Load Reading - AUDIT FEATURES

        /// <summary>
        /// Đọc TẤT CẢ tải phân bố trên TOÀN BỘ Frame theo pattern.
        /// Trả về danh sách RawSapLoad để xử lý thống kê.
        /// SỬ DỤNG DATABASE TABLE để đọc nhanh và chính xác.
        /// </summary>
        public static List<RawSapLoad> GetAllFrameDistributedLoads(string patternFilter = null)
        {
            var loads = new List<RawSapLoad>();
            
            // 1. Read Database Table "Frame Loads - Distributed"
            var rows = GetSapTableData("Frame Loads - Distributed", patternFilter);

            // 2. Cache Geometry for Z-coordinates (Optimization)
            var frameGeomMap = new Dictionary<string, double>();
            if (rows.Count > 0)
            {
                var frames = GetAllFramesGeometry();
                foreach (var f in frames) frameGeomMap[f.Name] = f.AverageZ;
            }

            // 3. Parse Rows theo đúng cấu trúc bảng SAP2000
            foreach (var row in rows)
            {
                // Required fields: Frame, LoadPat, FOverLA (hoặc FOverLB), Dir, CoordSys
                if (!row.ContainsKey("Frame") || !row.ContainsKey("LoadPat")) continue;

                string frameName = row["Frame"];
                    string pattern = TryGetRowValue(row, "LoadPat") ?? string.Empty;

                    // Value từ cột FOverLA (Force Over Length at point A)
                    double val1 = ParseDouble(TryGetRowValue(row, "FOverLA") ?? TryGetRowValue(row, "FOverLB") ?? TryGetRowValue(row, "FOverL") ?? "0");

                // Convert Unit (Table returns values in Present Units - typically kN/mm)
                val1 = ConvertLoadToKnPerM(val1);

                // Distance A và B (AbsDistA/AbsDistB hoặc RelDistA/RelDistB)
                double distA = row.ContainsKey("AbsDistA") ? ParseDouble(row["AbsDistA"]) : 0;
                double distB = row.ContainsKey("AbsDistB") ? ParseDouble(row["AbsDistB"]) : 0;
                bool isRelative = row.ContainsKey("DistType") && row["DistType"].Contains("Rel");
                
                // Get Z from cache
                double z = frameGeomMap.ContainsKey(frameName) ? frameGeomMap[frameName] : 0;

                loads.Add(new RawSapLoad
                {
                    ElementName = frameName,
                    LoadPattern = pattern,
                    Value1 = Math.Abs(ConvertLoadToKnPerM(val1)), // Use Abs for magnitude and convert
                    LoadType = "FrameDistributed",
                    Direction = TryGetRowValue(row, "Dir") ?? TryGetRowValue(row, "Direction") ?? "Gravity",
                    DistStart = distA,
                    DistEnd = distB,
                    CoordSys = TryGetRowValue(row, "CoordSys") ?? TryGetRowValue(row, "Coord. Sys.") ?? "GLOBAL",
                    ElementZ = z,
                    IsRelative = isRelative
                });
            }

            return loads;
        }

        /// <summary>
        /// Đọc tải tập trung (Point Load) trên Frame
        /// </summary>
        public static List<RawSapLoad> GetAllFramePointLoads(string patternFilter = null)
        {
            var results = new List<RawSapLoad>();
            var model = GetModel();
            if (model == null) return results;

            try
            {
                int frameCount = 0;
                string[] frameNames = null;
                model.FrameObj.GetNameList(ref frameCount, ref frameNames);
                if (frameCount == 0 || frameNames == null) return results;

                var frameGeometryCache = new Dictionary<string, SapFrame>();
                foreach (var fn in frameNames)
                {
                    var geo = GetFrameGeometry(fn);
                    if (geo != null) frameGeometryCache[fn] = geo;
                }

                foreach (var frameName in frameNames)
                {
                    int numberItems = 0;
                    string[] fNames = null;
                    string[] loadPatterns = null;
                    int[] myTypes = null;
                    string[] csys = null;
                    int[] dirs = null;
                    double[] rd = null;
                    double[] dist = null;
                    double[] val = null;

                    int ret = model.FrameObj.GetLoadPoint(
                        frameName, ref numberItems, ref fNames, ref loadPatterns,
                        ref myTypes, ref csys, ref dirs, ref rd, ref dist, ref val,
                        eItemType.Objects);

                    if (ret != 0 || numberItems == 0) continue;

                    for (int i = 0; i < numberItems; i++)
                    {
                        if (!string.IsNullOrEmpty(patternFilter) &&
                            !loadPatterns[i].Equals(patternFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        double avgZ = 0;
                        if (frameGeometryCache.TryGetValue(frameName, out var geo))
                            avgZ = geo.AverageZ;

                        results.Add(new RawSapLoad
                        {
                            ElementName = frameName,
                            LoadPattern = loadPatterns[i],
                            Value1 = ConvertForceToKn(val[i]),
                            LoadType = "FramePoint",
                            Direction = GetDirectionName(dirs[i]),
                            DistStart = dist[i],
                            IsRelative = rd[i] > 0.5,
                            CoordSys = csys[i],
                            ElementZ = avgZ
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetAllFramePointLoads failed: {ex}");
            }

            return results;
        }

        /// <summary>
        /// Đọc tải đều trên Area (Shell Uniform Load - kN/m²)
        /// SỬ DỤNG DATABASE TABLE "Area Loads - Uniform"
        /// </summary>
        public static List<RawSapLoad> GetAllAreaUniformLoads(string patternFilter = null)
        {
            var loads = new List<RawSapLoad>();
            var rows = GetSapTableData("Area Loads - Uniform", patternFilter);

            var areaGeomMap = new Dictionary<string, double>();
            if (rows.Count > 0)
            {
                var areas = GetAllAreasGeometry();
                foreach (var a in areas) areaGeomMap[a.Name] = a.AverageZ;
            }

            foreach (var row in rows)
            {
                try
                {
                    if (!row.ContainsKey("Area")) continue;

                    // Cột "UnifLoad" chứa giá trị tải (kN/mm²)
                    double val = ParseDouble(TryGetRowValue(row, "UnifLoad") ?? "0");
                    val = ConvertLoadToKnPerM2(val);

                    loads.Add(new RawSapLoad
                    {
                        ElementName = TryGetRowValue(row, "Area") ?? string.Empty,
                        LoadPattern = TryGetRowValue(row, "LoadPat") ?? TryGetRowValue(row, "OutputCase") ?? string.Empty,
                        Value1 = Math.Abs(val),
                        LoadType = "AreaUniform",
                        Direction = TryGetRowValue(row, "Dir") ?? "Gravity",
                        CoordSys = TryGetRowValue(row, "CoordSys") ?? "Local",
                        ElementZ = areaGeomMap.ContainsKey(TryGetRowValue(row, "Area") ?? string.Empty) ? areaGeomMap[TryGetRowValue(row, "Area")] : 0
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"GetAllAreaUniformLoads: row parse failed: {ex}");
                }
            }
            return loads;
        }

        /// <summary>
        /// Đọc tải Area Uniform To Frame (1-way/2-way distribution)
        /// SỬ DỤNG DATABASE TABLE "Area Loads - Uniform To Frame"
        /// </summary>
        public static List<RawSapLoad> GetAllAreaUniformToFrameLoads(string patternFilter = null)
        {
            var loads = new List<RawSapLoad>();
            var rows = GetSapTableData("Area Loads - Uniform To Frame", patternFilter);

            var areaGeomMap = new Dictionary<string, double>();
            if (rows.Count > 0)
            {
                var areas = GetAllAreasGeometry();
                foreach (var a in areas) areaGeomMap[a.Name] = a.AverageZ;
            }

            foreach (var row in rows)
            {
                try
                {
                    if (!row.ContainsKey("Area")) continue;

                    double val = ParseDouble(TryGetRowValue(row, "UnifLoad") ?? "0");
                    val = ConvertLoadToKnPerM2(val);

                    loads.Add(new RawSapLoad
                    {
                        ElementName = TryGetRowValue(row, "Area") ?? string.Empty,
                        LoadPattern = TryGetRowValue(row, "LoadPat") ?? TryGetRowValue(row, "OutputCase") ?? string.Empty,
                        Value1 = Math.Abs(val),
                        LoadType = "AreaUniformToFrame",
                        Direction = TryGetRowValue(row, "Dir") ?? "Gravity",
                        DistributionType = TryGetRowValue(row, "DistType") ?? "Two way",
                        CoordSys = TryGetRowValue(row, "CoordSys") ?? "GLOBAL",
                        ElementZ = areaGeomMap.ContainsKey(TryGetRowValue(row, "Area") ?? string.Empty) ? areaGeomMap[TryGetRowValue(row, "Area")] : 0
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"GetAllAreaUniformToFrameLoads: row parse failed: {ex}");
                }
            }
            return loads;
        }

        /// <summary>
        /// Đọc tải tập trung trên Point/Joint
        /// SỬ DỤNG DATABASE TABLE "Joint Loads - Force"
        /// CORRECTS: Đọc đầy đủ F1, F2, F3 để không bỏ sót tải ngang
        /// </summary>
        public static List<RawSapLoad> GetAllPointLoads(string patternFilter = null)
        {
            var loads = new List<RawSapLoad>();
            var model = GetModel();
            if (model == null) return loads;

            var rows = GetSapTableData("Joint Loads - Force", patternFilter);
            var pointGeomMap = new Dictionary<string, double>();
            if (rows.Count > 0)
            {
                var points = GetAllPoints();
                foreach (var p in points) pointGeomMap[p.Name] = p.Z;
            }

            // Cache load patterns once for existence checking
            HashSet<string> existingPatterns = null;
            try
            {
                existingPatterns = new HashSet<string>(GetLoadPatterns(), StringComparer.OrdinalIgnoreCase);
            }
            catch { existingPatterns = null; }

            foreach (var row in rows)
            {
                try
                {
                    if (!row.ContainsKey("Joint")) continue;
                    string joint = TryGetRowValue(row, "Joint");
                    string pattern = TryGetRowValue(row, "LoadPat") ?? TryGetRowValue(row, "OutputCase") ?? string.Empty;
                    if (!string.IsNullOrEmpty(patternFilter) && !pattern.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Any(p => p.Equals(patternFilter, StringComparison.OrdinalIgnoreCase))) continue;

                    if (existingPatterns != null && !existingPatterns.Contains(pattern)) continue;

                    string cSys = TryGetRowValue(row, "CoordSys") ?? "GLOBAL";
                    double z = pointGeomMap.ContainsKey(joint) ? pointGeomMap[joint] : 0;

                    // Read components
                    double f1 = ParseDouble(TryGetRowValue(row, "F1") ?? "0");
                    double f2 = ParseDouble(TryGetRowValue(row, "F2") ?? "0");
                    double f3 = ParseDouble(TryGetRowValue(row, "F3") ?? "0");

                    if (Math.Abs(f1) > 0.001)
                    {
                        loads.Add(new RawSapLoad
                        {
                            ElementName = joint,
                            LoadPattern = pattern,
                            Value1 = Math.Abs(ConvertForceToKn(f1)),
                            LoadType = "PointForce",
                            Direction = cSys.Equals("Local", StringComparison.OrdinalIgnoreCase) ? "Local-1 (X)" : "Global X",
                            CoordSys = cSys,
                            ElementZ = z
                        });
                    }

                    if (Math.Abs(f2) > 0.001)
                    {
                        loads.Add(new RawSapLoad
                        {
                            ElementName = joint,
                            LoadPattern = pattern,
                            Value1 = Math.Abs(ConvertForceToKn(f2)),
                            LoadType = "PointForce",
                            Direction = cSys.Equals("Local", StringComparison.OrdinalIgnoreCase) ? "Local-2 (Y)" : "Global Y",
                            CoordSys = cSys,
                            ElementZ = z
                        });
                    }

                    if (Math.Abs(f3) > 0.001)
                    {
                        loads.Add(new RawSapLoad
                        {
                            ElementName = joint,
                            LoadPattern = pattern,
                            Value1 = Math.Abs(ConvertForceToKn(f3)),
                            LoadType = "PointForce",
                            Direction = cSys.Equals("Local", StringComparison.OrdinalIgnoreCase) ? "Local-3 (Z)" : "Gravity",
                            CoordSys = cSys,
                            ElementZ = z
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"GetAllPointLoads: row parse failed: {ex}");
                }
            }

            return loads;
        }

        /// <summary>
        /// Đọc Joint Mass (khối lượng tham gia dao động)
        /// </summary>
        public static List<RawSapLoad> GetAllJointMasses()
        {
            var results = new List<RawSapLoad>();
            var model = GetModel();
            if (model == null) return results;

            try
            {
                int pointCount = 0;
                string[] pointNames = null;
                model.PointObj.GetNameList(ref pointCount, ref pointNames);
                if (pointCount == 0 || pointNames == null) return results;

                var pointCache = GetAllPoints().ToDictionary(p => p.Name, p => p);

                foreach (var pointName in pointNames)
                {
                    double[] m = new double[6];

                    int ret = model.PointObj.GetMass(pointName, ref m);

                    if (ret != 0) continue;

                    // m[0], m[1], m[2] = mass in X, Y, Z directions
                    double totalMass = m[0] + m[1] + m[2];
                    if (totalMass < 0.001) continue;

                    double z = 0;
                    if (pointCache.TryGetValue(pointName, out var pt))
                        z = pt.Z;

                    results.Add(new RawSapLoad
                    {
                        ElementName = pointName,
                        LoadPattern = "MASS",
                        Value1 = totalMass, // kg or kN*s²/m depending on units
                        LoadType = "JointMass",
                        Direction = "All",
                        ElementZ = z
                    });
                }
            }
            catch { }

            return results;
        }

        /// <summary>
        /// Lấy tất cả Area Geometry (boundary points)
        /// </summary>
        public static List<SapArea> GetAllAreasGeometry()
        {
            var results = new List<SapArea>();
            var model = GetModel();
            if (model == null) return results;

            try
            {
                int areaCount = 0;
                string[] areaNames = null;
                model.AreaObj.GetNameList(ref areaCount, ref areaNames);
                if (areaCount == 0 || areaNames == null) return results;

                foreach (var areaName in areaNames)
                {
                    var area = GetAreaGeometry(areaName);
                    if (area != null)
                        results.Add(area);
                }
            }
            catch { }

            return results;
        }

        /// <summary>
        /// Lấy geometry của một Area
        /// </summary>
        public static SapArea GetAreaGeometry(string areaName)
        {
            var model = GetModel();
            if (model == null) return null;

            try
            {
                int numPoints = 0;
                string[] pointNames = null;

                int ret = model.AreaObj.GetPoints(areaName, ref numPoints, ref pointNames);
                if (ret != 0 || numPoints < 3 || pointNames == null) return null;

                var area = new SapArea
                {
                    Name = areaName,
                    BoundaryPoints = new List<Point2D>(),
                    ZValues = new List<double>(),
                    JointNames = pointNames.ToList()
                };

                foreach (var pName in pointNames)
                {
                    double x = 0, y = 0, z = 0;
                    ret = model.PointObj.GetCoordCartesian(pName, ref x, ref y, ref z, "Global");
                    if (ret == 0)
                    {
                        area.BoundaryPoints.Add(new Point2D(x, y));
                        area.ZValues.Add(z);
                    }
                }

                if (area.BoundaryPoints.Count < 3) return null;

                return area;
            }
            catch
            {
                return null;
            }
        }


        /// <summary>
        /// Lấy phản lực đáy tổng cộng theo Load Pattern và Hướng.
        /// Hỗ trợ kiểm tra tải trọng ngang (Gió/Động đất).
        /// </summary>
        /// <param name="loadPattern">Tên Load Case/Pattern</param>
        /// <param name="direction">Hướng: "X", "Y", "Z" (Mặc định Z)</param>
        /// <returns>Giá trị phản lực (kN)</returns>
        public static double GetBaseReaction(string loadPattern, string direction = "Z")
        {
            var model = GetModel();
            if (model == null) return 0;

            try
            {
                model.Results.Setup.DeselectAllCasesAndCombosForOutput();
                model.Results.Setup.SetCaseSelectedForOutput(loadPattern);

                int tableVer = 0;
                string[] fields = null;
                int numRec = 0;
                string[] tableData = null;
                string[] input = new string[] { "" };

                int ret = model.DatabaseTables.GetTableForDisplayArray(
                    "Base Reactions",
                    ref input, "All", ref tableVer, ref fields, ref numRec, ref tableData);

                if (ret == 0 && numRec > 0 && fields != null && tableData != null)
                {
                    int idxCase = Array.IndexOf(fields, "OutputCase");
                    if (idxCase < 0) idxCase = Array.FindIndex(fields, f => f != null && f.ToLowerInvariant().Contains("case"));

                    // Xác định cột dữ liệu dựa trên hướng yêu cầu
                    string targetField = "GlobalFZ"; // Default Z
                    if (direction.ToUpper() == "X") targetField = "GlobalFX";
                    if (direction.ToUpper() == "Y") targetField = "GlobalFY";

                    int idxForce = Array.IndexOf(fields, targetField);
                    if (idxForce < 0) 
                    {
                        // Fallback tìm kiếm linh hoạt
                        idxForce = Array.FindIndex(fields, f => f != null && (f.Contains(direction) && f.Contains("Global")));
                    }

                    if (idxCase >= 0 && idxForce >= 0)
                    {
                        double totalForce = 0;
                        int cols = fields.Length;

                        for (int r = 0; r < numRec; r++)
                        {
                            string rowCase = tableData[r * cols + idxCase];
                            if (rowCase != null && rowCase.Equals(loadPattern, StringComparison.OrdinalIgnoreCase))
                            {
                                // StepType thường là "Combination" hoặc "Max"/"Min" nếu là envelop
                                // Ở đây ta cộng dồn hoặc lấy giá trị đầu tiên (Base Reaction thường chỉ có 1 dòng cho Static Load)
                                if (double.TryParse(tableData[r * cols + idxForce],
                                    NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                                {
                                    totalForce = val; // Base Reaction thường là tổng rồi, không cần cộng dồn nếu có nhiều dòng step
                                    break; 
                                }
                            }
                        }

                        // Convert từ đơn vị SAP sang kN (Internal Standard)
                        return ConvertForceToKn(totalForce);
                    }
                }
            }
            catch { }

            return 0;
        }

        // [Backward Compatibility Method]
        public static double GetBaseReactionZ(string loadPattern) => GetBaseReaction(loadPattern, "Z");

        /// <summary>
        /// Lấy tên Model SAP2000 hiện tại
        /// </summary>
        public static string GetModelName()
        {
            var model = GetModel();
            if (model == null) return "Unknown";

            try
            {
                return System.IO.Path.GetFileName(model.GetModelFilename());
            }
            catch
            {
                return "Unknown";
            }
        }

        #endregion

        #region Unit Conversion Helpers

        /// <summary>
        /// Chuyển đổi lực từ SAP sang kN
        /// </summary>
        private static double ConvertForceToKn(double sapValue)
        {
            string forceUnit = UnitManager.Info.ForceUnit.ToLowerInvariant();
            switch (forceUnit)
            {
                case "n":
                    return sapValue / 1000.0; // N -> kN
                case "kgf":
                    return sapValue * 0.00981; // kgf -> kN
                case "ton":
                    return sapValue * 9.81; // Ton -> kN
                case "lb":
                    return sapValue * 0.00445; // lb -> kN
                case "kip":
                    return sapValue * 4.448; // kip -> kN
                case "kn":
                default:
                    return sapValue; // kN
            }
        }

        /// <summary>
        /// Chuyển đổi tải diện tích từ SAP sang kN/m²
        /// </summary>
        private static double ConvertLoadToKnPerM2(double sapValue)
        {
            string lengthUnit = UnitManager.Info.LengthUnit.ToLowerInvariant();

            // Tải diện tích = Force / Length²
            // Nếu SAP dùng mm: val (kN/mm²) = val * 1000000 kN/m²
            // Nếu SAP dùng m: val đã là kN/m²

            switch (lengthUnit)
            {
                case "mm":
                    return sapValue * 1000000.0; // kN/mm² -> kN/m²
                case "cm":
                    return sapValue * 10000.0; // kN/cm² -> kN/m²
                case "m":
                    return sapValue; // kN/m²
                case "in":
                    return sapValue * 1550.0; // kN/in² -> kN/m² (approx)
                case "ft":
                    return sapValue * 10.764; // kN/ft² -> kN/m² (approx)
                default:
                    return sapValue * 1000000.0; // Default mm
            }
        }

        #endregion
    }
}