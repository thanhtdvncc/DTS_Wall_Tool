using System;
using DTS_Wall_Tool.Core.Primitives;

namespace DTS_Wall_Tool.DataStructures
{
	// Token: 0x02000010 RID: 16
	public struct SpatialEntry<T>
	{
		// Token: 0x04000079 RID: 121
		public T Item;

		// Token: 0x0400007A RID: 122
		public Point2D Position;

		// Token: 0x0400007B RID: 123
		public BoundingBox Bounds;
	}
}
