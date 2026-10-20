import { useCallback, useEffect, useRef, useState } from 'react'
import {
  PRIME_COUNT,
  command,
  fetchFleet,
  incidentKindLabel,
  incidentStateLabel,
  statusLabel,
  statusSeverity,
  type FleetSnapshot,
  type IncidentView,
  type WorkerView,
} from './fleet'

const POLL_MS = 250
const STALL_MS = 3000

const severityIcon = { ok: '●', warning: '▲', error: '✖', neutral: '○' } as const

function formatTime(iso: string) {
  return new Date(iso).toLocaleTimeString([], { hour12: false })
}

function WorkerCard({ worker }: { worker: WorkerView }) {
  const severity = statusSeverity[worker.status]
  const headingId = `${worker.id}-heading`
  const field = (label: string, value: string) => {
    const id = `${worker.id}-${label.toLowerCase().replace(/\s+/g, '-')}`
    return (
      <div className="field">
        <dt id={id}>{label}</dt>
        <dd aria-labelledby={id}>{value}</dd>
      </div>
    )
  }

  return (
    <article className={`card card-${severity}`} aria-labelledby={headingId}>
      <header className="card-head">
        <h2 id={headingId}>{worker.id}</h2>
        <span className={`pill pill-${severity}`} aria-label="Status">
          <span aria-hidden="true">{severityIcon[severity]} </span>
          {statusLabel[worker.status]}
        </span>
      </header>
      <dl className="fields">
        {field('Last prime', worker.lastValue?.toString() ?? '—')}
        {field('Progress', `${worker.lastIndex} / ${PRIME_COUNT}`)}
        {field('Latency', worker.lastLatencyMs == null ? '—' : `${Math.round(worker.lastLatencyMs)} ms`)}
        {field('Cycle', worker.cycle.toString())}
      </dl>
      <ol className="log" aria-label="Log">
        {worker.log.map((entry) => (
          <li key={entry.seq} className={`log-${entry.level.toLowerCase()}`}>
            <time dateTime={entry.at}>{formatTime(entry.at)}</time>{' '}
            <span className="log-level">{entry.level.toUpperCase()}</span> {entry.workerId} · {entry.message}
          </li>
        ))}
      </ol>
    </article>
  )
}

function AlertItem({ incident, onAct }: { incident: IncidentView; onAct: (path: string) => void }) {
  return (
    <li className={`alert alert-${incident.state.toLowerCase()}`}>
      <div>
        <strong>{incident.workerId}</strong> — {incidentKindLabel[incident.kind]}
        <span className="alert-state"> · {incidentStateLabel[incident.state]}</span>
        <div className="alert-time">since {formatTime(incident.openedAt)}</div>
      </div>
      <div className="alert-actions">
        {incident.state !== 'Acknowledged' && (
          <button type="button" onClick={() => onAct(`/api/incidents/${incident.id}/ack`)}>
            Acknowledge
          </button>
        )}
        <button type="button" onClick={() => onAct(`/api/workers/${incident.workerId}/restart`)}>
          Restart worker
        </button>
      </div>
    </li>
  )
}

function App() {
  const [fleet, setFleet] = useState<FleetSnapshot | null>(null)
  const [connected, setConnected] = useState(true)
  const [busy, setBusy] = useState(false)
  const lastVersion = useRef(0)

  const refresh = useCallback(async () => {
    try {
      const snapshot = await fetchFleet()
      setConnected(true)
      // Poll responses can arrive out of order; never step backwards.
      if (snapshot.version >= lastVersion.current) {
        lastVersion.current = snapshot.version
        setFleet(snapshot)
      }
    } catch {
      setConnected(false)
    }
  }, [])

  useEffect(() => {
    const initial = setTimeout(refresh, 0)
    const timer = setInterval(refresh, POLL_MS)
    return () => {
      clearTimeout(initial)
      clearInterval(timer)
    }
  }, [refresh])

  const act = useCallback(
    async (path: string) => {
      setBusy(true)
      try {
        await command(path)
      } catch {
        // The next poll shows the real state.
      } finally {
        setBusy(false)
        await refresh()
      }
    },
    [refresh],
  )

  const stalled =
    connected && fleet !== null && Date.parse(fleet.serverNow) - Date.parse(fleet.heartbeatAt) > STALL_MS
  const running = fleet?.running ?? false

  return (
    <main className={connected ? '' : 'disconnected'}>
      <header className="top">
        <div>
          <h1>Fleet Observer</h1>
          <p className="api-status">API: {connected ? 'ok' : 'unreachable'}</p>
        </div>
        <div className="controls">
          <button type="button" disabled={!fleet || running || busy} onClick={() => act('/api/fleet/start')}>
            Start fleet
          </button>
          <button type="button" disabled={!fleet || !running || busy} onClick={() => act('/api/fleet/stop')}>
            Stop fleet
          </button>
        </div>
      </header>

      {!connected && (
        <p className="banner banner-error" role="alert">
          Connection lost — retrying
        </p>
      )}
      {stalled && (
        <p className="banner banner-error" role="alert">
          Observer stalled
        </p>
      )}

      <section aria-label="Active alerts" className="alerts">
        <h2>Active alerts</h2>
        {fleet && fleet.incidents.length > 0 ? (
          <ul>
            {fleet.incidents.map((incident) => (
              <AlertItem key={incident.id} incident={incident} onAct={act} />
            ))}
          </ul>
        ) : (
          <p className="muted">No active alerts</p>
        )}
      </section>

      <section aria-label="Fleet" className="fleet">
        {fleet?.workers.map((worker) => <WorkerCard key={worker.id} worker={worker} />)}
      </section>
    </main>
  )
}

export default App
