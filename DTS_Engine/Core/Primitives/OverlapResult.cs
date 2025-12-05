using System;

namespace DTS_Wall_Tool.Core.Primitives
{
	// Token: 0x02000020 RID: 32
	public struct OverlapResult
	{
		// Token: 0x0600019F RID: 415 RVA: 0x0000DBE1 File Offset: 0x0000BDE1
		public override string ToString()
		{
			return this.HasOverlap ? string.Format("Overlap[L={0:0.0}, {1:P0}]", this.OverlapLength, this.OverlapPercent) : "No Overlap";
		}

		// Token: 0x040000C0 RID: 192
		public bool HasOverlap;

		// Token: 0x040000C1 RID: 193
		public double OverlapLength;

		// Token: 0x040000C2 RID: 194
		public double OverlapPercent;

		// Token: 0x040000C3 RID: 195
		public double OverlapStart;

		// Token: 0x040000C4 RID: 196
		public double OverlapEnd;
	}
}
