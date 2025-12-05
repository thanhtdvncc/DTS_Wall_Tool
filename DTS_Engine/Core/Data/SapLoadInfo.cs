using System;

namespace DTS_Wall_Tool.Core.Data
{
	// Token: 0x02000045 RID: 69
	public class SapLoadInfo
	{
		// Token: 0x17000136 RID: 310
		// (get) Token: 0x060003D4 RID: 980 RVA: 0x000178E2 File Offset: 0x00015AE2
		// (set) Token: 0x060003D5 RID: 981 RVA: 0x000178EA File Offset: 0x00015AEA
		public string FrameName { get; set; }

		// Token: 0x17000137 RID: 311
		// (get) Token: 0x060003D6 RID: 982 RVA: 0x000178F3 File Offset: 0x00015AF3
		// (set) Token: 0x060003D7 RID: 983 RVA: 0x000178FB File Offset: 0x00015AFB
		public string LoadPattern { get; set; }

		// Token: 0x17000138 RID: 312
		// (get) Token: 0x060003D8 RID: 984 RVA: 0x00017904 File Offset: 0x00015B04
		// (set) Token: 0x060003D9 RID: 985 RVA: 0x0001790C File Offset: 0x00015B0C
		public double LoadValue { get; set; }

		// Token: 0x17000139 RID: 313
		// (get) Token: 0x060003DA RID: 986 RVA: 0x00017915 File Offset: 0x00015B15
		// (set) Token: 0x060003DB RID: 987 RVA: 0x0001791D File Offset: 0x00015B1D
		public double DistanceI { get; set; }

		// Token: 0x1700013A RID: 314
		// (get) Token: 0x060003DC RID: 988 RVA: 0x00017926 File Offset: 0x00015B26
		// (set) Token: 0x060003DD RID: 989 RVA: 0x0001792E File Offset: 0x00015B2E
		public double DistanceJ { get; set; }

		// Token: 0x1700013B RID: 315
		// (get) Token: 0x060003DE RID: 990 RVA: 0x00017937 File Offset: 0x00015B37
		// (set) Token: 0x060003DF RID: 991 RVA: 0x0001793F File Offset: 0x00015B3F
		public string Direction { get; set; } = "Gravity";

		// Token: 0x1700013C RID: 316
		// (get) Token: 0x060003E0 RID: 992 RVA: 0x00017948 File Offset: 0x00015B48
		// (set) Token: 0x060003E1 RID: 993 RVA: 0x00017950 File Offset: 0x00015B50
		public string LoadType { get; set; } = "Distributed";

		// Token: 0x060003E2 RID: 994 RVA: 0x0001795C File Offset: 0x00015B5C
		public override string ToString()
		{
			return string.Format("{0}: {1}={2:0. 00}kN/m [{3:0}-{4:0}]", new object[] { this.FrameName, this.LoadPattern, this.LoadValue, this.DistanceI, this.DistanceJ });
		}
	}
}
