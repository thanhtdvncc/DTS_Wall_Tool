using System;

namespace DTS_Wall_Tool.Core.Data
{
	// Token: 0x02000044 RID: 68
	public class SyncInfo
	{
		// Token: 0x17000130 RID: 304
		// (get) Token: 0x060003C6 RID: 966 RVA: 0x00017838 File Offset: 0x00015A38
		// (set) Token: 0x060003C7 RID: 967 RVA: 0x00017840 File Offset: 0x00015A40
		public SyncState State { get; set; } = SyncState.NotSynced;

		// Token: 0x17000131 RID: 305
		// (get) Token: 0x060003C8 RID: 968 RVA: 0x00017849 File Offset: 0x00015A49
		// (set) Token: 0x060003C9 RID: 969 RVA: 0x00017851 File Offset: 0x00015A51
		public DateTime? LastSyncFromSap { get; set; }

		// Token: 0x17000132 RID: 306
		// (get) Token: 0x060003CA RID: 970 RVA: 0x0001785A File Offset: 0x00015A5A
		// (set) Token: 0x060003CB RID: 971 RVA: 0x00017862 File Offset: 0x00015A62
		public DateTime? LastSyncToSap { get; set; }

		// Token: 0x17000133 RID: 307
		// (get) Token: 0x060003CC RID: 972 RVA: 0x0001786B File Offset: 0x00015A6B
		// (set) Token: 0x060003CD RID: 973 RVA: 0x00017873 File Offset: 0x00015A73
		public string SapDataHash { get; set; }

		// Token: 0x17000134 RID: 308
		// (get) Token: 0x060003CE RID: 974 RVA: 0x0001787C File Offset: 0x00015A7C
		// (set) Token: 0x060003CF RID: 975 RVA: 0x00017884 File Offset: 0x00015A84
		public string CadDataHash { get; set; }

		// Token: 0x17000135 RID: 309
		// (get) Token: 0x060003D0 RID: 976 RVA: 0x0001788D File Offset: 0x00015A8D
		// (set) Token: 0x060003D1 RID: 977 RVA: 0x00017895 File Offset: 0x00015A95
		public SapLoadInfo SapLoadCache { get; set; }

		// Token: 0x060003D2 RID: 978 RVA: 0x000178A0 File Offset: 0x00015AA0
		public override string ToString()
		{
			return string.Format("Sync[{0}] LastSync: {1:yyyy-MM-dd HH:mm}", this.State, this.LastSyncFromSap);
		}
	}
}
