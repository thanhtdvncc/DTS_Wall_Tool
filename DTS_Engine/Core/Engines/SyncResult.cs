using System;
using DTS_Wall_Tool.Core.Data;

namespace DTS_Wall_Tool.Core.Engines
{
	// Token: 0x02000030 RID: 48
	public class SyncResult
	{
		// Token: 0x17000093 RID: 147
		// (get) Token: 0x0600022B RID: 555 RVA: 0x0000FADB File Offset: 0x0000DCDB
		// (set) Token: 0x0600022C RID: 556 RVA: 0x0000FAE3 File Offset: 0x0000DCE3
		public string Handle { get; set; }

		// Token: 0x17000094 RID: 148
		// (get) Token: 0x0600022D RID: 557 RVA: 0x0000FAEC File Offset: 0x0000DCEC
		// (set) Token: 0x0600022E RID: 558 RVA: 0x0000FAF4 File Offset: 0x0000DCF4
		public SyncState State { get; set; }

		// Token: 0x17000095 RID: 149
		// (get) Token: 0x0600022F RID: 559 RVA: 0x0000FAFD File Offset: 0x0000DCFD
		// (set) Token: 0x06000230 RID: 560 RVA: 0x0000FB05 File Offset: 0x0000DD05
		public string Message { get; set; }

		// Token: 0x17000096 RID: 150
		// (get) Token: 0x06000231 RID: 561 RVA: 0x0000FB0E File Offset: 0x0000DD0E
		// (set) Token: 0x06000232 RID: 562 RVA: 0x0000FB16 File Offset: 0x0000DD16
		public bool Success { get; set; }

		// Token: 0x17000097 RID: 151
		// (get) Token: 0x06000233 RID: 563 RVA: 0x0000FB1F File Offset: 0x0000DD1F
		// (set) Token: 0x06000234 RID: 564 RVA: 0x0000FB27 File Offset: 0x0000DD27
		public string OldFrameName { get; set; }

		// Token: 0x17000098 RID: 152
		// (get) Token: 0x06000235 RID: 565 RVA: 0x0000FB30 File Offset: 0x0000DD30
		// (set) Token: 0x06000236 RID: 566 RVA: 0x0000FB38 File Offset: 0x0000DD38
		public string NewFrameName { get; set; }

		// Token: 0x17000099 RID: 153
		// (get) Token: 0x06000237 RID: 567 RVA: 0x0000FB41 File Offset: 0x0000DD41
		// (set) Token: 0x06000238 RID: 568 RVA: 0x0000FB49 File Offset: 0x0000DD49
		public double? OldLoadValue { get; set; }

		// Token: 0x1700009A RID: 154
		// (get) Token: 0x06000239 RID: 569 RVA: 0x0000FB52 File Offset: 0x0000DD52
		// (set) Token: 0x0600023A RID: 570 RVA: 0x0000FB5A File Offset: 0x0000DD5A
		public double? NewLoadValue { get; set; }
	}
}
