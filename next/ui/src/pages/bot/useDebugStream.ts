import { useEffect, useRef, useState } from 'react';
import { api, normDebugFrame } from '../../api/client';
import type { DebugFrame, TransitionRecord } from '../../api/types';
import { useTopic, useTopicEvents } from '../../api/ws';

const HISTORY = 900; // ~3 min at 5 Hz

export interface DebugStream {
  latest: DebugFrame | undefined;
  history: DebugFrame[];
  transitions: TransitionRecord[]; // newest first, up to 200
  source: 'ws' | 'poll' | 'none';
  error: string | null;
}

const tkey = (t: TransitionRecord) => `${t.tick}|${t.layer}|${t.from}|${t.to}|${t.index ?? ''}`;

/** Live DebugFrames for one bot: WebSocket topic `debug` (falls back to polling GET /debug), with a local history
 *  buffer for scrubbing and the merged transition trace (frames' lastTransitions + topic `transition` + GET /transitions). */
export function useDebugStream(bot: string, enabled = true): DebugStream {
  const ws = useTopic<DebugFrame>(enabled ? { topic: 'debug', bot } : null, normDebugFrame);
  const [polled, setPolled] = useState<DebugFrame | undefined>();
  const [error, setError] = useState<string | null>(null);
  const [history, setHistory] = useState<DebugFrame[]>([]);
  const [transitions, setTransitions] = useState<TransitionRecord[]>([]);
  const seen = useRef(new Set<string>());
  const lastWs = useRef(0);

  const addTransitions = (ts: TransitionRecord[]) => {
    const fresh = ts.filter(t => !seen.current.has(tkey(t)));
    if (!fresh.length) return;
    fresh.forEach(t => seen.current.add(tkey(t)));
    setTransitions(cur => [...fresh, ...cur].sort((a, b) => b.tick - a.tick || Date.parse(b.at) - Date.parse(a.at)).slice(0, 200));
  };

  useEffect(() => {
    seen.current = new Set(); setTransitions([]); setHistory([]); setPolled(undefined);
    if (!enabled) return;
    api.transitions(bot).then(ts => addTransitions(ts.map(t => ({ ...t, kind: String(t.kind).toLowerCase() })))).catch(() => {});
  }, [bot, enabled]); // eslint-disable-line react-hooks/exhaustive-deps

  useTopicEvents(enabled ? { topic: 'transition', bot } : null, (t: TransitionRecord) => addTransitions([{ ...t, kind: String(t.kind).toLowerCase() }]));

  useEffect(() => { if (ws) lastWs.current = Date.now(); }, [ws]);

  // poll when the socket gives nothing (host without /ws yet, or disconnected)
  useEffect(() => {
    if (!enabled) return;
    let live = true;
    const t = window.setInterval(async () => {
      if (Date.now() - lastWs.current < 2000) return;
      try { const f = await api.debug(bot); if (live) { setPolled(f); setError(null); } }
      catch (e: any) { if (live) setError(e.message); }
    }, 500);
    return () => { live = false; window.clearInterval(t); };
  }, [bot, enabled]);

  const useWs = ws && Date.now() - lastWs.current < 2500;
  const latest = useWs ? ws : polled ?? ws;

  useEffect(() => {
    if (!latest) return;
    setHistory(h => (h.length && h[h.length - 1].tick === latest.tick ? h : [...h.slice(-(HISTORY - 1)), latest]));
    addTransitions(latest.lastTransitions || []);
  }, [latest]); // eslint-disable-line react-hooks/exhaustive-deps

  return { latest, history, transitions, source: useWs ? 'ws' : polled ? 'poll' : 'none', error };
}
