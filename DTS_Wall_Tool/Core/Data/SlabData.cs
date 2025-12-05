using DTS_Wall_Tool.Core.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Data
{
    /// <summary>
    /// Dữ liệu Sàn - Kế thừa từ ElementData và implement ILoadBearing.
    /// </summary>
    public class SlabData : ElementData, ILoadBearing
    {
        #region Identity Override

        public override ElementType ElementType => ElementType.Slab;

        #endregion

        #region Slab-Specific Properties

        /// <summary>
        /// Chiều dày sàn (mm)
        /// </summary>
        public double? Thickness { get; set; } = null;

        /// <summary>
        /// Tên sàn (VD: "S120", "S150")
        /// </summary>
        public string SlabName { get; set; } = null;

        /// <summary>
        /// Loại sàn (Solid, Ribbed, Hollow...)
        /// </summary>
        public string SlabType { get; set; } = "Solid";

        /// <summary>
        /// Diện tích sàn (m2)
        /// </summary>
        public double? Area { get; set; } = null;

        /// <summary>
        /// Vật liệu
        /// </summary>
        public string Material { get; set; } = "Concrete";

        /// <summary>
        /// Tải trọng hoạt tải (kN/m2)
        /// </summary>
        public double? LiveLoad { get; set; } = null;

        /// <summary>
        /// Tải trọng hoàn thiện (kN/m2)
        /// </summary>
        public double? FinishLoad { get; set; } = null;

        /// <summary>
        /// Dung trọng bê tông (kN/m³)
        /// </summary>
        public double UnitWeight { get; set; } = 25.0;

        /// <summary>
        /// Load pattern mặc định
        /// </summary>
        public string LoadPattern { get; set; } = "DL";

        #endregion

        #region ILoadBearing Implementation

        /// <summary>
        /// Danh sách tải trọng chuẩn theo Interface ILoadBearing
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
        /// Tính toán tải trọng sàn và đưa vào danh sách Loads.
        /// Tải sàn = Thickness(m) * UnitWeight(kN/m³) = kN/m²
        /// </summary>
        public void CalculateLoads()
        {
            ClearLoads();

            if (!Thickness.HasValue) return;

            // 1. Tính tải bản thân sàn (kN/m²)
            double thicknessM = Thickness.Value / 1000.0;
            double selfWeight = thicknessM * UnitWeight;
            selfWeight = System.Math.Round(selfWeight, 2);

            // Thêm tải bản thân
            Loads.Add(new LoadDefinition
            {
                Pattern = LoadPattern ?? "DL",
                Value = selfWeight,
                Type = LoadType.UniformArea,
                TargetElement = "Area",
                Direction = "Gravity"
            });

            // 2. Thêm tải hoàn thiện nếu có
            if (FinishLoad.HasValue && FinishLoad.Value > 0)
            {
                Loads.Add(new LoadDefinition
                {
                    Pattern = "SDL",
                    Value = FinishLoad.Value,
                    Type = LoadType.UniformArea,
                    TargetElement = "Area",
                    Direction = "Gravity"
                });
            }

            // 3. Thêm hoạt tải nếu có
            if (LiveLoad.HasValue && LiveLoad.Value > 0)
            {
                Loads.Add(new LoadDefinition
                {
                    Pattern = "LL",
                    Value = LiveLoad.Value,
                    Type = LoadType.UniformArea,
                    TargetElement = "Area",
                    Direction = "Gravity"
                });
            }
        }

        #endregion

        #region Override Methods

        public override bool HasValidData()
        {
            return Thickness.HasValue || !string.IsNullOrEmpty(SlabName);
        }

        public override ElementData Clone()
        {
            var clone = new SlabData
            {
                Thickness = Thickness,
                SlabName = SlabName,
                SlabType = SlabType,
                Area = Area,
                Material = Material,
                LiveLoad = LiveLoad,
                FinishLoad = FinishLoad,
                UnitWeight = UnitWeight,
                LoadPattern = LoadPattern
            };

            // Clone Loads (ILoadBearing)
            if (Loads != null)
            {
                clone.Loads = Loads.Select(l => l.Clone()).ToList();
            }

            CopyBaseTo(clone);
            return clone;
        }

        public override Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>();
            WriteBaseProperties(dict);

            if (Thickness.HasValue) dict["xThickness"] = Thickness.Value;
            if (!string.IsNullOrEmpty(SlabName)) dict["xSlabName"] = SlabName;
            if (!string.IsNullOrEmpty(SlabType)) dict["xSlabType"] = SlabType;
            if (Area.HasValue) dict["xArea"] = Area.Value;
            if (!string.IsNullOrEmpty(Material)) dict["xMaterial"] = Material;
            if (LiveLoad.HasValue) dict["xLiveLoad"] = LiveLoad.Value;
            if (FinishLoad.HasValue) dict["xFinishLoad"] = FinishLoad.Value;
            dict["xUnitWeight"] = UnitWeight;
            if (!string.IsNullOrEmpty(LoadPattern)) dict["xLoadPattern"] = LoadPattern;

            // Serialize Loads (ILoadBearing)
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
                    ["LoadFactor"] = l.LoadFactor
                }).ToList();
                dict["xLoads"] = serializer.Serialize(loadsList);
            }

            return dict;
        }

        public override void FromDictionary(Dictionary<string, object> dict)
        {
            ReadBaseProperties(dict);

            if (dict.TryGetValue("xThickness", out var t)) Thickness = ConvertToDouble(t);
            if (dict.TryGetValue("xSlabName", out var sn)) SlabName = sn?.ToString();
            if (dict.TryGetValue("xSlabType", out var st)) SlabType = st?.ToString();
            if (dict.TryGetValue("xArea", out var a)) Area = ConvertToDouble(a);
            if (dict.TryGetValue("xMaterial", out var mat)) Material = mat?.ToString();
            if (dict.TryGetValue("xLiveLoad", out var ll)) LiveLoad = ConvertToDouble(ll);
            if (dict.TryGetValue("xFinishLoad", out var fl)) FinishLoad = ConvertToDouble(fl);
            if (dict.TryGetValue("xUnitWeight", out var uw)) UnitWeight = ConvertToDouble(uw) ?? 25.0;
            if (dict.TryGetValue("xLoadPattern", out var lp)) LoadPattern = lp?.ToString();

            // Deserialize Loads (ILoadBearing)
            if (dict.TryGetValue("xLoads", out var loadsJson))
            {
                try
                {
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var loadsList = serializer.Deserialize<List<Dictionary<string, object>>>(loadsJson.ToString());
                    Loads = new List<LoadDefinition>();

                    foreach (var loadDict in loadsList)
                    {
                        var load = new LoadDefinition();
                        if (loadDict.TryGetValue("Pattern", out var p)) load.Pattern = p?.ToString();
                        if (loadDict.TryGetValue("Value", out var v)) load.Value = System.Convert.ToDouble(v);
                        if (loadDict.TryGetValue("Type", out var tp))
                        {
                            if (System.Enum.TryParse<LoadType>(tp.ToString(), out var lt))
                                load.Type = lt;
                        }
                        if (loadDict.TryGetValue("TargetElement", out var te)) load.TargetElement = te?.ToString();
                        if (loadDict.TryGetValue("Direction", out var d)) load.Direction = d?.ToString();
                        if (loadDict.TryGetValue("LoadFactor", out var lf)) load.LoadFactor = System.Convert.ToDouble(lf);
                        Loads.Add(load);
                    }
                }
                catch
                {
                    Loads = new List<LoadDefinition>();
                }
            }
        }

        #endregion

        #region Slab-Specific Methods

        public void EnsureSlabName()
        {
            if (string.IsNullOrEmpty(SlabName) && Thickness.HasValue)
            {
                SlabName = $"S{(int)Thickness.Value}";
            }
        }

        /// <summary>
        /// Tính tổng tải sàn (kN/m²) = Bản thân + Hoàn thiện + Hoạt tải
        /// </summary>
        public double GetTotalLoad()
        {
            double total = 0;

            // Bản thân
            if (Thickness.HasValue)
            {
                total += (Thickness.Value / 1000.0) * UnitWeight;
            }

            // Hoàn thiện
            if (FinishLoad.HasValue)
            {
                total += FinishLoad.Value;
            }

            // Hoạt tải
            if (LiveLoad.HasValue)
            {
                total += LiveLoad.Value;
            }

            return System.Math.Round(total, 2);
        }

        public override string ToString()
        {
            string thkStr = Thickness.HasValue ? $"{Thickness.Value:0}mm" : "[N/A]";
            string loadStr = HasLoads ? $"{Loads.Sum(l => l.Value):0.00}kN/m²" : "[N/A]";
            string linkStatus = IsLinked ? "Linked" : "Unlinked";
            return $"Slab[{SlabName ?? "N/A"}] T={thkStr}, Load={loadStr}, {SlabType}, {linkStatus}";
        }

        #endregion
    }
}