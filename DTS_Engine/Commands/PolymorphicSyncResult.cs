using System;
using DTS_Wall_Tool.Core.Data;

namespace DTS_Wall_Tool.Commands
{
	// Token: 0x02000055 RID: 85
	public class PolymorphicSyncResult
	{
		// Token: 0x1700014F RID: 335
		// (get) Token: 0x06000466 RID: 1126 RVA: 0x0001D26D File Offset: 0x0001B46D
		// (set) Token: 0x06000467 RID: 1127 RVA: 0x0001D275 File Offset: 0x0001B475
		public string Handle { get; set; }

		// Token: 0x17000150 RID: 336
		// (get) Token: 0x06000468 RID: 1128 RVA: 0x0001D27E File Offset: 0x0001B47E
		// (set) Token: 0x06000469 RID: 1129 RVA: 0x0001D286 File Offset: 0x0001B486
		public bool Success { get; set; }

		// Token: 0x17000151 RID: 337
		// (get) Token: 0x0600046A RID: 1130 RVA: 0x0001D28F File Offset: 0x0001B48F
		// (set) Token: 0x0600046B RID: 1131 RVA: 0x0001D297 File Offset: 0x0001B497
		public SyncState State { get; set; }

		// Token: 0x17000152 RID: 338
		// (get) Token: 0x0600046C RID: 1132 RVA: 0x0001D2A0 File Offset: 0x0001B4A0
		// (set) Token: 0x0600046D RID: 1133 RVA: 0x0001D2A8 File Offset: 0x0001B4A8
		public string Message { get; set; }

		// Token: 0x17000153 RID: 339
		// (get) Token: 0x0600046E RID: 1134 RVA: 0x0001D2B1 File Offset: 0x0001B4B1
		// (set) Token: 0x0600046F RID: 1135 RVA: 0x0001D2B9 File Offset: 0x0001B4B9
		public ElementType? ElementType { get; set; }

		// Token: 0x17000154 RID: 340
		// (get) Token: 0x06000470 RID: 1136 RVA: 0x0001D2C2 File Offset: 0x0001B4C2
		// (set) Token: 0x06000471 RID: 1137 RVA: 0x0001D2CA File Offset: 0x0001B4CA
		public double? OldLoadValue { get; set; }

		// Token: 0x17000155 RID: 341
		// (get) Token: 0x06000472 RID: 1138 RVA: 0x0001D2D3 File Offset: 0x0001B4D3
		// (set) Token: 0x06000473 RID: 1139 RVA: 0x0001D2DB File Offset: 0x0001B4DB
		public double? NewLoadValue { get; set; }
	}
}
