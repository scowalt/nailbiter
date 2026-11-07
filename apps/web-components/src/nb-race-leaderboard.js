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
      this.innerHTML = '<p class="leaderboard-empty">No leaderboard data</p>';
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
              ? `<div class="leaderboard-probability">
              <div class="standings-meter">
                <div class="leaderboard-meter-fill" style="width:${(winProb * 100).toFixed(1)}%;background:${teamColor}"></div>
              </div>
              <span class="leaderboard-percent">${(winProb * 100).toFixed(1)}%</span>
            </div>`
              : "";

          const headshot = entry.headshotUrl
            ? `<img src="${entry.headshotUrl}" alt="" class="leaderboard-headshot">`
            : "";

          const flag = entry.countryFlagHref
            ? `<img src="${entry.countryFlagHref}" alt="" class="standings-flag">`
            : "";

          const name =
            entry.abbreviation || entry.displayName || entry.fullName || "";
          const fullName = entry.fullName || entry.displayName || "";

          return `<div class="leaderboard-row" style="top:${i * rowHeight}px;transition:top 400ms ease" data-athlete="${entry.espnAthleteId}">
            <span class="standings-position">P${pos}</span>
            <div class="leaderboard-team-color" style="background:${teamColor}"></div>
            ${headshot}${flag}
            <span class="leaderboard-name">${name}</span>
            <span class="leaderboard-full-name">${fullName !== name ? fullName : ""}</span>
            <div class="leaderboard-spacer"></div>
            ${probBar}
          </div>`;
        })
        .join("")}
    </div>`;
  }
}

customElements.define("nb-race-leaderboard", NbRaceLeaderboard);
