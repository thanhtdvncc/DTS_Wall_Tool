using System;

namespace DTS_Wall_Tool.Core.Primitives
{
	// Token: 0x02000022 RID: 34
	public struct PointProjectionResult
	{
		// Token: 0x1700005B RID: 91
		// (get) Token: 0x060001A2 RID: 418 RVA: 0x0000DCAF File Offset: 0x0000BEAF
		public bool IsWithinSegment
		{
			get
			{
				return this.T >= 0.0 && this.T <= 1.0;
			}
		}

		// Token: 0x060001A3 RID: 419 RVA: 0x0000DCD9 File Offset: 0x0000BED9
		public override string ToString()
		{
			return string.Format("PointProj[{0}, t={1:0. 00}, d={2:0. 0}]", this.ProjectedPoint, this.T, this.Distance);
		}

		// Token: 0x040000C9 RID: 201
		public Point2D ProjectedPoint;

		// Token: 0x040000CA RID: 202
		public double T;

		// Token: 0x040000CB RID: 203
		public double Distance;
	}
}
