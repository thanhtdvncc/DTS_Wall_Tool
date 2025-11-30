using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Wall_Tool.Core;
using DTS_Wall_Tool.Models;

namespace DTS_Wall_Tool.Engines
{
    /// <summary>
    /// Calculates wall loads for SAP2000
    /// Replaces VBA module n03_ACAD_Wall_AutoForce_SAP2000
    /// </summary>
    public class LoadCalculator
    {
        #region Configuration

        /// <summary>
        /// Unit weight of masonry wall (kN/m³)
        /// </summary>
        public double WallUnitWeight { get; set; } = 18.0;

        /// <summary>
        /// Default story height (mm)
        /// </summary>
        public double DefaultStoryHeight { get; set; } = 3300;

        /// <summary>
        /// Height deduction for beam (mm)
        /// </summary>
        public double BeamHeightDeduction { get; set; } = 400;

        /// <summary>
        /// Plaster thickness per side (mm)
        /// </summary>
        public double PlasterThickness { get; set; } = 15;

        /// <summary>
        /// Unit weight of plaster (kN/m³)
        /// </summary>
        public double PlasterUnitWeight { get; set; } = 20.0;

        /// <summary>
        /// Load factor for dead load
        /// </summary>
        public double LoadFactorDead { get; set; } = 1.0;

        /// <summary>
        /// Load factor for super dead load (SDL)
        /// </summary>
        public double LoadFactorSDL { get; set; } = 1.0;

        /// <summary>
        /// Default load pattern name
        /// </summary>
        public string DefaultLoadPattern { get; set; } = "DL";

        /// <summary>
        /// List of modifier definitions
        /// </summary>
        public List<ModifierDef> Modifiers { get; set; } = new List<ModifierDef>();

        #endregion

        #region Load Calculation Methods

        /// <summary>
        /// Calculate line load for a wall segment (kN/m)
        /// </summary>
        /// <param name="thickness">Wall thickness in mm</param>
        /// <param name="height">Wall height in mm</param>
        /// <param name="modifiers">List of modifier names to apply</param>
        /// <returns>Line load in kN/m</returns>
        public double CalculateLineLoad(double thickness, double height, IEnumerable<string> modifiers = null)
        {
            // Convert mm to m
            double thickM = thickness / 1000.0;
            double heightM = height / 1000.0;
            double plasterM = PlasterThickness / 1000.0;

            // Base wall load (kN/m²)
            double wallLoadPerM2 = thickM * WallUnitWeight;

            // Plaster load (both sides)
            double plasterLoadPerM2 = plasterM * PlasterUnitWeight * 2;

            // Total area load
            double totalAreaLoad = wallLoadPerM2 + plasterLoadPerM2;

            // Convert to line load (kN/m)
            double lineLoad = totalAreaLoad * heightM;

            // Apply modifiers
            if (modifiers != null)
            {
                foreach (var modName in modifiers)
                {
                    var mod = Modifiers.FirstOrDefault(m =>
                        m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));

                    if (mod != null)
                    {
                        lineLoad = ApplyModifier(lineLoad, mod, height);
                    }
                }
            }

            // Apply load factor
            lineLoad *= LoadFactorDead;

            return Math.Round(lineLoad, 2);
        }

        /// <summary>
        /// Calculate line load using default story height with beam deduction
        /// </summary>
        public double CalculateLineLoadWithDeduction(double thickness, IEnumerable<string> modifiers = null)
        {
            double effectiveHeight = DefaultStoryHeight - BeamHeightDeduction;
            return CalculateLineLoad(thickness, effectiveHeight, modifiers);
        }

        /// <summary>
        /// Calculate load for a WallData object
        /// </summary>
        public void CalculateAndAssign(WallData wallData, double storyHeight = 0)
        {
            if (!wallData.Thickness.HasValue || wallData.Thickness.Value <= 0)
                return;

            double height = storyHeight > 0 ? storyHeight : DefaultStoryHeight;
            double effectiveHeight = height - BeamHeightDeduction;

            // Get modifiers from wall type (e.g., "W220_PARAPET" -> ["PARAPET"])
            var modifiers = ExtractModifiersFromType(wallData.WallType);

            double lineLoad = CalculateLineLoad(wallData.Thickness.Value, effectiveHeight, modifiers);

            wallData.LoadValue = lineLoad;
            wallData.LoadPattern = DefaultLoadPattern;
        }

        #endregion

        #region Helper Methods

        private double ApplyModifier(double baseLoad, ModifierDef mod, double height)
        {
            switch (mod.ModifierType.ToUpperInvariant())
            {
                case "FACTOR":
                    return baseLoad * mod.Factor;

                case "HEIGHT_OVERRIDE":
                    // Recalculate with different height
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

            // Parse modifiers from wall type (e.g., "W220_PARAPET_FIRE")
            var parts = wallType.Split('_');
            foreach (var part in parts.Skip(1)) // Skip "W220"
            {
                if (Modifiers.Any(m => m.Name.Equals(part, StringComparison.OrdinalIgnoreCase)))
                {
                    modifiers.Add(part);
                }
            }

            return modifiers;
        }

        #endregion

        #region Preset Methods

        /// <summary>
        /// Initialize default modifiers
        /// </summary>
        public void InitializeDefaultModifiers()
        {
            Modifiers = new List<ModifierDef>
            {
                new ModifierDef
                {
                    Name = "PARAPET",
                    ModifierType = "HEIGHT_OVERRIDE",
                    HeightOverride = 1200,
                    Description = "Parapet wall (1. 2m height)"
                },
                new ModifierDef
                {
                    Name = "HALF",
                    ModifierType = "FACTOR",
                    Factor = 0.5,
                    Description = "Half-height wall"
                },
                new ModifierDef
                {
                    Name = "FIRE",
                    ModifierType = "ADD",
                    AddValue = 0.5,
                    Description = "Fire-rated wall (+0.5 kN/m)"
                },
                new ModifierDef
                {
                    Name = "FULL",
                    ModifierType = "HEIGHT_OVERRIDE",
                    HeightOverride = DefaultStoryHeight,
                    Description = "Full height wall (no beam deduction)"
                }
            };
        }

        /// <summary>
        /// Get quick load value for common wall types
        /// </summary>
        public static Dictionary<int, double> GetQuickLoadTable(double storyHeight = 3300, double beamHeight = 400)
        {
            var calc = new LoadCalculator
            {
                DefaultStoryHeight = storyHeight,
                BeamHeightDeduction = beamHeight
            };

            int[] commonThicknesses = { 100, 110, 150, 200, 220, 250, 300 };
            var result = new Dictionary<int, double>();

            foreach (var t in commonThicknesses)
            {
                result[t] = calc.CalculateLineLoadWithDeduction(t);
            }

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Definition of a load modifier
    /// </summary>
    public class ModifierDef
    {
        public string Name { get; set; }
        public string ModifierType { get; set; } // FACTOR, HEIGHT_OVERRIDE, ADD, SUBTRACT
        public double Factor { get; set; } = 1.0;
        public double HeightOverride { get; set; }
        public double AddValue { get; set; }
        public string Description { get; set; }

        public override string ToString()
        {
            return $"{Name} ({ModifierType})";
        }
    }
}