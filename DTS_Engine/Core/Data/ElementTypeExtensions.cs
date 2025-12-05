using System;

namespace DTS_Wall_Tool.Core.Data
{
	// Token: 0x02000037 RID: 55
	public static class ElementTypeExtensions
	{
		// Token: 0x060002D2 RID: 722 RVA: 0x00014334 File Offset: 0x00012534
		public static ElementType ParseElementType(string xType)
		{
			bool flag = string.IsNullOrEmpty(xType);
			ElementType elementType;
			if (flag)
			{
				elementType = ElementType.Unknown;
			}
			else
			{
				string text = xType.ToUpperInvariant();
				string text2 = text;
				uint num = <PrivateImplementationDetails>.ComputeStringHash(text2);
				if (num <= 2828291657U)
				{
					if (num <= 1105666343U)
					{
						if (num != 361315706U)
						{
							if (num != 1073650669U)
							{
								if (num == 1105666343U)
								{
									if (text2 == "COLUMN")
									{
										return ElementType.Column;
									}
								}
							}
							else if (text2 == "LINTEL")
							{
								return ElementType.Lintel;
							}
						}
						else if (text2 == "ELEMENT_ORIGIN")
						{
							return ElementType.ElementOrigin;
						}
					}
					else if (num != 1349408634U)
					{
						if (num != 1975072421U)
						{
							if (num == 2828291657U)
							{
								if (text2 == "SLAB")
								{
									return ElementType.Slab;
								}
							}
						}
						else if (text2 == "REBAR")
						{
							return ElementType.Rebar;
						}
					}
					else if (text2 == "BEAM")
					{
						return ElementType.Beam;
					}
				}
				else if (num <= 3247669860U)
				{
					if (num != 2896649749U)
					{
						if (num != 3045870793U)
						{
							if (num == 3247669860U)
							{
								if (text2 == "FOUNDATION")
								{
									return ElementType.Foundation;
								}
							}
						}
						else if (text2 == "STORY_ORIGIN")
						{
							return ElementType.StoryOrigin;
						}
					}
					else if (text2 == "WALL")
					{
						return ElementType.Wall;
					}
				}
				else if (num != 3382773542U)
				{
					if (num != 3446449862U)
					{
						if (num == 3566641485U)
						{
							if (text2 == "PILE")
							{
								return ElementType.Pile;
							}
						}
					}
					else if (text2 == "SHEARWALL")
					{
						return ElementType.ShearWall;
					}
				}
				else if (text2 == "STAIR")
				{
					return ElementType.Stair;
				}
				elementType = ElementType.Unknown;
			}
			return elementType;
		}

		// Token: 0x060002D3 RID: 723 RVA: 0x00014540 File Offset: 0x00012740
		public static bool IsStructuralElement(this ElementType type)
		{
			return type >= ElementType.Beam && type <= ElementType.ShearWall;
		}

		// Token: 0x060002D4 RID: 724 RVA: 0x00014564 File Offset: 0x00012764
		public static bool IsOriginMarker(this ElementType type)
		{
			return type == ElementType.StoryOrigin || type == ElementType.ElementOrigin;
		}
	}
}
