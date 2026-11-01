import { Chart, registerables } from "chart.js";

Chart.register(...registerables);

export class NbWinProbabilityChart extends HTMLElement {
  static observedAttributes = [
    "data",
    "home-color",
    "draw-color",
    "away-color",
    "home-abbr",
    "away-abbr",
  ];

  connectedCallback() {
    if (!this.canvas) {
      this.canvas = document.createElement("canvas");
    }
    if (this.canvas.parentNode !== this) this.appendChild(this.canvas);

    const raceId = this.getAttribute("race-id");
    const sport = this.getAttribute("sport");
    if (raceId && sport && !this.getAttribute("data")) {
      this._loadRaceData(raceId, sport);
    } else {
      this.render();
      if (!this.chart) queueMicrotask(() => this.isConnected && this.render());
    }
  }

  disconnectedCallback() {
    queueMicrotask(() => {
      if (!this.isConnected && this.chart) {
        this.chart.destroy();
        this.chart = null;
        this.canvas?.remove();
        this.canvas = null;
      }
    });
  }

  attributeChangedCallback() {
    if (this.canvas) this.render();
  }

  async refresh() {
    const raceId = this.getAttribute("race-id");
    const sport = this.getAttribute("sport");
    if (raceId && sport) {
      await this._loadRaceData(raceId, sport);
      return;
    }
    const gameId = this.getAttribute("game-id");
    if (gameId) {
      try {
        const res = await fetch(`/api/games/${gameId}/odds`, {
          cache: "no-store",
        });
        if (!res.ok) return;
        const json = await res.json();
        this.setAttribute("data", JSON.stringify(json.oddsHistory || []));
      } catch {}
    }
  }

  async _loadRaceData(raceId, sport) {
    if (this._loadPromise) return this._loadPromise;
    this._loadPromise = (async () => {
      try {
        const res = await fetch(`/api/${sport}/race/${raceId}/chart`, {
          cache: "no-store",
        });
        if (!res.ok) return;
        const json = await res.json();
        if (json.length >= 2) {
          this.setAttribute("data", JSON.stringify(json));
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
    if (!raw) return;

    const snapshots = JSON.parse(raw);
    if (!snapshots || snapshots.length < 2) return;
    this.snapshots = snapshots;

    const homeColor = this.getAttribute("home-color") || "#3b82f6";
    const drawColor = this.getAttribute("draw-color") || "#f59e0b";
    const awayColor = this.getAttribute("away-color") || "#ef4444";
    const homeAbbr = this.getAttribute("home-abbr") || "Home";
    const drawAbbr = this.getAttribute("draw-abbr") || "Draw";
    const awayAbbr = this.getAttribute("away-abbr") || "Away";

    const labels = snapshots.map((s) => {
      if (s.label) return s.label;
      if (s.timestamp) {
        const d = new Date(s.timestamp);
        return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
      }
      return "";
    });

    const homeProbs = snapshots.map(
      (s) => (s.homeWinProbability ?? s.winProbability ?? 0.5) * 100,
    );
    const hasThreeWayData = snapshots.some(
      (s) => s.drawWinProbability != null && s.awayWinProbability != null,
    );
    const hasTwoWayData =
      !hasThreeWayData && snapshots.some((s) => s.awayWinProbability != null);

    const lineDataset = (label, data, color, fill = false) => ({
      label,
      data,
      borderColor: color,
      backgroundColor: `${color}20`,
      fill,
      tension: 0.3,
      pointRadius: 0,
      borderWidth: 2,
    });

    const datasets = hasThreeWayData
      ? [
          lineDataset(homeAbbr, homeProbs, homeColor),
          lineDataset(
            drawAbbr,
            snapshots.map((s) => (s.drawWinProbability ?? 0) * 100),
            drawColor,
          ),
          lineDataset(
            awayAbbr,
            snapshots.map((s) => (s.awayWinProbability ?? 0) * 100),
            awayColor,
          ),
        ]
      : hasTwoWayData
        ? [
            lineDataset(
              homeAbbr,
              snapshots.map((s) =>
                s.homeWinProbability == null
                  ? null
                  : s.homeWinProbability * 100,
              ),
              homeColor,
            ),
            lineDataset(
              awayAbbr,
              snapshots.map((s) =>
                s.awayWinProbability == null
                  ? null
                  : s.awayWinProbability * 100,
              ),
              awayColor,
            ),
          ]
        : [
            lineDataset(
              this.hasAttribute("home-abbr") ? `${homeAbbr} win %` : "Win %",
              homeProbs,
              homeColor,
              true,
            ),
          ];

    if (this.chart) {
      this.chart.data.labels = labels;
      this.chart.data.datasets = datasets;
      this.chart.update("none");
      return;
    }

    this.chart = new Chart(this.canvas, {
      type: "line",
      data: {
        labels,
        datasets,
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        plugins: {
          legend: {
            display:
              hasThreeWayData ||
              hasTwoWayData ||
              this.hasAttribute("home-abbr"),
            labels: { color: "#d4d4d8", usePointStyle: true, boxWidth: 8 },
          },
          tooltip: {
            callbacks: {
              title: (items) => {
                const s = this.snapshots[items[0].dataIndex];
                const base = items[0].label || "";
                if (s.homeScore != null && s.awayScore != null) {
                  return `${base} · ${awayAbbr} ${s.awayScore} - ${homeAbbr} ${s.homeScore}`;
                }
                return base;
              },
              label: (ctx) => {
                const s = this.snapshots[ctx.dataIndex];
                if (s.isProbabilityBound) {
                  const bound = ctx.parsed.y > 99 ? ">99%" : "<1%";
                  return `${ctx.dataset.label}: ${bound}`;
                }
                return `${ctx.dataset.label}: ${ctx.parsed.y.toFixed(1)}%`;
              },
            },
          },
        },
        scales: {
          x: {
            display: true,
            ticks: { color: "#71717a", maxTicksLimit: 6 },
            grid: { color: "#27272a" },
          },
          y: {
            display: true,
            min: 0,
            max: 100,
            ticks: {
              color: "#71717a",
              callback: (v) => `${v}%`,
              stepSize: 25,
            },
            grid: { color: "#27272a" },
          },
        },
        interaction: { intersect: false, mode: "index" },
      },
    });
  }
}

customElements.define("nb-win-probability-chart", NbWinProbabilityChart);
