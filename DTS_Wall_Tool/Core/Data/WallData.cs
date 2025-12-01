using System;
using System.Collections.Generic;

namespace DTS_Wall_Tool.Core.Data
{
    /// <summary>
    /// Dữ liệu tường lưu trong XData của AutoCAD entity
    /// </summary>
    public class WallData
    {
        #region Basic Properties

        ///<summary>
        ///Luôn trả về Wall để định danh tường
        ///</summary>
        public ElementType ElementType => ElementType.Wall;

        /// <summary>
        /// Độ dày tường (mm)
        /// </summary>
        public double? Thickness { get; set; } = null;

        /// <summary>
        /// Loại tường (VD: "W220")
        /// </summary>
        public string WallType { get; set; } = null;

        /// <summary>
        /// Load pattern trong SAP2000
        /// </summary>
        public string LoadPattern { get; set; } = null;

        /// <summary>
        /// Giá trị tải (kN/m)
        /// </summary>
        public double? LoadValue { get; set; } = null;

        /// <summary>
        /// Cao độ đáy tường (mm)
        /// </summary>
        public double? BaseZ { get; set; } = null;

        #endregion

        #region Mapping Data

        /// <summary>
        /// Danh sách mapping đến dầm SAP2000
        /// </summary>
        public List<MappingRecord> Mappings { get; set; } = new List<MappingRecord>();

        #endregion

        #region Relationship Links

        /// <summary>
        /// Handle của đối tượng cha (VD: Story origin)
        /// </summary>
        public string OriginHandle { get; set; } = null;

        /// <summary>
        /// Danh sách Handle con
        /// </summary>
        public List<string> ChildHandles { get; set; } = new List<string>();

        #endregion

        #region Methods

        /// <summary>
        /// Kiểm tra có dữ liệu hợp lệ không
        /// </summary>
        public bool HasValidData()
        {
            return Thickness.HasValue || !string.IsNullOrEmpty(WallType) || LoadValue.HasValue;
        }

        /// <summary>
        /// Tự động tạo WallType từ Thickness
        /// </summary>
        public void EnsureWallType()
        {
            if (string.IsNullOrEmpty(WallType) && Thickness.HasValue && Thickness.Value > 0)
            {
                WallType = "W" + ((int)Thickness.Value).ToString();
            }
        }

        /// <summary>
        /// Clone WallData
        /// </summary>
        public WallData Clone()
        {
            var clone = new WallData
            {
                Thickness = Thickness,
                WallType = WallType,
                LoadPattern = LoadPattern,
                LoadValue = LoadValue,
                BaseZ = BaseZ,
                OriginHandle = OriginHandle
            };
            clone.ChildHandles.AddRange(ChildHandles);
            foreach (var m in Mappings)
            {
                clone.Mappings.Add(m.Clone());
            }
            return clone;
        }

        public override string ToString()
        {
            string thkStr = Thickness.HasValue ? Thickness.Value.ToString("0") : "[N/A]";
            string loadStr = LoadValue.HasValue ? LoadValue.Value.ToString("0. 00") : "[N/A]";
            string parentInfo = string.IsNullOrEmpty(OriginHandle) ? "" : $" | Parent:{OriginHandle}";
            string childInfo = ChildHandles.Count > 0 ? $" | Children:{ChildHandles.Count}" : "";
            string mapInfo = Mappings.Count > 0 ? $" | Maps:{Mappings.Count}" : "";

            return $"Type={WallType ?? "[N/A]"}, T={thkStr}, Load={loadStr}{parentInfo}{childInfo}{mapInfo}";
        }

        #endregion
    }
}