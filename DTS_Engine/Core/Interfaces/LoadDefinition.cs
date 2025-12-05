using System;

namespace DTS_Wall_Tool.Core.Interfaces
{
	// Token: 0x02000028 RID: 40
	public class LoadDefinition
	{
		// Token: 0x1700006A RID: 106
		// (get) Token: 0x060001B9 RID: 441 RVA: 0x0000DD06 File Offset: 0x0000BF06
		// (set) Token: 0x060001BA RID: 442 RVA: 0x0000DD0E File Offset: 0x0000BF0E
		public string Pattern { get; set; } = "DL";

		// Token: 0x1700006B RID: 107
		// (get) Token: 0x060001BB RID: 443 RVA: 0x0000DD17 File Offset: 0x0000BF17
		// (set) Token: 0x060001BC RID: 444 RVA: 0x0000DD1F File Offset: 0x0000BF1F
		public double Value { get; set; }

		// Token: 0x1700006C RID: 108
		// (get) Token: 0x060001BD RID: 445 RVA: 0x0000DD28 File Offset: 0x0000BF28
		// (set) Token: 0x060001BE RID: 446 RVA: 0x0000DD30 File Offset: 0x0000BF30
		public LoadType Type { get; set; } = LoadType.DistributedLine;

		// Token: 0x1700006D RID: 109
		// (get) Token: 0x060001BF RID: 447 RVA: 0x0000DD39 File Offset: 0x0000BF39
		// (set) Token: 0x060001C0 RID: 448 RVA: 0x0000DD41 File Offset: 0x0000BF41
		public string TargetElement { get; set; } = "Frame";

		// Token: 0x1700006E RID: 110
		// (get) Token: 0x060001C1 RID: 449 RVA: 0x0000DD4A File Offset: 0x0000BF4A
		// (set) Token: 0x060001C2 RID: 450 RVA: 0x0000DD52 File Offset: 0x0000BF52
		public string Direction { get; set; } = "Gravity";

		// Token: 0x1700006F RID: 111
		// (get) Token: 0x060001C3 RID: 451 RVA: 0x0000DD5B File Offset: 0x0000BF5B
		// (set) Token: 0x060001C4 RID: 452 RVA: 0x0000DD63 File Offset: 0x0000BF63
		public double DistI { get; set; } = 0.0;

		// Token: 0x17000070 RID: 112
		// (get) Token: 0x060001C5 RID: 453 RVA: 0x0000DD6C File Offset: 0x0000BF6C
		// (set) Token: 0x060001C6 RID: 454 RVA: 0x0000DD74 File Offset: 0x0000BF74
		public double DistJ { get; set; } = 0.0;

		// Token: 0x17000071 RID: 113
		// (get) Token: 0x060001C7 RID: 455 RVA: 0x0000DD7D File Offset: 0x0000BF7D
		// (set) Token: 0x060001C8 RID: 456 RVA: 0x0000DD85 File Offset: 0x0000BF85
		public bool IsRelativeDistance { get; set; } = false;

		// Token: 0x17000072 RID: 114
		// (get) Token: 0x060001C9 RID: 457 RVA: 0x0000DD8E File Offset: 0x0000BF8E
		// (set) Token: 0x060001CA RID: 458 RVA: 0x0000DD96 File Offset: 0x0000BF96
		public double LoadFactor { get; set; } = 1.0;

		// Token: 0x060001CB RID: 459 RVA: 0x0000DDA0 File Offset: 0x0000BFA0
		public LoadDefinition Clone()
		{
			return new LoadDefinition
			{
				Pattern = this.Pattern,
				Value = this.Value,
				Type = this.Type,
				TargetElement = this.TargetElement,
				Direction = this.Direction,
				DistI = this.DistI,
				DistJ = this.DistJ,
				IsRelativeDistance = this.IsRelativeDistance,
				LoadFactor = this.LoadFactor
			};
		}

		// Token: 0x060001CC RID: 460 RVA: 0x0000DE2C File Offset: 0x0000C02C
		public override string ToString()
		{
			string unit = ((this.Type == LoadType.DistributedLine) ? "kN/m" : ((this.Type == LoadType.UniformArea) ? "kN/m²" : "kN"));
			return string.Format("{0}: {1:0.00} {2} ({3}) -> {4}", new object[] { this.Pattern, this.Value, unit, this.Type, this.TargetElement });
		}
	}
}
