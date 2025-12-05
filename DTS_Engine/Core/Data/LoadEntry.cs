using System;
using System.Collections.Generic;

namespace DTS_Wall_Tool.Core.Data
{
	// Token: 0x02000047 RID: 71
	public class LoadEntry
	{
		// Token: 0x1700013F RID: 319
		// (get) Token: 0x060003E9 RID: 1001 RVA: 0x00017A04 File Offset: 0x00015C04
		// (set) Token: 0x060003EA RID: 1002 RVA: 0x00017A0C File Offset: 0x00015C0C
		public string Pattern { get; set; }

		// Token: 0x17000140 RID: 320
		// (get) Token: 0x060003EB RID: 1003 RVA: 0x00017A15 File Offset: 0x00015C15
		// (set) Token: 0x060003EC RID: 1004 RVA: 0x00017A1D File Offset: 0x00015C1D
		public double Value { get; set; }

		// Token: 0x17000141 RID: 321
		// (get) Token: 0x060003ED RID: 1005 RVA: 0x00017A26 File Offset: 0x00015C26
		// (set) Token: 0x060003EE RID: 1006 RVA: 0x00017A2E File Offset: 0x00015C2E
		public List<LoadSegment> Segments { get; set; } = new List<LoadSegment>();

		// Token: 0x17000142 RID: 322
		// (get) Token: 0x060003EF RID: 1007 RVA: 0x00017A37 File Offset: 0x00015C37
		// (set) Token: 0x060003F0 RID: 1008 RVA: 0x00017A3F File Offset: 0x00015C3F
		public string Direction { get; set; } = "Gravity";

		// Token: 0x17000143 RID: 323
		// (get) Token: 0x060003F1 RID: 1009 RVA: 0x00017A48 File Offset: 0x00015C48
		// (set) Token: 0x060003F2 RID: 1010 RVA: 0x00017A50 File Offset: 0x00015C50
		public string LoadType { get; set; } = "Distributed";
	}
}
