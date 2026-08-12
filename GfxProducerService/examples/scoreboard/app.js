const scoreboard = document.getElementById("scoreboard");
const refs = {
  ctName: document.querySelector('[data-ref="ctName"]'),
  tName: document.querySelector('[data-ref="tName"]'),
  ctPlayers: document.querySelector('[data-ref="ctPlayers"]'),
  tPlayers: document.querySelector('[data-ref="tPlayers"]')
};

// Opening kills/deaths tracking
const openingStats = {
  // steamId -> { kills: number, deaths: number }
};
let lastRound = -1;
let roundHadFirstKill = false;
let lastCtScore = null;
let lastTScore = null;

function isPipeLike(ch) {
  return ch === "|" || ch === "\uFF5C" || ch === "\u00A6" || ch === "\u2223";
}

function isCoachName(name) {
  if (!name) return false;
  const trimmed = String(name).trimStart();
  if (trimmed.length < 7) return false;
  if (trimmed.slice(0, 5).toLowerCase() !== "coach") return false;
  return isPipeLike(trimmed[5]);
}

function isCoachPlayer(player) {
  return isCoachName(player?.name);
}

function resetOpeningStats() {
  for (const key of Object.keys(openingStats)) {
    delete openingStats[key];
  }
  lastRound = -1;
  roundHadFirstKill = false;
}

function shouldResetOpeningStats(gsi) {
  const ctScore = gsi?.map?.team_ct?.score;
  const tScore = gsi?.map?.team_t?.score;
  const currentRound = getEffectiveRoundNumber(gsi);

  const scoreResetToZero =
    ctScore === 0 &&
    tScore === 0 &&
    ((lastCtScore ?? 0) !== 0 || (lastTScore ?? 0) !== 0);

  const stillZeroZeroAfterRound =
    ctScore === 0 &&
    tScore === 0 &&
    Object.keys(openingStats).length > 0 &&
    typeof currentRound === "number" &&
    currentRound > 0;

  if (typeof ctScore === "number") lastCtScore = ctScore;
  if (typeof tScore === "number") lastTScore = tScore;

  return scoreResetToZero || stillZeroZeroAfterRound;
}

function getEffectiveRoundNumber(gsi) {
  const rawRound = gsi?.map?.round;
  if (typeof rawRound !== "number") return -1;

  const phase = gsi?.phase_countdowns?.phase;
  return phase === "over" ? rawRound - 1 : rawRound;
}

function createPlayerRowCT() {
  const row = document.createElement("div");
  row.className = "player-row";
  row.innerHTML = `
    <div class="cell name" data-ref="name">-</div>
    <div class="cell stat" data-ref="k">-</div>
    <div class="cell stat" data-ref="a">-</div>
    <div class="cell stat" data-ref="d">-</div>
    <div class="cell stat" data-ref="adr">-</div>
    <div class="cell stat" data-ref="hs">-</div>
    <div class="cell stat" data-ref="ud">-</div>
    <div class="cell stat" data-ref="ef">-</div>
    <div class="cell stat" data-ref="okd">-</div>
    <div class="cell stat" data-ref="dmg">-</div>
  `;
  const cellRefs = {};
  row.querySelectorAll("[data-ref]").forEach(el => {
    cellRefs[el.getAttribute("data-ref")] = el;
  });
  return { row, cellRefs };
}

function createPlayerRowT() {
  const row = document.createElement("div");
  row.className = "player-row";
  row.innerHTML = `
    <div class="cell stat" data-ref="dmg">-</div>
    <div class="cell stat" data-ref="okd">-</div>
    <div class="cell stat" data-ref="ef">-</div>
    <div class="cell stat" data-ref="ud">-</div>
    <div class="cell stat" data-ref="hs">-</div>
    <div class="cell stat" data-ref="adr">-</div>
    <div class="cell stat" data-ref="k">-</div>
    <div class="cell stat" data-ref="a">-</div>
    <div class="cell stat" data-ref="d">-</div>
    <div class="cell name" data-ref="name">-</div>
  `;
  const cellRefs = {};
  row.querySelectorAll("[data-ref]").forEach(el => {
    cellRefs[el.getAttribute("data-ref")] = el;
  });
  return { row, cellRefs };
}

// Initialize 5 rows per team
const ctRows = [];
const tRows = [];

for (let i = 0; i < 5; i++) {
  const ct = createPlayerRowCT();
  const t = createPlayerRowT();
  refs.ctPlayers.appendChild(ct.row);
  refs.tPlayers.appendChild(t.row);
  ctRows.push(ct);
  tRows.push(t);
}

function calcAdr(gsi, extras, steamId) {
  const totalDamage = calcTotalDamage(extras, steamId);
  const completedRounds = getEffectiveRoundNumber(gsi);
  if (totalDamage == null || completedRounds <= 0) return null;
  return Math.round(totalDamage / completedRounds);
}

function calcTotalDamage(extras, steamId) {
  const totalDamage = extras?.playerDamageStats?.[steamId]?.totalDamage;
  return typeof totalDamage === "number" ? totalDamage : null;
}

function getAuthoritativeStat(extras, steamId, name) {
  const value = extras?.playerDamageStats?.[steamId]?.[name];
  return typeof value === "number" ? value : null;
}

function calcHeadshotPercentage(extras, steamId, kills) {
  const headshotKills = getAuthoritativeStat(extras, steamId, "headshotKills");
  if (headshotKills == null || kills <= 0) return null;
  return Math.round((headshotKills / kills) * 100);
}

// Track opening kills/deaths from GSI round_kills
function trackOpeningKills(gsi) {
  if (!gsi || !gsi.map) return;

  if (shouldResetOpeningStats(gsi)) {
    resetOpeningStats();
  }

  const currentRound = getEffectiveRoundNumber(gsi);

  // Reset tracking on new round
  if (currentRound !== lastRound) {
    lastRound = currentRound;
    roundHadFirstKill = false;
  }

  // If we already recorded first kill this round, skip
  if (roundHadFirstKill) return;

  // Check round_kills for any kills
  if (!gsi.allplayers) return;

  for (const [steamId, player] of Object.entries(gsi.allplayers)) {
    if (isCoachPlayer(player)) continue;

    const roundKills = player.state?.round_kills ?? 0;

    if (roundKills > 0 && !roundHadFirstKill) {
      // This player got the first kill of the round
      roundHadFirstKill = true;

      if (!openingStats[steamId]) {
        openingStats[steamId] = { kills: 0, deaths: 0 };
      }
      openingStats[steamId].kills++;

      // Find who died first (check for player with round_kills = 0 and health = 0)
      for (const [victimId, victim] of Object.entries(gsi.allplayers)) {
        if (isCoachPlayer(victim)) continue;
        if (victimId !== steamId && victim.state?.health === 0) {
          if (!openingStats[victimId]) {
            openingStats[victimId] = { kills: 0, deaths: 0 };
          }
          openingStats[victimId].deaths++;
          break;
        }
      }
      break;
    }
  }
}

function getOpeningKills(steamId) {
  return openingStats[steamId]?.kills ?? 0;
}

function getOpeningDeaths(steamId) {
  return openingStats[steamId]?.deaths ?? 0;
}

function updatePlayerRow(rowData, player, steamId, gsi, extras, leaders) {
  const { cellRefs } = rowData;

  if (!player) {
    cellRefs.name.textContent = "-";
    cellRefs.k.textContent = "-";
    cellRefs.a.textContent = "-";
    cellRefs.d.textContent = "-";
    cellRefs.adr.textContent = "-";
    cellRefs.hs.textContent = "-";
    cellRefs.ud.textContent = "-";
    cellRefs.ef.textContent = "-";
    cellRefs.okd.textContent = "-";
    cellRefs.dmg.textContent = "-";
    cellRefs.okd.className = "cell stat";
    ["k", "a", "adr", "hs", "ud", "ef"].forEach(stat => cellRefs[stat].classList.remove("highlight"));
    return;
  }

  const k = player.match_stats?.kills ?? 0;
  const a = player.match_stats?.assists ?? 0;
  const d = player.match_stats?.deaths ?? 0;
  const adr = calcAdr(gsi, extras, steamId);
  const dmg = calcTotalDamage(extras, steamId);
  const hs = calcHeadshotPercentage(extras, steamId, k);
  const ud = getAuthoritativeStat(extras, steamId, "utilityDamage");
  const ef = getAuthoritativeStat(extras, steamId, "enemiesFlashed");
  const ok = getOpeningKills(steamId);
  const od = getOpeningDeaths(steamId);

  cellRefs.name.textContent = player.name || "-";
  cellRefs.k.textContent = String(k);
  cellRefs.a.textContent = String(a);
  cellRefs.d.textContent = String(d);
  cellRefs.adr.textContent = adr == null ? "-" : String(adr);
  cellRefs.hs.textContent = hs == null ? "-" : `${hs}%`;
  cellRefs.ud.textContent = ud == null ? "-" : String(ud);
  cellRefs.ef.textContent = ef == null ? "-" : String(ef);
  cellRefs.dmg.textContent = dmg == null ? "-" : String(dmg);
  cellRefs.okd.textContent = `${ok}/${od}`;

  // Color the opening KD
  cellRefs.okd.classList.remove("okd-positive", "okd-negative", "okd-neutral");
  if (ok > od) {
    cellRefs.okd.classList.add("okd-positive");
  } else if (ok < od) {
    cellRefs.okd.classList.add("okd-negative");
  } else {
    cellRefs.okd.classList.add("okd-neutral");
  }

  ["k", "a", "adr", "hs", "ud", "ef"].forEach(stat => {
    cellRefs[stat].classList.toggle("highlight", leaders?.[stat]?.has(steamId) ?? false);
  });
}

function getTeamStatLeaders(entries, gsi, extras) {
  const statValues = {
    k: entry => entry.player.match_stats?.kills ?? 0,
    a: entry => entry.player.match_stats?.assists ?? 0,
    adr: entry => calcAdr(gsi, extras, entry.steamId),
    hs: entry => calcHeadshotPercentage(extras, entry.steamId, entry.player.match_stats?.kills ?? 0),
    ud: entry => getAuthoritativeStat(extras, entry.steamId, "utilityDamage"),
    ef: entry => getAuthoritativeStat(extras, entry.steamId, "enemiesFlashed")
  };

  const leaders = {};
  for (const [stat, getValue] of Object.entries(statValues)) {
    const values = entries
      .map(entry => ({ steamId: entry.steamId, value: getValue(entry) }))
      .filter(entry => entry.value != null && entry.value > 0);
    const highest = values.reduce((max, entry) => Math.max(max, entry.value), 0);
    leaders[stat] = new Set(values.filter(entry => entry.value === highest).map(entry => entry.steamId));
  }
  return leaders;
}

function updateScoreboard(gsi, extras) {
  if (!gsi || !gsi.allplayers) return;

  // Track opening kills
  trackOpeningKills(gsi);

  // Separate players by team
  const ctPlayers = [];
  const tPlayers = [];

  Object.entries(gsi.allplayers).forEach(([steamId, player]) => {
    if (isCoachPlayer(player)) return;

    const team = (player.team || "").toLowerCase();
    if (team === "ct") {
      ctPlayers.push({ steamId, player });
    } else if (team === "t") {
      tPlayers.push({ steamId, player });
    }
  });

  // Sort by total damage descending
  const sortByDamage = (a, b) => {
    const damageDiff = (calcTotalDamage(extras, b.steamId) ?? 0) - (calcTotalDamage(extras, a.steamId) ?? 0);
    if (damageDiff !== 0) return damageDiff;
    return (b.player.match_stats?.kills ?? 0) - (a.player.match_stats?.kills ?? 0);
  };
  ctPlayers.sort(sortByDamage);
  tPlayers.sort(sortByDamage);

  const ctLeaders = getTeamStatLeaders(ctPlayers, gsi, extras);
  const tLeaders = getTeamStatLeaders(tPlayers, gsi, extras);

  // Update team names from map info if available
  if (gsi.map && gsi.map.team_ct && gsi.map.team_ct.name) {
    refs.ctName.textContent = gsi.map.team_ct.name;
  }
  if (gsi.map && gsi.map.team_t && gsi.map.team_t.name) {
    refs.tName.textContent = gsi.map.team_t.name;
  }

  // Update player rows
  for (let i = 0; i < 5; i++) {
    const ctEntry = ctPlayers[i];
    const tEntry = tPlayers[i];

    updatePlayerRow(ctRows[i], ctEntry?.player, ctEntry?.steamId, gsi, extras, ctLeaders);
    updatePlayerRow(tRows[i], tEntry?.player, tEntry?.steamId, gsi, extras, tLeaders);
  }
}

// GSI event listener
window.addEventListener("gsi:update", (e) => {
  const { gsi, extras } = e.detail || {};
  updateScoreboard(gsi, extras);
});

// HLAE trigger for animIn/animOut
const animOutTimers = new Map();

function notifyDone(action, target) {
  if (window.hotNotify && window.hotNotify.triggerDone) {
    window.hotNotify.triggerDone(action, target);
  }
}

window.hotTrigger = (action, target) => {
  if (target !== "scoreboard") return;

  if (action === "animIn") {
    const timer = animOutTimers.get(target);
    if (timer) {
      clearTimeout(timer);
      animOutTimers.delete(target);
    }
    scoreboard.classList.add("visible");
  }
  if (action === "animOut") {
    scoreboard.classList.remove("visible");
    const timer = animOutTimers.get(target);
    if (timer) {
      clearTimeout(timer);
    }
    animOutTimers.set(target, setTimeout(() => {
      animOutTimers.delete(target);
      notifyDone("animOut", "scoreboard");
    }, 420));
  }
};
