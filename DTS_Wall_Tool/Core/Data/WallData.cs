using DTS_Wall_Tool.Core.Interfaces;
using DTS_Wall_Tool.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Data
{
    /// <summary>
    /// ⚠️ LEGACY CLASSES - CHỈ ĐỂ ĐỌC DỮ LIỆU CŨ, KHÔNG SỬ DỤNG CHO CODE MỚI
    /// Giữ lại để deserialize XData từ các bản vẽ cũ.
    /// </summary>
    public class LoadSegment { public double I { get; set; } public double J { get; set; } }
    public class LoadEntry
    {
        public string Pattern { get; set; }
        public double Value { get; set; }
        public List<LoadSegment> Segments { get; set; } = new List<LoadSegment>();
        public string Direction { get; set; } = "Gravity";
        public string LoadType { get; set; } = "Distributed";
    }

    /// <summary>
    /// Dữ liệu Tường - Clean Architecture.
    /// 
    /// ⚠️ THIẾT KẾ MỚI (v2.1+):
    /// - Chỉ chứa thông tin hình học và vật liệu
    /// - Tải trọng được quản lý qua List&lt;LoadDefinition&gt; Loads (ILoadBearing)
    /// - Không còn các biến legacy: LoadValue, LoadPattern, LoadCases
    /// 
    /// ⚠️ BACKWARD COMPATIBILITY:
    /// - FromDictionary() vẫn đọc được xLoadValue, xLoadPattern từ bản vẽ cũ
    /// - Tự động migrate sang Loads list khi đọc
    /// - ToDictionary() chỉ lưu xLoads (format mới)
    /// </summary>
    public class WallData : ElementData, ILoadBearing
    {
        #region Identity Override

        public override ElementType ElementType => ElementType.Wall;

        #endregion

        #region Wall Geometry & Material Properties

        /// <summary>
        /// Độ dày tường (mm hoặc theo UnitManager)
        /// </summary>
        public double? Thickness { get; set; } = null;

        /// <summary>
        /// Loại tường (VD: "W220", "W200", "PARAPET")
        /// Dùng để tra modifier trong LoadCalculator
        /// </summary>
        public string WallType { get; set; } = null;

        /// <summary>
        /// Loại vật liệu tường (Brick, Concrete, Block, AAC...)
        /// </summary>
        public string Material { get; set; } = "Brick";

        /// <summary>
        /// Trọng lượng riêng vật liệu (kN/m³)
        /// ⚠️ ĐƠN VỊ CỐ ĐỊNH: Luôn nhập theo kN/m³
        /// - Gạch đặc: 18.0
        /// - Gạch rỗng: 13.0
        /// - Bê tông: 25.0
        /// - AAC: 6.0
        /// </summary>
        public double? UnitWeight { get; set; } = 18.0;

        /// <summary>
        /// Hệ số tải trọng (thường = 1.0 cho tĩnh tải)
        /// </summary>
        public double LoadFactor { get; set; } = 1.0;

        #endregion

        #region ILoadBearing Implementation

        /// <summary>
        /// Danh sách tải trọng đã tính toán.
        /// ⚠️ ĐÂY LÀ NƠI DUY NHẤT CHỨA THÔNG TIN TẢI TRỌNG
        /// - Không còn LoadValue, LoadPattern riêng lẻ
        /// - SyncEngine đọc trực tiếp từ list này
        /// - LabelUtils hiển thị từ list này
        /// </summary>
        public List<LoadDefinition> Loads { get; set; } = new List<LoadDefinition>();

        /// <summary>
        /// Kiểm tra có tải trọng để gán không
        /// </summary>
        public bool HasLoads => Loads != null && Loads.Count > 0;

        /// <summary>
        /// Xóa tất cả tải trọng đã tính
        /// </summary>
        public void ClearLoads()
        {
            Loads?.Clear();
        }

        /// <summary>
        /// Tính toán tải trọng tường và đưa vào danh sách Loads.
        /// 
        /// ⚠️ CÔNG THỨC:
        /// Load (kN/m) = Thickness(m) × Height(m) × UnitWeight(kN/m³) × LoadFactor
        /// 
        /// ⚠️ XỬ LÝ ĐƠN VỊ:
        /// - Thickness và Height từ CAD (mm hoặc theo UnitManager)
        /// - Quy đổi về Mét thông qua UnitManager.Info.LengthScaleToMeter
        /// - UnitWeight luôn là kN/m³
        /// - Kết quả là kN/m (tải phân bố)
        /// </summary>
        public void CalculateLoads()
        {
            ClearLoads();

            // Validate input
            if (!Thickness.HasValue || Thickness.Value <= 0) return;
            if (!Height.HasValue || Height.Value <= 0) return;
            if (!UnitWeight.HasValue || UnitWeight.Value <= 0) return;

            // Lấy hệ số quy đổi đơn vị
            double scaleToMeter = UnitManager.Info.LengthScaleToMeter;

            // Quy đổi về Mét
            double thicknessM = Thickness.Value * scaleToMeter;
            double heightM = Height.Value * scaleToMeter;

            // Tính tải phân bố (kN/m)
            double loadValue = thicknessM * heightM * UnitWeight.Value * LoadFactor;

            // Làm tròn 2 chữ số
            loadValue = Math.Round(loadValue, 2);

            // Thêm vào danh sách tải
            Loads.Add(new LoadDefinition
            {
                Pattern = "DL",  // Dead Load mặc định
                Value = loadValue,
                Type = LoadType.DistributedLine,
                TargetElement = "Frame",
                Direction = "Gravity",
                LoadFactor = LoadFactor
            });
        }

        #endregion

        #region Override Methods

        public override bool HasValidData()
        {
            return Thickness.HasValue || !string.IsNullOrEmpty(WallType);
        }

        public override ElementData Clone()
        {
            var clone = new WallData
            {
                Thickness = Thickness,
                WallType = WallType,
                Material = Material,
                UnitWeight = UnitWeight,
                LoadFactor = LoadFactor,
                // Deep clone loads
                Loads = Loads?.Select(l => l.Clone()).ToList() ?? new List<LoadDefinition>()
            };

            CopyBaseTo(clone);
            return clone;
        }

        /// <summary>
        /// Serialize sang Dictionary để lưu vào XData.
        /// ⚠️ CHỈ LƯU FORMAT MỚI:
        /// - xLoads: Danh sách LoadDefinition
        /// - Không lưu xLoadValue, xLoadPattern (legacy)
        /// </summary>
        public override Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>();

            // Write base properties (từ ElementData)
            WriteBaseProperties(dict);

            // Write wall-specific properties
            if (Thickness.HasValue)
                dict["xThickness"] = Thickness.Value;

            if (!string.IsNullOrEmpty(WallType))
                dict["xWallType"] = WallType;

            if (!string.IsNullOrEmpty(Material))
                dict["xMaterial"] = Material;

            if (UnitWeight.HasValue)
                dict["xUnitWeight"] = UnitWeight.Value;

            dict["xLoadFactor"] = LoadFactor;

            // Serialize Loads (FORMAT MỚI DUY NHẤT)
            if (Loads != null && Loads.Count > 0)
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var loadsList = Loads.Select(l => new Dictionary<string, object>
                {
                    ["Pattern"] = l.Pattern,
                    ["Value"] = l.Value,
                    ["Type"] = l.Type.ToString(),
                    ["TargetElement"] = l.TargetElement,
                    ["Direction"] = l.Direction,
                    ["DistI"] = l.DistI,
                    ["DistJ"] = l.DistJ,
                    ["IsRelativeDistance"] = l.IsRelativeDistance,
                    ["LoadFactor"] = l.LoadFactor
                }).ToList();
                dict["xLoads"] = serializer.Serialize(loadsList);
            }

            return dict;
        }

        /// <summary>
        /// Deserialize từ Dictionary (đọc từ XData).
        /// ⚠️ BACKWARD COMPATIBLE:
        /// - Ưu tiên đọc xLoads (format mới)
        /// - Nếu không có, migrate từ xLoadValue/xLoadPattern (legacy)
        /// </summary>
        public override void FromDictionary(Dictionary<string, object> dict)
        {
            // Read base properties
            ReadBaseProperties(dict);

            // Read wall-specific properties
            if (dict.TryGetValue("xThickness", out var thickness))
                Thickness = ConvertToDouble(thickness);

            if (dict.TryGetValue("xWallType", out var wallType))
                WallType = wallType?.ToString();

            if (dict.TryGetValue("xMaterial", out var material))
                Material = material?.ToString();

            if (dict.TryGetValue("xUnitWeight", out var unitWeight))
                UnitWeight = ConvertToDouble(unitWeight);

            if (dict.TryGetValue("xLoadFactor", out var loadFactor))
                LoadFactor = ConvertToDouble(loadFactor) ?? 1.0;

            // ========== ĐỌC TẢI TRỌNG ==========

            bool hasNewFormat = false;

            // 1. Thử đọc format mới (xLoads)
            if (dict.TryGetValue("xLoads", out var loadsJson) && loadsJson != null)
            {
                try
                {
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var loadsList = serializer.Deserialize<List<Dictionary<string, object>>>(loadsJson.ToString());

                    if (loadsList != null && loadsList.Count > 0)
                    {
                        Loads = new List<LoadDefinition>();

                        foreach (var loadDict in loadsList)
                        {
                            var load = new LoadDefinition();

                            if (loadDict.TryGetValue("Pattern", out var p)) 
                                load.Pattern = p?.ToString() ?? "DL";
                                            
                            if (loadDict.TryGetValue("Value", out var v)) 
                                load.Value = Convert.ToDouble(v);
                    
                            if (loadDict.TryGetValue("Type", out var t))
                            {
                                if (Enum.TryParse<LoadType>(t.ToString(), out var lt))
                                    load.Type = lt;
                            }
                    
                            if (loadDict.TryGetValue("TargetElement", out var te)) 
                                load.TargetElement = te?.ToString() ?? "Frame";
                                
                            if (loadDict.TryGetValue("Direction", out var d)) 
                                load.Direction = d?.ToString() ?? "Gravity";
                                
                            if (loadDict.TryGetValue("DistI", out var di)) 
                                load.DistI = Convert.ToDouble(di);
                                
                            if (loadDict.TryGetValue("DistJ", out var dj)) 
                                load.DistJ = Convert.ToDouble(dj);
                
                            if (loadDict.TryGetValue("IsRelativeDistance", out var ir)) 
                                load.IsRelativeDistance = Convert.ToBoolean(ir);
                            
                            if (loadDict.TryGetValue("LoadFactor", out var lf)) 
                                load.LoadFactor = Convert.ToDouble(lf);

                            Loads.Add(load);
                        }

                        hasNewFormat = true;
                    }
                }
                catch
                {
                    Loads = new List<LoadDefinition>();
                }
            }

            // 2. Nếu không có format mới, migrate từ legacy
            if (!hasNewFormat)
            {
                Loads = new List<LoadDefinition>();

                // Đọc legacy fields
                double? legacyLoadValue = null;
                string legacyPattern = "DL";

                if (dict.TryGetValue("xLoadValue", out var lv))
                    legacyLoadValue = ConvertToDouble(lv);

                if (dict.TryGetValue("xLoadPattern", out var lp))
                    legacyPattern = lp?.ToString() ?? "DL";

                // Tạo LoadDefinition từ legacy
                if (legacyLoadValue.HasValue && legacyLoadValue.Value > 0)
                {
                    Loads.Add(new LoadDefinition
                    {
                        Pattern = legacyPattern,
                        Value = legacyLoadValue.Value,
                        Type = LoadType.DistributedLine,
                        TargetElement = "Frame",
                        Direction = "Gravity",
                        LoadFactor = LoadFactor
                    });
                }
            }
        }

        #endregion

        #region Wall-Specific Methods

        /// <summary>
        /// Tự động tạo WallType từ Thickness nếu chưa có.
        /// Ví dụ: Thickness = 220 -> WallType = "W220"
        /// </summary>
        public void EnsureWallType()
        {
            if (string.IsNullOrEmpty(WallType) && Thickness.HasValue && Thickness.Value > 0)
            {
                WallType = "W" + ((int)Thickness.Value).ToString();
            }
        }

        /// <summary>
        /// Lấy giá trị tải đầu tiên trong danh sách (để hiển thị label).
        /// Trả về 0 nếu không có tải.
        /// </summary>
        public double GetPrimaryLoadValue()
        {
            return HasLoads ? Loads[0].Value : 0;
        }

        /// <summary>
        /// Lấy pattern của tải đầu tiên trong danh sách.
        /// Trả về "DL" nếu không có tải.
        /// </summary>
        public string GetPrimaryLoadPattern()
        {
            return HasLoads ? Loads[0].Pattern : "DL";
        }

        /// <summary>
        /// Hiển thị tóm tắt tất cả loadcases.
        /// Ví dụ: "*DL=7.20, SDL=1.50"
        /// </summary>
        public string GetLoadCasesDisplay()
        {
            if (!HasLoads) return "No loads";

            var lines = new List<string>();
            bool isFirst = true;

            foreach (var load in Loads)
            {
                string marker = isFirst ? "*" : " ";
                lines.Add($"{marker}{load.Pattern}={load.Value:0.00}");
                isFirst = false;
            }

            return string.Join(", ", lines);
        }

        public override string ToString()
        {
            string thkStr = Thickness.HasValue ? $"{Thickness.Value:0}mm" : "[N/A]";
            string loadStr = HasLoads ? $"{GetPrimaryLoadValue():0.00}kN/m" : "[N/A]";
            string linkStatus = IsLinked ? $"Linked:{OriginHandle}" : "Unlinked";

            return $"Wall[{WallType ?? "N/A"}] T={thkStr}, Load={loadStr}, {linkStatus}";
        }

        #endregion
    }
}