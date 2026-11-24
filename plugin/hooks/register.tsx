import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register, SessionRateLimit } from 'claude-code'

import type { Limit, Limits } from '../types'

const limits = atom({ plugin: 'usage-bar', key: 'limits' } as const, null)
const STORE_KEY = 'limits'
const SYNC_MS = 30_000
const WEEK = ['周日', '周一', '周二', '周三', '周四', '周五', '周六']

const pick = (rows: SessionRateLimit[], kind: string): Limit | null => {
  const row = rows.find(r => r.kind === kind)
  return row ? { percentUsed: row.percentUsed, resetsAt: row.resetsAt } : null
}

const pad = (n: number) => String(n).padStart(2, '0')

const isReset = (limit: Limit, now: number) => {
  if (!limit.resetsAt) return false
  const t = new Date(limit.resetsAt).getTime()
  return !Number.isNaN(t) && t <= now
}

const resetText = (iso: string | undefined, withDay: boolean) => {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  const time = `${pad(d.getHours())}:${pad(d.getMinutes())}`
  return withDay ? `${WEEK[d.getDay()]} ${time} 重置` : `${time} 重置`
}

const bar = (pct: number) => {
  const filled = Math.max(0, Math.min(10, Math.round(pct / 10)))
  return '█'.repeat(filled) + '░'.repeat(10 - filled)
}

const color = (pct: number) => (pct >= 90 ? 'red' : pct >= 70 ? 'yellow' : 'green')

// 哪些窗口已过重置时间，例如 "10"：5 小时已重置、本周未重置
const resetMask = (v: Limits | null, now: number) =>
  v ? `${v.fiveHour && isReset(v.fiveHour, now) ? 1 : 0}${v.sevenDay && isReset(v.sevenDay, now) ? 1 : 0}` : ''

// 所有会话共用的最新读数，放在本插件的数据文件夹里（升级插件也会保留）；
// 桌面悬浮条 UsageBarOverlay.exe 也读这份文件，Claude 后台没起来时也能显示
let dataFile: string | null = null
const sharedFile = async ($: EngineInterface) => {
  if (dataFile) return dataFile
  try {
    const dir = (await $.process.hookEnv()).CLAUDE_PLUGIN_DATA
    if (dir) dataFile = `${dir.replaceAll('\\', '/')}/usage.json`
  } catch {
    // 拿不到数据文件夹：只在本会话里显示
  }
  return dataFile
}

const readShared = async ($: EngineInterface): Promise<Limits | null> => {
  try {
    const file = await sharedFile($)
    if (!file) return null
    const v = JSON.parse(await $.fs.read(file)) as Limits
    return typeof v?.seenAt === 'number' ? v : null
  } catch {
    return null
  }
}

const save = async ($: EngineInterface, rows: SessionRateLimit[]) => {
  const fiveHour = pick(rows, 'five_hour')
  const sevenDay = pick(rows, 'seven_day')
  if (!fiveHour && !sevenDay) return
  const next: Limits = { fiveHour, sevenDay, seenAt: await $.clock.now() }
  await update($, limits, () => next)
  await $.store.set(STORE_KEY, next)
  const file = await sharedFile($)
  if (file) await $.fs.write(file, JSON.stringify(next)).catch(() => undefined)
}

// 别的会话拿到了更新的读数，或某个窗口刚过重置时间：刷新本会话的额度条
const sync = async ($: EngineInterface) => {
  const now = await $.clock.now()
  const current = await read($, limits)
  const shared = await readShared($)
  const newest = shared && (!current || shared.seenAt > current.seenAt) ? shared : current
  if (!newest) return
  if (newest !== current || resetMask(newest, now) !== resetMask(current, current?.checkedAt ?? 0)) {
    await update($, limits, () => ({ ...newest, checkedAt: now }))
  }
}

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    const result = await next(e)
    const kept = (await $.store.get(STORE_KEY)) as Limits | undefined
    if (kept && (await read($, limits)) === null) {
      await update($, limits, () => kept)
    }
    const usage = await $.session.usage()
    await save($, usage.rateLimits)
    await sync($)
    $.clock.every(SYNC_MS, () => sync($))
    return result
  })

  on('session.measure', async ($, e, next) => {
    await save($, e.rateLimits)
    return next(e)
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    const value = await read($, limits)
    if (e.props.hasSurvey) return next(e)

    const { Box, Text } = $.ui.resolve(e)

    if (!value) {
      return (
        <Box>
          <Text dimColor>额度：发一条消息后显示</Text>
        </Box>
      )
    }

    const now = value.checkedAt ?? value.seenAt
    const row = (label: string, limit: Limit | null, withDay: boolean) => {
      if (!limit) return <Text dimColor>{label} 暂无数据</Text>
      const done = isReset(limit, Math.max(now, Date.now()))
      const pct = done ? 0 : limit.percentUsed
      return (
        <Text>
          <Text dimColor>{label} </Text>
          <Text color={color(pct)}>{bar(pct)}</Text>
          <Text bold> {pct}%</Text>
          <Text dimColor> {done ? '已重置' : resetText(limit.resetsAt, withDay)}</Text>
        </Text>
      )
    }

    return (
      <Box flexDirection="row" columnGap={3} rowGap={0} flexWrap="wrap">
        {row('5小时', value.fiveHour, false)}
        {row('本周', value.sevenDay, true)}
      </Box>
    )
  })
}
