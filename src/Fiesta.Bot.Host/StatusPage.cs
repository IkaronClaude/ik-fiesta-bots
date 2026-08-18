namespace Fiesta.Bot.Host;

/// <summary>The super-simple public status page for bots.ikaron.uk — a live list of running bots (level + map + class)</summary>
internal static class StatusPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Fiesta Bots</title>
<style>
  :root { color-scheme: dark; }
  body { font-family: system-ui, -apple-system, Segoe UI, sans-serif; margin: 2rem; background: #0f1115; color: #e6e6e6; }
  h1 { font-size: 1.2rem; margin: 0 0 1rem; }
  .muted { color: #8a93a6; }
  table { border-collapse: collapse; width: 100%; max-width: 760px; }
  th, td { text-align: left; padding: .45rem .7rem; border-bottom: 1px solid #232833; }
  th { color: #8a93a6; font-weight: 600; font-size: .72rem; letter-spacing: .04em; text-transform: uppercase; }
  tbody tr:hover { background: #151922; }
  .dead td:first-child::after { content: " \00A0 DEAD"; color: #e06c75; font-size: .7rem; }
  .pill { font-size: .74rem; color: #98c379; }
</style>
</head>
<body>
<h1>Fiesta bots <span class="muted" id="count"></span></h1>
<table>
  <thead><tr><th>Bot</th><th>Lv</th><th>Class</th><th>Map</th><th>Phase</th><th>HP</th></tr></thead>
  <tbody id="rows"><tr><td colspan="6" class="muted">loading…</td></tr></tbody>
</table>
<p class="muted" id="ts"></p>
<script>
  const esc = s => String(s ?? "").replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));
  async function tick() {
    try {
      const bots = await (await fetch("status.json", { cache: "no-store" })).json();
      document.getElementById("count").textContent = "(" + bots.length + ")";
      document.getElementById("rows").innerHTML = bots.length
        ? bots.map(b => `<tr class="${b.dead ? "dead" : ""}">`
            + `<td>${esc(b.character || b.id)}</td>`
            + `<td>${b.level ?? "-"}</td>`
            + `<td>${esc(b.clsName || b.cls) || "-"}</td>`
            + `<td>${esc(b.map || "-")}</td>`
            + `<td class="pill">${esc(b.phase || "-")}</td>`
            + `<td>${b.hp != null ? b.hp + (b.maxHp ? "/" + b.maxHp : "") : "-"}</td></tr>`).join("")
        : `<tr><td colspan="6" class="muted">no bots running</td></tr>`;
      document.getElementById("ts").textContent = "updated " + new Date().toLocaleTimeString();
    } catch (e) {
      document.getElementById("ts").textContent = "update failed: " + e;
    }
  }
  tick();
  setInterval(tick, 3000);
</script>
</body>
</html>
""";
}
