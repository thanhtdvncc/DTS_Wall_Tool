using System;

namespace DTS_Wall_Tool.Core.Data
{
	// Token: 0x02000043 RID: 67
	public enum SyncState
	{
		// Token: 0x0400018E RID: 398
		NotSynced,
		// Token: 0x0400018F RID: 399
		Synced,
		// Token: 0x04000190 RID: 400
		CadModified,
		// Token: 0x04000191 RID: 401
		SapModified,
		// Token: 0x04000192 RID: 402
		Conflict,
		// Token: 0x04000193 RID: 403
		SapDeleted,
		// Token: 0x04000194 RID: 404
		NewElement
	}
}
