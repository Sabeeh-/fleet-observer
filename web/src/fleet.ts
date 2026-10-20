export type WorkerStatus = 'Stopped' | 'Starting' | 'Healthy' | 'Slow' | 'WrongAnswer' | 'Unresponsive'
export type IncidentKind = 'WrongAnswer' | 'Unresponsive'
export type IncidentState = 'Open' | 'Acknowledged' | 'Recovered'
export type LogLevel = 'Info' | 'Warning' | 'Error'

export type LogEntry = { seq: number; at: string; workerId: string; level: LogLevel; message: string }

export type WorkerView = {
  id: string
  status: WorkerStatus
  epoch: number
  cycle: number
  lastIndex: number
  lastValue: number | null
  lastLatencyMs: number | null
  lastReportAt: string | null
  log: LogEntry[]
}

export type IncidentView = { id: number; workerId: string; kind: IncidentKind; state: IncidentState; openedAt: string }

export type FleetSnapshot = {
  version: number
  running: boolean
  heartbeatAt: string
  serverNow: string
  workers: WorkerView[]
  incidents: IncidentView[]
}

export type Severity = 'ok' | 'warning' | 'error' | 'neutral'

export const PRIME_COUNT = 17

export const statusLabel: Record<WorkerStatus, string> = {
  Stopped: 'Stopped',
  Starting: 'Starting',
  Healthy: 'Healthy',
  Slow: 'Slow',
  WrongAnswer: 'Wrong answer',
  Unresponsive: 'Unresponsive',
}

export const statusSeverity: Record<WorkerStatus, Severity> = {
  Stopped: 'neutral',
  Starting: 'neutral',
  Healthy: 'ok',
  Slow: 'warning',
  WrongAnswer: 'error',
  Unresponsive: 'error',
}

export const incidentKindLabel: Record<IncidentKind, string> = {
  WrongAnswer: 'Wrong answer',
  Unresponsive: 'Unresponsive',
}

export const incidentStateLabel: Record<IncidentState, string> = {
  Open: 'Open',
  Acknowledged: 'Acknowledged',
  Recovered: 'Recovered — awaiting acknowledgement',
}

export async function fetchFleet(): Promise<FleetSnapshot> {
  const res = await fetch('/api/fleet')
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
  return (await res.json()) as FleetSnapshot
}

export async function command(path: string): Promise<void> {
  const res = await fetch(path, { method: 'POST' })
  if (!res.ok) throw new Error(`HTTP ${res.status}`)
}
