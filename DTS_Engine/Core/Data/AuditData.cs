using DTS_Engine.Core.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Raw data from SAP2000
    /// NEW: Hỗ trợ Global Axis và Direction Sign để phân biệt chính xác X/Y/Z
    /// </summary>
    public class RawSapLoad
    {
        public string ElementName { get; set; }
        public string LoadPattern { get; set; }
        public double Value1 { get; set; }
        public double Value2 { get; set; }
        public string LoadType { get; set; } // FrameDistributed, FramePoint, AreaUniform, PointForce
        public string Direction { get; set; } // Legacy: "Gravity", "Local 1"...
        
        // NEW: Global Axis resolution
        public string GlobalAxis { get; set; } // "X", "Y", "Z" after local→global conversion
        public double DirectionSign { get; set; } = -1.0; // +1 or -1 (direction của lực)

        // Resolved global components of the load (in same unit as Value1)
        // These are calculated by transforming the local/load direction into global X/Y/Z components.
        public double DirectionX { get; set; }
        public double DirectionY { get; set; }
        public double DirectionZ { get; set; }

        /// <summary>
        /// Is this load primarily lateral (X or Y) compared to Z?
        /// Uses a simple dominance test: max(|X|,|Y|) > 0.5 * |Z|
        /// </summary>
        public bool IsLateralLoad
        {
            get
            {
                double absX = Math.Abs(DirectionX);
                double absY = Math.Abs(DirectionY);
                double absZ = Math.Abs(DirectionZ);
                double lateralMag = Math.Max(absX, absY);
                return lateralMag > absZ * 0.5;
            }
        }
        
        public string DistributionType { get; set; }
        public double DistStart { get; set; }
        public double DistEnd { get; set; }
        public bool IsRelative { get; set; }
        public string CoordSys { get; set; } = "Global";
        public double ElementZ { get; set; }

        public string GetUnitString()
        {
            switch (LoadType)
            {
                case "AreaUniform":
                case "AreaUniformToFrame": return "kN/m²";
                case "FrameDistributed": return "kN/m";
                default: return "kN";
            }
        }

        /// <summary>
        /// Kiểm tra xem tải có theo phương Global nào không
        /// </summary>
        public bool IsGlobalX => !string.IsNullOrEmpty(GlobalAxis) && GlobalAxis.Equals("X", StringComparison.OrdinalIgnoreCase);
        public bool IsGlobalY => !string.IsNullOrEmpty(GlobalAxis) && GlobalAxis.Equals("Y", StringComparison.OrdinalIgnoreCase);
        public bool IsGlobalZ => !string.IsNullOrEmpty(GlobalAxis) && GlobalAxis.Equals("Z", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Tạo Vector3D từ các component global
        /// </summary>
        public Vector3D GetForceVector()
        {
            return new Vector3D(DirectionX, DirectionY, DirectionZ);
        }

        /// <summary>
        /// Gán các component global từ Vector3D
        /// </summary>
        public void SetForceVector(Vector3D forceVector)
        {
            DirectionX = forceVector.X;
            DirectionY = forceVector.Y;
            DirectionZ = forceVector.Z;

            // Auto-update GlobalAxis và Sign
            string primaryAxis = forceVector.GetPrimaryAxis();
            GlobalAxis = primaryAxis;

            switch (primaryAxis)
            {
                case "X": DirectionSign = Math.Sign(forceVector.X); break;
                case "Y": DirectionSign = Math.Sign(forceVector.Y); break;
                case "Z": DirectionSign = Math.Sign(forceVector.Z); break;
            }
        }

        public override string ToString() => $"{LoadPattern}|{ElementName}|{LoadType}|{Value1:0.00}|{GlobalAxis ?? Direction}";
    }

    /// <summary>
    /// Full Audit Report
    /// </summary>
    public class AuditReport
    {
        public string LoadPattern { get; set; }
        public DateTime AuditDate { get; set; }
        public List<AuditStoryGroup> Stories { get; set; } = new List<AuditStoryGroup>();

        public double TotalCalculatedForce => Stories.Sum(s => s.TotalForce);
        public double SapBaseReaction { get; set; }

        public double Difference => TotalCalculatedForce - Math.Abs(SapBaseReaction);
        // Percentage difference relative to calculated total (in %)
        public double DifferencePercent
        {
            get
            {
                if (TotalCalculatedForce == 0) return 0;
                return (Difference / TotalCalculatedForce) * 100.0;
            }
        }

        public string ModelName { get; set; }
        public string UnitInfo { get; set; }
        public bool IsAnalyzed { get; set; } = true; // Track if model has results
    }

    /// <summary>
    /// Group by Story
    /// </summary>
    public class AuditStoryGroup
    {
        public string StoryName { get; set; }
        public double Elevation { get; set; }
        public List<AuditLoadTypeGroup> LoadTypes { get; set; } = new List<AuditLoadTypeGroup>();

        // Backward-compatible alias expected by AuditEngine
        public List<AuditLoadTypeGroup> LoadTypeGroups
        {
            get => LoadTypes;
            set => LoadTypes = value;
        }

        public double TotalForce => LoadTypes.Sum(g => g.TotalForce);

        // Alias used in reporting code
        public double SubTotalForce => TotalForce;
    }

    /// <summary>
    /// Group by Load Type (Frame/Area/Point) - FLAT STRUCTURE
    /// </summary>
    public class AuditLoadTypeGroup
    {
        public string LoadTypeName { get; set; }
        // List of entries directly (legacy) and ValueGroups (new)
        public List<AuditEntry> Entries { get; set; } = new List<AuditEntry>();

        // Group by value buckets (used by AuditEngine)
        public List<AuditValueGroup> ValueGroups { get; set; } = new List<AuditValueGroup>();

        public double TotalForce
        {
            get
            {
                if (ValueGroups != null && ValueGroups.Count > 0)
                    return ValueGroups.Sum(v => v.TotalForce);
                return Entries?.Sum(e => e.TotalForce) ?? 0;
            }
        }

        public int ElementCount
        {
            get
            {
                if (ValueGroups != null && ValueGroups.Count > 0)
                    return ValueGroups.Sum(v => v.ElementCount);
                return Entries?.Sum(e => e.ElementCount) ?? 0;
            }
        }
    }

    /// <summary>
    /// Group by load value (bucket) used inside AuditLoadTypeGroup
    /// </summary>
    public class AuditValueGroup
    {
        public double LoadValue { get; set; }
        public string Direction { get; set; }
        public List<AuditEntry> Entries { get; set; } = new List<AuditEntry>();

        public double TotalForce => Entries?.Sum(e => e.TotalForce) ?? 0;
        public int ElementCount => Entries?.Sum(e => e.ElementCount) ?? 0;
    }

    /// <summary>
    /// Single row in the report table
    /// </summary>
    public class AuditEntry
    {
        public string GridLocation { get; set; }
        public string Explanation { get; set; } // Dimensions or Description

        public double Quantity { get; set; } // Length (m) or Area (m2) or Count
        public string QuantityUnit { get; set; } // m, m2, pcs

        public double UnitLoad { get; set; } // The load value (kN/m, kN/m2, kN)
        public string UnitLoadString { get; set; } // "12.5 kN/m"

        public double TotalForce { get; set; } // Calculated Force (kN)

        public string Direction { get; set; } // Gravity, X, Y

        public List<string> ElementList { get; set; } = new List<string>();
        public int ElementCount => ElementList?.Count ?? 0;
        // Backward-compatible property used in engines
        public double Force
        {
            get => TotalForce;
            set => TotalForce = value;
        }
    }
}
