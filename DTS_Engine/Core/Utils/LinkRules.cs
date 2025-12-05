using Autodesk.AutoCAD.DatabaseServices;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// Quy tắc liên kết giữa các phần tử trong DTS Engine.
    /// Đảm bảo tính toàn vẹn của cây liên kết.
    /// </summary>
    public static class LinkRules
    {
        /// <summary>
        /// Quy tắc 1: Phân cấp nghiêm ngặt cho Cha Chính (Primary Parent).
        /// Xác định xem một loại phần tử có thể làm cha của loại khác hay không.
        /// </summary>
        public static bool CanBePrimaryParent(ElementType parentType, ElementType childType)
        {
            if (parentType == ElementType.StoryOrigin)
                return childType.IsStructuralElement() || childType == ElementType.ElementOrigin;

            if (parentType.IsStructuralElement())
            {
                if (childType == ElementType.Rebar ||
                    childType == ElementType.Lintel ||
                    childType == ElementType.Stair) return true;

                if (childType.IsStructuralElement()) return true;

                if (childType == ElementType.Unknown) return true;
            }

            return false;
        }

        /// <summary>
        /// Quy tắc 2: Chống vòng lặp (Acyclic Check).
        /// Duyệt ngược từ Parent lên trên; nếu gặp Child Handle thì là vòng lặp.
        /// </summary>
        public static bool DetectCycle(DBObject parentObj, string childHandle, Transaction tr)
        {
            if (parentObj == null || string.IsNullOrEmpty(childHandle)) return false;

            string currentHandle = parentObj.Handle.ToString();
            if (currentHandle == childHandle) return true;

            var currentData = XDataUtils.ReadElementData(parentObj);
            if (currentData == null)
            {
                var story = XDataUtils.ReadStoryData(parentObj);
                if (story != null) return false;
            }

            int safetyCounter = 0;
            while (currentData != null && currentData.IsLinked && safetyCounter < 100)
            {
                if (currentData.OriginHandle == childHandle) return true;

                ObjectId parentId = AcadUtils.GetObjectIdFromHandle(currentData.OriginHandle);
                if (parentId == ObjectId.Null) break;

                try
                {
                    var parentEnt = tr.GetObject(parentId, OpenMode.ForRead);
                    currentData = XDataUtils.ReadElementData(parentEnt);
                    if (currentData == null && XDataUtils.ReadStoryData(parentEnt) != null) break;
                }
                catch { break; }

                safetyCounter++;
            }

            return false;
        }

        /// <summary>
        /// Quy tắc 3: Kiểm tra hợp lệ cho Reference (Cha thứ 2).
        /// Sử dụng chuỗi handle thay vì truy cập trực tiếp property Handle trên ElementData.
        /// </summary>
        public static bool CanAddReference(ElementData host, string hostHandle, string targetHandle)
        {
            if (host == null) return false;
            if (string.IsNullOrEmpty(hostHandle) || string.IsNullOrEmpty(targetHandle)) return false;
            if (hostHandle == targetHandle) return false; // Không tự tham chiếu
            if (host.OriginHandle == targetHandle) return false; // Không trùng Cha chính
            if (host.ChildHandles != null && host.ChildHandles.Contains(targetHandle)) return false; // Không tham chiếu tới con của chính nó
            return true;
        }
    }
}

