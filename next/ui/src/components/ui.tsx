import { useEffect, useState, type ReactNode } from 'react';
import { useApp } from '../lib/app';

export function fmtDuration(ms: number | null | undefined): string {
  if (ms == null || !isFinite(ms)) return '-';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(s < 10 ? 1 : 0)} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${Math.floor(s % 60)}s`;
  const h = Math.floor(m / 60);
  if (h < 48) return `${h}h ${m % 60}m`;
  return `${Math.floor(h / 24)}d ${h % 24}h`;
}

/** Date + time, always with the date (operator rule: times in state carry a date). */
export function fmtDate(iso: string | null | undefined, withSeconds = true): string {
  if (!iso) return '-';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return String(iso);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}${withSeconds ? ':' + p(d.getSeconds()) : ''}`;
}
export function fmtTime(iso: string | null | undefined): string {
  if (!iso) return '-';
  const d = new Date(iso); const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${String(d.getMilliseconds()).padStart(3, '0')}`;
}
export const fmtNum = (n: number | null | undefined, digits = 0) => (n == null || !isFinite(n) ? '-' : n.toLocaleString(undefined, { maximumFractionDigits: digits, minimumFractionDigits: 0 }));

export function useNow(intervalMs = 1000) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), intervalMs); return () => window.clearInterval(t); }, [intervalMs]);
  return now;
}

/** Button running an async action; disables while running and toasts the error. */
export function ActionButton({ onClick, children, kind, title, disabled, ok, small }: {
  onClick: () => Promise<unknown> | unknown; children: ReactNode; kind?: 'primary' | 'danger' | 'ghost'; title?: string; disabled?: boolean; ok?: string; small?: boolean;
}) {
  const [busy, setBusy] = useState(false);
  const { toast } = useApp();
  return (
    <button className={`btn ${kind ?? ''} ${small ? 'sm' : ''}`} title={title} disabled={disabled || busy} onClick={async () => {
      setBusy(true);
      try { await onClick(); if (ok) toast(ok, 'ok'); } catch (e: any) { toast(e?.message || String(e), 'error'); } finally { setBusy(false); }
    }}>{busy ? <span className="spin" /> : null}{children}</button>
  );
}

/** Two-step confirm inside the button itself (no window.confirm). */
export function ConfirmButton({ onConfirm, children, confirmText = 'Click again to confirm', kind = 'danger', small }: {
  onConfirm: () => Promise<unknown> | unknown; children: ReactNode; confirmText?: string; kind?: 'primary' | 'danger' | 'ghost'; small?: boolean;
}) {
  const [armed, setArmed] = useState(false);
  useEffect(() => { if (!armed) return; const t = window.setTimeout(() => setArmed(false), 3500); return () => window.clearTimeout(t); }, [armed]);
  if (!armed) return <button className={`btn ${kind === 'danger' ? 'ghost danger-text' : kind} ${small ? 'sm' : ''}`} onClick={() => setArmed(true)}>{children}</button>;
  return <ActionButton kind={kind} small={small} onClick={async () => { setArmed(false); await onConfirm(); }}>{confirmText}</ActionButton>;
}

export function Modal({ title, onClose, children, footer, wide }: { title: string; onClose: () => void; children: ReactNode; footer?: ReactNode; wide?: boolean }) {
  useEffect(() => {
    const k = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', k); return () => window.removeEventListener('keydown', k);
  }, [onClose]);
  return (
    <div className="modal-back" onMouseDown={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div className={'modal' + (wide ? ' wide' : '')} role="dialog" aria-label={title}>
        <div className="modal-head"><h3>{title}</h3><button className="btn ghost sm" onClick={onClose} aria-label="close">Close</button></div>
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-foot">{footer}</div>}
      </div>
    </div>
  );
}

export function Tabs<T extends string>({ tabs, value, onChange, right }: { tabs: { id: T; label: ReactNode }[]; value: T; onChange: (t: T) => void; right?: ReactNode }) {
  return (
    <div className="tabs" role="tablist">
      {tabs.map(t => <button key={t.id} role="tab" aria-selected={t.id === value} className={'tab' + (t.id === value ? ' on' : '')} onClick={() => onChange(t.id)}>{t.label}</button>)}
      <div className="tabs-right">{right}</div>
    </div>
  );
}

export function Sparkline({ values, width = 120, height = 24, color = 'var(--accent)' }: { values: number[]; width?: number; height?: number; color?: string }) {
  if (values.length < 2) return <svg width={width} height={height} />;
  const min = Math.min(...values), max = Math.max(...values), span = max - min || 1;
  const pts = values.map((v, i) => `${(i / (values.length - 1)) * (width - 2) + 1},${height - 2 - ((v - min) / span) * (height - 4)}`).join(' ');
  const last = values[values.length - 1];
  return (
    <svg width={width} height={height} className="spark" aria-label={`min ${min} max ${max} last ${last}`}>
      <polyline points={pts} fill="none" stroke={color} strokeWidth={1.5} strokeLinejoin="round" />
      <circle cx={width - 1} cy={height - 2 - ((last - min) / span) * (height - 4)} r={2} fill={color} />
    </svg>
  );
}

export function StateBadge({ state }: { state: string }) {
  const s = state.toLowerCase();
  const cls = s === 'inzone' ? 'ok' : s === 'paused' ? 'warn' : s === 'offline' ? 'off' : s === 'failed' ? 'bad' : 'mid';
  return <span className={`badge ${cls}`}>{state}</span>;
}

export function Empty({ children }: { children: ReactNode }) { return <div className="empty">{children}</div>; }

export function JsonValue({ v }: { v: unknown }) {
  if (v === null || v === undefined) return <span className="muted">nil</span>;
  if (typeof v === 'number') return <span className="num">{Number.isInteger(v) ? v : v.toFixed(3)}</span>;
  if (typeof v === 'boolean') return <span className={v ? 'true' : 'false'}>{String(v)}</span>;
  if (typeof v === 'string') return <span className="str">"{v}"</span>;
  return <span className="mono small">{JSON.stringify(v)}</span>;
}

export function Field({ label, children, hint }: { label: string; children: ReactNode; hint?: ReactNode }) {
  return <label className="field"><span className="field-label">{label}</span>{children}{hint && <span className="hint">{hint}</span>}</label>;
}

export function useAsync<T>(fn: () => Promise<T>, deps: unknown[]): { data: T | undefined; error: string | null; loading: boolean; reload: () => void } {
  const [data, setData] = useState<T | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [gen, setGen] = useState(0);
  useEffect(() => {
    let live = true; setLoading(true);
    fn().then(d => { if (live) { setData(d); setError(null); } }).catch(e => { if (live) setError(String(e?.message || e)); }).finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, gen]);
  return { data, error, loading, reload: () => setGen(g => g + 1) };
}
