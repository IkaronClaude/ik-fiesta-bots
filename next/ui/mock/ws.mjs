// Minimal RFC 6455 WebSocket server side (text frames, ping/close) and an RFC 6902 diff, zero dependencies.
import { createHash } from 'node:crypto';

export function acceptUpgrade(req, socket, onMessage, onClose) {
  const key = req.headers['sec-websocket-key'];
  if (!key) { socket.destroy(); return null; }
  const accept = createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n' +
    `Sec-WebSocket-Accept: ${accept}\r\n\r\n`);
  socket.setNoDelay(true);
  let buf = Buffer.alloc(0);
  let closed = false;
  const conn = {
    send(obj) {
      if (closed) return;
      const data = Buffer.from(typeof obj === 'string' ? obj : JSON.stringify(obj));
      let header;
      if (data.length < 126) header = Buffer.from([0x81, data.length]);
      else if (data.length < 65536) { header = Buffer.alloc(4); header[0] = 0x81; header[1] = 126; header.writeUInt16BE(data.length, 2); }
      else { header = Buffer.alloc(10); header[0] = 0x81; header[1] = 127; header.writeBigUInt64BE(BigInt(data.length), 2); }
      socket.write(Buffer.concat([header, data]));
    },
    close() { if (!closed) { closed = true; try { socket.end(Buffer.from([0x88, 0])); } catch { /* gone */ } onClose?.(); } },
    get closed() { return closed; },
  };
  socket.on('data', chunk => {
    buf = Buffer.concat([buf, chunk]);
    while (buf.length >= 2) {
      const op = buf[0] & 0x0f; const masked = (buf[1] & 0x80) !== 0; let len = buf[1] & 0x7f; let off = 2;
      if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
      const maskOff = off; if (masked) off += 4;
      if (buf.length < off + len) return;
      let payload = buf.subarray(off, off + len);
      if (masked) { const m = buf.subarray(maskOff, maskOff + 4); payload = Buffer.from(payload.map((b, i) => b ^ m[i & 3])); }
      buf = buf.subarray(off + len);
      if (op === 0x8) { conn.close(); return; }
      if (op === 0x9) { socket.write(Buffer.concat([Buffer.from([0x8a, payload.length]), payload])); continue; }
      if (op === 0x1) { try { onMessage(JSON.parse(payload.toString('utf8'))); } catch { /* ignore bad frames */ } }
    }
  });
  socket.on('close', () => { if (!closed) { closed = true; onClose?.(); } });
  socket.on('error', () => { if (!closed) { closed = true; onClose?.(); } });
  return conn;
}

const esc = k => String(k).replace(/~/g, '~0').replace(/\//g, '~1');

/** RFC 6902 patch from a to b (objects recursively; arrays replaced unless same length). */
export function diff(a, b, path = '', out = []) {
  if (a === b) return out;
  const ta = Array.isArray(a) ? 'array' : typeof a, tb = Array.isArray(b) ? 'array' : typeof b;
  if (ta !== tb || a === null || b === null || ta !== 'object' && ta !== 'array') {
    if (JSON.stringify(a) !== JSON.stringify(b)) out.push({ op: 'replace', path: path || '', value: b });
    return out;
  }
  if (ta === 'array') {
    if (a.length !== b.length) { out.push({ op: 'replace', path, value: b }); return out; }
    for (let i = 0; i < a.length; i++) diff(a[i], b[i], `${path}/${i}`, out);
    return out;
  }
  for (const k of Object.keys(a)) if (!(k in b)) out.push({ op: 'remove', path: `${path}/${esc(k)}` });
  for (const k of Object.keys(b)) {
    if (!(k in a)) out.push({ op: 'add', path: `${path}/${esc(k)}`, value: b[k] });
    else diff(a[k], b[k], `${path}/${esc(k)}`, out);
  }
  return out;
}
