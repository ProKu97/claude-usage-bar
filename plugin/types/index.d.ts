export type Limit = { percentUsed: number; resetsAt?: string }
// seenAt：读数时间；checkedAt：最近一次检查重置时间的时刻（变化时触发重画）
export type Limits = { fiveHour: Limit | null; sevenDay: Limit | null; seenAt: number; checkedAt?: number }

declare module 'claude-code' {
  interface PluginState {
    'usage-bar': { limits: Limits | null }
  }
}
