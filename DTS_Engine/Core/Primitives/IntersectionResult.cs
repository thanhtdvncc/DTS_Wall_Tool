using System;

namespace DTS_Wall_Tool.Core.Primitives
{
	// Token: 0x02000021 RID: 33
	public struct IntersectionResult
	{
		// Token: 0x1700005A RID: 90
		// (get) Token: 0x060001A0 RID: 416 RVA: 0x0000DC14 File Offset: 0x0000BE14
		public bool IsWithinBothSegments
		{
			get
			{
				return this.HasIntersection && this.T1 >= 0.0 && this.T1 <= 1.0 && this.T2 >= 0.0 && this.T2 <= 1.0;
			}
		}

		// Token: 0x060001A1 RID: 417 RVA: 0x0000DC73 File Offset: 0x0000BE73
		public override string ToString()
		{
			return this.HasIntersection ? string.Format("Intersection[{0}, t1={1:0.00}, t2={2:0.00}]", this.Point, this.T1, this.T2) : "No Intersection";
		}

		// Token: 0x040000C5 RID: 197
		public bool HasIntersection;

		// Token: 0x040000C6 RID: 198
		public Point2D Point;

		// Token: 0x040000C7 RID: 199
		public double T1;

		// Token: 0x040000C8 RID: 200
		public double T2;
	}
}
