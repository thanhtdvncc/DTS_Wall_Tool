using System;

namespace DTS_Wall_Tool.Core.Engines
{
	// Token: 0x0200002B RID: 43
	public class LoadModifier
	{
		// Token: 0x1700007E RID: 126
		// (get) Token: 0x060001F1 RID: 497 RVA: 0x0000E581 File Offset: 0x0000C781
		// (set) Token: 0x060001F2 RID: 498 RVA: 0x0000E589 File Offset: 0x0000C789
		public string Name { get; set; }

		// Token: 0x1700007F RID: 127
		// (get) Token: 0x060001F3 RID: 499 RVA: 0x0000E592 File Offset: 0x0000C792
		// (set) Token: 0x060001F4 RID: 500 RVA: 0x0000E59A File Offset: 0x0000C79A
		public string Type { get; set; }

		// Token: 0x17000080 RID: 128
		// (get) Token: 0x060001F5 RID: 501 RVA: 0x0000E5A3 File Offset: 0x0000C7A3
		// (set) Token: 0x060001F6 RID: 502 RVA: 0x0000E5AB File Offset: 0x0000C7AB
		public double Factor { get; set; } = 1.0;

		// Token: 0x17000081 RID: 129
		// (get) Token: 0x060001F7 RID: 503 RVA: 0x0000E5B4 File Offset: 0x0000C7B4
		// (set) Token: 0x060001F8 RID: 504 RVA: 0x0000E5BC File Offset: 0x0000C7BC
		public double HeightOverride { get; set; }

		// Token: 0x17000082 RID: 130
		// (get) Token: 0x060001F9 RID: 505 RVA: 0x0000E5C5 File Offset: 0x0000C7C5
		// (set) Token: 0x060001FA RID: 506 RVA: 0x0000E5CD File Offset: 0x0000C7CD
		public double AddValue { get; set; }

		// Token: 0x17000083 RID: 131
		// (get) Token: 0x060001FB RID: 507 RVA: 0x0000E5D6 File Offset: 0x0000C7D6
		// (set) Token: 0x060001FC RID: 508 RVA: 0x0000E5DE File Offset: 0x0000C7DE
		public string Description { get; set; }

		// Token: 0x060001FD RID: 509 RVA: 0x0000E5E7 File Offset: 0x0000C7E7
		public override string ToString()
		{
			return this.Name + " (" + this.Type + ")";
		}
	}
}
