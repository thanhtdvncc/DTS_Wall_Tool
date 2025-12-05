using Autodesk.AutoCAD.DatabaseServices;
using DTS_Engine.Core.Data;
using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace DTS_Engine.Core.Utils
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
                case "FOUNDATION":
                    return new FoundationData();
                case "SHEARWALL":
                    return new ShearWallData();
                case "STAIR":
                    return new StairData();
                case "PILE":
                    return new PileData();
                case "LINTEL":
                    return new LintelData();
                case "REBAR":
                    return new RebarData();
                // Thêm các loại mới ở đây...
                default:
                    return null;
            }
        }

        #endregion

        #region Specialized Readers (Backward Compatibility)

        /// <summary>
        /// Đọc WallData - Phương thức tiện ích (không cần Transaction)
        /// </summary>
        public static WallData ReadWallData(DBObject obj)
        {
            return ReadElementData<WallData>(obj);
        }

        /// <summary>
        /// Ghi WallData - Phương thức tiện ích
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

        #region StoryData (Trường hợp đặc biệt)

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

        #region Hệ thống quản lý liên kết (Hoạt động nguyên tử 2 chiều)

        /// <summary>
        /// [LEGACY] Thiết lập liên kết cha-con (tương thích ngược).
        /// Khuyến nghị: Sử dụng RegisterLink() để đảm bảo tính toán vẹn 2 chiều.
        /// </summary>
        public static void SetLink(DBObject child, DBObject parent, Transaction tr)
        {
            RegisterLink(child, parent, isReference: false, tr);
        }

        /// <summary>
        /// [LEGACY] Xoa lien ket cha-con (backward compatible).
        /// Recommend: Su dung UnregisterLink() hoac ClearAllLinks().
        /// </summary>
        public static void RemoveLink(DBObject child, Transaction tr)
        {
            var childData = ReadElementData(child);
            if (childData == null || !childData.IsLinked) return;
            UnregisterLink(child, childData.OriginHandle, tr);
        }

        /// <summary>
        /// [ATOMIC] Đăng ký liên kết 2 chiều giữa Con và Cha.
        /// Tự động cập nhật OriginHandle của Con VÀ ChildHandles của Cha.
        /// </summary>
        /// <param name="childObj">Đối tượng Con</param>
        /// <param name="parentObj">Đối tượng Cha</param>
        /// <param name="isReference">True = thêm vào ReferenceHandles, False = gán làm Cha chính</param>
        /// <param name="tr">Transaction hiện tại</param>
        /// <returns>Kết quả đăng ký: Primary, Reference, hoặc AlreadyLinked</returns>
        public static LinkRegistrationResult RegisterLink(DBObject childObj, DBObject parentObj, bool isReference, Transaction tr)
        {
            if (childObj == null || parentObj == null)
                return LinkRegistrationResult.Failed;

            string childHandle = childObj.Handle.ToString();
            string parentHandle = parentObj.Handle.ToString();

            // Đọc dữ liệu Con
            var childData = ReadElementData(childObj);
            if (childData == null)
                return LinkRegistrationResult.NoData;

            LinkRegistrationResult result;

            if (isReference)
            {
                // Thêm vào ReferenceHandles
                if (childData.ReferenceHandles == null)
                    childData.ReferenceHandles = new List<string>();

                if (childData.ReferenceHandles.Contains(parentHandle))
                    return LinkRegistrationResult.AlreadyLinked;

                childData.ReferenceHandles.Add(parentHandle);
                result = LinkRegistrationResult.Reference;
            }
            else
            {
                // Gán làm Cha chính (Primary)
                if (childData.OriginHandle == parentHandle)
                    return LinkRegistrationResult.AlreadyLinked;

                // Nếu đang có cha cũ khác, phải gỡ con khỏi cha cũ trước
                if (!string.IsNullOrEmpty(childData.OriginHandle) && childData.OriginHandle != parentHandle)
                {
                    RemoveChildFromParentList(childData.OriginHandle, childHandle, tr);
                }

                childData.OriginHandle = parentHandle;

                // Kế thừa cao độ nếu cha là Story
                var pStory = ReadStoryData(parentObj);
                if (pStory != null)
                {
                    childData.BaseZ = pStory.Elevation;
                    childData.Height = pStory.StoryHeight;
                }

                result = LinkRegistrationResult.Primary;
            }

            // Lưu Con
            WriteElementData(childObj, childData, tr);

            // Cập nhật Cha (thêm con vào danh sách)
            AddChildToParentList(parentObj, childHandle, tr);

            return result;
        }

        /// <summary>
        /// [ATOMIC] Gỡ bỏ liên kết 2 chiều cụ thể giữa Con và một Cha xác định.
        /// Nếu gỡ Cha chính và có Reference, tự động dọn Reference đầu tiên lên làm Cha chính.
        /// </summary>
        /// <param name="childObj">Đối tượng Con</param>
        /// <param name="targetParentHandle">Handle của Cha cần gỡ</param>
        /// <param name="tr">Transaction hiện tại</param>
        /// <returns>True nếu gỡ thành công, False nếu không tìm thấy liên kết</returns>
        public static bool UnregisterLink(DBObject childObj, string targetParentHandle, Transaction tr)
        {
            if (childObj == null || string.IsNullOrEmpty(targetParentHandle))
                return false;

            var childData = ReadElementData(childObj);
            if (childData == null) return false;

            bool changed = false;
            string childHandle = childObj.Handle.ToString();
            string promotedParent = null;

            // Trường hợp A: Gỡ Cha chính
            if (childData.OriginHandle == targetParentHandle)
            {
                childData.OriginHandle = null;
                changed = true;

                // Tự động dọn Reference đầu tiên lên làm Cha chính (nếu có)
                if (childData.ReferenceHandles != null && childData.ReferenceHandles.Count > 0)
                {
                    promotedParent = childData.ReferenceHandles[0];
                    childData.ReferenceHandles.RemoveAt(0);
                    childData.OriginHandle = promotedParent;
                }
            }
            // Trường hợp B: Gỡ Reference
            else if (childData.ReferenceHandles != null && childData.ReferenceHandles.Contains(targetParentHandle))
            {
                childData.ReferenceHandles.Remove(targetParentHandle);
                changed = true;
            }

            if (changed)
            {
                // Lưu Con
                WriteElementData(childObj, childData, tr);

                // Xóa Con khỏi danh sách của Cha (Target)
                RemoveChildFromParentList(targetParentHandle, childHandle, tr);

                return true;
            }

            return false;
        }

        /// <summary>
        /// [ATOMIC] Xoa TOAN BO lien ket cua con (voi moi cha: Primary + References).
        /// </summary>
        public static void ClearAllLinks(DBObject childObj, Transaction tr)
        {
            var data = ReadElementData(childObj);
            if (data == null) return;

            string myHandle = childObj.Handle.ToString();

            // 1. Go khoi Cha chinh
            if (!string.IsNullOrEmpty(data.OriginHandle))
            {
                RemoveChildFromParentList(data.OriginHandle, myHandle, tr);
                data.OriginHandle = null;
            }

            // 2. Go khoi tat ca Reference
            if (data.ReferenceHandles != null && data.ReferenceHandles.Count > 0)
            {
                foreach (string refHandle in data.ReferenceHandles)
                {
                    RemoveChildFromParentList(refHandle, myHandle, tr);
                }
                data.ReferenceHandles.Clear();
            }

            WriteElementData(childObj, data, tr);
        }

        /// <summary>
        /// Lay danh sach tat ca Parents (Primary + References) cua mot doi tuong.
        /// </summary>
        public static List<string> GetAllParentHandles(DBObject obj)
        {
            var result = new List<string>();
            var data = ReadElementData(obj);
            if (data == null) return result;

            if (!string.IsNullOrEmpty(data.OriginHandle))
                result.Add(data.OriginHandle);

            if (data.ReferenceHandles != null)
                result.AddRange(data.ReferenceHandles);

            return result;
        }

        /// <summary>
        /// Kiem tra doi tuong co lien ket nao khong (Primary hoac Reference).
        /// </summary>
        public static bool HasAnyLink(DBObject obj)
        {
            var data = ReadElementData(obj);
            if (data == null) return false;

            if (data.IsLinked) return true;
            if (data.ReferenceHandles != null && data.ReferenceHandles.Count > 0) return true;

            return false;
        }

        #region Private Helpers

        private static void AddChildToParentList(DBObject parentObj, string childHandle, Transaction tr)
        {
            // Thu doc StoryData
            var story = ReadStoryData(parentObj);
            if (story != null)
            {
                if (story.ChildHandles == null)
                    story.ChildHandles = new List<string>();
                if (!story.ChildHandles.Contains(childHandle))
                {
                    story.ChildHandles.Add(childHandle);
                    WriteStoryData(parentObj, story, tr);
                }
                return;
            }

            // Thu doc ElementData (neu cha la Dam/Cot)
            var elem = ReadElementData(parentObj);
            if (elem != null)
            {
                if (elem.ChildHandles == null)
                    elem.ChildHandles = new List<string>();
                if (!elem.ChildHandles.Contains(childHandle))
                {
                    elem.ChildHandles.Add(childHandle);
                    WriteElementData(parentObj, elem, tr);
                }
            }
        }

        private static void RemoveChildFromParentList(string parentHandle, string childHandle, Transaction tr)
        {
            if (string.IsNullOrEmpty(parentHandle)) return;

            ObjectId pid = AcadUtils.GetObjectIdFromHandle(parentHandle);
            if (pid == ObjectId.Null || pid.IsErased) return; // Cha da xoa, khong can xu ly

            try
            {
                var parentObj = tr.GetObject(pid, OpenMode.ForWrite);

                var story = ReadStoryData(parentObj);
                if (story != null && story.ChildHandles != null && story.ChildHandles.Contains(childHandle))
                {
                    story.ChildHandles.Remove(childHandle);
                    WriteStoryData(parentObj, story, tr);
                    return;
                }

                var elem = ReadElementData(parentObj);
                if (elem != null && elem.ChildHandles != null && elem.ChildHandles.Contains(childHandle))
                {
                    elem.ChildHandles.Remove(childHandle);
                    WriteElementData(parentObj, elem, tr);
                }
            }
            catch
            {
                // Ignore errors if parent is inaccessible
            }
        }

        #endregion

        #endregion // Kết thúc Hệ thống quản lý liên kết

        #region Truy cập XData cấp thấp

        /// <summary>
        /// Đọc Dictionary thô từ XData
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
        /// Ghi Dictionary thô vào XData
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
        /// Kiểm tra entity có XData DTS_APP hay không
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

        /// <summary>
        /// [SELF-HEALING] Kiểm tra và tự động cắt bỏ các liên kết gãy (trỏ tới đối tượng không tồn tại).
        /// </summary>
        public static bool ValidateAndFixLinks(DBObject obj, Transaction tr)
        {
            var data = ReadElementData(obj);
            if (data == null) return false;

            bool isModified = false;
            string myHandle = obj.Handle.ToString();

            // 1. Validate Primary Parent (Origin)
            if (data.IsLinked)
            {
                bool parentValid = false;
                ObjectId parentId = AcadUtils.GetObjectIdFromHandle(data.OriginHandle);

                if (parentId != ObjectId.Null && !parentId.IsErased)
                {
                    try
                    {
                        var parentObj = tr.GetObject(parentId, OpenMode.ForRead);
                        var pStory = ReadStoryData(parentObj);
                        var pElem = ReadElementData(parentObj);

                        // Cha phải nhận mình là con
                        if (pStory != null && pStory.ChildHandles.Contains(myHandle)) parentValid = true;
                        else if (pElem != null && pElem.ChildHandles.Contains(myHandle)) parentValid = true;
                    }
                    catch { }
                }

                if (!parentValid)
                {
                    data.OriginHandle = null; // Cắt link gãy
                    isModified = true;
                }
            }

            // 2. Validate Children
            if (data.ChildHandles != null && data.ChildHandles.Count > 0)
            {
                var validChildren = new List<string>();
                foreach (var childH in data.ChildHandles)
                {
                    bool childValid = false;
                    ObjectId childId = AcadUtils.GetObjectIdFromHandle(childH);

                    if (childId != ObjectId.Null && !childId.IsErased)
                    {
                        try
                        {
                            var childObj = tr.GetObject(childId, OpenMode.ForRead);
                            var cData = ReadElementData(childObj);
                            // Con phải nhận mình là Cha
                            if (cData != null && cData.OriginHandle == myHandle) childValid = true;
                        }
                        catch { }
                    }

                    if (childValid) validChildren.Add(childH);
                    else isModified = true; // Loại bỏ con "ma"
                }
                if (isModified) data.ChildHandles = validChildren;
            }

            // 3. Validate References [MỚI]
            if (data.ReferenceHandles != null && data.ReferenceHandles.Count > 0)
            {
                var validRefs = new List<string>();
                foreach (var refH in data.ReferenceHandles)
                {
                    ObjectId refId = AcadUtils.GetObjectIdFromHandle(refH);
                    if (refId != ObjectId.Null && !refId.IsErased) validRefs.Add(refH);
                    else isModified = true;
                }
                if (isModified) data.ReferenceHandles = validRefs;
            }

            if (isModified) WriteElementData(obj, data, tr);
            return isModified;
        }

        /// <summary>
        /// [ATOMIC UNLINK] Xoa lien ket an toan 2 chieu (Cha <-> Con).
        /// DEPRECATED: Su dung ClearAllLinks() thay the.
        /// </summary>
        [System.Obsolete("Sử dụng ClearAllLinks() để xóa toàn bộ liên kết, hoặc UnregisterLink() để xóa liên kết cụ thể.")]
        public static void RemoveLinkTwoWay(DBObject child, Transaction tr)
        {
            ClearAllLinks(child, tr);
        }

        #endregion // End of Low-Level XData Access
    }

    /// <summary>
    /// Ket qua dang ky lien ket.
    /// </summary>
    public enum LinkRegistrationResult
    {
        /// <summary>Dang ky lien ket chinh (Primary) thanh cong</summary>
        Primary,

        /// <summary>Dang ky lien ket phu (Reference) thanh cong</summary>
        Reference,

        /// <summary>Lien ket da ton tai truoc do</summary>
        AlreadyLinked,

        /// <summary>Doi tuong chua co du lieu DTS</summary>
        NoData,

        /// <summary>Dang ky that bai (loi khong xac dinh)</summary>
        Failed
    }
}