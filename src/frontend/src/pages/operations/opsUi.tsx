import { AlertTriangle, CheckCircle2, CircleDashed, Clock, MinusCircle, ShieldAlert, XCircle } from 'lucide-react';
import type {
  AnalysisResult,
  DeploymentStatus,
  IncidentSeverity,
  IncidentSource,
  IncidentStatus,
  LogLevel,
  RunConclusion,
  RunStatus,
} from '../../api/opsTypes';
import { Badge } from '../../components/ui';

type Tone = 'success' | 'warning' | 'danger' | 'info' | 'accent' | undefined;

/** Status is never conveyed by color alone: each badge carries an icon and a text label. */
function StatusBadge({ tone, icon, label }: { tone: Tone; icon: React.ReactNode; label: string }) {
  return (
    <Badge tone={tone}>
      {icon}
      {label}
    </Badge>
  );
}

const conclusionMeta: Record<RunConclusion, { label: string; tone: Tone; icon: React.ReactNode }> = {
  Success: { label: 'Correcto', tone: 'success', icon: <CheckCircle2 size={12} aria-hidden /> },
  Failure: { label: 'Fallido', tone: 'danger', icon: <XCircle size={12} aria-hidden /> },
  TimedOut: { label: 'Tiempo agotado', tone: 'danger', icon: <Clock size={12} aria-hidden /> },
  Cancelled: { label: 'Cancelado', tone: undefined, icon: <MinusCircle size={12} aria-hidden /> },
  Skipped: { label: 'Omitido', tone: undefined, icon: <MinusCircle size={12} aria-hidden /> },
  ActionRequired: { label: 'Requiere acción', tone: 'warning', icon: <AlertTriangle size={12} aria-hidden /> },
  Neutral: { label: 'Neutral', tone: undefined, icon: <MinusCircle size={12} aria-hidden /> },
  Stale: { label: 'Obsoleto', tone: undefined, icon: <MinusCircle size={12} aria-hidden /> },
  Unknown: { label: 'Desconocido', tone: undefined, icon: <CircleDashed size={12} aria-hidden /> },
};

export function RunBadge({ status, conclusion }: { status: RunStatus; conclusion: RunConclusion | null }) {
  if (status !== 'Completed' || !conclusion) {
    return <StatusBadge tone="info" icon={<CircleDashed size={12} aria-hidden />} label={status === 'Queued' ? 'En cola' : 'En curso'} />;
  }
  const meta = conclusionMeta[conclusion];
  return <StatusBadge tone={meta.tone} icon={meta.icon} label={meta.label} />;
}

const deploymentMeta: Record<DeploymentStatus, { label: string; tone: Tone; icon: React.ReactNode }> = {
  Pending: { label: 'Pendiente', tone: undefined, icon: <CircleDashed size={12} aria-hidden /> },
  Queued: { label: 'En cola', tone: 'info', icon: <CircleDashed size={12} aria-hidden /> },
  InProgress: { label: 'En curso', tone: 'info', icon: <CircleDashed size={12} aria-hidden /> },
  Success: { label: 'Correcto', tone: 'success', icon: <CheckCircle2 size={12} aria-hidden /> },
  Failure: { label: 'Fallido', tone: 'danger', icon: <XCircle size={12} aria-hidden /> },
  Error: { label: 'Error', tone: 'danger', icon: <XCircle size={12} aria-hidden /> },
  Inactive: { label: 'Inactivo', tone: undefined, icon: <MinusCircle size={12} aria-hidden /> },
};

export function DeploymentBadge({ status }: { status: DeploymentStatus }) {
  const meta = deploymentMeta[status];
  return <StatusBadge tone={meta.tone} icon={meta.icon} label={meta.label} />;
}

export const severityLabel: Record<IncidentSeverity, string> = { Critical: 'Crítica', High: 'Alta', Medium: 'Media', Low: 'Baja' };
export const SEVERITIES: IncidentSeverity[] = ['Critical', 'High', 'Medium', 'Low'];

export function SeverityBadge({ severity }: { severity: IncidentSeverity }) {
  const tone: Tone = severity === 'Critical' ? 'danger' : severity === 'High' ? 'warning' : severity === 'Medium' ? 'info' : undefined;
  return <StatusBadge tone={tone} icon={<ShieldAlert size={12} aria-hidden />} label={severityLabel[severity]} />;
}

export const incidentStatusLabel: Record<IncidentStatus, string> = {
  Open: 'Abierto',
  Investigating: 'Investigando',
  Mitigated: 'Mitigado',
  Resolved: 'Resuelto',
};
export const INCIDENT_STATUSES: IncidentStatus[] = ['Open', 'Investigating', 'Mitigated', 'Resolved'];

export function IncidentStatusBadge({ status }: { status: IncidentStatus }) {
  const tone: Tone = status === 'Resolved' ? 'success' : status === 'Mitigated' ? 'info' : status === 'Investigating' ? 'accent' : 'warning';
  const icon = status === 'Resolved' ? <CheckCircle2 size={12} aria-hidden /> : <AlertTriangle size={12} aria-hidden />;
  return <StatusBadge tone={tone} icon={icon} label={incidentStatusLabel[status]} />;
}

export const incidentSourceLabel: Record<IncidentSource, string> = {
  Manual: 'Manual',
  PipelineFailure: 'Pipeline fallido',
  DeploymentFailure: 'Despliegue fallido',
  ErrorGroup: 'Errores en logs',
};

export const levelTone: Record<LogLevel, Tone> = {
  Trace: undefined,
  Debug: undefined,
  Information: 'info',
  Warning: 'warning',
  Error: 'danger',
  Critical: 'danger',
};

export function ProviderNote({ provider, model }: { provider: string | null; model: string | null }) {
  if (!provider) return null;
  return provider === 'heuristic' ? (
    <Badge title="Análisis basado en reglas: no se usó ningún modelo de IA">Modo determinista · {model}</Badge>
  ) : (
    <Badge tone="accent" title="Generado por un modelo de IA: revisa antes de actuar">
      IA · {model}
    </Badge>
  );
}

function Confidence({ value }: { value: number | null }) {
  if (value === null) return <span className="muted small">Confianza: sin base suficiente</span>;
  const pct = Math.round(value * 100);
  return (
    <span className="row small" style={{ gap: 6 }}>
      <span className="muted">Confianza</span>
      <span className="bar-track" style={{ width: 60 }} aria-hidden>
        <span className="bar-fill" style={{ display: 'block', width: `${pct}%`, background: 'var(--chart-1)' }} />
      </span>
      <strong>{pct}%</strong>
    </span>
  );
}

const WARNING_TEXT: Record<string, string> = {
  input_truncated: 'La entrada se recortó al tamaño máximo admitido.',
  input_excerpted: 'Se envió un extracto centrado en los errores (entrada muy larga).',
};

function warningText(w: string): string {
  if (w.startsWith('provider_fallback:')) return `El proveedor de IA no respondió (${w.split(':')[1]}); se usó el análisis determinista.`;
  if (w.startsWith('unverified_facts_dropped:')) return `Se descartaron ${w.split(':')[1]} hecho(s) cuya evidencia no aparece en el log.`;
  if (w.startsWith('unknown_citations_dropped:')) return 'Se descartaron citas a documentos inexistentes.';
  return WARNING_TEXT[w] ?? w;
}

export function Warnings({ warnings }: { warnings: string[] }) {
  if (!warnings.length) return null;
  return (
    <div className="alert alert-warning" role="status">
      <AlertTriangle size={16} aria-hidden style={{ marginTop: 2 }} />
      <ul style={{ margin: 0, paddingLeft: 16 }}>
        {warnings.map((w) => (
          <li key={w}>{warningText(w)}</li>
        ))}
      </ul>
    </div>
  );
}

/** Renders an analysis keeping observed facts, hypotheses and recommendations visually distinct. */
export function AnalysisView({ result }: { result: AnalysisResult }) {
  return (
    <div className="stack">
      <div className="row-between">
        <div className="row">
          <Badge tone="accent">{result.classification.category}</Badge>
          <Badge tone={result.classification.severity === 'critical' || result.classification.severity === 'high' ? 'danger' : undefined}>
            Severidad {result.classification.severity}
          </Badge>
          <Confidence value={result.classification.confidence} />
        </div>
        <ProviderNote provider={result.provider} model={result.model} />
      </div>
      <p>{result.summary}</p>
      <Warnings warnings={result.warnings} />
      {result.redactions.count > 0 ? (
        <p className="muted small">Se ocultaron {result.redactions.count} dato(s) sensibles antes del análisis ({Object.keys(result.redactions.kinds).join(', ')}).</p>
      ) : null}

      <section className="stack-sm">
        <h3>Hechos observados</h3>
        <p className="muted small">Extraídos literalmente de los datos.</p>
        <ul className="stack-sm" style={{ margin: 0, paddingLeft: 18 }}>
          {result.facts.map((f, i) => (
            <li key={i}>
              {f.statement}
              {f.line ? <span className="muted small"> (línea {f.line})</span> : null}
              {f.evidence ? <pre className="code-block mono" style={{ marginTop: 4 }}>{f.evidence}</pre> : null}
            </li>
          ))}
        </ul>
      </section>

      <section className="stack-sm">
        <h3>Hipótesis</h3>
        <p className="muted small">Posibles causas; no están confirmadas.</p>
        {result.hypotheses.length === 0 ? <p className="muted">No hay base suficiente para proponer una causa.</p> : null}
        <ul className="stack-sm" style={{ margin: 0, paddingLeft: 18 }}>
          {result.hypotheses.map((h, i) => (
            <li key={i}>
              <div className="row-between">
                <strong>{h.statement}</strong>
                <Confidence value={h.confidence} />
              </div>
              <div className="muted small">{h.rationale}</div>
            </li>
          ))}
        </ul>
      </section>

      <section className="stack-sm">
        <h3>Recomendaciones</h3>
        <p className="muted small">DevPilot nunca ejecuta acciones automáticamente. Las marcadas requieren aprobación humana explícita.</p>
        <ol className="stack-sm" style={{ margin: 0, paddingLeft: 18 }}>
          {result.recommendations.map((r, i) => (
            <li key={i}>
              <div className="row" style={{ gap: 6 }}>
                <span>{r.action}</span>
                {r.requiresApproval ? <Badge tone="warning">Requiere aprobación</Badge> : null}
              </div>
              <div className="muted small">{r.rationale}</div>
            </li>
          ))}
        </ol>
      </section>

      {result.patterns.length > 0 ? (
        <section className="stack-sm">
          <h3>Patrones frecuentes</h3>
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th scope="col">Patrón</th>
                  <th scope="col">Ocurrencias</th>
                </tr>
              </thead>
              <tbody>
                {result.patterns.map((p) => (
                  <tr key={p.signature}>
                    <td className="mono small" style={{ wordBreak: 'break-all' }}>
                      {p.signature}
                    </td>
                    <td>{p.occurrences}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : null}
    </div>
  );
}

export function ExternalLink({ href, children }: { href: string | null; children: React.ReactNode }) {
  // URLs come from external systems; the backend only stores https links, and we always open them isolated.
  if (!href || !href.startsWith('https://')) return <>{children}</>;
  return (
    <a href={href} target="_blank" rel="noopener noreferrer">
      {children}
    </a>
  );
}
