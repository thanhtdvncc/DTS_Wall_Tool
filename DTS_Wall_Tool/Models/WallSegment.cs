using System;
using DTS_Wall_Tool.Core;

namespace DTS_Wall_Tool.Models
{
    /// <summary>
    /// Represents a wall segment for processing
    /// Equivalent to VBA Type WallSegment + CenterLine combined
    /// </summary>
    public class WallSegment
    {
        #region Identity

        /// <summary>
        /// AutoCAD handle for the source line entity
        /// </summary>
        public string Handle { get; set; }

        /// <summary>
        /// Unique identifier for deduplication (computed from geometry)
        /// </summary>
        public string UniqueID { get; private set; }

        /// <summary>
        /// Index in the processing array (for quick reference)
        /// </summary>
        public int Index { get; set; } = -1;

        #endregion

        #region Geometry

        public Point2D StartPt { get; set; }
        public Point2D EndPt { get; set; }

        public double Length => StartPt.DistanceTo(EndPt);
        public Point2D Midpoint => StartPt.MidpointTo(EndPt);
        public double Angle => Math.Atan2(EndPt.Y - StartPt.Y, EndPt.X - StartPt.X);

        /// <summary>
        /// Normalized angle [0, PI) - treats opposite directions as same
        /// </summary>
        public double NormalizedAngle
        {
            get
            {
                double a = Angle;
                while (a < 0) a += Math.PI;
                while (a >= Math.PI) a -= Math.PI;
                return a;
            }
        }

        /// <summary>
        /// Get as LineSegment2D for geometric operations
        /// </summary>
        public LineSegment2D AsSegment => new LineSegment2D(StartPt, EndPt);

        #endregion

        #region Wall Properties

        public double Thickness { get; set; } = 0;
        public string WallType { get; set; } = "";
        public string LoadPattern { get; set; } = "DL";
        public double LoadValue { get; set; } = 0;

        /// <summary>
        /// Story elevation (Z coordinate)
        /// </summary>
        public double StoryZ { get; set; } = 0;

        /// <summary>
        /// Story name for grouping
        /// </summary>
        public string StoryName { get; set; } = "";

        /// <summary>
        /// Layer name in AutoCAD
        /// </summary>
        public string Layer { get; set; } = "";

        #endregion

        #region Processing State

        /// <summary>
        /// True if this segment is a single line (not paired)
        /// </summary>
        public bool IsSingleLine { get; set; } = false;

        /// <summary>
        /// True if this segment has been processed/merged
        /// </summary>
        public bool IsProcessed { get; set; } = false;

        /// <summary>
        /// True if this segment is still active (not deleted)
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Index of paired segment (for double-line walls)
        /// -1 if no pair
        /// </summary>
        public int PairSegmentID { get; set; } = -1;

        /// <summary>
        /// Index of vector group this segment belongs to
        /// </summary>
        public int VectorID { get; set; } = -1;

        /// <summary>
        /// If merged, this points to the target segment ID
        /// </summary>
        public int MergedIntoID { get; set; } = -1;

        #endregion

        #region Constructors

        public WallSegment() { }

        public WallSegment(Point2D start, Point2D end)
        {
            StartPt = start;
            EndPt = end;
            UpdateUniqueID();
        }

        public WallSegment(double x1, double y1, double x2, double y2)
            : this(new Point2D(x1, y1), new Point2D(x2, y2))
        { }

        #endregion

        #region Methods

        /// <summary>
        /// Update unique ID based on current geometry
        /// </summary>
        public void UpdateUniqueID()
        {
            // Round to 0.1mm for stability
            string sx = StartPt.X.ToString("0.0");
            string sy = StartPt.Y.ToString("0. 0");
            string ex = EndPt.X.ToString("0.0");
            string ey = EndPt.Y.ToString("0. 0");
            string th = Thickness.ToString("0");

            UniqueID = $"{sx}_{sy}_{ex}_{ey}_T{th}";
        }

        /// <summary>
        /// Recalculate WallType from Thickness if empty
        /// </summary>
        public void EnsureWallType()
        {
            if (string.IsNullOrEmpty(WallType) && Thickness > 0)
            {
                WallType = "W" + ((int)Thickness).ToString();
            }
        }

        /// <summary>
        /// Create a copy with new geometry (for merging)
        /// </summary>
        public WallSegment CloneWithGeometry(Point2D newStart, Point2D newEnd)
        {
            return new WallSegment
            {
                Handle = Handle,
                StartPt = newStart,
                EndPt = newEnd,
                Thickness = Thickness,
                WallType = WallType,
                LoadPattern = LoadPattern,
                LoadValue = LoadValue,
                StoryZ = StoryZ,
                StoryName = StoryName,
                Layer = Layer,
                IsSingleLine = IsSingleLine,
                IsActive = IsActive
            };
        }

        public override string ToString()
        {
            return $"Wall[{Handle}]: {StartPt}->{EndPt}, L={Length:0.0}, T={Thickness}, {WallType}";
        }

        #endregion
    }
}