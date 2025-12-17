namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Support entity geometry for beam support detection.
    /// ISO 25010: Reusability - Centralized DTO for support detection algorithms.
    /// Used by BeamGroupDetector and other geometry analysis tools.
    /// </summary>
    public class SupportGeometry
    {
        /// <summary>CAD Entity Handle</summary>
        public string Handle { get; set; }

        /// <summary>SAP/User assigned name</summary>
        public string Name { get; set; }

        /// <summary>Support type: "Column", "Wall", "Beam"</summary>
        public string Type { get; set; }

        /// <summary>Center X coordinate (mm)</summary>
        public double CenterX { get; set; }

        /// <summary>Center Y coordinate (mm)</summary>
        public double CenterY { get; set; }

        /// <summary>Width in X direction (mm)</summary>
        public double Width { get; set; }

        /// <summary>Depth in Y direction (mm)</summary>
        public double Depth { get; set; }

        /// <summary>Associated grid name (e.g., "A", "1")</summary>
        public string GridName { get; set; }

        /// <summary>Z elevation (mm) - for story matching</summary>
        public double Elevation { get; set; }
    }
}
