export function installScheduleObservation() {
  window.readGroupedSchedule = (root = document) => [...root.querySelectorAll("#main-content section")].map((section) => ({
    heading: section.querySelector("h2")?.textContent,
    ids: [...section.querySelectorAll("a[href^='/game/']")].map((link) => link.getAttribute("href").slice(6)),
  }));
}

export function isNativeNavigationError(error) {
  return error.name === "InvalidStateError"
    && error.stack === ""
    && error.message === "Transition was aborted because of invalid state. ViewTransition opt-in disabled";
}
