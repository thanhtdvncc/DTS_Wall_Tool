using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Core.Engines
{
    /// <summary>
    /// Tính toán tải trọng tường và các phần tử khác.
    /// Hỗ trợ ILoadBearing interface cho đa hình.
    /// </summary>
    public class LoadCalculator
    {
        #region Configuration

        /// <summary>
        /// Dung trọng tường xây (kN/m³)
        /// </summary>
        public double WallUnitWeight { get; set; } = 18.0;

        /// <summary>
        /// Chiều cao tầng mặc định (mm)
        /// </summary>
        public double DefaultStoryHeight { get; set; } = 3300;

        /// <summary>
        /// Chiều cao dầm trừ đi (mm)
        /// </summary>
        public double BeamHeightDeduction { get; set; } = 400;

        /// <summary>
        /// Chiều dày vữa trát mỗi bên (mm)
        /// </summary>
        public double PlasterThickness { get; set; } = 15;

        /// <summary>
        /// Dung trọng vữa trát (kN/m³)
        /// </summary>
        public double PlasterUnitWeight { get; set; } = 20.0;

        /// <summary>
        /// Hệ số tải trọng
        /// </summary>
        public double LoadFactor { get; set; } = 1.0;

        /// <summary>
        /// Load pattern mặc định
        /// </summary>
        public string DefaultLoadPattern { get; set; } = "DL";

        /// <summary>
        /// Danh sách modifier
        /// </summary>
        public List<LoadModifier> Modifiers { get; set; } = new List<LoadModifier>();

        #endregion

        #region Constructors

        public LoadCalculator()
        {
            InitializeDefaultModifiers();
        }

        public LoadCalculator(double storyHeight, double beamHeight) : this()
        {
            DefaultStoryHeight = storyHeight;
            BeamHeightDeduction = beamHeight;
        }

        #endregion

        #region Main Calculation Methods

        /// <summary>
        /// Tính tải phân bố (kN/m)
        /// </summary>
        /// <param name="thickness">Độ dày tường (mm)</param>
        /// <param name="height">Chiều cao tường (mm)</param>
        /// <param name="modifierNames">Danh sách modifier áp dụng</param>
        public double CalculateLineLoad(double thickness, double height, IEnumerable<string> modifierNames = null)
        {
            // Chuyển mm sang m
            double thickM = thickness / 1000.0;
            double heightM = height / 1000.0;
            double plasterM = PlasterThickness / 1000.0;

            // Tải tường cơ bản (kN/m²)
            double wallLoadPerM2 = thickM * WallUnitWeight;

            // Tải vữa trát (2 mặt)
            double plasterLoadPerM2 = plasterM * PlasterUnitWeight * 2;

            // Tổng tải diện tích
            double totalAreaLoad = wallLoadPerM2 + plasterLoadPerM2;

            // Chuyển sang tải đường (kN/m)
            double lineLoad = totalAreaLoad * heightM;

            // Áp dụng modifiers
            if (modifierNames != null)
            {
                foreach (var modName in modifierNames)
                {
                    var mod = Modifiers.FirstOrDefault(m =>
                        m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));

                    if (mod != null)
                    {
                        lineLoad = ApplyModifier(lineLoad, mod, height);
                    }
                }
            }

            // Áp dụng hệ số tải
            lineLoad *= LoadFactor;

            return Math.Round(lineLoad, 2);
        }

        /// <summary>
        /// Tính tải với chiều cao mặc định (có trừ dầm)
        /// </summary>
        public double CalculateLineLoadWithDeduction(double thickness, IEnumerable<string> modifierNames = null)
        {
            double effectiveHeight = DefaultStoryHeight - BeamHeightDeduction;
            return CalculateLineLoad(thickness, effectiveHeight, modifierNames);
        }

        /// <summary>
        /// Tính và gán tải cho WallData (backward compatibility)
        /// </summary>
        public void CalculateAndAssign(WallData wallData, double storyHeight = 0)
        {
            if (!wallData.Thickness.HasValue || wallData.Thickness.Value <= 0)
                return;

            double height = storyHeight > 0 ? storyHeight : DefaultStoryHeight;
            double effectiveHeight = height - BeamHeightDeduction;

            var modifiers = ExtractModifiersFromType(wallData.WallType);

            double lineLoad = CalculateLineLoad(wallData.Thickness.Value, effectiveHeight, modifiers);

            wallData.LoadValue = lineLoad;
            wallData.LoadPattern = DefaultLoadPattern;

            // Cập nhật Height để CalculateLoads có thể sử dụng
            wallData.Height = effectiveHeight;
        }

        /// <summary>
        /// Tính và gán tải cho phần tử ILoadBearing (đa hình)
        /// </summary>
        public void CalculateAndAssign(ILoadBearing loadBearing, double storyHeight = 0)
        {
            // Phân luồng xử lý theo loại phần tử
            if (loadBearing is WallData wallData)
            {
                CalculateAndAssign(wallData, storyHeight);

                // Sau khi tính xong, gọi CalculateLoads để đồng bộ vào Loads list
                wallData.CalculateLoads();
            }
            else if (loadBearing is SlabData slabData)
            {
                CalculateSlabLoad(slabData);
            }
            else if (loadBearing is BeamData beamData)
            {
                CalculateBeamLoad(beamData);
            }
            // Thêm các loại phần tử khác ở đây...
        }

        /// <summary>
        /// Tính tải sàn (kN/m²) - Placeholder cho tương lai
        /// </summary>
        private void CalculateSlabLoad(SlabData slabData)
        {
            // TODO: Implement slab load calculation
            // slabData.CalculateLoads();
        }

        /// <summary>
        /// Tính tải dầm (kN/m) - Placeholder cho tương lai
        /// </summary>
        private void CalculateBeamLoad(BeamData beamData)
        {
            // TODO: Implement beam self-weight calculation
            // beamData.CalculateLoads();
        }

        #endregion

        #region Helper Methods

        private double ApplyModifier(double baseLoad, LoadModifier mod, double height)
        {
            switch (mod.Type.ToUpperInvariant())
            {
                case "FACTOR":
                    return baseLoad * mod.Factor;

                case "HEIGHT_OVERRIDE":
                    double heightM = mod.HeightOverride / 1000.0;
                    return baseLoad * (heightM / (height / 1000.0));

                case "ADD":
                    return baseLoad + mod.AddValue;

                case "SUBTRACT":
                    return baseLoad - mod.AddValue;

                default:
                    return baseLoad;
            }
        }

        private List<string> ExtractModifiersFromType(string wallType)
        {
            var modifiers = new List<string>();

            if (string.IsNullOrEmpty(wallType))
                return modifiers;

            var parts = wallType.Split('_');
            foreach (var part in parts.Skip(1))
            {
                if (Modifiers.Any(m => m.Name.Equals(part, StringComparison.OrdinalIgnoreCase)))
                {
                    modifiers.Add(part);
                }
            }

            return modifiers;
        }

        #endregion

        #region Initialization

        private void InitializeDefaultModifiers()
        {
            Modifiers = new List<LoadModifier>
            {
                new LoadModifier
                {
                    Name = "PARAPET",
                    Type = "HEIGHT_OVERRIDE",
                    HeightOverride = 1200,
                    Description = "Tường lan can (cao 1.2m)"
                },
                new LoadModifier
                {
                    Name = "HALF",
                    Type = "FACTOR",
                    Factor = 0.5,
                    Description = "Tường nửa chiều cao"
                },
                new LoadModifier
                {
                    Name = "FIRE",
                    Type = "ADD",
                    AddValue = 0.5,
                    Description = "Tường chống cháy (+0.5 kN/m)"
                },
                new LoadModifier
                {
                    Name = "FULL",
                    Type = "HEIGHT_OVERRIDE",
                    HeightOverride = DefaultStoryHeight,
                    Description = "Tường full (không trừ dầm)"
                }
            };
        }

        #endregion

        #region Static Helpers

        /// <summary>
        /// Bảng tra nhanh tải cho các độ dày phổ biến
        /// </summary>
        public static Dictionary<int, double> GetQuickLoadTable(double storyHeight = 3300, double beamHeight = 400)
        {
            var calc = new LoadCalculator(storyHeight, beamHeight);

            int[] thicknesses = { 100, 110, 150, 200, 220, 250, 300 };
            var result = new Dictionary<int, double>();

            foreach (var t in thicknesses)
            {
                result[t] = calc.CalculateLineLoadWithDeduction(t);
            }

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Định nghĩa modifier cho tải trọng
    /// </summary>
    public class LoadModifier
    {
        public string Name { get; set; }
        public string Type { get; set; } // FACTOR, HEIGHT_OVERRIDE, ADD, SUBTRACT
        public double Factor { get; set; } = 1.0;
        public double HeightOverride { get; set; }
        public double AddValue { get; set; }
        public string Description { get; set; }

        public override string ToString() => $"{Name} ({Type})";
    }
}