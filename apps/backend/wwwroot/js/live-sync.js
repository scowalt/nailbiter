const warning = document.getElementById("live-reconnect-warning");
let syncing = false;
let pending = false;
let stopped = false;
let events = null;

function stableComponentKey(el) {
  return [
    el.tagName,
    el.id,
    el.getAttribute("game-id"),
    el.getAttribute("race-id"),
    el.getAttribute("sport"),
  ].join(":");
}

function isTerminal(root = document) {
  return root.querySelector("[data-live-terminal='true']") !== null;
}

function localizeTimes(root) {
  for (const element of root.querySelectorAll(".nb-localtime[datetime]")) {
    const date = new Date(element.getAttribute("datetime"));
    if (Number.isNaN(date.getTime())) continue;
    const month = date.toLocaleString(undefined, { month: "short" });
    const day = date.getDate();
    const hours = date.getHours();
    const hour = hours % 12 || 12;
    const minutes = date.getMinutes().toString().padStart(2, "0");
    const period = hours >= 12 ? "PM" : "AM";
    element.textContent = `${month} ${day}, ${hour}:${minutes} ${period}`;
  }
}

function visibleAnchor(root) {
  const isVisible = (el) => {
    const r = el.getBoundingClientRect();
    return r.bottom > 0 && r.top < innerHeight;
  };
  return (
    [
      ...root.querySelectorAll(
        'a[href^="/game/"], [id^="qualification-credit-"] a[href]',
      ),
    ].find(isVisible) ??
    [...root.querySelectorAll("[id], a[href]")].find(isVisible)
  );
}

function selectorFor(el) {
  if (!el) return null;
  if (el.id) return `#${CSS.escape(el.id)}`;
  const href = el.getAttribute("href");
  if (!href) return null;
  const owner = href.startsWith("/game/")
    ? null
    : el.parentElement?.closest("[id]");
  const scope = owner ? `#${CSS.escape(owner.id)} ` : "";
  return `${scope}a[href=${JSON.stringify(href)}]`;
}

function synchronizeAttributes(existing, replacement) {
  const becameFinal =
    existing.getAttribute("data-live-final") !== "true" &&
    replacement.getAttribute("data-live-final") === "true";
  for (const attribute of [...existing.attributes]) {
    if (attribute.name === "data" && !replacement.hasAttribute("data"))
      continue;
    if (!replacement.hasAttribute(attribute.name))
      existing.removeAttribute(attribute.name);
  }
  for (const { name, value } of [...replacement.attributes]) {
    if (existing.getAttribute(name) !== value)
      existing.setAttribute(name, value);
  }
  return becameFinal;
}

async function refreshComponents(root, finalTransitions, embeddedComponents) {
  await Promise.allSettled(
    [...root.querySelectorAll("nb-sparkline, nb-win-probability-chart")]
      .filter(
        (el) =>
          (el.getAttribute("data-live-final") !== "true" ||
            finalTransitions.has(el)) &&
          !embeddedComponents.has(el) &&
          typeof el.refresh === "function",
      )
      .map((el) => el.refresh()),
  );
}

function stop() {
  stopped = true;
  pending = false;
  events?.close();
  if (warning) warning.hidden = true;
}

async function synchronize() {
  if (stopped) return;
  if (syncing) {
    pending = true;
    return;
  }
  syncing = true;
  try {
    const response = await fetch(location.href, {
      headers: { "X-Nailbiter-Live-Snapshot": "1" },
      cache: "no-store",
    });
    if (!response.ok) throw new Error(`snapshot ${response.status}`);
    if (stopped) return;
    const nextDocument = new DOMParser().parseFromString(
      await response.text(),
      "text/html",
    );
    const current = document.getElementById("main-content");
    const next = nextDocument.getElementById("main-content");
    if (!current || !next) return;
    localizeTimes(next);

    const focusedSelector = current.contains(document.activeElement)
      ? selectorFor(document.activeElement)
      : null;
    const anchor = visibleAnchor(current);
    const anchorSelector = selectorFor(anchor);
    const anchorOffset = anchor?.getBoundingClientRect().top ?? 0;
    const components = new Map(
      [
        ...current.querySelectorAll("nb-sparkline, nb-win-probability-chart"),
      ].map((el) => [stableComponentKey(el), el]),
    );
    const finalTransitions = new Set();
    const embeddedComponents = new Set();
    for (const replacement of next.querySelectorAll(
      "nb-sparkline, nb-win-probability-chart",
    )) {
      const existing = components.get(stableComponentKey(replacement));
      if (replacement.hasAttribute("data"))
        embeddedComponents.add(existing ?? replacement);
      if (existing) {
        if (synchronizeAttributes(existing, replacement))
          finalTransitions.add(existing);
        replacement.replaceWith(existing);
      }
    }

    current.replaceChildren(...next.childNodes);
    if (warning) warning.hidden = true;
    document.title = nextDocument.title;
    if (focusedSelector) {
      current.querySelector(focusedSelector)?.focus({ preventScroll: true });
    }
    if (anchorSelector) {
      const moved = current.querySelector(anchorSelector);
      if (moved) scrollBy(0, moved.getBoundingClientRect().top - anchorOffset);
    }
    await refreshComponents(current, finalTransitions, embeddedComponents);
    if (isTerminal(current)) stop();
  } catch {
    if (!stopped && warning) warning.hidden = false;
  } finally {
    syncing = false;
    if (pending && !stopped) {
      pending = false;
      void synchronize();
    }
  }
}

localizeTimes(document);

if (isTerminal()) {
  stop();
} else {
  events = new EventSource("/sse/page-sync");
  events.addEventListener("open", () => {
    if (!stopped && warning) warning.hidden = true;
  });
  events.addEventListener("sync", () => {
    if (!stopped) void synchronize();
  });
  events.addEventListener("error", () => {
    if (!stopped && warning) warning.hidden = false;
  });
}
