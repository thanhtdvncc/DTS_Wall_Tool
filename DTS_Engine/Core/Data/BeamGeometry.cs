using System;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Lightweight geometry data for beam detection algorithms.
    /// ISO 25010: Reusability - Centralized DTO for geometric operations.
    /// NOTE: For XData serialization, use BeamData (ElementData-based) instead.
    /// </summary>
    public class BeamGeometry
    {
        /// <summary>CAD Entity Handle</summary>
        public string Handle { get; set; }

        /// <summary>SAP/User assigned name</summary>
        public string Name { get; set; }

        /// <summary>Start point X (mm)</summary>
        public double StartX { get; set; }

        /// <summary>Start point Y (mm)</summary>
        public double StartY { get; set; }

        /// <summary>Start point Z (mm) - for story matching</summary>
        public double StartZ { get; set; }

        /// <summary>End point X (mm)</summary>
        public double EndX { get; set; }

        /// <summary>End point Y (mm)</summary>
        public double EndY { get; set; }

        /// <summary>Section width (mm)</summary>
        public double Width { get; set; }

        /// <summary>Section height/depth (mm)</summary>
        public double Height { get; set; }

        /// <summary>Computed length in XY plane (mm)</summary>
        public double Length => Math.Sqrt(Math.Pow(EndX - StartX, 2) + Math.Pow(EndY - StartY, 2));

        /// <summary>Center point X</summary>
        public double CenterX => (StartX + EndX) / 2;

        /// <summary>Center point Y</summary>
        public double CenterY => (StartY + EndY) / 2;

        /// <summary>Direction: "X" if mostly horizontal, "Y" if mostly vertical</summary>
        public string Direction => Math.Abs(EndX - StartX) > Math.Abs(EndY - StartY) ? "X" : "Y";
    }
}
