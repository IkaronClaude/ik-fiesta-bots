// One WebSocket per page (API_SPEC 8.4), multiplexed: ref-counted subscriptions, delta frames
// ({full:false, patch}) applied with RFC 6902, automatic reconnect + resubscribe.
import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { getToken } from './client';
import { applyPatch } from './patch';

type Listener = (data: any) => void;
interface Sub { key: string; msg: Record<string, unknown>; listeners: Set<Listener>; data: any }

class Hub {
  private ws: WebSocket | null = null;
  private subs = new Map<string, Sub>();
  private retry = 0;
  private timer: number | undefined;
  status: 'connecting' | 'open' | 'closed' = 'closed';
  private statusListeners = new Set<() => void>();

  private setStatus(s: Hub['status']) { this.status = s; this.statusListeners.forEach(l => l()); }
  onStatus(l: () => void) { this.statusListeners.add(l); return () => { this.statusListeners.delete(l); }; }

  private connect() {
    if (this.ws || !this.subs.size) return;
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const tok = getToken();
    const ws = new WebSocket(`${proto}//${location.host}/api/v3/ws${tok ? `?token=${encodeURIComponent(tok)}` : ''}`);
    this.ws = ws;
    this.setStatus('connecting');
    ws.onopen = () => {
      this.retry = 0; this.setStatus('open');
      for (const s of this.subs.values()) ws.send(JSON.stringify({ op: 'sub', ...s.msg }));
    };
    ws.onmessage = ev => {
      let m: any; try { m = JSON.parse(ev.data); } catch { return; }
      for (const s of this.subs.values()) {
        if (s.msg.topic !== m.topic) continue;
        if (s.msg.bot && m.bot && s.msg.bot !== m.bot) continue;
        if (m.full === false && Array.isArray(m.patch)) {
          if (s.data === undefined) continue; // wait for a full frame
          try { s.data = applyPatch(s.data, m.patch); } catch { continue; }
        } else s.data = m.data;
        s.listeners.forEach(l => l(s.data));
      }
    };
    ws.onclose = () => {
      this.ws = null; this.setStatus('closed');
      for (const s of this.subs.values()) s.data = s.msg.topic === 'debug' || s.msg.topic === 'fleet' || s.msg.topic === 'sensors' ? undefined : s.data;
      if (this.subs.size) {
        const delay = Math.min(10000, 500 * 2 ** this.retry++);
        this.timer = window.setTimeout(() => this.connect(), delay);
      }
    };
    ws.onerror = () => ws.close();
  }

  subscribe(msg: Record<string, unknown>, l: Listener): () => void {
    const key = JSON.stringify(msg);
    let s = this.subs.get(key);
    if (!s) {
      s = { key, msg, listeners: new Set(), data: undefined };
      this.subs.set(key, s);
      if (this.ws?.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify({ op: 'sub', ...msg }));
    }
    s.listeners.add(l);
    if (s.data !== undefined) l(s.data);
    this.connect();
    return () => {
      const cur = this.subs.get(key); if (!cur) return;
      cur.listeners.delete(l);
      if (!cur.listeners.size) {
        this.subs.delete(key);
        if (this.ws?.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify({ op: 'unsub', ...msg }));
        if (!this.subs.size) { window.clearTimeout(this.timer); this.ws?.close(); }
      }
    };
  }
}

export const hub = new Hub();

/** Latest value of a topic (full frame + patches applied). */
export function useTopic<D>(msg: Record<string, unknown> | null, map?: (d: any) => D): D | undefined {
  const [data, setData] = useState<D | undefined>(undefined);
  const mapRef = useRef(map); mapRef.current = map;
  const key = msg ? JSON.stringify(msg) : '';
  useEffect(() => {
    setData(undefined);
    if (!msg) return;
    return hub.subscribe(msg, d => setData(mapRef.current ? mapRef.current(d) : d));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  return data;
}

/** Each message of an event topic (transition, log, alert). */
export function useTopicEvents(msg: Record<string, unknown> | null, onEvent: (d: any) => void) {
  const ref = useRef(onEvent); ref.current = onEvent;
  const key = msg ? JSON.stringify(msg) : '';
  useEffect(() => {
    if (!msg) return;
    let first = true;
    return hub.subscribe(msg, d => { if (first) { first = false; } ref.current(d); });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
}

export function useWsStatus() {
  return useSyncExternalStore(l => hub.onStatus(l), () => hub.status);
}
