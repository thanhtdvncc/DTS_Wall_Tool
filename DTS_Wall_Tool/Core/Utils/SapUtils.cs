using System;
using System.Collections.Generic;
using SAP2000v1;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Primitives;

namespace DTS_Wall_Tool.Core.Utils
{
    /// <summary>
    /// Tiện ích kết nối và làm việc với SAP2000
    /// </summary>
    public static class SapUtils
    {
        #region Connection

        private static cOAPI _sapObject = null;
        private static cSapModel _sapModel = null;

        /// <summary>
        /// Kết nối đến SAP2000 đang chạy
        /// </summary>
        public static bool Connect(out string message)
        {
            try
            {
                if (_sapModel != null)
                {
                    message = "Đã kết nối SAP2000. ";
                    return true;
                }

                // Kết nối đến SAP2000 đang chạy
                _sapObject = (cOAPI)System.Runtime.InteropServices.Marshal.GetActiveObject("CSI.SAP2000. API. SapObject");

                if (_sapObject == null)
                {
                    message = "Không tìm thấy SAP2000 đang chạy! ";
                    return false;
                }

                _sapModel = _sapObject.SapModel;

                if (_sapModel == null)
                {
                    message = "Không lấy được SapModel!";
                    return false;
                }

                string modelPath = "";
                modelPath = _sapModel.GetModelFilename();
                message = $"Kết nối SAP2000 thành công: {System.IO.Path.GetFileName(modelPath)}";
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                message = "SAP2000 chưa được mở hoặc không có model nào đang active! ";
                return false;
            }
            catch (Exception ex)
            {
                message = $"Lỗi kết nối: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Ngắt kết nối
        /// </summary>
        public static void Disconnect()
        {
            _sapModel = null;
            _sapObject = null;
        }

        /// <summary>
        /// Kiểm tra đã kết nối chưa
        /// </summary>
        public static bool IsConnected => _sapModel != null;

        /// <summary>
        /// Lấy SapModel
        /// </summary>
        public static cSapModel GetModel()
        {
            if (_sapModel == null)
            {
                Connect(out _);
            }
            return _sapModel;
        }

        #endregion

        #region Frame Data

        /// <summary>
        /// Đếm số lượng Frame
        /// </summary>
        public static int CountFrames()
        {
            var model = GetModel();
            if (model == null) return -1;

            int count = 0;
            string[] names = null;
            int ret = model.FrameObj.GetNameList(ref count, ref names);

            return (ret == 0) ? count : 0;
        }

        /// <summary>
        /// Lấy tất cả geometry của Frame
        /// </summary>
        public static List<SapFrame> GetAllFramesGeometry()
        {
            var frames = new List<SapFrame>();
            var model = GetModel();
            if (model == null) return frames;

            int count = 0;
            string[] names = null;
            int ret = model.FrameObj.GetNameList(ref count, ref names);

            if (ret != 0 || names == null) return frames;

            foreach (string frameName in names)
            {
                var frame = GetFrameGeometry(frameName);
                if (frame != null)
                {
                    frames.Add(frame);
                }
            }

            return frames;
        }

        /// <summary>
        /// Lấy geometry của một Frame
        /// </summary>
        public static SapFrame GetFrameGeometry(string frameName)
        {
            var model = GetModel();
            if (model == null) return null;

            string point1 = "", point2 = "";
            int ret = model.FrameObj.GetPoints(frameName, ref point1, ref point2);
            if (ret != 0) return null;

            double x1 = 0, y1 = 0;
            double x2 = 0, y2 = 0, z2 = 0;

            ret = model.PointObj.GetCoordCartesian(point1, ref x1, ref y1, ref z2);
            if (ret != 0) return null;

            ret = model.PointObj.GetCoordCartesian(point2, ref x2, ref y2, ref z2);
            if (ret != 0) return null;

            return new SapFrame
            {
                Name = frameName,
                StartPt = new Point2D(x1, y1),
                EndPt = new Point2D(x2, y2),
                Z1 = z2,
                Z2 = z2
            };
        }

        /// <summary>
        /// Lấy danh sách dầm (bỏ qua cột)
        /// </summary>
        public static List<SapFrame> GetBeams()
        {
            var allFrames = GetAllFramesGeometry();
            var beams = new List<SapFrame>();

            foreach (var frame in allFrames)
            {
                if (frame.IsBeam)
                {
                    beams.Add(frame);
                }
            }

            return beams;
        }

        /// <summary>
        /// Lấy danh sách dầm theo cao độ
        /// </summary>
        public static List<SapFrame> GetBeamsAtElevation(double elevation, double tolerance = 200)
        {
            var beams = GetBeams();
            var result = new List<SapFrame>();

            foreach (var beam in beams)
            {
                double beamZ = beam.AverageZ;
                if (Math.Abs(beamZ - elevation) <= tolerance)
                {
                    result.Add(beam);
                }
            }

            return result;
        }

        #endregion

        #region Load Assignment

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
                // Loại tải: 1 = Force per unit length
                int myType = 1;
                // Hướng tải: 10 = Gravity direction (Global -Z)
                int dir = 10;
                // Khoảng cách tương đối hay tuyệt đối
                bool relDist = isRelative;

                int ret = model.FrameObj.SetLoadDistributed(
                    frameName,
                    loadPattern,
                    myType,
                    dir,
                    distI,          // Khoảng cách từ đầu I
                    distJ,          // Khoảng cách từ đầu I đến cuối tải
                    loadValue,      // Giá trị tải tại đầu
                    loadValue,      // Giá trị tải tại cuối (đều)
                    "Global",
                    relDist,
                    true,           // Replace existing
                    eItemType.Objects
                );

                return ret == 0;
            }
            catch (Exception)
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
        /// Xóa tất cả tải trên frame theo load pattern
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

        #endregion

        #region Load Patterns

        /// <summary>
        /// Kiểm tra load pattern có tồn tại không
        /// </summary>
        public static bool LoadPatternExists(string patternName)
        {
            var model = GetModel();
            if (model == null) return false;

            int count = 0;
            string[] names = null;
            model.LoadPatterns.GetNameList(ref count, ref names);

            if (names == null) return false;

            foreach (string name in names)
            {
                if (name.Equals(patternName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Lấy danh sách load patterns
        /// </summary>
        public static List<string> GetLoadPatterns()
        {
            var patterns = new List<string>();
            var model = GetModel();
            if (model == null) return patterns;

            int count = 0;
            string[] names = null;
            model.LoadPatterns.GetNameList(ref count, ref names);

            if (names != null)
            {
                patterns.AddRange(names);
            }
            return patterns;
        }

        #endregion

        #region Stories

        /// <summary>
        /// Lấy danh sách tầng từ SAP2000
        /// </summary>
        public static List<StoryData> GetStories()
        {
            var stories = new List<StoryData>();
            var model = GetModel();
            if (model == null) return stories;

            // SAP2000 stores story data in a database table called "Story Definitions"
            int tableVersion = 0;
            string[] fieldNames = null;
            string[] tableData = null;
            int numberRecords = 0;
            string[] fieldsKeysIncluded = null;

            // Use GetTableForDisplayArray instead of GetTableForDisplay
            int ret = model.DatabaseTables.GetTableForDisplayArray(
                "Story Definitions",
                ref fieldNames,
                "",
                ref tableVersion,
                ref fieldsKeysIncluded,
                ref numberRecords,
                ref tableData
            );

            if (ret == 0 && tableData != null && fieldNames != null)
            {
                int colCount = fieldNames.Length;

                // Find column indices for StoryName, Elevation, and StoryHeight
                int storyNameIdx = Array.IndexOf(fieldNames, "Story");
                int elevationIdx = Array.IndexOf(fieldNames, "Elevation");
                int heightIdx = Array.IndexOf(fieldNames, "Height");

                for (int i = 0; i < numberRecords; i++)
                {
                    var storyData = new StoryData();
                    if (storyNameIdx >= 0)
                        storyData.StoryName = tableData[i * colCount + storyNameIdx];
                    if (elevationIdx >= 0 && double.TryParse(tableData[i * colCount + elevationIdx], out double elev))
                        storyData.Elevation = elev;
                    if (heightIdx >= 0 && double.TryParse(tableData[i * colCount + heightIdx], out double h))
                        storyData.StoryHeight = h;

                    stories.Add(storyData);
                }
            }

            return stories;
        }

        #endregion

        #region Model Operations

        /// <summary>
        /// Refresh view trong SAP2000
        /// </summary>
        public static void RefreshView()
        {
            var model = GetModel();
            model?.View.RefreshView();
        }

        /// <summary>
        /// Lấy đường dẫn file model
        /// </summary>
        public static string GetModelPath()
        {
            var model = GetModel();
            if (model == null) return "";

            // string path = "";
            // model.GetModelFilename(ref path);
            // return path;

            // Fix: Call GetModelFilename without 'ref'
            return model.GetModelFilename();
        }

        #endregion
    }
}