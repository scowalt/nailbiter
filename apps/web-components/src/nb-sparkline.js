const sparklineCache = new Map();

export class NbSparkline extends HTMLElement {
  static observedAttributes = ["data", "color", "width", "height"];

  connectedCallback() {
    const gameId = this.getAttribute("game-id");
    if (gameId && !this.getAttribute("data")) {
      this._observer = new IntersectionObserver(
        (entries) => {
          if (entries[0].isIntersecting) {
            this._observer.disconnect();
            this._loadData(gameId);
          }
        },
        { rootMargin: "200px" },
      );
      this._observer.observe(this);
    } else {
      this.render();
    }
  }

  disconnectedCallback() {
    if (this._observer) this._observer.disconnect();
  }

  attributeChangedCallback() {
    this.render();
  }

  async refresh() {
    const gameId = this.getAttribute("game-id");
    if (gameId) await this._loadData(gameId, true);
  }

  async _loadData(gameId, force = false) {
    if (this._loadPromise) return this._loadPromise;
    if (!force && sparklineCache.has(gameId)) {
      this.setAttribute("data", JSON.stringify(sparklineCache.get(gameId)));
      return;
    }
    this._loadPromise = (async () => {
      try {
        const res = await fetch(`/api/games/${gameId}/odds`, {
          cache: "no-store",
        });
        if (!res.ok) return;
        const json = await res.json();
        const probs = (json.oddsHistory || []).map((o) => o.homeWinProbability);
        if (probs.length >= 2) {
          sparklineCache.set(gameId, probs);
          this.setAttribute("data", JSON.stringify(probs));
        }
      } catch {
      } finally {
        this._loadPromise = null;
      }
    })();
    return this._loadPromise;
  }

  render() {
    const raw = this.getAttribute("data");
    if (!raw) {
      this.innerHTML = "";
      return;
    }

    const points = JSON.parse(raw);
    if (!points || points.length < 2) {
      this.innerHTML = "";
      return;
    }

    const w = parseInt(this.getAttribute("width") || "80", 10);
    const h = parseInt(this.getAttribute("height") || "24", 10);
    const color = this.getAttribute("color") || "#3b82f6";
    const pad = 2;

    const pathPoints = points.map((v, i) => {
      const probability = Math.min(1, Math.max(0, v));
      const x = pad + (i / (points.length - 1)) * (w - 2 * pad);
      const y = h - pad - probability * (h - 2 * pad);
      return `${x},${y}`;
    });

    const d = `M${pathPoints.join("L")}`;

    this.innerHTML = `<svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}" role="img" aria-label="Win probability trend" style="display:block">
      <path d="${d}" fill="none" stroke="${color}" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/>
    </svg>`;
  }
}

customElements.define("nb-sparkline", NbSparkline);
