import { useEffect, useRef, useState } from 'react';

/** Grey overlay with a throbber over a live view while the bot / stream is down. Absolutely positioned (never moves
 *  the layout), shown only when the down state lasts longer than `delayMs`, removed at once when it is back. */
export function ConnOverlay({ down, reason, delayMs = 1500 }: { down: boolean; reason: string; delayMs?: number }) {
  const [shown, setShown] = useState(false);
  const since = useRef<number | null>(null);
  useEffect(() => {
    if (!down) { since.current = null; setShown(false); return; }
    since.current ??= Date.now();
    const t = window.setTimeout(() => setShown(true), Math.max(0, delayMs - (Date.now() - since.current)));
    return () => window.clearTimeout(t);
  }, [down, delayMs]);
  return (
    <div className={'conn-overlay' + (shown ? ' on' : '')} aria-hidden={!shown} role="status">
      <div className="conn-box">
        <span className="throbber" />
        <div><strong>bot not connected</strong><div className="small">{reason}</div></div>
      </div>
    </div>
  );
}
