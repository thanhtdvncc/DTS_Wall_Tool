using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization; // Thư viện JSON
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

namespace DTS_Wall_Tool.Core
{
    public static class XDataUtils
    {
        private const string APP_NAME = "DTS_APP";

        // =============================================================
        // PHẦN 1: CÁC HÀM CẤP CAO (High-Level)
        // =============================================================

        public static WallData ReadWallData(DBObject obj, Transaction tr = null)
        {
            var dict = GetEntityData(obj);

            if (dict.Count == 0) return null;
            if (dict.ContainsKey("xType") && dict["xType"].ToString() != "WALL") return null;

            WallData data = new WallData();

            // Mapping các trường cơ bản
            if (dict.ContainsKey("xThickness")) data.Thickness = ConvertToDouble(dict["xThickness"]);
            if (dict.ContainsKey("xWallType")) data.WallType = Convert.ToString(dict["xWallType"]);
            if (dict.ContainsKey("xLoadPattern")) data.LoadPattern = Convert.ToString(dict["xLoadPattern"]);
            if (dict.ContainsKey("xLoadValue")) data.LoadValue = ConvertToDouble(dict["xLoadValue"]);
            if (dict.ContainsKey("xOriginHandle")) data.OriginHandle = Convert.ToString(dict["xOriginHandle"]);
            if (dict.ContainsKey("xBaseZ")) data.BaseZ = ConvertToDouble(dict["xBaseZ"]);

            // --- MỚI: Mapping danh sách con ---
            if (dict.ContainsKey("xChildHandles"))
            {
                data.ChildHandles = ConvertToStringList(dict["xChildHandles"]);
            }

            // --- MỚI: Mapping danh sách Mapping SAP2000 ---
            if (dict.ContainsKey("xMappings")) data.Mappings = ConvertToMappingList(dict["xMappings"]);

            return data;
        }

        public static void SaveWallData(DBObject obj, WallData data, Transaction tr)
        {
            var updates = new Dictionary<string, object>();
            updates["xType"] = "WALL";

            if (data.Thickness.HasValue) updates["xThickness"] = data.Thickness.Value;
            if (data.WallType != null) updates["xWallType"] = data.WallType;
            if (data.LoadPattern != null) updates["xLoadPattern"] = data.LoadPattern;
            if (data.LoadValue.HasValue) updates["xLoadValue"] = data.LoadValue.Value;
            if (data.OriginHandle != null) updates["xOriginHandle"] = data.OriginHandle;
            if (data.BaseZ.HasValue) updates["xBaseZ"] = data.BaseZ.Value;

            // --- MỚI: Lưu danh sách con ---
            if (data.ChildHandles != null && data.ChildHandles.Count > 0)
            {
                updates["xChildHandles"] = data.ChildHandles;
            }

            // --- MỚI: Lưu danh sách Mapping SAP2000 ---
            if (data.Mappings != null && data.Mappings.Count > 0) updates["xMappings"] = data.Mappings;
            UpdateData(obj, updates, tr);

            UpdateData(obj, updates, tr);
        }

        public static void WriteStoryData(DBObject obj, StoryData data, Transaction tr)
        {
            var dict = new Dictionary<string, object>();
            dict["xType"] = "STORY_ORIGIN";
            dict["xStoryName"] = data.StoryName;
            dict["xElevation"] = data.Elevation;
            SetEntityData(obj, dict, tr);
        }

        public static StoryData ReadStoryData(DBObject obj, Transaction tr = null)
        {
            var dict = GetEntityData(obj);
            if (dict == null || !dict.ContainsKey("xType")) return null;
            if (dict["xType"].ToString() != "STORY_ORIGIN") return null;

            StoryData data = new StoryData();
            if (dict.ContainsKey("xStoryName")) data.StoryName = dict["xStoryName"].ToString();
            if (dict.ContainsKey("xElevation")) data.Elevation = ConvertToDouble(dict["xElevation"]) ?? 0;
            return data;
        }

        // =============================================================
        // PHẦN 2: CÁC HÀM CẤP THẤP (Low-Level)
        // =============================================================

        private static Dictionary<string, object> GetEntityData(DBObject obj)
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

        private static void SetEntityData(DBObject obj, Dictionary<string, object> data, Transaction tr)
        {
            if (data == null || data.Count == 0) return;
            AddRegAppTableRecord(APP_NAME, tr);

            var serializer = new JavaScriptSerializer();
            string jsonStr = serializer.Serialize(data);

            ResultBuffer rb = new ResultBuffer();
            rb.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, APP_NAME));

            int chunkSize = 250;
            for (int i = 0; i < jsonStr.Length; i += chunkSize)
            {
                int len = Math.Min(chunkSize, jsonStr.Length - i);
                rb.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, jsonStr.Substring(i, len)));
            }
            obj.XData = rb;
        }

        private static void UpdateData(DBObject obj, Dictionary<string, object> updates, Transaction tr)
        {
            var currentData = GetEntityData(obj);
            foreach (var kvp in updates) currentData[kvp.Key] = kvp.Value;
            SetEntityData(obj, currentData, tr);
        }

        // =============================================================
        // PHẦN 3: HÀM PHỤ TRỢ (Helper)
        // =============================================================

        private static double? ConvertToDouble(object val)
        {
            if (val == null) return null;
            if (double.TryParse(val.ToString(), out double d)) return d;
            return null;
        }

        // --- HÀM MỚI: Chuyển đổi object sang List<string> an toàn ---
        private static List<string> ConvertToStringList(object val)
        {
            var list = new List<string>();
            // Khi deserialize JSON array, nó thường trả về ArrayList hoặc Object[]
            if (val is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null) list.Add(item.ToString());
                }
            }
            return list;
        }

        private static void AddRegAppTableRecord(string regAppName, Transaction tr)
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
                        list.Add(rec);
                    }
                }
            }
            return list;
        }


    }
}