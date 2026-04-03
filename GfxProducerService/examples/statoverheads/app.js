const cards = new Map();
const animOutTimers = new Map();

function createCardDOM(cardEl) {
  cardEl.innerHTML = `
    <div class="inner">
      <div class="shell">
        <div class="header">
          <span class="tag"><span class="dot"></span><span>Player stats</span></span>
        </div>

        <div class="name" data-ref="name">-</div>

        <div class="stats" aria-label="Player stats">
          <div class="statBox">
            <div class="statLabel">Kills</div>
            <div class="statValue" data-ref="k">-</div>
          </div>
          <div class="statBox">
            <div class="statLabel">Assists</div>
            <div class="statValue" data-ref="a">-</div>
          </div>
          <div class="statBox">
            <div class="statLabel">Deaths</div>
            <div class="statValue" data-ref="d">-</div>
          </div>
          <div class="statBox">
            <div class="statLabel">ADR</div>
            <div class="statValue" data-ref="adr">-</div>
          </div>
        </div>

        <div class="meta" data-ref="meta"></div>
      </div>
    </div>
  `;

  const refs = {};
  cardEl.querySelectorAll("[data-ref]").forEach(el => {
    refs[el.getAttribute("data-ref")] = el;
  });
  return refs;
}

function setCardValues(cardEl, refs, { name, k, a, d, adr, meta, teamClass }) {
  cardEl.classList.remove("team-ct", "team-t");
  if (teamClass) cardEl.classList.add(teamClass);

  refs.name.textContent = name;
  refs.k.textContent = k;
  refs.a.textContent = a;
  refs.d.textContent = d;
  refs.adr.textContent = adr;
  refs.meta.textContent = meta || "";
}

function calcAdr(extras, steamId) {
  const adr = extras?.playerDamageStats?.[steamId]?.adr;
  return typeof adr === "number" ? Math.round(adr) : null;
}

function mapObserverSlot(rawSlot) {
  return rawSlot === 9 ? 0 : rawSlot + 1;
}

document.querySelectorAll(".card").forEach(cardEl => {
  const gfx = cardEl.dataset.gfx;
  const refs = createCardDOM(cardEl);

  cards.set(gfx, { el: cardEl, refs });

  setCardValues(cardEl, refs, {
    name: "-",
    k: "-",
    a: "-",
    d: "-",
    adr: "-",
    meta: "",
    teamClass: null
  });
});

function updateCards(gsi, extras) {
  if (!gsi || !gsi.allplayers) return;

  const players = Object.entries(gsi.allplayers).map(([steamId, player]) => ({
    steamId,
    player
  }));

  for (let slot = 0; slot < 10; slot++) {
    const item = cards.get(`slot${slot}`);
    if (!item) continue;

    const { el, refs } = item;

    const entry = players.find(p => {
      const rawSlot = p.player.observer_slot;
      const resolved = typeof rawSlot === "number" ? mapObserverSlot(rawSlot) : rawSlot;
      return resolved === slot;
    });

    if (!entry) {
      setCardValues(el, refs, {
        name: `Slot ${slot}`,
        k: "-",
        a: "-",
        d: "-",
        adr: "-",
        meta: "",
        teamClass: null
      });
      continue;
    }

    const p = entry.player;
    const team = (p.team || "").toLowerCase();
    const teamClass = team === "ct" ? "team-ct" : (team === "t" ? "team-t" : null);

    const k = p.match_stats?.kills ?? 0;
    const d = p.match_stats?.deaths ?? 0;
    const a = p.match_stats?.assists ?? 0;
    const adr = calcAdr(extras, entry.steamId);

    setCardValues(el, refs, {
      name: p.name || `Slot ${slot}`,
      k: String(k),
      a: String(a),
      d: String(d),
      adr: adr == null ? "-" : String(adr),
      meta: team ? team.toUpperCase() : "",
      teamClass
    });
  }
}

window.addEventListener("gsi:update", (e) => {
  const { gsi, extras } = e.detail || {};
  updateCards(gsi, extras);
});

function notifyDone(action, target) {
  if (window.hotNotify && window.hotNotify.TriggerDone) {
    window.hotNotify.TriggerDone(action, target);
    return;
  }
  if (window.CefSharp && typeof CefSharp.PostMessage === "function") {
    CefSharp.PostMessage({ type: "hotNotify", action, target });
  }
}

window.hotTrigger = (action, target) => {
  const item = cards.get(target);
  if (!item) return;

  if (action === "animIn") {
    const timer = animOutTimers.get(target);
    if (timer) {
      clearTimeout(timer);
      animOutTimers.delete(target);
    }
    item.el.classList.add("visible");
  }
  if (action === "animOut") {
    item.el.classList.remove("visible");
    const timer = animOutTimers.get(target);
    if (timer) {
      clearTimeout(timer);
    }
    animOutTimers.set(target, setTimeout(() => {
      animOutTimers.delete(target);
      notifyDone("animOut", target);
    }, 560));
  }
};

document.addEventListener("hot:trigger", (e) => {
  const { action, target } = e.detail || {};
  window.hotTrigger(action, target);
});
