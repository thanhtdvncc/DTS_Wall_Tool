using System;
using System.Collections.Generic;
using SAP2000v1; // Bắt buộc Reference SAP2000v1 (Copy Local=True, Embed=False)
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Primitives;

namespace DTS_Wall_Tool.Core.Utils
{
    /// <summary>
    /// Tiện ích kết nối và làm việc với SAP2000 (Phiên bản chuẩn v26)
    /// </summary>
    public static class SapUtils
    {
        #region Connection

        private static cOAPI _sapObject = null;
        private static cSapModel _sapModel = null;

        /// <summary>
        /// Kết nối đến SAP2000 đang chạy (Sử dụng Helper chuẩn)
        /// </summary>
        public static bool Connect(out string message)
        {
            _sapObject = null;
            _sapModel = null;
            try
            {
                // 1. Dùng Helper - Cách duy nhất ổn định cho SAP v26+
                cHelper myHelper = new Helper();

                // 2. Lấy object đang chạy
                _sapObject = myHelper.GetObject("CSI.SAP2000.API.SapObject");

                if (_sapObject != null)
                {
                    // 3. Lấy Model
                    _sapModel = _sapObject.SapModel;

                    // 4. Thiết lập đơn vị chuẩn (kN, mm, C) để đồng bộ với CAD
                    // eUnits.kN_mm_C tương đương enum 5
                    _sapModel.SetPresentUnits(eUnits.kN_mm_C);

                    // Lấy tên file để confirm
                    string modelName = "Unknown";
                    try { modelName = System.IO.Path.GetFileName(_sapModel.GetModelFilename()); } catch { }

                    message = $"Kết nối thành công tới: {modelName}";
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
        /// Lấy SapModel (Tự động kết nối nếu cần)
        /// </summary>
        public static cSapModel GetModel()
        {
            if (_sapModel == null)
            {
                Connect(message: out string msg);
            }
            return _sapModel;
        }

        #endregion

        #region Frame Geometry

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

        /// <summary>
        /// Lấy geometry của một Frame (Sửa lỗi mất Z)
        /// </summary>
        public static SapFrame GetFrameGeometry(string frameName)
        {
            var model = GetModel();
            if (model == null) return null;

            string p1Name = "", p2Name = "";
            int ret = model.FrameObj.GetPoints(frameName, ref p1Name, ref p2Name);
            if (ret != 0) return null;

            // Lấy tọa độ điểm 1
            double x1 = 0, y1 = 0, z1 = 0;
            ret = model.PointObj.GetCoordCartesian(p1Name, ref x1, ref y1, ref z1);
            if (ret != 0) return null;

            // Lấy tọa độ điểm 2 (SỬA LỖI: Dùng biến riêng z2)
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

        /// <summary>
        /// Lấy danh sách dầm theo cao độ (Có dung sai)
        /// </summary>
        public static List<SapFrame> GetBeamsAtElevation(double elevation, double tolerance = 200)
        {
            var result = new List<SapFrame>();
            var allFrames = GetAllFramesGeometry();

            foreach (var f in allFrames)
            {
                // Bỏ qua cột
                if (f.IsVertical) continue;

                // Kiểm tra cao độ trung bình
                double avgZ = (f.Z1 + f.Z2) / 2.0;
                if (Math.Abs(avgZ - elevation) <= tolerance)
                {
                    result.Add(f);
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
                // Đổi đơn vị: kN/m -> kN/mm (Vì SAP đang set mm)
                double val_kN_mm = loadValue / 1000.0;

                int ret = model.FrameObj.SetLoadDistributed(
                    frameName,
                    loadPattern,
                    1,          // Force/Length
                    10,         // Gravity Direction
                    distI, distJ,
                    val_kN_mm, val_kN_mm,
                    "Global",
                    isRelative, // False = Absolute (mm)
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

        #region Load Patterns & Stories

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
            foreach (var n in names)
            {
                if (n.Equals(patternName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Lấy danh sách Load Patterns
        /// </summary>
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

        /// <summary>
        /// Lấy danh sách tầng từ SAP2000
        /// </summary>
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

            // Lấy bảng "Story Definitions"
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
                        storyData.Elevation = elev; // Đơn vị mm (do đã SetPresentUnits)
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