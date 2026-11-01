import { test as base } from "@e2e-dev/web";
import { expect } from "e2e";

declare global {
  interface Window {
    __pilotWrongLink?: {
      observer: MutationObserver;
      originals: Map<Element, string | null>;
      applied: number;
      error: string | null;
    };
  }
}

const scenario = process.env.NAILBITER_PILOT_SCENARIO ?? "normal";
if (scenario !== "normal" && scenario !== "wrong-link")
  throw new Error("BLOCKED: unknown fixture scenario");

export const test = base.extend<{ pilotIsolation: undefined }>({
  pilotIsolation: async ({ browser }, use) => {
    await expect(await browser.cookies()).toEqual([]);
    if (scenario === "wrong-link") {
      await browser.addInitScript(() => {
        const state = {
          observer: new MutationObserver(() => apply()),
          originals: new Map<Element, string | null>(),
          applied: 0,
          error: null as string | null,
        };
        window.__pilotWrongLink = state;
        function apply() {
          if (location.pathname !== "/nba") return;
          const cards = [...document.querySelectorAll("a")].filter((card) =>
            ["PHO", "PSO", "107", "113"].every((text) =>
              [...card.querySelectorAll("span")].some(
                (span) => span.innerText.trim() === text,
              ),
            ),
          );
          if (!cards.length) return;
          if (cards.length !== 1) {
            state.error = "intended card not unique";
            return;
          }
          const card = cards[0];
          if (!state.originals.has(card)) {
            const original = card.getAttribute("href");
            state.originals.set(card, original);
            if (original !== "/game/pilot-nba-completed") {
              state.error = "unexpected original href";
              return;
            }
          }
          if (card.getAttribute("href") !== "/game/pilot-nba-distraction") {
            card.setAttribute("href", "/game/pilot-nba-distraction");
            state.applied++;
          }
        }
        state.observer.observe(document, {
          subtree: true,
          childList: true,
          attributes: true,
          attributeFilter: ["href"],
        });
        apply();
      });
    }
    try {
      await use(undefined);
    } finally {
      let mutation: unknown = null;
      if (scenario === "wrong-link") {
        try {
          mutation = await browser.evaluate(() => {
            const state = window.__pilotWrongLink;
            if (!state)
              return {
                applied: 0,
                original: [],
                observed: [],
                restored: false,
                disconnected: false,
                error: "mutation state unavailable",
              };
            state.observer.disconnect();
            const observed = [...state.originals.keys()]
              .filter((card) => card.isConnected)
              .map((card) => card.getAttribute("href"));
            const originals = [...state.originals.values()];
            for (const [card, href] of state.originals) {
              if (href === null) card.removeAttribute("href");
              else card.setAttribute("href", href);
            }
            const restored = [...state.originals].every(
              ([card, href]) => card.getAttribute("href") === href,
            );
            delete window.__pilotWrongLink;
            return {
              applied: state.applied,
              original: originals,
              observed,
              restored,
              disconnected: true,
              error: state.error,
            };
          });
        } catch {
          mutation = {
            restored: false,
            error: "negative context unavailable during cleanup",
          };
        }
      }
      console.log(
        `PILOT_FIXTURE_EVIDENCE=${JSON.stringify({ freshCookies: true, scenario, mutation })}`,
      );
    }
  },
});
