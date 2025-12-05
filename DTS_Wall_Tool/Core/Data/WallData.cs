using DTS_Wall_Tool.Core.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Data
{
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
    /// Dữ liệu Tường - Kế thừa từ ElementData và implement ILoadBearing.
    /// Chứa các thuộc tính đặc thù của Tường và hỗ trợ gán tải đa năng.
    /// </summary>
    public class WallData : ElementData, ILoadBearing
    {
        #region Identity Override

        public override ElementType ElementType => ElementType.Wall;

        #endregion

        #region Wall-Specific Properties

        /// <summary>
        /// Độ dày tường (mm)
        /// </summary>
        public double? Thickness { get; set; } = null;

        /// <summary>
        /// Loại tường (VD: "W220", "W200")
        /// </summary>
        public string WallType { get; set; } = null;

        /// <summary>
        /// Loại vật liệu tường (Brick, Concrete, Block...)
        /// </summary>
        public string Material { get; set; } = "Brick";

        /// <summary>
        /// Trọng lượng riêng vật liệu (kN/m3)
        /// </summary>
        public double? UnitWeight { get; set; } = 18.0;

        // Default pattern being displayed in label (backward compatibility)
        public string LoadPattern { get; set; } = "DL";
        public double? LoadValue { get; set; } = null;
        public double LoadFactor { get; set; } = 1.0;

        // Cache totals by pattern (backward compatibility)
        public Dictionary<string, double> LoadCases { get; set; } = new Dictionary<string, double>();
        public string LoadCasesLastSync { get; set; } = null;

        // Detailed entries with segments (backward compatibility for SAP sync)
        public List<LoadEntry> LoadEntries { get; set; } = new List<LoadEntry>();

        #endregion

        #region ILoadBearing Implementation

        /// <summary>
        /// Danh sách tải trọng chuẩn theo Interface ILoadBearing
        /// </summary>
        public List<LoadDefinition> Loads { get; set; } = new List<LoadDefinition>();

        /// <summary>
        /// Mappings đã có trong ElementData base class
        /// </summary>
        // List<MappingRecord> Mappings is inherited from ElementData

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
        /// Tính toán tải trọng và đưa vào danh sách Loads.
        /// Đồng thời cập nhật LoadValue cho backward compatibility.
        /// </summary>
        public void CalculateLoads()
        {
            // Xóa tải cũ
            ClearLoads();

            if (!Thickness.HasValue || !Height.HasValue || !UnitWeight.HasValue)
                return;

            // 1. Tính toán giá trị tải phân bố (Line Load)
            double thicknessM = Thickness.Value / 1000.0;
            double heightM = Height.Value / 1000.0;
            double val = thicknessM * heightM * UnitWeight.Value * LoadFactor;

            // 2. Làm tròn 2 chữ số
            val = System.Math.Round(val, 2);

            // 3. Cập nhật thuộc tính cũ (backward compatibility - để Label hiển thị đúng)
            LoadValue = val;

            // 4. Thêm vào danh sách tải chuẩn (để SyncEngine xử lý)
            Loads.Add(new LoadDefinition
            {
                Pattern = LoadPattern ?? "DL",
                Value = val,
                Type = Interfaces.LoadType.DistributedLine,
                TargetElement = "Frame",
                Direction = "Gravity",
                LoadFactor = LoadFactor
            });

            // 5. Cập nhật LoadCases cache
            UpdateLoadCase(LoadPattern ?? "DL", val);
        }

        #endregion

        #region Override Methods

        public override bool HasValidData()
        {
            return Thickness.HasValue || !string.IsNullOrEmpty(WallType) || LoadValue.HasValue;
        }

        public override ElementData Clone()
        {
            var clone = new WallData
            {
                Thickness = Thickness,
                WallType = WallType,
                Material = Material,
                UnitWeight = UnitWeight,
                LoadPattern = LoadPattern,
                LoadValue = LoadValue,
                LoadFactor = LoadFactor,
                LoadCasesLastSync = LoadCasesLastSync
            };

            // Clone LoadCases
            if (LoadCases != null)
            {
                clone.LoadCases = new Dictionary<string, double>(LoadCases);
            }

            // Clone LoadEntries
            if (LoadEntries != null)
            {
                clone.LoadEntries = LoadEntries.Select(e => new LoadEntry
                {
                    Pattern = e.Pattern,
                    Value = e.Value,
                    Direction = e.Direction,
                    LoadType = e.LoadType,
                    Segments = e.Segments?.Select(s => new LoadSegment { I = s.I, J = s.J }).ToList()
                    ?? new List<LoadSegment>()
                }).ToList();
            }

            // Clone Loads (ILoadBearing)
            if (Loads != null)
            {
                clone.Loads = Loads.Select(l => l.Clone()).ToList();
            }

            // Copy base properties
            CopyBaseTo(clone);

            return clone;
        }

        public override Dictionary<string, object> ToDictionary()
        {
            var dict = new Dictionary<string, object>();

            // Write base properties
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

            if (!string.IsNullOrEmpty(LoadPattern))
                dict["xLoadPattern"] = LoadPattern;

            if (LoadValue.HasValue)
                dict["xLoadValue"] = LoadValue.Value;

            dict["xLoadFactor"] = LoadFactor;

            // Serialize totals
            if (LoadCases != null && LoadCases.Count > 0)
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                dict["xLoadCases"] = serializer.Serialize(LoadCases);
            }

            if (!string.IsNullOrEmpty(LoadCasesLastSync))
                dict["xLoadCasesLastSync"] = LoadCasesLastSync;

            // Serialize detailed entries
            if (LoadEntries != null && LoadEntries.Count > 0)
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                dict["xLoadEntries"] = serializer.Serialize(LoadEntries);
            }

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
                    ["DistI"] = l.DistI,
                    ["DistJ"] = l.DistJ,
                    ["IsRelativeDistance"] = l.IsRelativeDistance,
                    ["LoadFactor"] = l.LoadFactor
                }).ToList();
                dict["xLoads"] = serializer.Serialize(loadsList);
            }

            return dict;
        }

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

            if (dict.TryGetValue("xLoadPattern", out var loadPattern))
                LoadPattern = loadPattern?.ToString();

            if (dict.TryGetValue("xLoadValue", out var loadValue))
                LoadValue = ConvertToDouble(loadValue);

            if (dict.TryGetValue("xLoadFactor", out var loadFactor))
                LoadFactor = ConvertToDouble(loadFactor) ?? 1.0;

            if (dict.TryGetValue("xLoadCases", out var loadCasesJson))
            {
                try
                {
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    LoadCases = serializer.Deserialize<Dictionary<string, double>>(loadCasesJson.ToString());
                }
                catch
                {
                    LoadCases = new Dictionary<string, double>();
                }
            }

            if (dict.TryGetValue("xLoadCasesLastSync", out var lastSync))
                LoadCasesLastSync = lastSync?.ToString();

            // Deserialize detailed entries
            if (dict.TryGetValue("xLoadEntries", out var loadEntriesJson))
            {
                try
                {
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    LoadEntries = serializer.Deserialize<List<LoadEntry>>(loadEntriesJson.ToString());
                }
                catch
                {
                    LoadEntries = new List<LoadEntry>();
                }
            }

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
                        if (loadDict.TryGetValue("Type", out var t))
                        {
                            if (System.Enum.TryParse<Interfaces.LoadType>(t.ToString(), out var lt))
                                load.Type = lt;
                        }
                        if (loadDict.TryGetValue("TargetElement", out var te)) load.TargetElement = te?.ToString();
                        if (loadDict.TryGetValue("Direction", out var d)) load.Direction = d?.ToString();
                        if (loadDict.TryGetValue("DistI", out var di)) load.DistI = System.Convert.ToDouble(di);
                        if (loadDict.TryGetValue("DistJ", out var dj)) load.DistJ = System.Convert.ToDouble(dj);
                        if (loadDict.TryGetValue("IsRelativeDistance", out var ir)) load.IsRelativeDistance = System.Convert.ToBoolean(ir);
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

        #region Wall-Specific Methods

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
        /// Tính tải trọng tường (kN/m) - Backward compatibility method
        /// LoadValue = Thickness(m) * Height(m) * UnitWeight(kN/m3) * LoadFactor
        /// </summary>
        public void CalculateLoad()
        {
            // Delegate to new method
            CalculateLoads();
        }

        public override string ToString()
        {
            string thkStr = Thickness.HasValue ? $"{Thickness.Value:0}mm" : "[N/A]";
            string loadStr = LoadValue.HasValue ? $"{LoadValue.Value:0.00}kN/m" : "[N/A]";
            string linkStatus = IsLinked ? $"Linked:{OriginHandle}" : "Unlinked";

            return $"Wall[{WallType ?? "N/A"}] T={thkStr}, Load={loadStr}, {linkStatus}";
        }

        // Update cache total per pattern
        public void UpdateLoadCase(string pattern, double value)
        {
            if (LoadCases == null) LoadCases = new Dictionary<string, double>();

            LoadCases[pattern] = value;
            LoadCasesLastSync = System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            // Nếu pattern là pattern mặc định hiện tại thì cập nhật LoadValue luôn
            if (pattern == LoadPattern) LoadValue = value;
        }

        // Cache load case without overwriting current LoadValue
        public void CacheLoadCase(string pattern, double value)
        {
            if (LoadCases == null) LoadCases = new Dictionary<string, double>();
            LoadCases[pattern] = value;
            LoadCasesLastSync = System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        }

        // Clear load entries
        public void ClearLoadEntries() { LoadEntries?.Clear(); }

        public string GetLoadCasesDisplay()
        {
            if (LoadCases == null || LoadCases.Count == 0) return "No loadcases";
            var lines = new List<string>();
            foreach (var kvp in LoadCases)
            {
                string marker = (kvp.Key == LoadPattern) ? "*" : " ";
                lines.Add($"{marker}{kvp.Key}={kvp.Value:0.00}");
            }
            return string.Join(", ", lines);
        }

        #endregion
    }
}