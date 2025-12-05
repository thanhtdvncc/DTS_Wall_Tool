using System;
using DTS_Wall_Tool.Core.Data;

namespace DTS_Wall_Tool.Core.Engines
{
	// Token: 0x0200002E RID: 46
	internal class FrameCandidate
	{
		// Token: 0x1700008B RID: 139
		// (get) Token: 0x0600020E RID: 526 RVA: 0x0000E9C5 File Offset: 0x0000CBC5
		// (set) Token: 0x0600020F RID: 527 RVA: 0x0000E9CD File Offset: 0x0000CBCD
		public SapFrame Frame { get; set; }

		// Token: 0x1700008C RID: 140
		// (get) Token: 0x06000210 RID: 528 RVA: 0x0000E9D6 File Offset: 0x0000CBD6
		// (set) Token: 0x06000211 RID: 529 RVA: 0x0000E9DE File Offset: 0x0000CBDE
		public double OverlapLength { get; set; }

		// Token: 0x1700008D RID: 141
		// (get) Token: 0x06000212 RID: 530 RVA: 0x0000E9E7 File Offset: 0x0000CBE7
		// (set) Token: 0x06000213 RID: 531 RVA: 0x0000E9EF File Offset: 0x0000CBEF
		public double PerpDist { get; set; }

		// Token: 0x1700008E RID: 142
		// (get) Token: 0x06000214 RID: 532 RVA: 0x0000E9F8 File Offset: 0x0000CBF8
		// (set) Token: 0x06000215 RID: 533 RVA: 0x0000EA00 File Offset: 0x0000CC00
		public double Score { get; set; }

		// Token: 0x1700008F RID: 143
		// (get) Token: 0x06000216 RID: 534 RVA: 0x0000EA09 File Offset: 0x0000CC09
		// (set) Token: 0x06000217 RID: 535 RVA: 0x0000EA11 File Offset: 0x0000CC11
		public double WallProjStart { get; set; }

		// Token: 0x17000090 RID: 144
		// (get) Token: 0x06000218 RID: 536 RVA: 0x0000EA1A File Offset: 0x0000CC1A
		// (set) Token: 0x06000219 RID: 537 RVA: 0x0000EA22 File Offset: 0x0000CC22
		public double WallProjEnd { get; set; }

		// Token: 0x17000091 RID: 145
		// (get) Token: 0x0600021A RID: 538 RVA: 0x0000EA2B File Offset: 0x0000CC2B
		// (set) Token: 0x0600021B RID: 539 RVA: 0x0000EA33 File Offset: 0x0000CC33
		public double OverlapStart { get; set; }

		// Token: 0x17000092 RID: 146
		// (get) Token: 0x0600021C RID: 540 RVA: 0x0000EA3C File Offset: 0x0000CC3C
		// (set) Token: 0x0600021D RID: 541 RVA: 0x0000EA44 File Offset: 0x0000CC44
		public double OverlapEnd { get; set; }
	}
}
