using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Primitives;
using SAP2000v1;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Utils
{
    /// <summary>
    /// Tiện ích kết nối và làm việc với SAP2000
    /// Hỗ trợ đồng bộ 2 chiều: Đọc/Ghi tải trọng
    /// </summary>
    public static class SapUtils
    {
        #region Connection

        private static cOAPI _sapObject = null;
        private static cSapModel _sapModel = null;

        public static bool Connect(out string message)
        {
            _sapObject = null;
            _sapModel = null;
            message = "";

            try
            {
                cHelper myHelper = new Helper();
                _sapObject = myHelper.GetObject("CSI. SAP2000. API. SapObject");

                if (_sapObject != null)
                {
                    _sapModel = _sapObject.SapModel;
                    _sapModel.SetPresentUnits(eUnits.kN_mm_C);

                    string modelName = "Unknown";
                    try { modelName = System.IO.Path.GetFileName(_sapModel.GetModelFilename()); } catch { }

                    message = $"Kết nối thành công: {modelName}";
                    return true;
                }
                else
                {
                    message = "Không tìm thấy SAP2000 đang chạy.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = $"Lỗi kết nối SAP: {ex.Message}";
                return false;
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
            ret = model.PointObj.GetCoordCartesian(p1Name, ref x1, ref y1, ref z1);
            if (ret != 0) return null;

            double x2 = 0, y2 = 0, z2 = 0;
            ret = model.PointObj.GetCoordCartesian(p2Name, ref x2, ref y2, ref z2);
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
        /// Đọc tất cả tải phân bố trên một frame
        /// </summary>
        public static List<SapLoadInfo> GetFrameDistributedLoads(string frameName, string loadPattern = null)
        {
            var loads = new List<SapLoadInfo>();
            var model = GetModel();
            if (model == null) return loads;

            try
            {
                int numberItems = 0;
                string[] frameNames = null;
                string[] loadPatterns = null;
                int[] myTypes = null;
                string[] csys = null;
                int[] dirs = null;
                double[] rd1 = null, rd2 = null;
                double[] dist1 = null, dist2 = null;
                double[] val1 = null, val2 = null;

                int ret = model.FrameObj.GetLoadDistributed(
                    frameName,
                    ref numberItems,
                    ref frameNames,
                    ref loadPatterns,
                    ref myTypes,
                    ref csys,
                    ref dirs,
                    ref rd1, ref rd2,
                    ref dist1, ref dist2,
                    ref val1, ref val2,
                    eItemType.Objects
                );

                if (ret != 0 || numberItems == 0) return loads;

                for (int i = 0; i < numberItems; i++)
                {
                    // Lọc theo pattern nếu có
                    if (!string.IsNullOrEmpty(loadPattern) &&
                        !loadPatterns[i].Equals(loadPattern, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var loadInfo = new SapLoadInfo
                    {
                        FrameName = frameNames[i],
                        LoadPattern = loadPatterns[i],
                        LoadValue = val1[i] * 1000.0, // kN/mm -> kN/m
                        DistanceI = dist1[i],
                        DistanceJ = dist2[i],
                        Direction = GetDirectionName(dirs[i]),
                        LoadType = "Distributed"
                    };

                    loads.Add(loadInfo);
                }
            }
            catch { }

            return loads;
        }

        /// <summary>
        /// Đọc tổng tải trọng trên frame theo pattern
        /// </summary>
        public static double GetFrameTotalLoad(string frameName, string loadPattern)
        {
            var loads = GetFrameDistributedLoads(frameName, loadPattern);
            if (loads.Count == 0) return 0;

            // Tính tổng tải (có thể có nhiều đoạn)
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

            // Lấy tất cả frame names
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
        /// Gán tải phân bố lên frame
        /// </summary>
        public static bool AssignDistributedLoad(string frameName, string loadPattern,
            double loadValue, double distI = 0, double distJ = 0, bool isRelative = false)
        {
            var model = GetModel();
            if (model == null) return false;

            try
            {
                // Đổi đơn vị: kN/m -> kN/mm
                double val_kN_mm = loadValue / 1000.0;

                int ret = model.FrameObj.SetLoadDistributed(
                    frameName,
                    loadPattern,
                    1,          // Force/Length
                    10,         // Gravity Direction
                    distI, distJ,
                    val_kN_mm, val_kN_mm,
                    "Global",
                    isRelative,
                    true,       // Replace existing
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

            // Hash = "X1,Y1,Z1|X2,Y2,Z2"
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
                // Kiểm tra overlap với vị trí cũ
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

        public static List<StoryData> GetStories()
        {
            var stories = new List<StoryData>();
            var model = GetModel();
            if (model == null) return stories;

            int tableVersion = 0;
            string[] fieldNames = null;
            string[] tableData = null;
            int numberRecords = 0;
            string[] fieldsKeysIncluded = null;

            int ret = model.DatabaseTables.GetTableForDisplayArray(
                "Story Definitions",
                ref fieldNames, "", ref tableVersion, ref fieldsKeysIncluded,
                ref numberRecords, ref tableData
            );

            if (ret == 0 && tableData != null && fieldNames != null)
            {
                int colCount = fieldNames.Length;
                int storyNameIdx = Array.IndexOf(fieldNames, "Story");
                int elevationIdx = Array.IndexOf(fieldNames, "Elevation");
                int heightIdx = Array.IndexOf(fieldNames, "Height");

                for (int i = 0; i < numberRecords; i++)
                {
                    var storyData = new StoryData();
                    if (storyNameIdx >= 0) storyData.StoryName = tableData[i * colCount + storyNameIdx];
                    if (elevationIdx >= 0 && double.TryParse(tableData[i * colCount + elevationIdx], out double elev))
                        storyData.Elevation = elev;
                    if (heightIdx >= 0 && double.TryParse(tableData[i * colCount + heightIdx], out double h))
                        storyData.StoryHeight = h;

                    stories.Add(storyData);
                }
            }
            return stories;
        }

        public static void RefreshView()
        {
            GetModel()?.View.RefreshView();
        }

        #endregion
    }
}