using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using DTS_Wall_Tool.Core.Data;

namespace DTS_Wall_Tool.Core.Utils
{
    /// <summary>
    /// Tiện ích đọc/ghi XData dạng JSON
    /// </summary>
    public static class XDataUtils
    {
        private const string APP_NAME = "DTS_APP";
        private const int CHUNK_SIZE = 250;

        #region High-Level: WallData

        /// <summary>
        /// Đọc WallData từ entity
        /// </summary>
        public static WallData ReadWallData(DBObject obj, Transaction tr = null)
        {
            var dict = GetEntityData(obj);
            // Kiểm tra chặt chẽ hơn

            if (dict.Count == 0) return null;
            if (dict.ContainsKey("xType") || dict["xType"].ToString() != "WALL") return null;

            WallData data = new WallData();

            if (dict.ContainsKey("xBaseZ")) data.BaseZ = ConvertToDouble(dict["xBaseZ"]);
            if (dict.ContainsKey("xThickness")) data.Thickness = ConvertToDouble(dict["xThickness"]);
            if (dict.ContainsKey("xWallType")) data.WallType = Convert.ToString(dict["xWallType"]);
            if (dict.ContainsKey("xLoadPattern")) data.LoadPattern = Convert.ToString(dict["xLoadPattern"]);
            if (dict.ContainsKey("xLoadValue")) data.LoadValue = ConvertToDouble(dict["xLoadValue"]);
            if (dict.ContainsKey("xOriginHandle")) data.OriginHandle = Convert.ToString(dict["xOriginHandle"]);
            if (dict.ContainsKey("xChildHandles")) data.ChildHandles=ConvertToStringList(dict["xChildHandles"]);
            if (dict.ContainsKey("xMappings"))data.Mappings = ConvertToMappingList(dict["xMappings"]);
    

            return data;
        }

        /// <summary>
        /// [MỚI] Hàm đọc nhanh loại đối tượng từ XData mà không cần deserialize toàn bộ
        /// </summary>
        public static ElementType GetElementType(DBObject obj)
        {
            var dict = GetEntityData(obj);
            if (dict == null || !dict.ContainsKey("xType")) return ElementType.Unknown;

            string typeStr = dict["xType"].ToString();

            // Mapping chuỗi sang Enum
            if (typeStr == "BEAM") return ElementType.Beam;
            if (typeStr == "COLUMN") return ElementType.Column;
            if (typeStr == "SLAB") return ElementType.Slab;
            if (typeStr == "WALL") return ElementType.Wall;
            if (typeStr == "FOUNDATION") return ElementType.Foundation;
            if (typeStr == "STAIR") return ElementType.Stair;
            if (typeStr == "PILE") return ElementType.Pile;
            if (typeStr == "LINTEL") return ElementType.Lintel;
            if (typeStr == "REBAR") return ElementType.Rebar;
            if (typeStr == "STORY_ORIGIN") return ElementType.StoryOrigin;

            return ElementType.Unknown;
        }


        /// <summary>
        /// Ghi WallData vào entity (merge với dữ liệu cũ)
        /// Trả về False nếu đối tượng đang là loại khác (vd. COLUMN, BEAM)
        /// Chỉ cập nhật các trường có giá trị (null thì không ghi)
        /// </summary>
        public static bool SaveWallData(DBObject obj, WallData data, Transaction tr)
        {
            //1. Kiểm tra loại đối tượng hiện tại
            ElementType currentType = GetElementType(obj);

            // Nếu đã có dữ liệu khác loại, từ chối ghi
            if (currentType != ElementType.Unknown && currentType != ElementType.Wall)
            {
                return false;
            }

            // 2. Chuẩn bị dữ liệu cập nhật
            var updates = new Dictionary<string, object>();

            // Định danh loại (trường hợp mới hoàn toàn)
            updates["xType"] = "WALL";


            // 3. Xử lý các trường dữ liệu (chỉ ghi các trường có giá trị)
            // Nếu data.Thickess là null thì không ghi
            // Muốn xóa trường thì dùng ClearElementData

            if (data.Thickness.HasValue) updates["xThickness"] = data.Thickness.Value;
            if (data.WallType != null) updates["xWallType"] = data.WallType;
            if (data.LoadPattern != null) updates["xLoadPattern"] = data.LoadPattern;
            if (data.LoadValue.HasValue) updates["xLoadValue"] = data.LoadValue.Value;
            if (data.OriginHandle != null) updates["xOriginHandle"] = data.OriginHandle;
            if (data.BaseZ.HasValue) updates["xBaseZ"] = data.BaseZ.Value;

            if (data.ChildHandles != null && data.ChildHandles.Count > 0)
            {
                updates["xChildHandles"] = data.ChildHandles;
            }

            if (data.Mappings != null && data.Mappings.Count > 0)
            {
                updates["xMappings"] = ConvertMappingsToSerializable(data.Mappings);
            }

            // 4. Cập nhật dữ liệu
            UpdateData(obj, updates, tr);
            return true;
        }

        /// <summary>
        /// Xóa Element Data khỏi entity
        /// </summary>
        public static void ClearElementData(DBObject obj, Transaction tr)
        {   
            ClearEntityData(obj, tr);
        }

        #endregion

        #region High-Level: StoryData

        /// <summary>
        /// Ghi StoryData vào entity
        /// </summary>
        public static void WriteStoryData(DBObject obj, StoryData data, Transaction tr)
        {
            var dict = new Dictionary<string, object>();
            dict["xType"] = "STORY_ORIGIN";
            dict["xStoryName"] = data.StoryName;
            dict["xElevation"] = data.Elevation;
            dict["xStoryHeight"] = data.StoryHeight;
            dict["xOffsetX"] = data.OffsetX;
            dict["xOffsetY"] = data.OffsetY;
            SetEntityData(obj, dict, tr);
        }

        /// <summary>
        /// Đọc StoryData từ entity
        /// </summary>
        public static StoryData ReadStoryData(DBObject obj, Transaction tr = null)
        {
            var dict = GetEntityData(obj);
            if (dict == null || !dict.ContainsKey("xType")) return null;
            if (dict["xType"].ToString() != "STORY_ORIGIN") return null;

            StoryData data = new StoryData();
            if (dict.ContainsKey("xStoryName")) data.StoryName = dict["xStoryName"].ToString();
            if (dict.ContainsKey("xElevation")) data.Elevation = ConvertToDouble(dict["xElevation"]) ?? 0;
            if (dict.ContainsKey("xStoryHeight")) data.StoryHeight = ConvertToDouble(dict["xStoryHeight"]) ?? 3300;
            if (dict.ContainsKey("xOffsetX")) data.OffsetX = ConvertToDouble(dict["xOffsetX"]) ?? 0;
            if (dict.ContainsKey("xOffsetY")) data.OffsetY = ConvertToDouble(dict["xOffsetY"]) ?? 0;
            return data;
        }

        #endregion

        #region Low-Level: Generic Data Access

        /// <summary>
        /// Đọc dữ liệu JSON từ XData
        /// </summary>
        public static Dictionary<string, object> GetEntityData(DBObject obj)
        {
            var dict = new Dictionary<string, object>();
            ResultBuffer rb = obj.GetXDataForApplication(APP_NAME);
            if (rb == null) return dict;

            StringBuilder jsonBuilder = new StringBuilder();
            foreach (TypedValue tv in rb)
            {
                if (tv.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    jsonBuilder.Append(tv.Value.ToString());
            }
            string jsonStr = jsonBuilder.ToString();
            if (string.IsNullOrEmpty(jsonStr)) return dict;

            try
            {
                var serializer = new JavaScriptSerializer();
                var result = serializer.Deserialize<Dictionary<string, object>>(jsonStr);
                if (result != null) dict = result;
            }
            catch { }
            return dict;
        }

        /// <summary>
        /// Ghi dữ liệu JSON vào XData (ghi đè)
        /// </summary>
        public static void SetEntityData(DBObject obj, Dictionary<string, object> data, Transaction tr)
        {
            if (data == null || data.Count == 0) return;
            EnsureRegApp(APP_NAME, tr);

            var serializer = new JavaScriptSerializer();
            string jsonStr = serializer.Serialize(data);

            ResultBuffer rb = new ResultBuffer();
            rb.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, APP_NAME));

            for (int i = 0; i < jsonStr.Length; i += CHUNK_SIZE)
            {
                int len = Math.Min(CHUNK_SIZE, jsonStr.Length - i);
                rb.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, jsonStr.Substring(i, len)));
            }
            obj.XData = rb;
        }

        /// <summary>
        /// Cập nhật dữ liệu (merge với dữ liệu cũ)
        /// </summary>
        public static void UpdateData(DBObject obj, Dictionary<string, object> updates, Transaction tr)
        {
            var currentData = GetEntityData(obj);
            foreach (var kvp in updates)
            {
                currentData[kvp.Key] = kvp.Value;
            }
            SetEntityData(obj, currentData, tr);
        }

        /// <summary>
        /// Xóa XData
        /// </summary>
        public static void ClearEntityData(DBObject obj, Transaction tr)
        {
            EnsureRegApp(APP_NAME, tr);
            ResultBuffer rb = new ResultBuffer();
            rb.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, APP_NAME));
            obj.XData = rb;
        }

        #endregion

        #region Helpers

        private static double? ConvertToDouble(object val)
        {
            if (val == null) return null;
            if (double.TryParse(val.ToString(), out double d)) return d;
            return null;
        }

        private static List<string> ConvertToStringList(object val)
        {
            var list = new List<string>();
            if (val is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null) list.Add(item.ToString());
                }
            }
            return list;
        }

        private static List<MappingRecord> ConvertToMappingList(object val)
        {
            var list = new List<MappingRecord>();
            if (val is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is Dictionary<string, object> dict)
                    {
                        var rec = new MappingRecord();
                        if (dict.ContainsKey("TargetFrame")) rec.TargetFrame = dict["TargetFrame"].ToString();
                        if (dict.ContainsKey("MatchType")) rec.MatchType = dict["MatchType"].ToString();
                        if (dict.ContainsKey("DistI")) rec.DistI = Convert.ToDouble(dict["DistI"]);
                        if (dict.ContainsKey("DistJ")) rec.DistJ = Convert.ToDouble(dict["DistJ"]);
                        if (dict.ContainsKey("CoveredLength")) rec.CoveredLength = Convert.ToDouble(dict["CoveredLength"]);
                        list.Add(rec);
                    }
                }
            }
            return list;
        }

        private static List<Dictionary<string, object>> ConvertMappingsToSerializable(List<MappingRecord> mappings)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var m in mappings)
            {
                var dict = new Dictionary<string, object>
                {
                    ["TargetFrame"] = m.TargetFrame,
                    ["MatchType"] = m.MatchType,
                    ["DistI"] = m.DistI,
                    ["DistJ"] = m.DistJ,
                    ["CoveredLength"] = m.CoveredLength
                };
                list.Add(dict);
            }
            return list;
        }

        private static void EnsureRegApp(string regAppName, Transaction tr)
        {
            RegAppTable rat = (RegAppTable)tr.GetObject(AcadUtils.Db.RegAppTableId, OpenMode.ForRead);
            if (!rat.Has(regAppName))
            {
                rat.UpgradeOpen();
                RegAppTableRecord ratr = new RegAppTableRecord { Name = regAppName };
                rat.Add(ratr);
                tr.AddNewlyCreatedDBObject(ratr, true);
            }
        }

        #endregion
    }
}