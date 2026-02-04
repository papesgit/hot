const value = document.getElementById("value");
const bar = document.getElementById("bar");
const kd = document.getElementById("kd");
const adr = document.getElementById("adr");
const hs = document.getElementById("hs");
const gfxNodes = Array.from(document.querySelectorAll("[data-gfx]"));

function setHidden(el, hidden) {
  if (hidden) el.classList.add("is-hidden");
  else el.classList.remove("is-hidden");
}

function trigger(el, action) {
  el.classList.remove("anim-in", "anim-out");
  if (action === "animIn") {
    setHidden(el, false);
    el.classList.add("anim-in");
  } else if (action === "animOut") {
    el.classList.add("anim-out");
  }
}

function notifyDone(action, target) {
  if (window.hotNotify && window.hotNotify.TriggerDone) {
    window.hotNotify.TriggerDone(action, target);
    return;
  }
  if (window.CefSharp && typeof CefSharp.PostMessage === "function") {
    CefSharp.PostMessage({ type: "hotNotify", action, target });
  }
}

gfxNodes.forEach((el) => {
  el.addEventListener("animationend", (e) => {
    if (e.animationName === "gfxOut") {
      setHidden(el, true);
      el.classList.remove("anim-out");
      notifyDone("animOut", el.dataset.gfx || "");
    } else if (e.animationName === "gfxIn") {
      el.classList.remove("anim-in");
    }
  });
});

window.hotTrigger = (action, target) => {
  const nodes = target ? gfxNodes.filter((el) => el.dataset.gfx === target) : gfxNodes;
  nodes.forEach((el) => trigger(el, action));
};

document.addEventListener("hot:trigger", (e) => {
  if (!e.detail) return;
  window.hotTrigger(e.detail.action, e.detail.target);
});

let t = 0;
function tick() {
  t += 0.02;
  const v = (Math.sin(t) * 0.5 + 0.5) * 100;
  const pct = Math.round(v);
  value.textContent = pct + "%";
  bar.style.width = pct + "%";
  kd.textContent = (0.8 + 0.4 * Math.sin(t * 1.2 + 1)).toFixed(2);
  adr.textContent = Math.round(80 + 25 * Math.sin(t * 0.9));
  hs.textContent = Math.round(40 + 30 * Math.sin(t * 1.4 + 2));
  requestAnimationFrame(tick);
}
tick();
