using System;

namespace DTS_Wall_Tool.Core.Utils
{
	// Token: 0x02000018 RID: 24
	public class UnitInfo
	{
		// Token: 0x17000035 RID: 53
		// (get) Token: 0x06000125 RID: 293 RVA: 0x0000BD80 File Offset: 0x00009F80
		// (set) Token: 0x06000126 RID: 294 RVA: 0x0000BD88 File Offset: 0x00009F88
		public DtsUnit Unit { get; private set; }

		// Token: 0x17000036 RID: 54
		// (get) Token: 0x06000127 RID: 295 RVA: 0x0000BD91 File Offset: 0x00009F91
		// (set) Token: 0x06000128 RID: 296 RVA: 0x0000BD99 File Offset: 0x00009F99
		public string ForceUnit { get; private set; }

		// Token: 0x17000037 RID: 55
		// (get) Token: 0x06000129 RID: 297 RVA: 0x0000BDA2 File Offset: 0x00009FA2
		// (set) Token: 0x0600012A RID: 298 RVA: 0x0000BDAA File Offset: 0x00009FAA
		public string LengthUnit { get; private set; }

		// Token: 0x17000038 RID: 56
		// (get) Token: 0x0600012B RID: 299 RVA: 0x0000BDB3 File Offset: 0x00009FB3
		// (set) Token: 0x0600012C RID: 300 RVA: 0x0000BDBB File Offset: 0x00009FBB
		public double LengthScaleToMeter { get; private set; }

		// Token: 0x17000039 RID: 57
		// (get) Token: 0x0600012D RID: 301 RVA: 0x0000BDC4 File Offset: 0x00009FC4
		// (set) Token: 0x0600012E RID: 302 RVA: 0x0000BDCC File Offset: 0x00009FCC
		public double LengthScaleToMm { get; private set; }

		// Token: 0x0600012F RID: 303 RVA: 0x0000BDD5 File Offset: 0x00009FD5
		public UnitInfo(DtsUnit unit)
		{
			this.Unit = unit;
			this.ParseUnit();
		}

		// Token: 0x06000130 RID: 304 RVA: 0x0000BDF0 File Offset: 0x00009FF0
		private void ParseUnit()
		{
			string s = this.Unit.ToString();
			string[] parts = s.Split(new char[] { '_' });
			bool flag = parts.Length >= 2;
			if (flag)
			{
				this.ForceUnit = parts[0];
				this.LengthUnit = parts[1];
			}
			else
			{
				this.ForceUnit = "kN";
				this.LengthUnit = "mm";
			}
			string text = this.LengthUnit.ToLowerInvariant();
			string text2 = text;
			if (!(text2 == "mm"))
			{
				if (!(text2 == "cm"))
				{
					if (!(text2 == "m"))
					{
						if (!(text2 == "in"))
						{
							if (!(text2 == "ft"))
							{
								this.LengthScaleToMeter = 0.001;
								this.LengthScaleToMm = 1.0;
							}
							else
							{
								this.LengthScaleToMeter = 0.3048;
								this.LengthScaleToMm = 304.8;
							}
						}
						else
						{
							this.LengthScaleToMeter = 0.0254;
							this.LengthScaleToMm = 25.4;
						}
					}
					else
					{
						this.LengthScaleToMeter = 1.0;
						this.LengthScaleToMm = 1000.0;
					}
				}
				else
				{
					this.LengthScaleToMeter = 0.01;
					this.LengthScaleToMm = 10.0;
				}
			}
			else
			{
				this.LengthScaleToMeter = 0.001;
				this.LengthScaleToMm = 1.0;
			}
		}

		// Token: 0x06000131 RID: 305 RVA: 0x0000BF98 File Offset: 0x0000A198
		public override string ToString()
		{
			return this.ForceUnit + "-" + this.LengthUnit;
		}

		// Token: 0x06000132 RID: 306 RVA: 0x0000BFB0 File Offset: 0x0000A1B0
		public string GetLineLoadUnit()
		{
			return this.ForceUnit + "/m";
		}

		// Token: 0x06000133 RID: 307 RVA: 0x0000BFC2 File Offset: 0x0000A1C2
		public string GetAreaLoadUnit()
		{
			return this.ForceUnit + "/m²";
		}
	}
}
