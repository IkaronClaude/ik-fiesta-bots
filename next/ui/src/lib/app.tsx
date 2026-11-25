import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api } from '../api/client';
import type { ApiManifest, HostInfo, LibraryItem } from '../api/types';
import { setLuaLibraries, setLuaManifest } from './monaco';

export interface Toast { id: number; kind: 'ok' | 'error' | 'info'; text: string }

interface AppState {
  host: HostInfo | null;
  hostError: string | null;
  manifest: ApiManifest | null;
  libraries: LibraryItem[];
  librarySources: Record<string, string>;
  refreshLibraries: () => Promise<void>;
  toast: (text: string, kind?: Toast['kind']) => void;
  toasts: Toast[];
  dismiss: (id: number) => void;
  reloadAll: () => void;
}

const Ctx = createContext<AppState>(null as any);
export const useApp = () => useContext(Ctx);

let toastId = 0;

export function AppProvider({ children }: { children: ReactNode }) {
  const [host, setHost] = useState<HostInfo | null>(null);
  const [hostError, setHostError] = useState<string | null>(null);
  const [manifest, setManifest] = useState<ApiManifest | null>(null);
  const [libraries, setLibraries] = useState<LibraryItem[]>([]);
  const [librarySources, setLibrarySources] = useState<Record<string, string>>({});
  const [toasts, setToasts] = useState<Toast[]>([]);
  const [gen, setGen] = useState(0);

  const toast = useCallback((text: string, kind: Toast['kind'] = 'info') => {
    const id = ++toastId;
    setToasts(t => [...t.slice(-4), { id, kind, text }]);
    window.setTimeout(() => setToasts(t => t.filter(x => x.id !== id)), kind === 'error' ? 9000 : 4000);
  }, []);
  const dismiss = useCallback((id: number) => setToasts(t => t.filter(x => x.id !== id)), []);

  const refreshLibraries = useCallback(async () => {
    try {
      const libs = await api.libraries();
      setLibraries(libs);
      const entries = await Promise.all(libs.map(async l => {
        try { return [l.path, (await api.libraryVersion(l.path, l.latest)).source || ''] as const; } catch { return [l.path, ''] as const; }
      }));
      const srcs = Object.fromEntries(entries);
      setLibrarySources(srcs);
      setLuaLibraries(srcs);
    } catch { /* shown by the pages */ }
  }, []);

  useEffect(() => {
    setHostError(null);
    api.host().then(setHost).catch(e => {
      setHostError(String(e.message || e));
      // the host may not implement GET /host yet: keep working with spec defaults
      setHost(null);
    });
    api.manifest().then(m => { setManifest(m); setLuaManifest(m); }).catch(() => setManifest(null));
    refreshLibraries();
  }, [gen, refreshLibraries]);

  const value = useMemo(() => ({ host, hostError, manifest, libraries, librarySources, refreshLibraries, toast, toasts, dismiss, reloadAll: () => setGen(g => g + 1) }),
    [host, hostError, manifest, libraries, librarySources, refreshLibraries, toast, toasts, dismiss]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

/** Manifest-derived pickers, with fallbacks so the editor works before the host serves the extra fields. */
export function useManifestLists() {
  const { manifest } = useApp();
  return useMemo(() => {
    const m = manifest;
    const events = m?.events?.map(e => e.name) ?? Object.keys(m?.constants?.ev ?? {});
    const sensors = m?.sensors?.map(s => s.name) ?? (m?.functions ?? []).filter(f => f.module !== '_G' && (f.params?.length ?? 0) === 0 && /number|integer|pct|ms|boolean/.test(f.returns)).map(f => `${f.module}.${f.name}`);
    return {
      events, sensors,
      resources: m?.resources ?? [],
      procedures: m?.procedures ?? [],
      jobs: m?.jobs ?? [],
      actionKinds: m?.actionKinds ?? [],
      eventDoc: Object.fromEntries((m?.events ?? []).map(e => [e.name, `${e.doc ?? ''}${e.arg1 && e.arg1 !== '-' ? ` (arg1 ${e.arg1}` : ''}${e.arg2 && e.arg2 !== '-' ? `, arg2 ${e.arg2})` : e.arg1 && e.arg1 !== '-' ? ')' : ''}`])),
      sensorDoc: Object.fromEntries((m?.sensors ?? []).map(s => [s.name, `${s.returns ?? ''}${s.doc ? ': ' + s.doc : ''}`])),
    };
  }, [manifest]);
}
