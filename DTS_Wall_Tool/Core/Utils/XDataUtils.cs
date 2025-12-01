using Autodesk.AutoCAD.DatabaseServices;
using DTS_Wall_Tool.Core.Data;
using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace DTS_Wall_Tool.Core.Utils
{
    /// <summary>
    /// Tiện ích đọc/ghi XData với Factory Pattern. 
    /// Tự động nhận diện và tạo đúng loại ElementData dựa trên xType.
    /// Tuân thủ ISO/IEC 25010: Maintainability, Modularity. 
    /// </summary>
    public static class XDataUtils
    {
        #region Constants

        private const string APP_NAME = "DTS_APP";
        private const int CHUNK_SIZE = 250;

        #endregion

        #region Factory Pattern - Core API

        /// <summary>
        /// Đọc ElementData từ entity - Factory tự động tạo đúng loại
        /// </summary>
        /// <returns>WallData, ColumnData, BeamData...  hoặc null</returns>
        public static ElementData ReadElementData(DBObject obj)
        {
            var dict = GetRawData(obj);
            if (dict == null || dict.Count == 0) return null;

            // Lấy xType để xác định loại
            if (!dict.TryGetValue("xType", out var xTypeObj)) return null;
            string xType = xTypeObj?.ToString()?.ToUpperInvariant();

            // Factory: Tạo đúng instance dựa trên xType
            ElementData element = CreateElementByType(xType);
            if (element == null) return null;

            // Đọc dữ liệu vào instance
            element.FromDictionary(dict);
            return element;
        }

        /// <summary>
        /// Đọc ElementData và cast sang kiểu cụ thể
        /// </summary>
        public static T ReadElementData<T>(DBObject obj) where T : ElementData
        {
            var element = ReadElementData(obj);
            return element as T;
        }

        /// <summary>
        /// Ghi ElementData vào entity
        /// </summary>
        public static void WriteElementData(DBObject obj, ElementData data, Transaction tr)
        {
            if (data == null) return;

            data.UpdateTimestamp();
            var dict = data.ToDictionary();
            SetRawData(obj, dict, tr);
        }

        /// <summary>
        /// Cập nhật ElementData (merge với dữ liệu cũ)
        /// </summary>
        public static void UpdateElementData(DBObject obj, ElementData data, Transaction tr)
        {
            // Đọc dữ liệu cũ
            var currentDict = GetRawData(obj) ?? new Dictionary<string, object>();

            // Merge với dữ liệu mới
            data.UpdateTimestamp();
            var newDict = data.ToDictionary();

            foreach (var kvp in newDict)
            {
                currentDict[kvp.Key] = kvp.Value;
            }

            SetRawData(obj, currentDict, tr);
        }

        /// <summary>
        /// Factory method: Tạo instance ElementData dựa trên xType
        /// </summary>
        private static ElementData CreateElementByType(string xType)
        {
            if (string.IsNullOrEmpty(xType)) return null;

            switch (xType)
            {
                case "WALL":
                    return new WallData();
                case "COLUMN":
                    return new ColumnData();
                case "BEAM":
                    return new BeamData();
                case "SLAB":
                    return new SlabData();
                // Thêm các loại mới ở đây...
                default:
                    return null;
            }
        }

        #endregion

        #region Specialized Readers (Backward Compatibility)

        /// <summary>
        /// Đọc WallData - Shortcut method (không cần Transaction)
        /// </summary>
        public static WallData ReadWallData(DBObject obj)
        {
            return ReadElementData<WallData>(obj);
        }

        /// <summary>
        /// Ghi WallData - Shortcut method
        /// </summary>
        public static void SaveWallData(DBObject obj, WallData data, Transaction tr)
        {
            WriteElementData(obj, data, tr);
        }

        /// <summary>
        /// Đọc ColumnData
        /// </summary>
        public static ColumnData ReadColumnData(DBObject obj)
        {
            return ReadElementData<ColumnData>(obj);
        }

        /// <summary>
        /// Đọc BeamData
        /// </summary>
        public static BeamData ReadBeamData(DBObject obj)
        {
            return ReadElementData<BeamData>(obj);
        }

        /// <summary>
        /// Đọc SlabData
        /// </summary>
        public static SlabData ReadSlabData(DBObject obj)
        {
            return ReadElementData<SlabData>(obj);
        }

        /// <summary>
        /// Xóa dữ liệu entity (alias cho ClearData)
        /// </summary>
        public static void ClearElementData(DBObject obj, Transaction tr)
        {
            ClearData(obj, tr);
        }

        #endregion

        #region StoryData (Special Case)

        /// <summary>
        /// Đọc StoryData từ entity (không cần Transaction)
        /// </summary>
        public static StoryData ReadStoryData(DBObject obj)
        {
            var dict = GetRawData(obj);
            if (dict == null || dict.Count == 0) return null;

            if (!dict.TryGetValue("xType", out var xTypeObj)) return null;
            if (xTypeObj?.ToString()?.ToUpperInvariant() != "STORY_ORIGIN") return null;

            var storyData = new StoryData();
            storyData.FromDictionary(dict);
            return storyData;
        }

        /// <summary>
        /// Ghi StoryData vào entity
        /// </summary>
        public static void WriteStoryData(DBObject obj, StoryData data, Transaction tr)
        {
            if (data == null) return;
            SetRawData(obj, data.ToDictionary(), tr);
        }

        #endregion

        #region Link Management

        /// <summary>
        /// Thiết lập liên kết cha-con
        /// </summary>
        public static void SetLink(DBObject child, DBObject parent, Transaction tr)
        {
            var childElement = ReadElementData(child);
            if (childElement == null) return;

            string parentHandle = parent.Handle.ToString();
            childElement.OriginHandle = parentHandle;
            WriteElementData(child, childElement, tr);

            // Cập nhật cha
            var parentStory = ReadStoryData(parent);
            if (parentStory != null)
            {
                string childHandle = child.Handle.ToString();
                if (!parentStory.ChildHandles.Contains(childHandle))
                {
                    parentStory.ChildHandles.Add(childHandle);
                    WriteStoryData(parent, parentStory, tr);
                }
            }
            else
            {
                var parentElement = ReadElementData(parent);
                if (parentElement != null)
                {
                    string childHandle = child.Handle.ToString();
                    if (!parentElement.ChildHandles.Contains(childHandle))
                    {
                        parentElement.ChildHandles.Add(childHandle);
                        WriteElementData(parent, parentElement, tr);
                    }
                }
            }
        }

        /// <summary>
        /// Xóa liên kết cha-con
        /// </summary>
        public static void RemoveLink(DBObject child, Transaction tr)
        {
            var childElement = ReadElementData(child);
            if (childElement == null || !childElement.IsLinked) return;

            string parentHandle = childElement.OriginHandle;
            childElement.OriginHandle = null;
            WriteElementData(child, childElement, tr);

            try
            {
                ObjectId parentId = AcadUtils.GetObjectIdFromHandle(parentHandle);
                if (parentId != ObjectId.Null)
                {
                    DBObject parentObj = tr.GetObject(parentId, OpenMode.ForWrite);
                    string childHandle = child.Handle.ToString();

                    var parentStory = ReadStoryData(parentObj);
                    if (parentStory != null)
                    {
                        parentStory.ChildHandles.Remove(childHandle);
                        WriteStoryData(parentObj, parentStory, tr);
                    }
                    else
                    {
                        var parentElement = ReadElementData(parentObj);
                        if (parentElement != null)
                        {
                            parentElement.ChildHandles.Remove(childHandle);
                            WriteElementData(parentObj, parentElement, tr);
                        }
                    }
                }
            }
            catch { }
        }

        #endregion // End of Link Management

        #region Low-Level XData Access

        /// <summary>
        /// Đọc raw Dictionary từ XData
        /// </summary>
        public static Dictionary<string, object> GetRawData(DBObject obj)
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
        /// Ghi raw Dictionary vào XData
        /// </summary>
        public static void SetRawData(DBObject obj, Dictionary<string, object> data, Transaction tr)
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
        /// Xóa XData khỏi entity
        /// </summary>
        public static void ClearData(DBObject obj, Transaction tr)
        {
            EnsureRegApp(APP_NAME, tr);
            ResultBuffer rb = new ResultBuffer();
            rb.Add(new TypedValue((int)DxfCode.ExtendedDataRegAppName, APP_NAME));
            obj.XData = rb;
        }

        /// <summary>
        /// Kiểm tra entity có XData DTS_APP không
        /// </summary>
        public static bool HasDtsData(DBObject obj)
        {
            ResultBuffer rb = obj.GetXDataForApplication(APP_NAME);
            return rb != null;
        }

        /// <summary>
        /// Lấy xType của entity
        /// </summary>
        public static string GetXType(DBObject obj)
        {
            var dict = GetRawData(obj);
            if (dict.TryGetValue("xType", out var xType))
                return xType?.ToString();
            return null;
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

        #endregion // End of Low-Level XData Access
    }
}