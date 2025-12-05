using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Wall_Tool.Core.Data;

namespace DTS_Wall_Tool.Core.Engines
{
	// Token: 0x0200002D RID: 45
	public class MappingResult
	{
		// Token: 0x17000084 RID: 132
		// (get) Token: 0x060001FF RID: 511 RVA: 0x0000E61C File Offset: 0x0000C81C
		// (set) Token: 0x06000200 RID: 512 RVA: 0x0000E624 File Offset: 0x0000C824
		public string WallHandle { get; set; }

		// Token: 0x17000085 RID: 133
		// (get) Token: 0x06000201 RID: 513 RVA: 0x0000E62D File Offset: 0x0000C82D
		// (set) Token: 0x06000202 RID: 514 RVA: 0x0000E635 File Offset: 0x0000C835
		public double WallLength { get; set; }

		// Token: 0x17000086 RID: 134
		// (get) Token: 0x06000203 RID: 515 RVA: 0x0000E63E File Offset: 0x0000C83E
		// (set) Token: 0x06000204 RID: 516 RVA: 0x0000E646 File Offset: 0x0000C846
		public List<MappingRecord> Mappings { get; set; } = new List<MappingRecord>();

		// Token: 0x17000087 RID: 135
		// (get) Token: 0x06000205 RID: 517 RVA: 0x0000E650 File Offset: 0x0000C850
		public double CoveredLength
		{
			get
			{
				return this.Mappings.Where((MappingRecord m) => m.TargetFrame != "New").Sum((MappingRecord m) => m.CoveredLength);
			}
		}

		// Token: 0x17000088 RID: 136
		// (get) Token: 0x06000206 RID: 518 RVA: 0x0000E6AB File Offset: 0x0000C8AB
		public double CoveragePercent
		{
			get
			{
				return (this.WallLength > 0.0) ? (this.CoveredLength / this.WallLength * 100.0) : 0.0;
			}
		}

		// Token: 0x17000089 RID: 137
		// (get) Token: 0x06000207 RID: 519 RVA: 0x0000E6E0 File Offset: 0x0000C8E0
		public bool IsFullyCovered
		{
			get
			{
				return this.CoveragePercent >= 95.0;
			}
		}

		// Token: 0x1700008A RID: 138
		// (get) Token: 0x06000208 RID: 520 RVA: 0x0000E6F6 File Offset: 0x0000C8F6
		public bool HasMapping
		{
			get
			{
				bool flag;
				if (this.Mappings.Count > 0)
				{
					flag = this.Mappings.Any((MappingRecord m) => m.TargetFrame != "New");
				}
				else
				{
					flag = false;
				}
				return flag;
			}
		}

		// Token: 0x06000209 RID: 521 RVA: 0x0000E734 File Offset: 0x0000C934
		public int GetColorIndex()
		{
			bool flag = !this.HasMapping;
			int num;
			if (flag)
			{
				num = 1;
			}
			else
			{
				bool isFullyCovered = this.IsFullyCovered;
				if (isFullyCovered)
				{
					num = 3;
				}
				else
				{
					num = 2;
				}
			}
			return num;
		}

		// Token: 0x0600020A RID: 522 RVA: 0x0000E768 File Offset: 0x0000C968
		public string GetTopLabelText()
		{
			bool flag = this.Mappings.Count == 0 || !this.HasMapping;
			string text;
			if (flag)
			{
				text = "{\\C1;-> NEW}";
			}
			else
			{
				MappingRecord mapping = this.Mappings.First<MappingRecord>();
				int color = this.GetColorIndex();
				bool flag2 = mapping.MatchType == "FULL" || mapping.MatchType == "EXACT";
				if (flag2)
				{
					text = string.Format("{{\\C{0};-> {1} (full)}}", color, mapping.TargetFrame);
				}
				else
				{
					text = string.Format("{{\\C{0};-> {1}}}", color, mapping.TargetFrame);
				}
			}
			return text;
		}

		// Token: 0x0600020B RID: 523 RVA: 0x0000E80C File Offset: 0x0000CA0C
		public string GetBottomLabelText(string wallType, string loadPattern, double loadValue)
		{
			int color = this.GetColorIndex();
			string loadStr = string.Format("{0} {1}={2:0.00}", wallType, loadPattern, loadValue);
			return string.Format("{{\\C{0};{1}}}", color, loadStr);
		}

		// Token: 0x0600020C RID: 524 RVA: 0x0000E84C File Offset: 0x0000CA4C
		public string GetLabelText(string wallType, string loadPattern, double loadValue)
		{
			string loadStr = string.Format("{0} {1}={2:0.00}", wallType, loadPattern, loadValue);
			bool flag = this.Mappings.Count == 0 || !this.HasMapping;
			string text;
			if (flag)
			{
				text = loadStr + " -> New";
			}
			else
			{
				bool flag2 = this.Mappings.Count == 1;
				if (flag2)
				{
					MappingRecord i = this.Mappings[0];
					bool flag3 = i.MatchType == "FULL" || i.MatchType == "EXACT";
					if (flag3)
					{
						text = loadStr + string.Format(" -> {0} (full {1:0.0}m)", i.TargetFrame, i.FrameLength / 1000.0);
					}
					else
					{
						text = loadStr + string.Format(" -> {0} I={1:0.0}to{2:0.0}", i.TargetFrame, i.DistI / 1000.0, i.DistJ / 1000.0);
					}
				}
				else
				{
					IEnumerable<string> frameNames = this.Mappings.Select((MappingRecord m) => m.TargetFrame).Distinct<string>();
					text = loadStr + " -> " + string.Join(", ", frameNames);
				}
			}
			return text;
		}
	}
}
