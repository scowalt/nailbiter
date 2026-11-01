export class NbGameHighlights extends HTMLElement {
  constructor() {
    super();
    this.load = this.load.bind(this);
  }

  connectedCallback() {
    this.button = this.querySelector("button");
    this.button?.addEventListener("click", this.load);
  }

  disconnectedCallback() {
    this.button?.removeEventListener("click", this.load);
  }

  fail() {
    this.button?.remove();
    if (this.querySelector("[role='status']")) return;
    const message = document.createElement("p");
    message.id = "highlights-player-error";
    message.setAttribute("role", "status");
    message.textContent =
      "The embedded player could not load. Try Watch on YouTube; regional restrictions may still apply.";
    this.append(message);
  }

  load() {
    if (this.querySelector("iframe")) return;
    const videoId = this.dataset.videoId;
    if (!/^[A-Za-z0-9_-]{11}$/.test(videoId ?? "")) {
      this.fail();
      return;
    }
    try {
      const iframe = document.createElement("iframe");
      iframe.id = "highlights-player";
      iframe.title = `Game highlights: ${this.closest("#game-highlights")?.querySelector("#highlights-title")?.textContent || "MLB recap"}`;
      iframe.width = "560";
      iframe.height = "315";
      iframe.referrerPolicy = "strict-origin-when-cross-origin";
      iframe.allow = "encrypted-media; picture-in-picture; fullscreen";
      iframe.allowFullscreen = true;
      iframe.addEventListener(
        "error",
        () => {
          iframe.remove();
          this.fail();
        },
        { once: true },
      );
      iframe.src = `https://www.youtube-nocookie.com/embed/${videoId}?autoplay=0&controls=1`;
      this.button?.remove();
      this.append(iframe);
      iframe.focus();
    } catch {
      this.querySelector("iframe")?.remove();
      this.fail();
    }
  }
}

customElements.define("nb-game-highlights", NbGameHighlights);
