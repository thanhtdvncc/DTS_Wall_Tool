using Autodesk.AutoCAD.DatabaseServices;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Algorithms;
using System;
using System.Collections.Generic;
using System.Linq;
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

        // Rebar XData Keys - tránh magic strings
        public const string KEY_TOP_REBAR = "TopRebar";
        public const string KEY_BOT_REBAR = "BotRebar";
        public const string KEY_STIRRUP = "Stirrup";
        public const string KEY_SIDE_BAR = "SideBar";
        public const string KEY_BEAM_GROUP = "BeamGroupName";
        public const string KEY_BEAM_TYPE = "BeamType";
        public const string KEY_LAST_MODIFIED = "LastModified";

        // NOD Keys for BeamGroup persistence
        public const string NOD_BEAM_GROUPS = "DTS_BeamGroups";

        // V5.0: Rebar Options Persistence Keys
        public const string KEY_GROUP_IDENTITY = "GroupIdentity";  // "G:ABC123|I:0"
        public const string KEY_GROUP_STATE = "GroupState";        // "Idx:2|Lock:1"
        public const string KEY_CALCULATED_AT = "CalculatedAt";
        public const string KEY_IS_MANUAL = "IsManual";

        #endregion

        /// <summary>
        /// Kiểm tra xem đối tượng có XData của DTS_APP hay không
        /// </summary>
        public static bool HasAppXData(DBObject obj)
        {
            if (obj == null) return false;
            var rb = obj.XData;
            if (rb == null) return false;

            foreach (var tv in rb)
            {
                if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName &&
                    tv.Value.ToString().Equals(APP_NAME, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }


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
                case "REBAR_DATA":
                    return new BeamResultData();
                case "REBAR_SOLUTION":
#pragma warning disable CS0618 // Kept for backward compatibility
                    return new BeamRebarSolution();
#pragma warning restore CS0618
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

        /// <summary>
        /// [SPECIAL] Đọc BeamResultData từ XData bất kể xType.
        /// Dùng cho các hàm REBAR cần đọc dữ liệu thiết kế thép
        /// ngay cả khi xType vẫn là "BEAM" (từ DTS_PLOT_FROM_SAP).
        /// </summary>
        public static BeamResultData ReadRebarData(DBObject obj)
        {
            var dict = GetRawData(obj);
            if (dict == null) return null;

            // Check if beam data exists (by key presence, not xType)
            // Include SupportI/SupportJ for Girder detection before SAP result import
            bool hasRebarData = dict.ContainsKey("TopArea") || dict.ContainsKey("SapElementName");
            bool hasSupportData = dict.ContainsKey("SupportI") || dict.ContainsKey("xSupport_I");
            if (!hasRebarData && !hasSupportData)
                return null;

            var result = new BeamResultData();
            result.FromDictionary(dict);
            return result;
        }

        /// <summary>
        /// Ghi thông tin thép vào XData của entity
        /// </summary>
        /// <param name="obj">Entity (LINE/POLYLINE)</param>
        /// <param name="tr">Transaction</param>
        /// <param name="topRebar">Thép trên (VD: "3D20")</param>
        /// <param name="botRebar">Thép dưới (VD: "3D22")</param>
        /// <param name="stirrup">Đai (VD: "D8@150")</param>
        /// <param name="sideBar">Thép hông (VD: "2D14")</param>
        /// <param name="groupName">Tên nhóm dầm liên tục</param>
        public static void WriteRebarXData(
            DBObject obj, Transaction tr,
            string topRebar, string botRebar,
            string stirrup, string sideBar,
            string groupName = null,
            string beamType = null)
        {
            if (obj == null || tr == null) return;

            // Read existing data
            var dict = GetRawData(obj) ?? new Dictionary<string, object>();

            // Update rebar fields using const keys
            if (!string.IsNullOrEmpty(topRebar))
                dict[KEY_TOP_REBAR] = topRebar;
            if (!string.IsNullOrEmpty(botRebar))
                dict[KEY_BOT_REBAR] = botRebar;
            if (!string.IsNullOrEmpty(stirrup))
                dict[KEY_STIRRUP] = stirrup;
            if (!string.IsNullOrEmpty(sideBar))
                dict[KEY_SIDE_BAR] = sideBar;
            if (!string.IsNullOrEmpty(groupName))
                dict[KEY_BEAM_GROUP] = groupName;
            if (!string.IsNullOrEmpty(beamType))
                dict[KEY_BEAM_TYPE] = beamType;

            // Update timestamp
            dict[KEY_LAST_MODIFIED] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            SetRawData(obj, dict, tr);
        }

        /// <summary>
        /// Đọc thông tin thép từ XData
        /// </summary>
        public static RebarXDataInfo ReadRebarXData(DBObject obj)
        {
            var dict = GetRawData(obj);
            if (dict == null || dict.Count == 0) return null;

            return new RebarXDataInfo
            {
                TopRebar = dict.ContainsKey(KEY_TOP_REBAR) ? dict[KEY_TOP_REBAR]?.ToString() : null,
                BotRebar = dict.ContainsKey(KEY_BOT_REBAR) ? dict[KEY_BOT_REBAR]?.ToString() : null,
                Stirrup = dict.ContainsKey(KEY_STIRRUP) ? dict[KEY_STIRRUP]?.ToString() : null,
                SideBar = dict.ContainsKey(KEY_SIDE_BAR) ? dict[KEY_SIDE_BAR]?.ToString() : null,
                BeamGroupName = dict.ContainsKey(KEY_BEAM_GROUP) ? dict[KEY_BEAM_GROUP]?.ToString() : null,
                BeamType = dict.ContainsKey(KEY_BEAM_TYPE) ? dict[KEY_BEAM_TYPE]?.ToString() : "Beam"
            };
        }

        /// <summary>
        /// Merge a small set of keys into XData without writing/overwriting xType.
        /// Use this for lightweight updates (e.g., BeamName) where ElementData serialization could overwrite other schemas.
        /// </summary>
        public static void MergeRawData(DBObject obj, Transaction tr, IDictionary<string, object> updates, bool updateTimestamp = true)
        {
            if (obj == null || tr == null || updates == null || updates.Count == 0) return;

            var dict = GetRawData(obj) ?? new Dictionary<string, object>();
            foreach (var kv in updates)
                dict[kv.Key] = kv.Value;

            if (updateTimestamp)
                dict[KEY_LAST_MODIFIED] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            SetRawData(obj, dict, tr);
        }

        /// <summary>
        /// XData-first: cập nhật kết quả bố trí thép vào XData của phần tử, nhưng KHÔNG ghi đè các key khác (đặc biệt xType=BEAM).
        /// Lưu các field của BeamResultData: TopRebarString/BotRebarString/StirrupString/WebBarString + TopAreaProv/BotAreaProv.
        /// Đồng thời giữ tương thích ngược bằng cách update các key legacy (TopRebar/BotRebar/Stirrup/SideBar).
        /// V5: Thêm SelectedDesignJson để persist phương án đã chốt.
        /// </summary>
        public static void UpdateBeamSolutionXData(
            DBObject obj,
            Transaction tr,
            string[] topRebarString,
            string[] botRebarString,
            string[] stirrupString,
            string[] webBarString,
            string belongToGroup = null,
            string beamType = null,
            string selectedDesignJson = null,
            string backboneOptionsJson = null)  // FIX: Add BackboneOptions persistence
        {
            if (obj == null || tr == null) return;

            var dict = GetRawData(obj) ?? new Dictionary<string, object>();

            string[] top = null;
            string[] bot = null;
            string[] stir = null;
            string[] web = null;

            if (topRebarString != null)
            {
                top = Normalize3(topRebarString);
                dict["TopRebarString"] = top;
                dict["TopAreaProv"] = top.Select(RebarCalculator.ParseRebarArea).ToArray();
                dict[KEY_TOP_REBAR] = top[1] ?? "";
            }

            if (botRebarString != null)
            {
                bot = Normalize3(botRebarString);
                dict["BotRebarString"] = bot;
                dict["BotAreaProv"] = bot.Select(RebarCalculator.ParseRebarArea).ToArray();
                dict[KEY_BOT_REBAR] = bot[1] ?? "";
            }

            if (stirrupString != null)
            {
                stir = Normalize3(stirrupString);
                dict["StirrupString"] = stir;
                dict[KEY_STIRRUP] = stir[1] ?? "";
            }

            if (webBarString != null)
            {
                web = Normalize3(webBarString);
                dict["WebBarString"] = web;
                dict[KEY_SIDE_BAR] = web[1] ?? "";
            }

            if (!string.IsNullOrEmpty(belongToGroup)) dict["BelongToGroup"] = belongToGroup;
            if (!string.IsNullOrEmpty(beamType)) dict["BeamType"] = beamType;

            // V5: Persist SelectedDesign (backbone info + locked timestamp)
            if (!string.IsNullOrEmpty(selectedDesignJson))
            {
                dict["SelectedDesignJson"] = selectedDesignJson;
            }

            // FIX: Persist BackboneOptions (all calculation proposals)
            // This ensures Viewer shows calculation results when reopening
            if (!string.IsNullOrEmpty(backboneOptionsJson))
            {
                dict["BackboneOptionsJson"] = backboneOptionsJson;
            }

            // Legacy meta
            if (!string.IsNullOrEmpty(belongToGroup)) dict[KEY_BEAM_GROUP] = belongToGroup;
            if (!string.IsNullOrEmpty(beamType)) dict[KEY_BEAM_TYPE] = beamType;
            dict[KEY_LAST_MODIFIED] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            SetRawData(obj, dict, tr);
        }

        /// <summary>
        /// XData-first: cập nhật dữ liệu yêu cầu thép (từ SAP) vào XData của phần tử,
        /// nhưng KHÔNG ghi đè các key layout/solution (TopRebarString/BotRebarString/StirrupString/WebBarString...).
        /// Đồng thời KHÔNG ghi đè xType (thường là BEAM từ DTS_PLOT_FROM_SAP).
        /// </summary>
        public static void UpdateBeamRequiredXData(
            DBObject obj,
            Transaction tr,
            double[] topArea,
            double[] botArea,
            double[] torsionArea,
            double[] shearArea,
            double[] ttArea,
            string designCombo = null,
            string sectionName = null,
            double? width = null,
            double? sectionHeight = null,
            double? torsionFactorUsed = null,
            string sapElementName = null,
            string mappingSource = null)
        {
            if (obj == null || tr == null) return;

            var dict = GetRawData(obj) ?? new Dictionary<string, object>();

            if (topArea != null) dict["TopArea"] = RoundArray8(Normalize3(topArea));
            if (botArea != null) dict["BotArea"] = RoundArray8(Normalize3(botArea));
            if (torsionArea != null) dict["TorsionArea"] = RoundArray8(Normalize3(torsionArea));
            if (shearArea != null) dict["ShearArea"] = RoundArray8(Normalize3(shearArea));
            if (ttArea != null) dict["TTArea"] = RoundArray8(Normalize3(ttArea));

            if (!string.IsNullOrEmpty(designCombo)) dict["DesignCombo"] = designCombo;

            if (!string.IsNullOrEmpty(sectionName)) dict["SectionName"] = sectionName;
            if (width.HasValue) dict["Width"] = Math.Round(width.Value, 8);
            if (sectionHeight.HasValue) dict["SectionHeight"] = Math.Round(sectionHeight.Value, 8);
            if (torsionFactorUsed.HasValue) dict["TorsionFactorUsed"] = Math.Round(torsionFactorUsed.Value, 8);

            if (!string.IsNullOrEmpty(sapElementName)) dict["SapElementName"] = sapElementName;
            if (!string.IsNullOrEmpty(mappingSource)) dict["MappingSource"] = mappingSource;

            dict[KEY_LAST_MODIFIED] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SetRawData(obj, dict, tr);
        }

        private static double[] Normalize3(double[] arr)
        {
            var res = new double[3];
            if (arr == null) return res;
            for (int i = 0; i < 3 && i < arr.Length; i++)
                res[i] = arr[i];
            return res;
        }

        private static double[] RoundArray8(double[] arr)
        {
            if (arr == null) return null;
            var res = new double[arr.Length];
            for (int i = 0; i < arr.Length; i++)
                res[i] = Math.Round(arr[i], 8);
            return res;
        }

        private static string[] Normalize3(string[] arr)
        {
            var res = new string[3];
            if (arr == null) return res;
            for (int i = 0; i < 3 && i < arr.Length; i++)
                res[i] = arr[i] ?? "";
            return res;
        }

        #endregion

        // V5: NOD Persistence removed. All data stored in XData on individual entities.
        // Use TopologyBuilder.BuildGraph() for runtime group creation.

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

            // Lưu Con (CRITICAL: Use UpdateElementData to MERGE, not WriteElementData which REPLACES all XData)
            // This preserves existing RebarData (TopArea, BotArea) which would be lost with WriteElementData
            UpdateElementData(childObj, childData, tr);

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
                // Lưu Con (CRITICAL: Use UpdateElementData to preserve RebarData)
                UpdateElementData(childObj, childData, tr);

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

            // CRITICAL: Use UpdateElementData to preserve RebarData
            UpdateElementData(childObj, data, tr);
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
            try
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
            catch { /* Ignore if already exists or locked */ }
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

        #region V5.0: Rebar Options Persistence

        /// <summary>
        /// [V5.0] Write GroupIdentity (GroupId + SpanIndex) to entity.
        /// Format: "G:{groupId}|I:{spanIndex}"
        /// </summary>
        /// <param name="obj">Entity to write to</param>
        /// <param name="groupId">Unique group identifier (GUID)</param>
        /// <param name="spanIndex">0-based span index within group</param>
        /// <param name="tr">Transaction (required per Rule 11.2)</param>
        public static void WriteGroupIdentity(DBObject obj, string groupId, int spanIndex, Transaction tr)
        {
            if (obj == null || tr == null || string.IsNullOrEmpty(groupId)) return;

            var dict = GetRawData(obj) ?? new Dictionary<string, object>();
            dict[KEY_GROUP_IDENTITY] = $"G:{groupId}|I:{spanIndex}";
            SetRawData(obj, dict, tr);
        }

        /// <summary>
        /// [V5.0] Read GroupIdentity from entity.
        /// </summary>
        /// <returns>Tuple of (GroupId, SpanIndex). Returns (null, -1) if not found.</returns>
        public static (string GroupId, int SpanIndex) ReadGroupIdentity(DBObject obj)
        {
            var dict = GetRawData(obj);
            if (dict == null) return (null, -1);

            // Try new v5.0 format first
            if (dict.TryGetValue(KEY_GROUP_IDENTITY, out var gi) && gi != null)
            {
                return ParseGroupIdentity(gi.ToString());
            }

            // Backward compatibility: try old format
            if (dict.TryGetValue("BelongToGroup", out var bg) && bg != null)
            {
                return (bg.ToString(), 0);  // Legacy: no SpanIndex info
            }

            if (dict.TryGetValue(KEY_BEAM_GROUP, out var bgn) && bgn != null)
            {
                return (bgn.ToString(), 0);
            }

            return (null, -1);
        }

        /// <summary>
        /// [V5.0] Parse GroupIdentity string format.
        /// </summary>
        private static (string GroupId, int SpanIndex) ParseGroupIdentity(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return (null, -1);

            try
            {
                // Format: "G:ABC123|I:0"
                var parts = identity.Split('|');
                string groupId = null;
                int spanIndex = 0;

                foreach (var part in parts)
                {
                    if (part.StartsWith("G:"))
                        groupId = part.Substring(2);
                    else if (part.StartsWith("I:"))
                        int.TryParse(part.Substring(2), out spanIndex);
                }

                return (groupId, spanIndex);
            }
            catch
            {
                return (null, -1);
            }
        }

        /// <summary>
        /// [V5.0] Write GroupState (SelectedIdx + IsLocked) to entity.
        /// Format: "Idx:{idx}|Lock:{0/1}"
        /// </summary>
        public static void WriteGroupState(DBObject obj, int selectedIdx, bool isLocked, Transaction tr)
        {
            if (obj == null || tr == null) return;

            var dict = GetRawData(obj) ?? new Dictionary<string, object>();
            dict[KEY_GROUP_STATE] = $"Idx:{selectedIdx}|Lock:{(isLocked ? 1 : 0)}";
            SetRawData(obj, dict, tr);
        }

        /// <summary>
        /// [V5.0] Read GroupState from entity.
        /// </summary>
        /// <returns>Tuple of (SelectedIdx, IsLocked). Returns (-1, false) if not found.</returns>
        public static (int SelectedIdx, bool IsLocked) ReadGroupState(DBObject obj)
        {
            var dict = GetRawData(obj);
            if (dict == null) return (-1, false);

            if (dict.TryGetValue(KEY_GROUP_STATE, out var gs) && gs != null)
            {
                return ParseGroupState(gs.ToString());
            }

            return (-1, false);
        }

        /// <summary>
        /// [V5.0] Parse GroupState string format.
        /// </summary>
        private static (int SelectedIdx, bool IsLocked) ParseGroupState(string state)
        {
            if (string.IsNullOrEmpty(state)) return (-1, false);

            try
            {
                // Format: "Idx:2|Lock:1"
                var parts = state.Split('|');
                int idx = -1;
                bool locked = false;

                foreach (var part in parts)
                {
                    if (part.StartsWith("Idx:"))
                        int.TryParse(part.Substring(4), out idx);
                    else if (part.StartsWith("Lock:"))
                        locked = part.Substring(5) == "1";
                }

                return (idx, locked);
            }
            catch
            {
                return (-1, false);
            }
        }

        /// <summary>
        /// [V5.0] Clear all rebar options and calculation results from entity XData.
        /// Used when ungrouping to prevent inheriting old group's rebar data.
        /// </summary>
        public static void ClearRebarOptions(DBObject obj, Transaction tr)
        {
            if (obj == null || tr == null) return;

            var dict = GetRawData(obj) ?? new Dictionary<string, object>();

            // Clear Opt0..4 compact format
            for (int i = 0; i < 5; i++)
            {
                dict.Remove($"Opt{i}");
                dict.Remove($"TopOpt{i}");
                dict.Remove($"BotOpt{i}");
            }

            // Clear current rebar
            dict.Remove("TopL0");
            dict.Remove("TopL1");
            dict.Remove("BotL0");
            dict.Remove("BotL1");
            dict.Remove("CurrentRebar");

            // Clear calculation state
            dict.Remove("IsManual");
            dict.Remove("CalculatedAt");
            dict.Remove("SelectedDesignJson");
            dict.Remove("DesignLocked");
            dict.Remove("BackboneOptionsJson");

            SetRawData(obj, dict, tr);
        }

        /// <summary>
        /// [V5.0] Write rebar options to entity using compact spec format.
        /// Format: "Opt{i}" = "T:L0|L1;B:L0" where L0=backbone, L1=addon
        /// Spec Section 2: "T:2d20|1d12;B:3d18"
        /// </summary>
        /// <param name="obj">Entity to write to</param>
        /// <param name="options">List of options, each containing (TopL0, TopL1, BotL0, BotL1) strings</param>
        /// <param name="tr">Transaction</param>
        public static void WriteRebarOptions(DBObject obj, List<RebarOptionData> options, Transaction tr)
        {
            if (obj == null || tr == null) return;

            var dict = GetRawData(obj) ?? new Dictionary<string, object>();

            // Clear old format keys if exist
            for (int i = 0; i < 5; i++)
            {
                dict.Remove($"TopOpt{i}");
                dict.Remove($"BotOpt{i}");
            }

            // Write up to 5 options in compact format
            if (options != null)
            {
                for (int i = 0; i < Math.Min(5, options.Count); i++)
                {
                    var opt = options[i];
                    if (opt == null) continue;

                    // Format: "T:backbone|addon;B:backbone|addon"
                    string topPart = string.IsNullOrEmpty(opt.TopL1)
                        ? $"T:{opt.TopL0 ?? ""}"
                        : $"T:{opt.TopL0 ?? ""}|{opt.TopL1}";
                    string botPart = string.IsNullOrEmpty(opt.BotL1)
                        ? $"B:{opt.BotL0 ?? ""}"
                        : $"B:{opt.BotL0 ?? ""}|{opt.BotL1}";

                    dict[$"Opt{i}"] = $"{topPart};{botPart}";
                }
            }

            dict[KEY_CALCULATED_AT] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SetRawData(obj, dict, tr);
        }

        /// <summary>
        /// [V5.0] Overload for legacy compatibility - accepts separate Top/Bot arrays.
        /// </summary>
        public static void WriteRebarOptions(DBObject obj, List<string[]> topOptions, List<string[]> botOptions, Transaction tr)
        {
            if (obj == null || tr == null) return;

            var options = new List<RebarOptionData>();
            int count = Math.Max(topOptions?.Count ?? 0, botOptions?.Count ?? 0);

            for (int i = 0; i < Math.Min(5, count); i++)
            {
                var top = topOptions != null && i < topOptions.Count ? topOptions[i] : null;
                var bot = botOptions != null && i < botOptions.Count ? botOptions[i] : null;

                options.Add(new RebarOptionData
                {
                    TopL0 = top?.Length > 0 ? top[0] : "",
                    TopL1 = top?.Length > 1 ? top[1] : "",
                    BotL0 = bot?.Length > 0 ? bot[0] : "",
                    BotL1 = bot?.Length > 1 ? bot[1] : ""
                });
            }

            WriteRebarOptions(obj, options, tr);
        }

        /// <summary>
        /// [V5.0] Read rebar options from entity.
        /// Supports both new compact format (Opt0) and legacy format (TopOpt0/BotOpt0).
        /// </summary>
        public static List<RebarOptionData> ReadRebarOptionsV5(DBObject obj)
        {
            var result = new List<RebarOptionData>();
            var dict = GetRawData(obj);
            if (dict == null) return result;

            for (int i = 0; i < 5; i++)
            {
                // Try new compact format first
                if (dict.TryGetValue($"Opt{i}", out var optVal) && optVal != null)
                {
                    result.Add(ParseCompactOption(optVal.ToString()));
                }
                // Fallback to legacy format
                else
                {
                    object topVal = null, botVal = null;
                    dict.TryGetValue($"TopOpt{i}", out topVal);
                    dict.TryGetValue($"BotOpt{i}", out botVal);

                    if (topVal != null || botVal != null)
                    {
                        var topParts = ParseOptionString(topVal?.ToString() ?? "");
                        var botParts = ParseOptionString(botVal?.ToString() ?? "");
                        result.Add(new RebarOptionData
                        {
                            TopL0 = topParts[0],
                            TopL1 = topParts.Length > 1 ? topParts[1] : "",
                            BotL0 = botParts[0],
                            BotL1 = botParts.Length > 1 ? botParts[1] : ""
                        });
                    }
                    else
                    {
                        result.Add(new RebarOptionData());
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// [V5.0] Legacy overload for backward compatibility.
        /// </summary>
        public static (List<string[]> TopOptions, List<string[]> BotOptions) ReadRebarOptions(DBObject obj)
        {
            var options = ReadRebarOptionsV5(obj);
            var topOptions = options.Select(o => new[] { o.TopL0, o.TopL1, "" }).ToList();
            var botOptions = options.Select(o => new[] { o.BotL0, o.BotL1, "" }).ToList();
            return (topOptions, botOptions);
        }

        /// <summary>
        /// [V5.0] Parse compact option format "T:L0|L1;B:L0|L1".
        /// </summary>
        private static RebarOptionData ParseCompactOption(string optStr)
        {
            var result = new RebarOptionData();
            if (string.IsNullOrEmpty(optStr)) return result;

            try
            {
                // Format: "T:2d20|1d12;B:3d18"
                var sections = optStr.Split(';');
                foreach (var section in sections)
                {
                    if (section.StartsWith("T:"))
                    {
                        var layers = section.Substring(2).Split('|');
                        result.TopL0 = layers.Length > 0 ? layers[0] : "";
                        result.TopL1 = layers.Length > 1 ? layers[1] : "";
                    }
                    else if (section.StartsWith("B:"))
                    {
                        var layers = section.Substring(2).Split('|');
                        result.BotL0 = layers.Length > 0 ? layers[0] : "";
                        result.BotL1 = layers.Length > 1 ? layers[1] : "";
                    }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// [V5.0] Parse legacy option string format "L|M|R" to string[3].
        /// </summary>
        private static string[] ParseOptionString(string optStr)
        {
            if (string.IsNullOrEmpty(optStr)) return new string[] { "", "", "" };

            var parts = optStr.Split('|');
            return new string[]
            {
                parts.Length > 0 ? parts[0] : "",
                parts.Length > 1 ? parts[1] : "",
                parts.Length > 2 ? parts[2] : ""
            };
        }

        /// <summary>
        /// [V5.0] Data class for rebar option.
        /// </summary>
        public class RebarOptionData
        {
            public string TopL0 { get; set; } = "";  // Top backbone (e.g., "2d20")
            public string TopL1 { get; set; } = "";  // Top addon (e.g., "1d12")
            public string BotL0 { get; set; } = "";  // Bot backbone
            public string BotL1 { get; set; } = "";  // Bot addon
        }

        /// <summary>
        /// [V5.0] Write current rebar state (layers) to entity.
        /// </summary>
        public static void WriteCurrentRebar(DBObject obj, string[] topL0, string[] topL1, string[] botL0, string[] botL1, Transaction tr)
        {
            if (obj == null || tr == null) return;

            var dict = GetRawData(obj) ?? new Dictionary<string, object>();

            if (topL0 != null && topL0.Length >= 3)
                dict["TopL0"] = $"{topL0[0] ?? ""}|{topL0[1] ?? ""}|{topL0[2] ?? ""}";
            if (topL1 != null && topL1.Length >= 3)
                dict["TopL1"] = $"{topL1[0] ?? ""}|{topL1[1] ?? ""}|{topL1[2] ?? ""}";
            if (botL0 != null && botL0.Length >= 3)
                dict["BotL0"] = $"{botL0[0] ?? ""}|{botL0[1] ?? ""}|{botL0[2] ?? ""}";
            if (botL1 != null && botL1.Length >= 3)
                dict["BotL1"] = $"{botL1[0] ?? ""}|{botL1[1] ?? ""}|{botL1[2] ?? ""}";

            SetRawData(obj, dict, tr);
        }

        /// <summary>
        /// [V5.0] Read current rebar state from entity.
        /// </summary>
        public static (string[] TopL0, string[] TopL1, string[] BotL0, string[] BotL1) ReadCurrentRebar(DBObject obj)
        {
            var dict = GetRawData(obj);
            if (dict == null) return (null, null, null, null);

            // Try new spec-compliant keys first, fallback to old keys for backward compat
            string[] topL0 = dict.TryGetValue("TopL0", out var t0) ? ParseOptionString(t0?.ToString())
                : dict.TryGetValue("TopRebarL0", out var t0Old) ? ParseOptionString(t0Old?.ToString()) : null;
            string[] topL1 = dict.TryGetValue("TopL1", out var t1) ? ParseOptionString(t1?.ToString())
                : dict.TryGetValue("TopRebarL1", out var t1Old) ? ParseOptionString(t1Old?.ToString()) : null;
            string[] botL0 = dict.TryGetValue("BotL0", out var b0) ? ParseOptionString(b0?.ToString())
                : dict.TryGetValue("BotRebarL0", out var b0Old) ? ParseOptionString(b0Old?.ToString()) : null;
            string[] botL1 = dict.TryGetValue("BotL1", out var b1) ? ParseOptionString(b1?.ToString())
                : dict.TryGetValue("BotRebarL1", out var b1Old) ? ParseOptionString(b1Old?.ToString()) : null;

            return (topL0, topL1, botL0, botL1);
        }

        /// <summary>
        /// [V5.0] Set IsManual flag on entity.
        /// </summary>
        public static void SetIsManual(DBObject obj, bool isManual, Transaction tr)
        {
            if (obj == null || tr == null) return;

            var dict = GetRawData(obj) ?? new Dictionary<string, object>();
            dict[KEY_IS_MANUAL] = isManual ? "1" : "0";
            SetRawData(obj, dict, tr);
        }

        /// <summary>
        /// [V5.0] Read IsManual flag from entity.
        /// </summary>
        public static bool ReadIsManual(DBObject obj)
        {
            var dict = GetRawData(obj);
            if (dict == null) return false;

            if (dict.TryGetValue(KEY_IS_MANUAL, out var val) && val != null)
            {
                return val.ToString() == "1";
            }

            return false;
        }

        /// <summary>
        /// [V5.0] Read CalculatedAt timestamp from entity.
        /// </summary>
        public static DateTime? ReadCalculatedAt(DBObject obj)
        {
            var dict = GetRawData(obj);
            if (dict == null) return null;

            if (dict.TryGetValue(KEY_CALCULATED_AT, out var val) && val != null)
            {
                if (DateTime.TryParse(val.ToString(), out var dt))
                    return dt;
            }

            return null;
        }

        #endregion // V5.0: Rebar Options Persistence
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

    /// <summary>
    /// Thông tin thép từ XData
    /// </summary>
    public class RebarXDataInfo
    {
        public string TopRebar { get; set; }
        public string BotRebar { get; set; }
        public string Stirrup { get; set; }
        public string SideBar { get; set; }
        public string BeamGroupName { get; set; }
        public string BeamType { get; set; }
    }
}