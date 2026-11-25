import { useSyncExternalStore } from 'react';

// Hash routes: #/fleet, #/bots/<id>[/<tab>], #/brains, #/brains/<name>[@<hash>], #/libraries[/<path>],
// #/versions/<brain>, #/settings
function current() { return decodeURI(location.hash.replace(/^#/, '') || '/fleet'); }

export function useRoute(): string[] {
  const h = useSyncExternalStore(l => { window.addEventListener('hashchange', l); return () => window.removeEventListener('hashchange', l); }, current);
  return h.split('?')[0].split('/').filter(Boolean);
}

export function useQuery(): URLSearchParams {
  const h = useSyncExternalStore(l => { window.addEventListener('hashchange', l); return () => window.removeEventListener('hashchange', l); }, current);
  return new URLSearchParams(h.split('?')[1] || '');
}

export function go(path: string) { location.hash = '#' + path; }
export const href = (path: string) => '#' + path;
