export class NbRaceLeaderboard extends HTMLElement {
  static observedAttributes = ["data"];

  connectedCallback() {
    this.render();
  }

  attributeChangedCallback() {
    this.render();
  }

  render() {
    const raw = this.getAttribute("data");
    if (!raw) return;

    const entries = JSON.parse(raw);
    if (!entries || entries.length === 0) {
      this.innerHTML =
        '<p class="text-text-muted text-center py-4">No leaderboard data</p>';
      return;
    }

    const rowHeight = 40;
    const containerHeight = entries.length * rowHeight;

    this.innerHTML = `<div style="position:relative;height:${containerHeight}px">
      ${entries
        .map((entry, i) => {
          const pos = entry.position ?? i + 1;
          const teamColor = entry.teamColor || "#6b7280";
          const winProb = entry.winProbability;
          const probBar =
            winProb != null
              ? `<div class="flex items-center gap-1 w-20">
              <div class="flex-1 h-1.5 rounded-full overflow-hidden bg-bg-card-hover">
                <div class="h-full rounded-full" style="width:${(winProb * 100).toFixed(1)}%;background:${teamColor}"></div>
              </div>
              <span class="text-text-muted text-xs tabular-nums">${(winProb * 100).toFixed(1)}%</span>
            </div>`
              : "";

          const headshot = entry.headshotUrl
            ? `<img src="${entry.headshotUrl}" alt="" class="w-5 h-5 rounded-full object-cover">`
            : "";

          const flag = entry.countryFlagHref
            ? `<img src="${entry.countryFlagHref}" alt="" class="w-5 h-3 object-cover">`
            : "";

          const name =
            entry.abbreviation || entry.displayName || entry.fullName || "";
          const fullName = entry.fullName || entry.displayName || "";

          return `<div class="absolute left-0 right-0 flex items-center gap-3 py-1 px-3" style="top:${i * rowHeight}px;transition:top 400ms ease" data-athlete="${entry.espnAthleteId}">
            <span class="text-text-muted text-sm font-mono w-8 text-right">P${pos}</span>
            <div class="w-1 h-5 rounded-full" style="background:${teamColor}"></div>
            ${headshot}${flag}
            <span class="font-medium text-text-primary text-sm">${name}</span>
            <span class="text-text-muted text-xs hidden sm:inline">${fullName !== name ? fullName : ""}</span>
            <div class="flex-1"></div>
            ${probBar}
          </div>`;
        })
        .join("")}
    </div>`;
  }
}

customElements.define("nb-race-leaderboard", NbRaceLeaderboard);
