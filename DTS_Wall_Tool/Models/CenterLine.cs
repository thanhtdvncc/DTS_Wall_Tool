using System;
using System.Collections.Generic;
using DTS_Wall_Tool.Core;

namespace DTS_Wall_Tool.Models
{
    /// <summary>
    /// Represents a processed centerline ready for output
    /// </summary>
    public class CenterLine
    {
        #region Geometry

        public Point2D StartPt { get; set; }
        public Point2D EndPt { get; set; }

        public double Length => StartPt.DistanceTo(EndPt);
        public Point2D Midpoint => StartPt.MidpointTo(EndPt);
        public double Angle => Math.Atan2(EndPt.Y - StartPt.Y, EndPt.X - StartPt.X);

        public LineSegment2D AsSegment => new LineSegment2D(StartPt, EndPt);

        #endregion

        #region Wall Properties

        public double Thickness { get; set; } = 0;
        public string WallType { get; set; } = "";
        public double StoryZ { get; set; } = 0;

        #endregion

        #region Processing State

        /// <summary>
        /// Index of source wall pair (-1 if from single line)
        /// </summary>
        public int SourcePairID { get; set; } = -1;

        /// <summary>
        /// Vector group index for collinear processing
        /// </summary>
        public int VectorID { get; set; } = -1;

        /// <summary>
        /// Active flag for lazy deletion
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Unique identifier for deduplication
        /// </summary>
        public string UniqueID { get; private set; }

        /// <summary>
        /// Source handles that contributed to this centerline
        /// </summary>
        public List<string> SourceHandles { get; set; } = new List<string>();

        #endregion

        #region Methods

        public void UpdateUniqueID()
        {
            string sx = StartPt.X.ToString("0.00");
            string sy = StartPt.Y.ToString("0.00");
            string ex = EndPt.X.ToString("0.00");
            string ey = EndPt.Y.ToString("0.00");
            string th = Thickness.ToString("0");

            UniqueID = $"{sx}_{sy}_{ex}_{ey}_T{th}_A{Angle:0.000}";
        }

        /// <summary>
        /// Merge another centerline into this one
        /// </summary>
        public void MergeWith(CenterLine other)
        {
            // Use GeoAlgo to merge geometry
            var merged = GeoAlgo.MergeCollinearSegments(AsSegment, other.AsSegment);
            StartPt = merged.Start;
            EndPt = merged.End;

            // Take larger thickness
            if (other.Thickness > Thickness)
            {
                Thickness = other.Thickness;
                WallType = other.WallType;
            }

            // Merge source handles
            SourceHandles.AddRange(other.SourceHandles);

            UpdateUniqueID();
        }

        public override string ToString()
        {
            return $"CL: {StartPt}->{EndPt}, L={Length:0.0}, T={Thickness}";
        }

        #endregion
    }
}