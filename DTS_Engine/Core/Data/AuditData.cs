using DTS_Engine.Core.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Raw load data from SAP2000 with enrichment fields.
    /// 
    /// LIFECYCLE:
    /// 1. Read from SAP API (Value1, DirectionCode, ElementZ)
    /// 2. Enriched by LoadEnricher (GlobalCenter, PreCalculatedGridLoc, VectorKey)
    /// 3. Grouped by LoadGrouper (no modification)
    /// 4. Processed by ReportBuilder (geometry calculations)
    /// </summary>
    public class RawSapLoad
    {
        #region Core Properties (from SAP API)

        public string ElementName { get; set; }
        public string LoadPattern { get; set; }
        public double Value1 { get; set; } // Primary load value
        public double Value2 { get; set; } // Secondary value (for trapezoidal loads)
        public string LoadType { get; set; } // FrameDistributed, AreaUniform, PointForce
        public string Direction { get; set; } // Legacy: "Gravity", "Local 1", etc.
        public int DirectionCode { get; set; } // SAP2000 direction code
        public double ElementZ { get; set; } // Elevation for story assignment

        #endregion

        #region Vector Properties (calculated during read)

        public double DirectionX { get; set; }
        public double DirectionY { get; set; }
        public double DirectionZ { get; set; }
        public string GlobalAxis { get; set; } // "X", "Y", "Z"
        public double DirectionSign { get; set; } = -1.0;

        #endregion

        #region Enrichment Properties (calculated by LoadEnricher)

        public Point2D GlobalCenter { get; set; }
        public string PreCalculatedGridLoc { get; set; }
        public string VectorKey { get; set; }

        #endregion

        #region Load Distribution (for partial loads)

        public string DistributionType { get; set; }
        public double DistStart { get; set; }
        public double DistEnd { get; set; }
        public bool IsRelative { get; set; }
        public string CoordSys { get; set; } = "Global";

        #endregion

        #region Computed Properties

        public bool IsLateralLoad
        {
            get
            {
                double absX = Math.Abs(DirectionX);
                double absY = Math.Abs(DirectionY);
                double absZ = Math.Abs(DirectionZ);
                return Math.Max(absX, absY) > absZ * 0.5;
            }
        }

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

        public Vector3D GetForceVector()
        {
            return new Vector3D(DirectionX, DirectionY, DirectionZ);
        }

        public void SetForceVector(Vector3D forceVector)
        {
            DirectionX = forceVector.X;
            DirectionY = forceVector.Y;
            DirectionZ = forceVector.Z;

            string primaryAxis = forceVector.GetPrimaryAxis();
            GlobalAxis = primaryAxis;

            switch (primaryAxis)
            {
                case "X": DirectionSign = Math.Sign(forceVector.X); break;
                case "Y": DirectionSign = Math.Sign(forceVector.Y); break;
                case "Z": DirectionSign = Math.Sign(forceVector.Z); break;
            }

            UpdateVectorKey();
        }

        public void UpdateVectorKey()
        {
            var vec = GetForceVector().Normalized;
            VectorKey = $"Dir_{vec.X:0.000}_{vec.Y:0.000}_{vec.Z:0.000}";
        }

        #endregion

        public override string ToString() => $"{LoadPattern}|{ElementName}|{LoadType}|{Value1:0.00}|{GlobalAxis}|{PreCalculatedGridLoc}";
    }

    /// <summary>
    /// Full Audit Report - output of the audit pipeline.
    /// </summary>
    public class AuditReport
    {
        public string LoadPattern { get; set; }
        public string ModelName { get; set; }
        public DateTime AuditDate { get; set; }
        public List<AuditStoryGroup> Stories { get; set; } = new List<AuditStoryGroup>();

        // Vector totals (sum of all entries)
        public double CalculatedFx { get; set; }
        public double CalculatedFy { get; set; }
        public double CalculatedFz { get; set; }

        // Magnitude = sqrt(Fx² + Fy² + Fz²)
        public double TotalCalculatedForce => Math.Sqrt(CalculatedFx * CalculatedFx + CalculatedFy * CalculatedFy + CalculatedFz * CalculatedFz);

        // Comparison with SAP
        public double SapBaseReaction { get; set; }
        public bool IsAnalyzed { get; set; } = true;

        public double Difference => TotalCalculatedForce - Math.Abs(SapBaseReaction);
        public double DifferencePercent => TotalCalculatedForce > 0 ? (Difference / TotalCalculatedForce) * 100.0 : 0;

        public string UnitInfo { get; set; }
    }

    /// <summary>
    /// Group by Story/Elevation.
    /// </summary>
    public class AuditStoryGroup
    {
        public string StoryName { get; set; }
        public double Elevation { get; set; }
        public List<AuditLoadTypeGroup> LoadTypes { get; set; } = new List<AuditLoadTypeGroup>();

        public double TotalForce => LoadTypes.Sum(g => g.TotalForce);
    }

    /// <summary>
    /// Group by Load Type (Area/Frame/Point).
    /// </summary>
    public class AuditLoadTypeGroup
    {
        public string LoadTypeName { get; set; }
        public List<AuditEntry> Entries { get; set; } = new List<AuditEntry>();

        // Vector subtotals
        public double SubTotalFx { get; set; }
        public double SubTotalFy { get; set; }
        public double SubTotalFz { get; set; }

        public double TotalForce => Math.Sqrt(SubTotalFx * SubTotalFx + SubTotalFy * SubTotalFy + SubTotalFz * SubTotalFz);

        public int ElementCount => Entries?.Sum(e => e.ElementCount) ?? 0;
    }

    /// <summary>
    /// Single row in the report table.
    /// 
    /// FORMULA: Force = Quantity × UnitLoad × Direction
    /// </summary>
    public class AuditEntry
    {
        #region Display Columns

        /// <summary>
        /// Grid location string (e.g., "1-2/A-B")
        /// </summary>
        public string GridLocation { get; set; }

        /// <summary>
        /// Explanation text for display (e.g., "4×5 + 2×3")
        /// NOTE: This is for display only, not used in calculations
        /// </summary>
        public string Explanation { get; set; }

        /// <summary>
        /// Quantity value - area (m²), length (m), or count
        /// </summary>
        public double Quantity { get; set; }

        /// <summary>
        /// Unit of quantity: "m²", "m", or "pcs"
        /// </summary>
        public string QuantityUnit { get; set; }

        /// <summary>
        /// Unit load value (kN/m², kN/m, or kN)
        /// </summary>
        public double UnitLoad { get; set; }

        /// <summary>
        /// Formatted unit load string for display
        /// </summary>
        public string UnitLoadString { get; set; }

        /// <summary>
        /// Direction description (e.g., "-Z (Gravity)", "+X")
        /// </summary>
        public string Direction { get; set; }

        /// <summary>
        /// Direction sign (+1 or -1)
        /// </summary>
        public double DirectionSign { get; set; } = -1.0;

        #endregion

        #region Force Components

        /// <summary>
        /// Force X component (kN). Formula: Quantity × UnitLoad × DirX
        /// </summary>
        public double ForceX { get; set; }

        /// <summary>
        /// Force Y component (kN). Formula: Quantity × UnitLoad × DirY
        /// </summary>
        public double ForceY { get; set; }

        /// <summary>
        /// Force Z component (kN). Formula: Quantity × UnitLoad × DirZ
        /// </summary>
        public double ForceZ { get; set; }

        /// <summary>
        /// Total force magnitude = sqrt(Fx² + Fy² + Fz²)
        /// </summary>
        public double TotalForce { get; set; }

        #endregion

        #region Element Information

        /// <summary>
        /// List of element names contributing to this entry
        /// </summary>
        public List<string> ElementList { get; set; } = new List<string>();

        /// <summary>
        /// Count of elements
        /// </summary>
        public int ElementCount => ElementList?.Count ?? 0;

        #endregion

        #region Backward Compatibility

        public double Force
        {
            get => TotalForce;
            set => TotalForce = value;
        }

        #endregion
    }
}
