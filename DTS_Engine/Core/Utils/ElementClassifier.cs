using DTS_Engine.Core.Primitives;
using System;
using DTS_Engine.Core.Data; // For SapFrame/SapArea if needed, though Vectors usually suffice

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// Centralized Logic for Classifying SAP Elements based on their Geometry.
    /// Source of Truth: SapLoadDiagnostics.DTS_DEBUG_SELECTED
    /// Rules:
    /// - Strict Threshold: Direction Cosine > 0.9
    /// </summary>
    public static class ElementClassifier
    {
        private const double STRICT_THRESHOLD = 0.9;

        public enum GlobalAxis
        {
            Unknown = 0,
            PositiveX = 1,
            NegativeX = 2,
            PositiveY = 3,
            NegativeY = 4,
            PositiveZ = 5,
            NegativeZ = 6,
            Mix = 99 // Oblique/Skewed
        }

        public enum ElementType
        {
            Unknown,
            Beam,
            Column,
            Trace, // Vertical but small/short
            Wall,
            Slab,
            ObliqueFrame,
            ObliqueArea
        }

        /// <summary>
        /// Analyzes a vector (usually L3 Normal) to determine its Global Axis alignment.
        /// </summary>
        public static void AnalyzeGlobalAxis(Vector3D normalVector, out string axisName, out int sign, out GlobalAxis axisType)
        {
            double gx = normalVector.X;
            double gy = normalVector.Y;
            double gz = normalVector.Z;

            if (Math.Abs(gx) > STRICT_THRESHOLD)
            {
                axisName = gx > 0 ? "Global +X" : "Global -X";
                sign = gx > 0 ? 1 : -1;
                axisType = gx > 0 ? GlobalAxis.PositiveX : GlobalAxis.NegativeX;
            }
            else if (Math.Abs(gy) > STRICT_THRESHOLD)
            {
                axisName = gy > 0 ? "Global +Y" : "Global -Y";
                sign = gy > 0 ? 1 : -1;
                axisType = gy > 0 ? GlobalAxis.PositiveY : GlobalAxis.NegativeY;
            }
            else if (Math.Abs(gz) > STRICT_THRESHOLD)
            {
                axisName = gz > 0 ? "Global +Z" : "Global -Z";
                sign = gz > 0 ? 1 : -1;
                axisType = gz > 0 ? GlobalAxis.PositiveZ : GlobalAxis.NegativeZ;
            }
            else
            {
                axisName = "Mix";
                sign = 1;
                axisType = GlobalAxis.Mix;
            }
        }

        /// <summary>
        /// Determines if a FRAME is Column, Beam, or Oblique.
        /// </summary>
        public static ElementType DetermineFrameType(SapFrame frame)
        {
            if (frame == null) return ElementType.Unknown;

            // Logic from SapLoadDiagnostics:
            // IsVertical (Length2D < 1mm) -> Column
            if (frame.IsVertical) return ElementType.Column;

            // Slope check: 
            // If projected length < 50% of real length -> Steep slope (> 60 deg) -> Column
            // Else -> Beam (including sloped beams)
            double length3D = System.Math.Sqrt(System.Math.Pow(frame.Length2D, 2) + System.Math.Pow(frame.Z1 - frame.Z2, 2));
            if (length3D < 1e-6) return ElementType.Column; // Point-like

            double slopeRatio = frame.Length2D / length3D;
            
            if (slopeRatio < 0.5) // Angle with horiz > 60 deg
            {
                return ElementType.Column;
            }
            else
            {
                return ElementType.Beam;
            }

            // Note: We could use vectors to detect Oblique frames, but usually 
            // the Bm/Col distinction is purely Z-based in typical Building Audits.
            // If stricter Oblique Frame detection is needed:
            /*
            var vec = frame.EndPt.ToVector3D() - frame.StartPt.ToVector3D();
            vec = vec.Normalized();
            AnalyzeGlobalAxis(vec, out _, out _, out var axis);
            if (axis == GlobalAxis.Mix) return ElementType.ObliqueFrame;
            */
        }

        /// <summary>
        /// Determines if an AREA is Wall, Slab, or Oblique.
        /// Uses the Normal Vector (L3).
        /// </summary>
        public static ElementType DetermineAreaType(Vector3D normalL3)
        {
            AnalyzeGlobalAxis(normalL3, out _, out _, out var axis);

            switch (axis)
            {
                case GlobalAxis.PositiveZ:
                case GlobalAxis.NegativeZ:
                    return ElementType.Slab; // Normal is Z -> Surface is XY -> Slab

                case GlobalAxis.PositiveX:
                case GlobalAxis.NegativeX:
                case GlobalAxis.PositiveY:
                case GlobalAxis.NegativeY:
                    return ElementType.Wall; // Normal is X or Y -> Surface is Vertical -> Wall

                case GlobalAxis.Mix:
                default:
                    return ElementType.ObliqueArea;
            }
        }
    }
}
