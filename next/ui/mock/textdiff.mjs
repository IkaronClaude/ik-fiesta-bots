// Line-based unified diff (LCS), for GET /brains/{name}/diff in the mock.
export function unifiedDiff(a, b, nameA = 'a', nameB = 'b', context = 3) {
  const x = a.split('\n'), y = b.split('\n');
  const n = x.length, m = y.length;
  const dp = Array.from({ length: n + 1 }, () => new Int32Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) dp[i][j] = x[i] === y[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
  const ops = [];
  let i = 0, j = 0;
  while (i < n && j < m) {
    if (x[i] === y[j]) { ops.push([' ', x[i], i, j]); i++; j++; }
    else if (dp[i + 1][j] >= dp[i][j + 1]) { ops.push(['-', x[i], i, j]); i++; }
    else { ops.push(['+', y[j], i, j]); j++; }
  }
  while (i < n) { ops.push(['-', x[i], i, j]); i++; }
  while (j < m) { ops.push(['+', y[j], i, j]); j++; }
  if (!ops.some(o => o[0] !== ' ')) return '';
  const out = [`--- ${nameA}`, `+++ ${nameB}`];
  let k = 0;
  while (k < ops.length) {
    if (ops[k][0] === ' ') { k++; continue; }
    const s = Math.max(0, k - context);
    let e = k;
    while (e < ops.length) {
      if (ops[e][0] !== ' ') { e++; continue; }
      let run = 0; while (e + run < ops.length && ops[e + run][0] === ' ') run++;
      if (run > context * 2 || e + run >= ops.length) { e = Math.min(ops.length, e + context); break; }
      e += run;
    }
    const hunk = ops.slice(s, e);
    const aStart = hunk[0][2] + 1, bStart = hunk[0][3] + 1;
    const aLen = hunk.filter(o => o[0] !== '+').length, bLen = hunk.filter(o => o[0] !== '-').length;
    out.push(`@@ -${aStart},${aLen} +${bStart},${bLen} @@`);
    for (const o of hunk) out.push(o[0] + o[1]);
    k = e;
  }
  return out.join('\n');
}
