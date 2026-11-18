// src/nb-game-highlights.js
class li extends HTMLElement {
  constructor() {
    super();
    this.load = this.load.bind(this);
  }
  connectedCallback() {
    this.button = this.querySelector("button"), this.button?.addEventListener("click", this.load);
  }
  disconnectedCallback() {
    this.button?.removeEventListener("click", this.load);
  }
  fail() {
    if (this.button?.remove(), this.querySelector("[role='status']"))
      return;
    let t = document.createElement("p");
    t.id = "highlights-player-error", t.setAttribute("role", "status"), t.textContent = "The embedded player could not load. Try Watch on YouTube; regional restrictions may still apply.", this.append(t);
  }
  load() {
    if (this.querySelector("iframe"))
      return;
    let t = this.dataset.videoId;
    if (!/^[A-Za-z0-9_-]{11}$/.test(t ?? "")) {
      this.fail();
      return;
    }
    try {
      let e = document.createElement("iframe");
      e.id = "highlights-player", e.title = `Game highlights: ${this.closest("#game-highlights")?.querySelector("#highlights-title")?.textContent || "MLB recap"}`, e.width = "560", e.height = "315", e.referrerPolicy = "strict-origin-when-cross-origin", e.allow = "encrypted-media; picture-in-picture; fullscreen", e.allowFullscreen = !0, e.addEventListener("error", () => {
        e.remove(), this.fail();
      }, { once: !0 }), e.src = `https://www.youtube-nocookie.com/embed/${t}?autoplay=0&controls=1`, this.button?.remove(), this.append(e), e.focus();
    } catch {
      this.querySelector("iframe")?.remove(), this.fail();
    }
  }
}
customElements.define("nb-game-highlights", li);
// src/nb-race-leaderboard.js
class ci extends HTMLElement {
  static observedAttributes = ["data"];
  connectedCallback() {
    this.render();
  }
  attributeChangedCallback() {
    this.render();
  }
  render() {
    let t = this.getAttribute("data");
    if (!t)
      return;
    let e = JSON.parse(t);
    if (!e || e.length === 0) {
      this.innerHTML = '<p class="leaderboard-empty">No leaderboard data</p>';
      return;
    }
    let i = 40, s = e.length * i;
    this.innerHTML = `<div style="position:relative;height:${s}px">
      ${e.map((n, o) => {
      let a = n.position ?? o + 1, r = n.teamColor || "#6b7280", l = n.winProbability, c = l != null ? `<div class="leaderboard-probability">
              <div class="standings-meter">
                <div class="leaderboard-meter-fill" style="width:${(l * 100).toFixed(1)}%;background:${r}"></div>
              </div>
              <span class="leaderboard-percent">${(l * 100).toFixed(1)}%</span>
            </div>` : "", h = n.headshotUrl ? `<img src="${n.headshotUrl}" alt="" class="leaderboard-headshot">` : "", d = n.countryFlagHref ? `<img src="${n.countryFlagHref}" alt="" class="standings-flag">` : "", u = n.abbreviation || n.displayName || n.fullName || "", f = n.fullName || n.displayName || "";
      return `<div class="leaderboard-row" style="top:${o * i}px;transition:top 400ms ease" data-athlete="${n.espnAthleteId}">
            <span class="standings-position">P${a}</span>
            <div class="leaderboard-team-color" style="background:${r}"></div>
            ${h}${d}
            <span class="leaderboard-name">${u}</span>
            <span class="leaderboard-full-name">${f !== u ? f : ""}</span>
            <div class="leaderboard-spacer"></div>
            ${c}
          </div>`;
    }).join("")}
    </div>`;
  }
}
customElements.define("nb-race-leaderboard", ci);
// src/nb-sparkline.js
var hi = /* @__PURE__ */ new Map;

class di extends HTMLElement {
  static observedAttributes = ["data", "color", "width", "height"];
  connectedCallback() {
    let t = this.getAttribute("game-id");
    if (t && !this.getAttribute("data"))
      this._observer = new IntersectionObserver((e) => {
        if (e[0].isIntersecting)
          this._observer.disconnect(), this._loadData(t);
      }, { rootMargin: "200px" }), this._observer.observe(this);
    else
      this.render();
  }
  disconnectedCallback() {
    if (this._observer)
      this._observer.disconnect();
  }
  attributeChangedCallback() {
    this.render();
  }
  async refresh() {
    let t = this.getAttribute("game-id");
    if (t)
      await this._loadData(t, !0);
  }
  async _loadData(t, e = !1) {
    if (this._loadPromise)
      return this._loadPromise;
    if (!e && hi.has(t)) {
      this.setAttribute("data", JSON.stringify(hi.get(t)));
      return;
    }
    return this._loadPromise = (async () => {
      try {
        let i = await fetch(`/api/games/${t}/odds`, {
          cache: "no-store"
        });
        if (!i.ok)
          return;
        let n = ((await i.json()).oddsHistory || []).map((o) => o.homeWinProbability);
        if (n.length >= 2)
          hi.set(t, n), this.setAttribute("data", JSON.stringify(n));
      } catch {} finally {
        this._loadPromise = null;
      }
    })(), this._loadPromise;
  }
  render() {
    let t = this.getAttribute("data");
    if (!t) {
      this.innerHTML = "";
      return;
    }
    let e = JSON.parse(t);
    if (!e || e.length < 2) {
      this.innerHTML = "";
      return;
    }
    let i = parseInt(this.getAttribute("width") || "80", 10), s = parseInt(this.getAttribute("height") || "24", 10), n = this.getAttribute("color") || "#3b82f6", o = 2, r = `M${e.map((l, c) => {
      let h = Math.min(1, Math.max(0, l)), d = o + c / (e.length - 1) * (i - 2 * o), u = s - o - h * (s - 2 * o);
      return `${d},${u}`;
    }).join("L")}`;
    this.innerHTML = `<svg width="${i}" height="${s}" viewBox="0 0 ${i} ${s}" role="img" aria-label="Win probability trend" style="display:block">
      <path d="${r}" fill="none" stroke="${n}" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/>
    </svg>`;
  }
}
customElements.define("nb-sparkline", di);
// node_modules/@kurkle/color/dist/color.esm.js
/*!
 * @kurkle/color v0.3.4
 * https://github.com/kurkle/color#readme
 * (c) 2024 Jukka Kurkela
 * Released under the MIT License
 */
function ne(t) {
  return t + 0.5 | 0;
}
var vt = (t, e, i) => Math.max(Math.min(t, i), e);
function se(t) {
  return vt(ne(t * 2.55), 0, 255);
}
function Mt(t) {
  return vt(ne(t * 255), 0, 255);
}
function ft(t) {
  return vt(ne(t / 2.55) / 100, 0, 1);
}
function xs(t) {
  return vt(ne(t * 100), 0, 100);
}
var it = { 0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 5, 6: 6, 7: 7, 8: 8, 9: 9, A: 10, B: 11, C: 12, D: 13, E: 14, F: 15, a: 10, b: 11, c: 12, d: 13, e: 14, f: 15 }, fi = [..."0123456789ABCDEF"], ia = (t) => fi[t & 15], sa = (t) => fi[(t & 240) >> 4] + fi[t & 15], Pe = (t) => (t & 240) >> 4 === (t & 15), na = (t) => Pe(t.r) && Pe(t.g) && Pe(t.b) && Pe(t.a);
function oa(t) {
  var e = t.length, i;
  if (t[0] === "#") {
    if (e === 4 || e === 5)
      i = {
        r: 255 & it[t[1]] * 17,
        g: 255 & it[t[2]] * 17,
        b: 255 & it[t[3]] * 17,
        a: e === 5 ? it[t[4]] * 17 : 255
      };
    else if (e === 7 || e === 9)
      i = {
        r: it[t[1]] << 4 | it[t[2]],
        g: it[t[3]] << 4 | it[t[4]],
        b: it[t[5]] << 4 | it[t[6]],
        a: e === 9 ? it[t[7]] << 4 | it[t[8]] : 255
      };
  }
  return i;
}
var aa = (t, e) => t < 255 ? e(t) : "";
function ra(t) {
  var e = na(t) ? ia : sa;
  return t ? "#" + e(t.r) + e(t.g) + e(t.b) + aa(t.a, e) : void 0;
}
var la = /^(hsla?|hwb|hsv)\(\s*([-+.e\d]+)(?:deg)?[\s,]+([-+.e\d]+)%[\s,]+([-+.e\d]+)%(?:[\s,]+([-+.e\d]+)(%)?)?\s*\)$/;
function Ms(t, e, i) {
  let s = e * Math.min(i, 1 - i), n = (o, a = (o + t / 30) % 12) => i - s * Math.max(Math.min(a - 3, 9 - a, 1), -1);
  return [n(0), n(8), n(4)];
}
function ca(t, e, i) {
  let s = (n, o = (n + t / 60) % 6) => i - i * e * Math.max(Math.min(o, 4 - o, 1), 0);
  return [s(5), s(3), s(1)];
}
function ha(t, e, i) {
  let s = Ms(t, 1, 0.5), n;
  if (e + i > 1)
    n = 1 / (e + i), e *= n, i *= n;
  for (n = 0;n < 3; n++)
    s[n] *= 1 - e - i, s[n] += e;
  return s;
}
function da(t, e, i, s, n) {
  if (t === n)
    return (e - i) / s + (e < i ? 6 : 0);
  if (e === n)
    return (i - t) / s + 2;
  return (t - e) / s + 4;
}
function gi(t) {
  let i = t.r / 255, s = t.g / 255, n = t.b / 255, o = Math.max(i, s, n), a = Math.min(i, s, n), r = (o + a) / 2, l, c, h;
  if (o !== a)
    h = o - a, c = r > 0.5 ? h / (2 - o - a) : h / (o + a), l = da(i, s, n, h, o), l = l * 60 + 0.5;
  return [l | 0, c || 0, r];
}
function pi(t, e, i, s) {
  return (Array.isArray(e) ? t(e[0], e[1], e[2]) : t(e, i, s)).map(Mt);
}
function mi(t, e, i) {
  return pi(Ms, t, e, i);
}
function ua(t, e, i) {
  return pi(ha, t, e, i);
}
function fa(t, e, i) {
  return pi(ca, t, e, i);
}
function ks(t) {
  return (t % 360 + 360) % 360;
}
function ga(t) {
  let e = la.exec(t), i = 255, s;
  if (!e)
    return;
  if (e[5] !== s)
    i = e[6] ? se(+e[5]) : Mt(+e[5]);
  let n = ks(+e[2]), o = +e[3] / 100, a = +e[4] / 100;
  if (e[1] === "hwb")
    s = ua(n, o, a);
  else if (e[1] === "hsv")
    s = fa(n, o, a);
  else
    s = mi(n, o, a);
  return {
    r: s[0],
    g: s[1],
    b: s[2],
    a: i
  };
}
function pa(t, e) {
  var i = gi(t);
  i[0] = ks(i[0] + e), i = mi(i), t.r = i[0], t.g = i[1], t.b = i[2];
}
function ma(t) {
  if (!t)
    return;
  let e = gi(t), i = e[0], s = xs(e[1]), n = xs(e[2]);
  return t.a < 255 ? `hsla(${i}, ${s}%, ${n}%, ${ft(t.a)})` : `hsl(${i}, ${s}%, ${n}%)`;
}
var _s = {
  x: "dark",
  Z: "light",
  Y: "re",
  X: "blu",
  W: "gr",
  V: "medium",
  U: "slate",
  A: "ee",
  T: "ol",
  S: "or",
  B: "ra",
  C: "lateg",
  D: "ights",
  R: "in",
  Q: "turquois",
  E: "hi",
  P: "ro",
  O: "al",
  N: "le",
  M: "de",
  L: "yello",
  F: "en",
  K: "ch",
  G: "arks",
  H: "ea",
  I: "ightg",
  J: "wh"
}, ys = {
  OiceXe: "f0f8ff",
  antiquewEte: "faebd7",
  aqua: "ffff",
  aquamarRe: "7fffd4",
  azuY: "f0ffff",
  beige: "f5f5dc",
  bisque: "ffe4c4",
  black: "0",
  blanKedOmond: "ffebcd",
  Xe: "ff",
  XeviTet: "8a2be2",
  bPwn: "a52a2a",
  burlywood: "deb887",
  caMtXe: "5f9ea0",
  KartYuse: "7fff00",
  KocTate: "d2691e",
  cSO: "ff7f50",
  cSnflowerXe: "6495ed",
  cSnsilk: "fff8dc",
  crimson: "dc143c",
  cyan: "ffff",
  xXe: "8b",
  xcyan: "8b8b",
  xgTMnPd: "b8860b",
  xWay: "a9a9a9",
  xgYF: "6400",
  xgYy: "a9a9a9",
  xkhaki: "bdb76b",
  xmagFta: "8b008b",
  xTivegYF: "556b2f",
  xSange: "ff8c00",
  xScEd: "9932cc",
  xYd: "8b0000",
  xsOmon: "e9967a",
  xsHgYF: "8fbc8f",
  xUXe: "483d8b",
  xUWay: "2f4f4f",
  xUgYy: "2f4f4f",
  xQe: "ced1",
  xviTet: "9400d3",
  dAppRk: "ff1493",
  dApskyXe: "bfff",
  dimWay: "696969",
  dimgYy: "696969",
  dodgerXe: "1e90ff",
  fiYbrick: "b22222",
  flSOwEte: "fffaf0",
  foYstWAn: "228b22",
  fuKsia: "ff00ff",
  gaRsbSo: "dcdcdc",
  ghostwEte: "f8f8ff",
  gTd: "ffd700",
  gTMnPd: "daa520",
  Way: "808080",
  gYF: "8000",
  gYFLw: "adff2f",
  gYy: "808080",
  honeyMw: "f0fff0",
  hotpRk: "ff69b4",
  RdianYd: "cd5c5c",
  Rdigo: "4b0082",
  ivSy: "fffff0",
  khaki: "f0e68c",
  lavFMr: "e6e6fa",
  lavFMrXsh: "fff0f5",
  lawngYF: "7cfc00",
  NmoncEffon: "fffacd",
  ZXe: "add8e6",
  ZcSO: "f08080",
  Zcyan: "e0ffff",
  ZgTMnPdLw: "fafad2",
  ZWay: "d3d3d3",
  ZgYF: "90ee90",
  ZgYy: "d3d3d3",
  ZpRk: "ffb6c1",
  ZsOmon: "ffa07a",
  ZsHgYF: "20b2aa",
  ZskyXe: "87cefa",
  ZUWay: "778899",
  ZUgYy: "778899",
  ZstAlXe: "b0c4de",
  ZLw: "ffffe0",
  lime: "ff00",
  limegYF: "32cd32",
  lRF: "faf0e6",
  magFta: "ff00ff",
  maPon: "800000",
  VaquamarRe: "66cdaa",
  VXe: "cd",
  VScEd: "ba55d3",
  VpurpN: "9370db",
  VsHgYF: "3cb371",
  VUXe: "7b68ee",
  VsprRggYF: "fa9a",
  VQe: "48d1cc",
  VviTetYd: "c71585",
  midnightXe: "191970",
  mRtcYam: "f5fffa",
  mistyPse: "ffe4e1",
  moccasR: "ffe4b5",
  navajowEte: "ffdead",
  navy: "80",
  Tdlace: "fdf5e6",
  Tive: "808000",
  TivedBb: "6b8e23",
  Sange: "ffa500",
  SangeYd: "ff4500",
  ScEd: "da70d6",
  pOegTMnPd: "eee8aa",
  pOegYF: "98fb98",
  pOeQe: "afeeee",
  pOeviTetYd: "db7093",
  papayawEp: "ffefd5",
  pHKpuff: "ffdab9",
  peru: "cd853f",
  pRk: "ffc0cb",
  plum: "dda0dd",
  powMrXe: "b0e0e6",
  purpN: "800080",
  YbeccapurpN: "663399",
  Yd: "ff0000",
  Psybrown: "bc8f8f",
  PyOXe: "4169e1",
  saddNbPwn: "8b4513",
  sOmon: "fa8072",
  sandybPwn: "f4a460",
  sHgYF: "2e8b57",
  sHshell: "fff5ee",
  siFna: "a0522d",
  silver: "c0c0c0",
  skyXe: "87ceeb",
  UXe: "6a5acd",
  UWay: "708090",
  UgYy: "708090",
  snow: "fffafa",
  sprRggYF: "ff7f",
  stAlXe: "4682b4",
  tan: "d2b48c",
  teO: "8080",
  tEstN: "d8bfd8",
  tomato: "ff6347",
  Qe: "40e0d0",
  viTet: "ee82ee",
  JHt: "f5deb3",
  wEte: "ffffff",
  wEtesmoke: "f5f5f5",
  Lw: "ffff00",
  LwgYF: "9acd32"
};
function ba() {
  let t = {}, e = Object.keys(ys), i = Object.keys(_s), s, n, o, a, r;
  for (s = 0;s < e.length; s++) {
    a = r = e[s];
    for (n = 0;n < i.length; n++)
      o = i[n], r = r.replace(o, _s[o]);
    o = parseInt(ys[a], 16), t[r] = [o >> 16 & 255, o >> 8 & 255, o & 255];
  }
  return t;
}
var De;
function xa(t) {
  if (!De)
    De = ba(), De.transparent = [0, 0, 0, 0];
  let e = De[t.toLowerCase()];
  return e && {
    r: e[0],
    g: e[1],
    b: e[2],
    a: e.length === 4 ? e[3] : 255
  };
}
var _a = /^rgba?\(\s*([-+.\d]+)(%)?[\s,]+([-+.e\d]+)(%)?[\s,]+([-+.e\d]+)(%)?(?:[\s,/]+([-+.e\d]+)(%)?)?\s*\)$/;
function ya(t) {
  let e = _a.exec(t), i = 255, s, n, o;
  if (!e)
    return;
  if (e[7] !== s) {
    let a = +e[7];
    i = e[8] ? se(a) : vt(a * 255, 0, 255);
  }
  return s = +e[1], n = +e[3], o = +e[5], s = 255 & (e[2] ? se(s) : vt(s, 0, 255)), n = 255 & (e[4] ? se(n) : vt(n, 0, 255)), o = 255 & (e[6] ? se(o) : vt(o, 0, 255)), {
    r: s,
    g: n,
    b: o,
    a: i
  };
}
function va(t) {
  return t && (t.a < 255 ? `rgba(${t.r}, ${t.g}, ${t.b}, ${ft(t.a)})` : `rgb(${t.r}, ${t.g}, ${t.b})`);
}
var ui = (t) => t <= 0.0031308 ? t * 12.92 : Math.pow(t, 0.4166666666666667) * 1.055 - 0.055, Ht = (t) => t <= 0.04045 ? t / 12.92 : Math.pow((t + 0.055) / 1.055, 2.4);
function Ma(t, e, i) {
  let s = Ht(ft(t.r)), n = Ht(ft(t.g)), o = Ht(ft(t.b));
  return {
    r: Mt(ui(s + i * (Ht(ft(e.r)) - s))),
    g: Mt(ui(n + i * (Ht(ft(e.g)) - n))),
    b: Mt(ui(o + i * (Ht(ft(e.b)) - o))),
    a: t.a + i * (e.a - t.a)
  };
}
function Ae(t, e, i) {
  if (t) {
    let s = gi(t);
    s[e] = Math.max(0, Math.min(s[e] + s[e] * i, e === 0 ? 360 : 1)), s = mi(s), t.r = s[0], t.g = s[1], t.b = s[2];
  }
}
function ws(t, e) {
  return t ? Object.assign(e || {}, t) : t;
}
function vs(t) {
  var e = { r: 0, g: 0, b: 0, a: 255 };
  if (Array.isArray(t)) {
    if (t.length >= 3) {
      if (e = { r: t[0], g: t[1], b: t[2], a: 255 }, t.length > 3)
        e.a = Mt(t[3]);
    }
  } else
    e = ws(t, { r: 0, g: 0, b: 0, a: 1 }), e.a = Mt(e.a);
  return e;
}
function ka(t) {
  if (t.charAt(0) === "r")
    return ya(t);
  return ga(t);
}

class jt {
  constructor(t) {
    if (t instanceof jt)
      return t;
    let e = typeof t, i;
    if (e === "object")
      i = vs(t);
    else if (e === "string")
      i = oa(t) || xa(t) || ka(t);
    this._rgb = i, this._valid = !!i;
  }
  get valid() {
    return this._valid;
  }
  get rgb() {
    var t = ws(this._rgb);
    if (t)
      t.a = ft(t.a);
    return t;
  }
  set rgb(t) {
    this._rgb = vs(t);
  }
  rgbString() {
    return this._valid ? va(this._rgb) : void 0;
  }
  hexString() {
    return this._valid ? ra(this._rgb) : void 0;
  }
  hslString() {
    return this._valid ? ma(this._rgb) : void 0;
  }
  mix(t, e) {
    if (t) {
      let i = this.rgb, s = t.rgb, n, o = e === n ? 0.5 : e, a = 2 * o - 1, r = i.a - s.a, l = ((a * r === -1 ? a : (a + r) / (1 + a * r)) + 1) / 2;
      n = 1 - l, i.r = 255 & l * i.r + n * s.r + 0.5, i.g = 255 & l * i.g + n * s.g + 0.5, i.b = 255 & l * i.b + n * s.b + 0.5, i.a = o * i.a + (1 - o) * s.a, this.rgb = i;
    }
    return this;
  }
  interpolate(t, e) {
    if (t)
      this._rgb = Ma(this._rgb, t._rgb, e);
    return this;
  }
  clone() {
    return new jt(this.rgb);
  }
  alpha(t) {
    return this._rgb.a = Mt(t), this;
  }
  clearer(t) {
    let e = this._rgb;
    return e.a *= 1 - t, this;
  }
  greyscale() {
    let t = this._rgb, e = ne(t.r * 0.3 + t.g * 0.59 + t.b * 0.11);
    return t.r = t.g = t.b = e, this;
  }
  opaquer(t) {
    let e = this._rgb;
    return e.a *= 1 + t, this;
  }
  negate() {
    let t = this._rgb;
    return t.r = 255 - t.r, t.g = 255 - t.g, t.b = 255 - t.b, this;
  }
  lighten(t) {
    return Ae(this._rgb, 2, t), this;
  }
  darken(t) {
    return Ae(this._rgb, 2, -t), this;
  }
  saturate(t) {
    return Ae(this._rgb, 1, t), this;
  }
  desaturate(t) {
    return Ae(this._rgb, 1, -t), this;
  }
  rotate(t) {
    return pa(this._rgb, t), this;
  }
}

// node_modules/chart.js/dist/chunks/helpers.dataset.js
/*!
 * Chart.js v4.5.1
 * https://www.chartjs.org
 * (c) 2025 Chart.js Contributors
 * Released under the MIT License
 */
function lt() {}
var Is = (() => {
  let t = 0;
  return () => t++;
})();
function A(t) {
  return t === null || t === void 0;
}
function E(t) {
  if (Array.isArray && Array.isArray(t))
    return !0;
  let e = Object.prototype.toString.call(t);
  if (e.slice(0, 7) === "[object" && e.slice(-6) === "Array]")
    return !0;
  return !1;
}
function O(t) {
  return t !== null && Object.prototype.toString.call(t) === "[object Object]";
}
function W(t) {
  return (typeof t === "number" || t instanceof Number) && isFinite(+t);
}
function J(t, e) {
  return W(t) ? t : e;
}
function P(t, e) {
  return typeof t > "u" ? e : t;
}
var Fs = (t, e) => typeof t === "string" && t.endsWith("%") ? parseFloat(t) / 100 : +t / e, _i = (t, e) => typeof t === "string" && t.endsWith("%") ? parseFloat(t) / 100 * e : +t;
function R(t, e, i) {
  if (t && typeof t.call === "function")
    return t.apply(i, e);
}
function L(t, e, i, s) {
  let n, o, a;
  if (E(t))
    if (o = t.length, s)
      for (n = o - 1;n >= 0; n--)
        e.call(i, t[n], n);
    else
      for (n = 0;n < o; n++)
        e.call(i, t[n], n);
  else if (O(t)) {
    a = Object.keys(t), o = a.length;
    for (n = 0;n < o; n++)
      e.call(i, t[a[n]], a[n]);
  }
}
function re(t, e) {
  let i, s, n, o;
  if (!t || !e || t.length !== e.length)
    return !1;
  for (i = 0, s = t.length;i < s; ++i)
    if (n = t[i], o = e[i], n.datasetIndex !== o.datasetIndex || n.index !== o.index)
      return !1;
  return !0;
}
function Le(t) {
  if (E(t))
    return t.map(Le);
  if (O(t)) {
    let e = Object.create(null), i = Object.keys(t), s = i.length, n = 0;
    for (;n < s; ++n)
      e[i[n]] = Le(t[i[n]]);
    return e;
  }
  return t;
}
function zs(t) {
  return [
    "__proto__",
    "prototype",
    "constructor"
  ].indexOf(t) === -1;
}
function wa(t, e, i, s) {
  if (!zs(t))
    return;
  let n = e[t], o = i[t];
  if (O(n) && O(o))
    Yt(n, o, s);
  else
    e[t] = Le(o);
}
function Yt(t, e, i) {
  let s = E(e) ? e : [
    e
  ], n = s.length;
  if (!O(t))
    return t;
  i = i || {};
  let o = i.merger || wa, a;
  for (let r = 0;r < n; ++r) {
    if (a = s[r], !O(a))
      continue;
    let l = Object.keys(a);
    for (let c = 0, h = l.length;c < h; ++c)
      o(l[c], t, a, i);
  }
  return t;
}
function Xt(t, e) {
  return Yt(t, e, {
    merger: Sa
  });
}
function Sa(t, e, i) {
  if (!zs(t))
    return;
  let s = e[t], n = i[t];
  if (O(s) && O(n))
    Xt(s, n);
  else if (!Object.prototype.hasOwnProperty.call(e, t))
    e[t] = Le(n);
}
var Ss = {
  "": (t) => t,
  x: (t) => t.x,
  y: (t) => t.y
};
function Pa(t) {
  let e = t.split("."), i = [], s = "";
  for (let n of e)
    if (s += n, s.endsWith("\\"))
      s = s.slice(0, -1) + ".";
    else
      i.push(s), s = "";
  return i;
}
function Da(t) {
  let e = Pa(t);
  return (i) => {
    for (let s of e) {
      if (s === "")
        break;
      i = i && i[s];
    }
    return i;
  };
}
function mt(t, e) {
  return (Ss[e] || (Ss[e] = Da(e)))(t);
}
function Fe(t) {
  return t.charAt(0).toUpperCase() + t.slice(1);
}
var Kt = (t) => typeof t < "u", gt = (t) => typeof t === "function", yi = (t, e) => {
  if (t.size !== e.size)
    return !1;
  for (let i of t)
    if (!e.has(i))
      return !1;
  return !0;
};
function Bs(t) {
  return t.type === "mouseup" || t.type === "click" || t.type === "contextmenu";
}
var T = Math.PI, I = 2 * T, Aa = I + T, Re = Number.POSITIVE_INFINITY, Ca = T / 180, N = T / 2, Rt = T / 4, Ps = T * 2 / 3, { log10: pt, sign: ot } = Math;
function qt(t, e, i) {
  return Math.abs(t - e) < i;
}
function vi(t) {
  let e = Math.round(t);
  t = qt(t, e, t / 1000) ? e : t;
  let i = Math.pow(10, Math.floor(pt(t))), s = t / i;
  return (s <= 1 ? 1 : s <= 2 ? 2 : s <= 5 ? 5 : 10) * i;
}
function Ws(t) {
  let e = [], i = Math.sqrt(t), s;
  for (s = 1;s < i; s++)
    if (t % s === 0)
      e.push(s), e.push(t / s);
  if (i === (i | 0))
    e.push(i);
  return e.sort((n, o) => n - o).pop(), e;
}
function Oa(t) {
  return typeof t === "symbol" || typeof t === "object" && t !== null && !((Symbol.toPrimitive in t) || ("toString" in t) || ("valueOf" in t));
}
function Ft(t) {
  return !Oa(t) && !isNaN(parseFloat(t)) && isFinite(t);
}
function Ns(t, e) {
  let i = Math.round(t);
  return i - e <= t && i + e >= t;
}
function Mi(t, e, i) {
  let s, n, o;
  for (s = 0, n = t.length;s < n; s++)
    if (o = t[s][i], !isNaN(o))
      e.min = Math.min(e.min, o), e.max = Math.max(e.max, o);
}
function st(t) {
  return t * (T / 180);
}
function ze(t) {
  return t * (180 / T);
}
function ki(t) {
  if (!W(t))
    return;
  let e = 1, i = 0;
  while (Math.round(t * e) / e !== t)
    e *= 10, i++;
  return i;
}
function wi(t, e) {
  let i = e.x - t.x, s = e.y - t.y, n = Math.sqrt(i * i + s * s), o = Math.atan2(s, i);
  if (o < -0.5 * T)
    o += I;
  return {
    angle: o,
    distance: n
  };
}
function Ee(t, e) {
  return Math.sqrt(Math.pow(e.x - t.x, 2) + Math.pow(e.y - t.y, 2));
}
function Ta(t, e) {
  return (t - e + Aa) % I - T;
}
function X(t) {
  return (t % I + I) % I;
}
function Gt(t, e, i, s) {
  let n = X(t), o = X(e), a = X(i), r = X(o - n), l = X(a - n), c = X(n - o), h = X(n - a);
  return n === o || n === a || s && o === a || r > l && c < h;
}
function Y(t, e, i) {
  return Math.max(e, Math.min(i, t));
}
function Vs(t) {
  return Y(t, -32768, 32767);
}
function ct(t, e, i, s = 0.000001) {
  return t >= Math.min(e, i) - s && t <= Math.max(e, i) + s;
}
function Be(t, e, i) {
  i = i || ((a) => t[a] < e);
  let s = t.length - 1, n = 0, o;
  while (s - n > 1)
    if (o = n + s >> 1, i(o))
      n = o;
    else
      s = o;
  return {
    lo: n,
    hi: s
  };
}
var at = (t, e, i, s) => Be(t, i, s ? (n) => {
  let o = t[n][e];
  return o < i || o === i && t[n + 1][e] === i;
} : (n) => t[n][e] < i), Hs = (t, e, i) => Be(t, i, (s) => t[s][e] >= i);
function js(t, e, i) {
  let s = 0, n = t.length;
  while (s < n && t[s] < e)
    s++;
  while (n > s && t[n - 1] > i)
    n--;
  return s > 0 || n < t.length ? t.slice(s, n) : t;
}
var $s = [
  "push",
  "pop",
  "shift",
  "splice",
  "unshift"
];
function Ys(t, e) {
  if (t._chartjs) {
    t._chartjs.listeners.push(e);
    return;
  }
  Object.defineProperty(t, "_chartjs", {
    configurable: !0,
    enumerable: !1,
    value: {
      listeners: [
        e
      ]
    }
  }), $s.forEach((i) => {
    let s = "_onData" + Fe(i), n = t[i];
    Object.defineProperty(t, i, {
      configurable: !0,
      enumerable: !1,
      value(...o) {
        let a = n.apply(this, o);
        return t._chartjs.listeners.forEach((r) => {
          if (typeof r[s] === "function")
            r[s](...o);
        }), a;
      }
    });
  });
}
function Si(t, e) {
  let i = t._chartjs;
  if (!i)
    return;
  let s = i.listeners, n = s.indexOf(e);
  if (n !== -1)
    s.splice(n, 1);
  if (s.length > 0)
    return;
  $s.forEach((o) => {
    delete t[o];
  }), delete t._chartjs;
}
function Pi(t) {
  let e = new Set(t);
  if (e.size === t.length)
    return t;
  return Array.from(e);
}
var Di = function() {
  if (typeof window > "u")
    return function(t) {
      return t();
    };
  return window.requestAnimationFrame;
}();
function Ai(t, e) {
  let i = [], s = !1;
  return function(...n) {
    if (i = n, !s)
      s = !0, Di.call(window, () => {
        s = !1, t.apply(e, i);
      });
  };
}
function Us(t, e) {
  let i;
  return function(...s) {
    if (e)
      clearTimeout(i), i = setTimeout(t, e, s);
    else
      t.apply(this, s);
    return e;
  };
}
var We = (t) => t === "start" ? "left" : t === "end" ? "right" : "center", K = (t, e, i) => t === "start" ? e : t === "end" ? i : (e + i) / 2, Xs = (t, e, i, s) => t === (s ? "left" : "right") ? i : t === "center" ? (e + i) / 2 : e;
function Ci(t, e, i) {
  let s = e.length, n = 0, o = s;
  if (t._sorted) {
    let { iScale: a, vScale: r, _parsed: l } = t, c = t.dataset ? t.dataset.options ? t.dataset.options.spanGaps : null : null, h = a.axis, { min: d, max: u, minDefined: f, maxDefined: m } = a.getUserBounds();
    if (f) {
      if (n = Math.min(at(l, h, d).lo, i ? s : at(e, h, a.getPixelForValue(d)).lo), c) {
        let g = l.slice(0, n + 1).reverse().findIndex((p) => !A(p[r.axis]));
        n -= Math.max(0, g);
      }
      n = Y(n, 0, s - 1);
    }
    if (m) {
      let g = Math.max(at(l, a.axis, u, !0).hi + 1, i ? 0 : at(e, h, a.getPixelForValue(u), !0).hi + 1);
      if (c) {
        let p = l.slice(g - 1).findIndex((b) => !A(b[r.axis]));
        g += Math.max(0, p);
      }
      o = Y(g, n, s) - n;
    } else
      o = s - n;
  }
  return {
    start: n,
    count: o
  };
}
function Oi(t) {
  let { xScale: e, yScale: i, _scaleRanges: s } = t, n = {
    xmin: e.min,
    xmax: e.max,
    ymin: i.min,
    ymax: i.max
  };
  if (!s)
    return t._scaleRanges = n, !0;
  let o = s.xmin !== e.min || s.xmax !== e.max || s.ymin !== i.min || s.ymax !== i.max;
  return Object.assign(s, n), o;
}
var Ce = (t) => t === 0 || t === 1, Ds = (t, e, i) => -(Math.pow(2, 10 * (t -= 1)) * Math.sin((t - e) * I / i)), As = (t, e, i) => Math.pow(2, -10 * t) * Math.sin((t - e) * I / i) + 1, $t = {
  linear: (t) => t,
  easeInQuad: (t) => t * t,
  easeOutQuad: (t) => -t * (t - 2),
  easeInOutQuad: (t) => (t /= 0.5) < 1 ? 0.5 * t * t : -0.5 * (--t * (t - 2) - 1),
  easeInCubic: (t) => t * t * t,
  easeOutCubic: (t) => (t -= 1) * t * t + 1,
  easeInOutCubic: (t) => (t /= 0.5) < 1 ? 0.5 * t * t * t : 0.5 * ((t -= 2) * t * t + 2),
  easeInQuart: (t) => t * t * t * t,
  easeOutQuart: (t) => -((t -= 1) * t * t * t - 1),
  easeInOutQuart: (t) => (t /= 0.5) < 1 ? 0.5 * t * t * t * t : -0.5 * ((t -= 2) * t * t * t - 2),
  easeInQuint: (t) => t * t * t * t * t,
  easeOutQuint: (t) => (t -= 1) * t * t * t * t + 1,
  easeInOutQuint: (t) => (t /= 0.5) < 1 ? 0.5 * t * t * t * t * t : 0.5 * ((t -= 2) * t * t * t * t + 2),
  easeInSine: (t) => -Math.cos(t * N) + 1,
  easeOutSine: (t) => Math.sin(t * N),
  easeInOutSine: (t) => -0.5 * (Math.cos(T * t) - 1),
  easeInExpo: (t) => t === 0 ? 0 : Math.pow(2, 10 * (t - 1)),
  easeOutExpo: (t) => t === 1 ? 1 : -Math.pow(2, -10 * t) + 1,
  easeInOutExpo: (t) => Ce(t) ? t : t < 0.5 ? 0.5 * Math.pow(2, 10 * (t * 2 - 1)) : 0.5 * (-Math.pow(2, -10 * (t * 2 - 1)) + 2),
  easeInCirc: (t) => t >= 1 ? t : -(Math.sqrt(1 - t * t) - 1),
  easeOutCirc: (t) => Math.sqrt(1 - (t -= 1) * t),
  easeInOutCirc: (t) => (t /= 0.5) < 1 ? -0.5 * (Math.sqrt(1 - t * t) - 1) : 0.5 * (Math.sqrt(1 - (t -= 2) * t) + 1),
  easeInElastic: (t) => Ce(t) ? t : Ds(t, 0.075, 0.3),
  easeOutElastic: (t) => Ce(t) ? t : As(t, 0.075, 0.3),
  easeInOutElastic(t) {
    return Ce(t) ? t : t < 0.5 ? 0.5 * Ds(t * 2, 0.1125, 0.45) : 0.5 + 0.5 * As(t * 2 - 1, 0.1125, 0.45);
  },
  easeInBack(t) {
    return t * t * (2.70158 * t - 1.70158);
  },
  easeOutBack(t) {
    return (t -= 1) * t * (2.70158 * t + 1.70158) + 1;
  },
  easeInOutBack(t) {
    let e = 1.70158;
    if ((t /= 0.5) < 1)
      return 0.5 * (t * t * (((e *= 1.525) + 1) * t - e));
    return 0.5 * ((t -= 2) * t * (((e *= 1.525) + 1) * t + e) + 2);
  },
  easeInBounce: (t) => 1 - $t.easeOutBounce(1 - t),
  easeOutBounce(t) {
    if (t < 0.36363636363636365)
      return 7.5625 * t * t;
    if (t < 0.7272727272727273)
      return 7.5625 * (t -= 0.5454545454545454) * t + 0.75;
    if (t < 0.9090909090909091)
      return 7.5625 * (t -= 0.8181818181818182) * t + 0.9375;
    return 7.5625 * (t -= 0.9545454545454546) * t + 0.984375;
  },
  easeInOutBounce: (t) => t < 0.5 ? $t.easeInBounce(t * 2) * 0.5 : $t.easeOutBounce(t * 2 - 1) * 0.5 + 0.5
};
function Ti(t) {
  if (t && typeof t === "object") {
    let e = t.toString();
    return e === "[object CanvasPattern]" || e === "[object CanvasGradient]";
  }
  return !1;
}
function Li(t) {
  return Ti(t) ? t : new jt(t);
}
function bi(t) {
  return Ti(t) ? t : new jt(t).saturate(0.5).darken(0.1).hexString();
}
var La = [
  "x",
  "y",
  "borderWidth",
  "radius",
  "tension"
], Ra = [
  "color",
  "borderColor",
  "backgroundColor"
];
function Ea(t) {
  t.set("animation", {
    delay: void 0,
    duration: 1000,
    easing: "easeOutQuart",
    fn: void 0,
    from: void 0,
    loop: void 0,
    to: void 0,
    type: void 0
  }), t.describe("animation", {
    _fallback: !1,
    _indexable: !1,
    _scriptable: (e) => e !== "onProgress" && e !== "onComplete" && e !== "fn"
  }), t.set("animations", {
    colors: {
      type: "color",
      properties: Ra
    },
    numbers: {
      type: "number",
      properties: La
    }
  }), t.describe("animations", {
    _fallback: "animation"
  }), t.set("transitions", {
    active: {
      animation: {
        duration: 400
      }
    },
    resize: {
      animation: {
        duration: 0
      }
    },
    show: {
      animations: {
        colors: {
          from: "transparent"
        },
        visible: {
          type: "boolean",
          duration: 0
        }
      }
    },
    hide: {
      animations: {
        colors: {
          to: "transparent"
        },
        visible: {
          type: "boolean",
          easing: "linear",
          fn: (e) => e | 0
        }
      }
    }
  });
}
function Ia(t) {
  t.set("layout", {
    autoPadding: !0,
    padding: {
      top: 0,
      right: 0,
      bottom: 0,
      left: 0
    }
  });
}
var Cs = /* @__PURE__ */ new Map;
function Fa(t, e) {
  e = e || {};
  let i = t + JSON.stringify(e), s = Cs.get(i);
  if (!s)
    s = new Intl.NumberFormat(t, e), Cs.set(i, s);
  return s;
}
function Jt(t, e, i) {
  return Fa(e, i).format(t);
}
var Ks = {
  values(t) {
    return E(t) ? t : "" + t;
  },
  numeric(t, e, i) {
    if (t === 0)
      return "0";
    let s = this.chart.options.locale, n, o = t;
    if (i.length > 1) {
      let c = Math.max(Math.abs(i[0].value), Math.abs(i[i.length - 1].value));
      if (c < 0.0001 || c > 1000000000000000)
        n = "scientific";
      o = za(t, i);
    }
    let a = pt(Math.abs(o)), r = isNaN(a) ? 1 : Math.max(Math.min(-1 * Math.floor(a), 20), 0), l = {
      notation: n,
      minimumFractionDigits: r,
      maximumFractionDigits: r
    };
    return Object.assign(l, this.options.ticks.format), Jt(t, s, l);
  },
  logarithmic(t, e, i) {
    if (t === 0)
      return "0";
    let s = i[e].significand || t / Math.pow(10, Math.floor(pt(t)));
    if ([
      1,
      2,
      3,
      5,
      10,
      15
    ].includes(s) || e > 0.8 * i.length)
      return Ks.numeric.call(this, t, e, i);
    return "";
  }
};
function za(t, e) {
  let i = e.length > 3 ? e[2].value - e[1].value : e[1].value - e[0].value;
  if (Math.abs(i) >= 1 && t !== Math.floor(t))
    i = t - Math.floor(t);
  return i;
}
var le = {
  formatters: Ks
};
function Ba(t) {
  t.set("scale", {
    display: !0,
    offset: !1,
    reverse: !1,
    beginAtZero: !1,
    bounds: "ticks",
    clip: !0,
    grace: 0,
    grid: {
      display: !0,
      lineWidth: 1,
      drawOnChartArea: !0,
      drawTicks: !0,
      tickLength: 8,
      tickWidth: (e, i) => i.lineWidth,
      tickColor: (e, i) => i.color,
      offset: !1
    },
    border: {
      display: !0,
      dash: [],
      dashOffset: 0,
      width: 1
    },
    title: {
      display: !1,
      text: "",
      padding: {
        top: 4,
        bottom: 4
      }
    },
    ticks: {
      minRotation: 0,
      maxRotation: 50,
      mirror: !1,
      textStrokeWidth: 0,
      textStrokeColor: "",
      padding: 3,
      display: !0,
      autoSkip: !0,
      autoSkipPadding: 3,
      labelOffset: 0,
      callback: le.formatters.values,
      minor: {},
      major: {},
      align: "center",
      crossAlign: "near",
      showLabelBackdrop: !1,
      backdropColor: "rgba(255, 255, 255, 0.75)",
      backdropPadding: 2
    }
  }), t.route("scale.ticks", "color", "", "color"), t.route("scale.grid", "color", "", "borderColor"), t.route("scale.border", "color", "", "borderColor"), t.route("scale.title", "color", "", "color"), t.describe("scale", {
    _fallback: !1,
    _scriptable: (e) => !e.startsWith("before") && !e.startsWith("after") && e !== "callback" && e !== "parser",
    _indexable: (e) => e !== "borderDash" && e !== "tickBorderDash" && e !== "dash"
  }), t.describe("scales", {
    _fallback: "scale"
  }), t.describe("scale.ticks", {
    _scriptable: (e) => e !== "backdropPadding" && e !== "callback",
    _indexable: (e) => e !== "backdropPadding"
  });
}
var St = Object.create(null), Ne = Object.create(null);
function oe(t, e) {
  if (!e)
    return t;
  let i = e.split(".");
  for (let s = 0, n = i.length;s < n; ++s) {
    let o = i[s];
    t = t[o] || (t[o] = Object.create(null));
  }
  return t;
}
function xi(t, e, i) {
  if (typeof e === "string")
    return Yt(oe(t, e), i);
  return Yt(oe(t, ""), e);
}

class qs {
  constructor(t, e) {
    this.animation = void 0, this.backgroundColor = "rgba(0,0,0,0.1)", this.borderColor = "rgba(0,0,0,0.1)", this.color = "#666", this.datasets = {}, this.devicePixelRatio = (i) => i.chart.platform.getDevicePixelRatio(), this.elements = {}, this.events = [
      "mousemove",
      "mouseout",
      "click",
      "touchstart",
      "touchmove"
    ], this.font = {
      family: "'Helvetica Neue', 'Helvetica', 'Arial', sans-serif",
      size: 12,
      style: "normal",
      lineHeight: 1.2,
      weight: null
    }, this.hover = {}, this.hoverBackgroundColor = (i, s) => bi(s.backgroundColor), this.hoverBorderColor = (i, s) => bi(s.borderColor), this.hoverColor = (i, s) => bi(s.color), this.indexAxis = "x", this.interaction = {
      mode: "nearest",
      intersect: !0,
      includeInvisible: !1
    }, this.maintainAspectRatio = !0, this.onHover = null, this.onClick = null, this.parsing = !0, this.plugins = {}, this.responsive = !0, this.scale = void 0, this.scales = {}, this.showLine = !0, this.drawActiveElementsOnTop = !0, this.describe(t), this.apply(e);
  }
  set(t, e) {
    return xi(this, t, e);
  }
  get(t) {
    return oe(this, t);
  }
  describe(t, e) {
    return xi(Ne, t, e);
  }
  override(t, e) {
    return xi(St, t, e);
  }
  route(t, e, i, s) {
    let n = oe(this, t), o = oe(this, i), a = "_" + e;
    Object.defineProperties(n, {
      [a]: {
        value: n[e],
        writable: !0
      },
      [e]: {
        enumerable: !0,
        get() {
          let r = this[a], l = o[s];
          if (O(r))
            return Object.assign({}, l, r);
          return P(r, l);
        },
        set(r) {
          this[a] = r;
        }
      }
    });
  }
  apply(t) {
    t.forEach((e) => e(this));
  }
}
var F = /* @__PURE__ */ new qs({
  _scriptable: (t) => !t.startsWith("on"),
  _indexable: (t) => t !== "events",
  hover: {
    _fallback: "interaction"
  },
  interaction: {
    _scriptable: !1,
    _indexable: !1
  }
}, [
  Ea,
  Ia,
  Ba
]);
function Wa(t) {
  if (!t || A(t.size) || A(t.family))
    return null;
  return (t.style ? t.style + " " : "") + (t.weight ? t.weight + " " : "") + t.size + "px " + t.family;
}
function ae(t, e, i, s, n) {
  let o = e[n];
  if (!o)
    o = e[n] = t.measureText(n).width, i.push(n);
  if (o > s)
    s = o;
  return s;
}
function Gs(t, e, i, s) {
  s = s || {};
  let n = s.data = s.data || {}, o = s.garbageCollect = s.garbageCollect || [];
  if (s.font !== e)
    n = s.data = {}, o = s.garbageCollect = [], s.font = e;
  t.save(), t.font = e;
  let a = 0, r = i.length, l, c, h, d, u;
  for (l = 0;l < r; l++)
    if (d = i[l], d !== void 0 && d !== null && !E(d))
      a = ae(t, n, o, a, d);
    else if (E(d)) {
      for (c = 0, h = d.length;c < h; c++)
        if (u = d[c], u !== void 0 && u !== null && !E(u))
          a = ae(t, n, o, a, u);
    }
  t.restore();
  let f = o.length / 2;
  if (f > i.length) {
    for (l = 0;l < f; l++)
      delete n[o[l]];
    o.splice(0, f);
  }
  return a;
}
function Pt(t, e, i) {
  let s = t.currentDevicePixelRatio, n = i !== 0 ? Math.max(i / 2, 0.5) : 0;
  return Math.round((e - n) * s) / s + n;
}
function Ri(t, e) {
  if (!e && !t)
    return;
  e = e || t.getContext("2d"), e.save(), e.resetTransform(), e.clearRect(0, 0, t.width, t.height), e.restore();
}
function Ve(t, e, i, s) {
  Ei(t, e, i, s, null);
}
function Ei(t, e, i, s, n) {
  let o, a, r, l, c, h, d, u, { pointStyle: f, rotation: m, radius: g } = e, p = (m || 0) * Ca;
  if (f && typeof f === "object") {
    if (o = f.toString(), o === "[object HTMLImageElement]" || o === "[object HTMLCanvasElement]") {
      t.save(), t.translate(i, s), t.rotate(p), t.drawImage(f, -f.width / 2, -f.height / 2, f.width, f.height), t.restore();
      return;
    }
  }
  if (isNaN(g) || g <= 0)
    return;
  switch (t.beginPath(), f) {
    default:
      if (n)
        t.ellipse(i, s, n / 2, g, 0, 0, I);
      else
        t.arc(i, s, g, 0, I);
      t.closePath();
      break;
    case "triangle":
      h = n ? n / 2 : g, t.moveTo(i + Math.sin(p) * h, s - Math.cos(p) * g), p += Ps, t.lineTo(i + Math.sin(p) * h, s - Math.cos(p) * g), p += Ps, t.lineTo(i + Math.sin(p) * h, s - Math.cos(p) * g), t.closePath();
      break;
    case "rectRounded":
      c = g * 0.516, l = g - c, a = Math.cos(p + Rt) * l, d = Math.cos(p + Rt) * (n ? n / 2 - c : l), r = Math.sin(p + Rt) * l, u = Math.sin(p + Rt) * (n ? n / 2 - c : l), t.arc(i - d, s - r, c, p - T, p - N), t.arc(i + u, s - a, c, p - N, p), t.arc(i + d, s + r, c, p, p + N), t.arc(i - u, s + a, c, p + N, p + T), t.closePath();
      break;
    case "rect":
      if (!m) {
        l = Math.SQRT1_2 * g, h = n ? n / 2 : l, t.rect(i - h, s - l, 2 * h, 2 * l);
        break;
      }
      p += Rt;
    case "rectRot":
      d = Math.cos(p) * (n ? n / 2 : g), a = Math.cos(p) * g, r = Math.sin(p) * g, u = Math.sin(p) * (n ? n / 2 : g), t.moveTo(i - d, s - r), t.lineTo(i + u, s - a), t.lineTo(i + d, s + r), t.lineTo(i - u, s + a), t.closePath();
      break;
    case "crossRot":
      p += Rt;
    case "cross":
      d = Math.cos(p) * (n ? n / 2 : g), a = Math.cos(p) * g, r = Math.sin(p) * g, u = Math.sin(p) * (n ? n / 2 : g), t.moveTo(i - d, s - r), t.lineTo(i + d, s + r), t.moveTo(i + u, s - a), t.lineTo(i - u, s + a);
      break;
    case "star":
      d = Math.cos(p) * (n ? n / 2 : g), a = Math.cos(p) * g, r = Math.sin(p) * g, u = Math.sin(p) * (n ? n / 2 : g), t.moveTo(i - d, s - r), t.lineTo(i + d, s + r), t.moveTo(i + u, s - a), t.lineTo(i - u, s + a), p += Rt, d = Math.cos(p) * (n ? n / 2 : g), a = Math.cos(p) * g, r = Math.sin(p) * g, u = Math.sin(p) * (n ? n / 2 : g), t.moveTo(i - d, s - r), t.lineTo(i + d, s + r), t.moveTo(i + u, s - a), t.lineTo(i - u, s + a);
      break;
    case "line":
      a = n ? n / 2 : Math.cos(p) * g, r = Math.sin(p) * g, t.moveTo(i - a, s - r), t.lineTo(i + a, s + r);
      break;
    case "dash":
      t.moveTo(i, s), t.lineTo(i + Math.cos(p) * (n ? n / 2 : g), s + Math.sin(p) * g);
      break;
    case !1:
      t.closePath();
      break;
  }
  if (t.fill(), e.borderWidth > 0)
    t.stroke();
}
function rt(t, e, i) {
  return i = i || 0.5, !e || t && t.x > e.left - i && t.x < e.right + i && t.y > e.top - i && t.y < e.bottom + i;
}
function ce(t, e) {
  t.save(), t.beginPath(), t.rect(e.left, e.top, e.right - e.left, e.bottom - e.top), t.clip();
}
function he(t) {
  t.restore();
}
function Js(t, e, i, s, n) {
  if (!e)
    return t.lineTo(i.x, i.y);
  if (n === "middle") {
    let o = (e.x + i.x) / 2;
    t.lineTo(o, e.y), t.lineTo(o, i.y);
  } else if (n === "after" !== !!s)
    t.lineTo(e.x, i.y);
  else
    t.lineTo(i.x, e.y);
  t.lineTo(i.x, i.y);
}
function Zs(t, e, i, s) {
  if (!e)
    return t.lineTo(i.x, i.y);
  t.bezierCurveTo(s ? e.cp1x : e.cp2x, s ? e.cp1y : e.cp2y, s ? i.cp2x : i.cp1x, s ? i.cp2y : i.cp1y, i.x, i.y);
}
function Na(t, e) {
  if (e.translation)
    t.translate(e.translation[0], e.translation[1]);
  if (!A(e.rotation))
    t.rotate(e.rotation);
  if (e.color)
    t.fillStyle = e.color;
  if (e.textAlign)
    t.textAlign = e.textAlign;
  if (e.textBaseline)
    t.textBaseline = e.textBaseline;
}
function Va(t, e, i, s, n) {
  if (n.strikethrough || n.underline) {
    let o = t.measureText(s), a = e - o.actualBoundingBoxLeft, r = e + o.actualBoundingBoxRight, l = i - o.actualBoundingBoxAscent, c = i + o.actualBoundingBoxDescent, h = n.strikethrough ? (l + c) / 2 : c;
    t.strokeStyle = t.fillStyle, t.beginPath(), t.lineWidth = n.decorationWidth || 2, t.moveTo(a, h), t.lineTo(r, h), t.stroke();
  }
}
function Ha(t, e) {
  let i = t.fillStyle;
  t.fillStyle = e.color, t.fillRect(e.left, e.top, e.width, e.height), t.fillStyle = i;
}
function Dt(t, e, i, s, n, o = {}) {
  let a = E(e) ? e : [
    e
  ], r = o.strokeWidth > 0 && o.strokeColor !== "", l, c;
  t.save(), t.font = n.string, Na(t, o);
  for (l = 0;l < a.length; ++l) {
    if (c = a[l], o.backdrop)
      Ha(t, o.backdrop);
    if (r) {
      if (o.strokeColor)
        t.strokeStyle = o.strokeColor;
      if (!A(o.strokeWidth))
        t.lineWidth = o.strokeWidth;
      t.strokeText(c, i, s, o.maxWidth);
    }
    t.fillText(c, i, s, o.maxWidth), Va(t, i, s, c, o), s += Number(n.lineHeight);
  }
  t.restore();
}
function Zt(t, e) {
  let { x: i, y: s, w: n, h: o, radius: a } = e;
  t.arc(i + a.topLeft, s + a.topLeft, a.topLeft, 1.5 * T, T, !0), t.lineTo(i, s + o - a.bottomLeft), t.arc(i + a.bottomLeft, s + o - a.bottomLeft, a.bottomLeft, T, N, !0), t.lineTo(i + n - a.bottomRight, s + o), t.arc(i + n - a.bottomRight, s + o - a.bottomRight, a.bottomRight, N, 0, !0), t.lineTo(i + n, s + a.topRight), t.arc(i + n - a.topRight, s + a.topRight, a.topRight, 0, -N, !0), t.lineTo(i + a.topLeft, s);
}
var ja = /^(normal|(\d+(?:\.\d+)?)(px|em|%)?)$/, $a = /^(normal|italic|initial|inherit|unset|(oblique( -?[0-9]?[0-9]deg)?))$/;
function Ya(t, e) {
  let i = ("" + t).match(ja);
  if (!i || i[1] === "normal")
    return e * 1.2;
  switch (t = +i[2], i[3]) {
    case "px":
      return t;
    case "%":
      t /= 100;
      break;
  }
  return e * t;
}
var Ua = (t) => +t || 0;
function He(t, e) {
  let i = {}, s = O(e), n = s ? Object.keys(e) : e, o = O(t) ? s ? (a) => P(t[a], t[e[a]]) : (a) => t[a] : () => t;
  for (let a of n)
    i[a] = Ua(o(a));
  return i;
}
function Ii(t) {
  return He(t, {
    top: "y",
    right: "x",
    bottom: "y",
    left: "x"
  });
}
function At(t) {
  return He(t, [
    "topLeft",
    "topRight",
    "bottomLeft",
    "bottomRight"
  ]);
}
function q(t) {
  let e = Ii(t);
  return e.width = e.left + e.right, e.height = e.top + e.bottom, e;
}
function H(t, e) {
  t = t || {}, e = e || F.font;
  let i = P(t.size, e.size);
  if (typeof i === "string")
    i = parseInt(i, 10);
  let s = P(t.style, e.style);
  if (s && !("" + s).match($a))
    console.warn('Invalid font style specified: "' + s + '"'), s = void 0;
  let n = {
    family: P(t.family, e.family),
    lineHeight: Ya(P(t.lineHeight, e.lineHeight), i),
    size: i,
    style: s,
    weight: P(t.weight, e.weight),
    string: ""
  };
  return n.string = Wa(n), n;
}
function Qt(t, e, i, s) {
  let n = !0, o, a, r;
  for (o = 0, a = t.length;o < a; ++o) {
    if (r = t[o], r === void 0)
      continue;
    if (e !== void 0 && typeof r === "function")
      r = r(e), n = !1;
    if (i !== void 0 && E(r))
      r = r[i % r.length], n = !1;
    if (r !== void 0) {
      if (s && !n)
        s.cacheable = !1;
      return r;
    }
  }
}
function Qs(t, e, i) {
  let { min: s, max: n } = t, o = _i(e, (n - s) / 2), a = (r, l) => i && r === 0 ? 0 : r + l;
  return {
    min: a(s, -Math.abs(o)),
    max: a(n, o)
  };
}
function bt(t, e) {
  return Object.assign(Object.create(t), e);
}
function je(t, e = [
  ""
], i, s, n = () => t[0]) {
  let o = i || t;
  if (typeof s > "u")
    s = sn("_fallback", t);
  let a = {
    [Symbol.toStringTag]: "Object",
    _cacheable: !0,
    _scopes: t,
    _rootScopes: o,
    _fallback: s,
    _getTarget: n,
    override: (r) => je([
      r,
      ...t
    ], e, o, s)
  };
  return new Proxy(a, {
    deleteProperty(r, l) {
      return delete r[l], delete r._keys, delete t[0][l], !0;
    },
    get(r, l) {
      return tn(r, l, () => tr(l, e, t, r));
    },
    getOwnPropertyDescriptor(r, l) {
      return Reflect.getOwnPropertyDescriptor(r._scopes[0], l);
    },
    getPrototypeOf() {
      return Reflect.getPrototypeOf(t[0]);
    },
    has(r, l) {
      return Ts(r).includes(l);
    },
    ownKeys(r) {
      return Ts(r);
    },
    set(r, l, c) {
      let h = r._storage || (r._storage = n());
      return r[l] = h[l] = c, delete r._keys, !0;
    }
  });
}
function It(t, e, i, s) {
  let n = {
    _cacheable: !1,
    _proxy: t,
    _context: e,
    _subProxy: i,
    _stack: /* @__PURE__ */ new Set,
    _descriptors: Fi(t, s),
    setContext: (o) => It(t, o, i, s),
    override: (o) => It(t.override(o), e, i, s)
  };
  return new Proxy(n, {
    deleteProperty(o, a) {
      return delete o[a], delete t[a], !0;
    },
    get(o, a, r) {
      return tn(o, a, () => Ka(o, a, r));
    },
    getOwnPropertyDescriptor(o, a) {
      return o._descriptors.allKeys ? Reflect.has(t, a) ? {
        enumerable: !0,
        configurable: !0
      } : void 0 : Reflect.getOwnPropertyDescriptor(t, a);
    },
    getPrototypeOf() {
      return Reflect.getPrototypeOf(t);
    },
    has(o, a) {
      return Reflect.has(t, a);
    },
    ownKeys() {
      return Reflect.ownKeys(t);
    },
    set(o, a, r) {
      return t[a] = r, delete o[a], !0;
    }
  });
}
function Fi(t, e = {
  scriptable: !0,
  indexable: !0
}) {
  let { _scriptable: i = e.scriptable, _indexable: s = e.indexable, _allKeys: n = e.allKeys } = t;
  return {
    allKeys: n,
    scriptable: i,
    indexable: s,
    isScriptable: gt(i) ? i : () => i,
    isIndexable: gt(s) ? s : () => s
  };
}
var Xa = (t, e) => t ? t + Fe(e) : e, zi = (t, e) => O(e) && t !== "adapters" && (Object.getPrototypeOf(e) === null || e.constructor === Object);
function tn(t, e, i) {
  if (Object.prototype.hasOwnProperty.call(t, e) || e === "constructor")
    return t[e];
  let s = i();
  return t[e] = s, s;
}
function Ka(t, e, i) {
  let { _proxy: s, _context: n, _subProxy: o, _descriptors: a } = t, r = s[e];
  if (gt(r) && a.isScriptable(e))
    r = qa(e, r, t, i);
  if (E(r) && r.length)
    r = Ga(e, r, t, a.isIndexable);
  if (zi(e, r))
    r = It(r, n, o && o[e], a);
  return r;
}
function qa(t, e, i, s) {
  let { _proxy: n, _context: o, _subProxy: a, _stack: r } = i;
  if (r.has(t))
    throw Error("Recursion detected: " + Array.from(r).join("->") + "->" + t);
  r.add(t);
  let l = e(o, a || s);
  if (r.delete(t), zi(t, l))
    l = Bi(n._scopes, n, t, l);
  return l;
}
function Ga(t, e, i, s) {
  let { _proxy: n, _context: o, _subProxy: a, _descriptors: r } = i;
  if (typeof o.index < "u" && s(t))
    return e[o.index % e.length];
  else if (O(e[0])) {
    let l = e, c = n._scopes.filter((h) => h !== l);
    e = [];
    for (let h of l) {
      let d = Bi(c, n, t, h);
      e.push(It(d, o, a && a[t], r));
    }
  }
  return e;
}
function en(t, e, i) {
  return gt(t) ? t(e, i) : t;
}
var Ja = (t, e) => t === !0 ? e : typeof t === "string" ? mt(e, t) : void 0;
function Za(t, e, i, s, n) {
  for (let o of e) {
    let a = Ja(i, o);
    if (a) {
      t.add(a);
      let r = en(a._fallback, i, n);
      if (typeof r < "u" && r !== i && r !== s)
        return r;
    } else if (a === !1 && typeof s < "u" && i !== s)
      return null;
  }
  return !1;
}
function Bi(t, e, i, s) {
  let n = e._rootScopes, o = en(e._fallback, i, s), a = [
    ...t,
    ...n
  ], r = /* @__PURE__ */ new Set;
  r.add(s);
  let l = Os(r, a, i, o || i, s);
  if (l === null)
    return !1;
  if (typeof o < "u" && o !== i) {
    if (l = Os(r, a, o, l, s), l === null)
      return !1;
  }
  return je(Array.from(r), [
    ""
  ], n, o, () => Qa(e, i, s));
}
function Os(t, e, i, s, n) {
  while (i)
    i = Za(t, e, i, s, n);
  return i;
}
function Qa(t, e, i) {
  let s = t._getTarget();
  if (!(e in s))
    s[e] = {};
  let n = s[e];
  if (E(n) && O(i))
    return i;
  return n || {};
}
function tr(t, e, i, s) {
  let n;
  for (let o of e)
    if (n = sn(Xa(o, t), i), typeof n < "u")
      return zi(t, n) ? Bi(i, s, t, n) : n;
}
function sn(t, e) {
  for (let i of e) {
    if (!i)
      continue;
    let s = i[t];
    if (typeof s < "u")
      return s;
  }
}
function Ts(t) {
  let e = t._keys;
  if (!e)
    e = t._keys = er(t._scopes);
  return e;
}
function er(t) {
  let e = /* @__PURE__ */ new Set;
  for (let i of t)
    for (let s of Object.keys(i).filter((n) => !n.startsWith("_")))
      e.add(s);
  return Array.from(e);
}
function Wi(t, e, i, s) {
  let { iScale: n } = t, { key: o = "r" } = this._parsing, a = Array(s), r, l, c, h;
  for (r = 0, l = s;r < l; ++r)
    c = r + i, h = e[c], a[r] = {
      r: n.parse(mt(h, o), c)
    };
  return a;
}
var ir = Number.EPSILON || 0.00000000000001, Ut = (t, e) => e < t.length && !t[e].skip && t[e], nn = (t) => t === "x" ? "y" : "x";
function sr(t, e, i, s) {
  let n = t.skip ? e : t, o = e, a = i.skip ? e : i, r = Ee(o, n), l = Ee(a, o), c = r / (r + l), h = l / (r + l);
  c = isNaN(c) ? 0 : c, h = isNaN(h) ? 0 : h;
  let d = s * c, u = s * h;
  return {
    previous: {
      x: o.x - d * (a.x - n.x),
      y: o.y - d * (a.y - n.y)
    },
    next: {
      x: o.x + u * (a.x - n.x),
      y: o.y + u * (a.y - n.y)
    }
  };
}
function nr(t, e, i) {
  let s = t.length, n, o, a, r, l, c = Ut(t, 0);
  for (let h = 0;h < s - 1; ++h) {
    if (l = c, c = Ut(t, h + 1), !l || !c)
      continue;
    if (qt(e[h], 0, ir)) {
      i[h] = i[h + 1] = 0;
      continue;
    }
    if (n = i[h] / e[h], o = i[h + 1] / e[h], r = Math.pow(n, 2) + Math.pow(o, 2), r <= 9)
      continue;
    a = 3 / Math.sqrt(r), i[h] = n * a * e[h], i[h + 1] = o * a * e[h];
  }
}
function or(t, e, i = "x") {
  let s = nn(i), n = t.length, o, a, r, l = Ut(t, 0);
  for (let c = 0;c < n; ++c) {
    if (a = r, r = l, l = Ut(t, c + 1), !r)
      continue;
    let h = r[i], d = r[s];
    if (a)
      o = (h - a[i]) / 3, r[`cp1${i}`] = h - o, r[`cp1${s}`] = d - o * e[c];
    if (l)
      o = (l[i] - h) / 3, r[`cp2${i}`] = h + o, r[`cp2${s}`] = d + o * e[c];
  }
}
function ar(t, e = "x") {
  let i = nn(e), s = t.length, n = Array(s).fill(0), o = Array(s), a, r, l, c = Ut(t, 0);
  for (a = 0;a < s; ++a) {
    if (r = l, l = c, c = Ut(t, a + 1), !l)
      continue;
    if (c) {
      let h = c[e] - l[e];
      n[a] = h !== 0 ? (c[i] - l[i]) / h : 0;
    }
    o[a] = !r ? n[a] : !c ? n[a - 1] : ot(n[a - 1]) !== ot(n[a]) ? 0 : (n[a - 1] + n[a]) / 2;
  }
  nr(t, n, o), or(t, o, e);
}
function Oe(t, e, i) {
  return Math.max(Math.min(t, i), e);
}
function rr(t, e) {
  let i, s, n, o, a, r = rt(t[0], e);
  for (i = 0, s = t.length;i < s; ++i) {
    if (a = o, o = r, r = i < s - 1 && rt(t[i + 1], e), !o)
      continue;
    if (n = t[i], a)
      n.cp1x = Oe(n.cp1x, e.left, e.right), n.cp1y = Oe(n.cp1y, e.top, e.bottom);
    if (r)
      n.cp2x = Oe(n.cp2x, e.left, e.right), n.cp2y = Oe(n.cp2y, e.top, e.bottom);
  }
}
function on(t, e, i, s, n) {
  let o, a, r, l;
  if (e.spanGaps)
    t = t.filter((c) => !c.skip);
  if (e.cubicInterpolationMode === "monotone")
    ar(t, n);
  else {
    let c = s ? t[t.length - 1] : t[0];
    for (o = 0, a = t.length;o < a; ++o)
      r = t[o], l = sr(c, r, t[Math.min(o + 1, a - (s ? 0 : 1)) % a], e.tension), r.cp1x = l.previous.x, r.cp1y = l.previous.y, r.cp2x = l.next.x, r.cp2y = l.next.y, c = r;
  }
  if (e.capBezierPoints)
    rr(t, i);
}
function $e() {
  return typeof window < "u" && typeof document < "u";
}
function Ye(t) {
  let e = t.parentNode;
  if (e && e.toString() === "[object ShadowRoot]")
    e = e.host;
  return e;
}
function Ie(t, e, i) {
  let s;
  if (typeof t === "string") {
    if (s = parseInt(t, 10), t.indexOf("%") !== -1)
      s = s / 100 * e.parentNode[i];
  } else
    s = t;
  return s;
}
var Ue = (t) => t.ownerDocument.defaultView.getComputedStyle(t, null);
function lr(t, e) {
  return Ue(t).getPropertyValue(e);
}
var cr = [
  "top",
  "right",
  "bottom",
  "left"
];
function Et(t, e, i) {
  let s = {};
  i = i ? "-" + i : "";
  for (let n = 0;n < 4; n++) {
    let o = cr[n];
    s[o] = parseFloat(t[e + "-" + o + i]) || 0;
  }
  return s.width = s.left + s.right, s.height = s.top + s.bottom, s;
}
var hr = (t, e, i) => (t > 0 || e > 0) && (!i || !i.shadowRoot);
function dr(t, e) {
  let i = t.touches, s = i && i.length ? i[0] : t, { offsetX: n, offsetY: o } = s, a = !1, r, l;
  if (hr(n, o, t.target))
    r = n, l = o;
  else {
    let c = e.getBoundingClientRect();
    r = s.clientX - c.left, l = s.clientY - c.top, a = !0;
  }
  return {
    x: r,
    y: l,
    box: a
  };
}
function Ct(t, e) {
  if ("native" in t)
    return t;
  let { canvas: i, currentDevicePixelRatio: s } = e, n = Ue(i), o = n.boxSizing === "border-box", a = Et(n, "padding"), r = Et(n, "border", "width"), { x: l, y: c, box: h } = dr(t, i), d = a.left + (h && r.left), u = a.top + (h && r.top), { width: f, height: m } = e;
  if (o)
    f -= a.width + r.width, m -= a.height + r.height;
  return {
    x: Math.round((l - d) / f * i.width / s),
    y: Math.round((c - u) / m * i.height / s)
  };
}
function ur(t, e, i) {
  let s, n;
  if (e === void 0 || i === void 0) {
    let o = t && Ye(t);
    if (!o)
      e = t.clientWidth, i = t.clientHeight;
    else {
      let a = o.getBoundingClientRect(), r = Ue(o), l = Et(r, "border", "width"), c = Et(r, "padding");
      e = a.width - c.width - l.width, i = a.height - c.height - l.height, s = Ie(r.maxWidth, o, "clientWidth"), n = Ie(r.maxHeight, o, "clientHeight");
    }
  }
  return {
    width: e,
    height: i,
    maxWidth: s || Re,
    maxHeight: n || Re
  };
}
var wt = (t) => Math.round(t * 10) / 10;
function an(t, e, i, s) {
  let n = Ue(t), o = Et(n, "margin"), a = Ie(n.maxWidth, t, "clientWidth") || Re, r = Ie(n.maxHeight, t, "clientHeight") || Re, l = ur(t, e, i), { width: c, height: h } = l;
  if (n.boxSizing === "content-box") {
    let u = Et(n, "border", "width"), f = Et(n, "padding");
    c -= f.width + u.width, h -= f.height + u.height;
  }
  if (c = Math.max(0, c - o.width), h = Math.max(0, s ? c / s : h - o.height), c = wt(Math.min(c, a, l.maxWidth)), h = wt(Math.min(h, r, l.maxHeight)), c && !h)
    h = wt(c / 2);
  if ((e !== void 0 || i !== void 0) && s && l.height && h > l.height)
    h = l.height, c = wt(Math.floor(h * s));
  return {
    width: c,
    height: h
  };
}
function Ni(t, e, i) {
  let s = e || 1, n = wt(t.height * s), o = wt(t.width * s);
  t.height = wt(t.height), t.width = wt(t.width);
  let a = t.canvas;
  if (a.style && (i || !a.style.height && !a.style.width))
    a.style.height = `${t.height}px`, a.style.width = `${t.width}px`;
  if (t.currentDevicePixelRatio !== s || a.height !== n || a.width !== o)
    return t.currentDevicePixelRatio = s, a.height = n, a.width = o, t.ctx.setTransform(s, 0, 0, s, 0, 0), !0;
  return !1;
}
var rn = function() {
  let t = !1;
  try {
    let e = {
      get passive() {
        return t = !0, !1;
      }
    };
    if ($e())
      window.addEventListener("test", null, e), window.removeEventListener("test", null, e);
  } catch (e) {}
  return t;
}();
function Vi(t, e) {
  let i = lr(t, e), s = i && i.match(/^(\d+)(\.\d+)?px$/);
  return s ? +s[1] : void 0;
}
function kt(t, e, i, s) {
  return {
    x: t.x + i * (e.x - t.x),
    y: t.y + i * (e.y - t.y)
  };
}
function ln(t, e, i, s) {
  return {
    x: t.x + i * (e.x - t.x),
    y: s === "middle" ? i < 0.5 ? t.y : e.y : s === "after" ? i < 1 ? t.y : e.y : i > 0 ? e.y : t.y
  };
}
function cn(t, e, i, s) {
  let n = {
    x: t.cp2x,
    y: t.cp2y
  }, o = {
    x: e.cp1x,
    y: e.cp1y
  }, a = kt(t, n, i), r = kt(n, o, i), l = kt(o, e, i), c = kt(a, r, i), h = kt(r, l, i);
  return kt(c, h, i);
}
var fr = function(t, e) {
  return {
    x(i) {
      return t + t + e - i;
    },
    setWidth(i) {
      e = i;
    },
    textAlign(i) {
      if (i === "center")
        return i;
      return i === "right" ? "left" : "right";
    },
    xPlus(i, s) {
      return i - s;
    },
    leftForLtr(i, s) {
      return i - s;
    }
  };
}, gr = function() {
  return {
    x(t) {
      return t;
    },
    setWidth(t) {},
    textAlign(t) {
      return t;
    },
    xPlus(t, e) {
      return t + e;
    },
    leftForLtr(t, e) {
      return t;
    }
  };
};
function zt(t, e, i) {
  return t ? fr(e, i) : gr();
}
function Hi(t, e) {
  let i, s;
  if (e === "ltr" || e === "rtl")
    i = t.canvas.style, s = [
      i.getPropertyValue("direction"),
      i.getPropertyPriority("direction")
    ], i.setProperty("direction", e, "important"), t.prevTextDirection = s;
}
function ji(t, e) {
  if (e !== void 0)
    delete t.prevTextDirection, t.canvas.style.setProperty("direction", e[0], e[1]);
}
function hn(t) {
  if (t === "angle")
    return {
      between: Gt,
      compare: Ta,
      normalize: X
    };
  return {
    between: ct,
    compare: (e, i) => e - i,
    normalize: (e) => e
  };
}
function Ls({ start: t, end: e, count: i, loop: s, style: n }) {
  return {
    start: t % i,
    end: e % i,
    loop: s && (e - t + 1) % i === 0,
    style: n
  };
}
function pr(t, e, i) {
  let { property: s, start: n, end: o } = i, { between: a, normalize: r } = hn(s), l = e.length, { start: c, end: h, loop: d } = t, u, f;
  if (d) {
    c += l, h += l;
    for (u = 0, f = l;u < f; ++u) {
      if (!a(r(e[c % l][s]), n, o))
        break;
      c--, h--;
    }
    c %= l, h %= l;
  }
  if (h < c)
    h += l;
  return {
    start: c,
    end: h,
    loop: d,
    style: t.style
  };
}
function $i(t, e, i) {
  if (!i)
    return [
      t
    ];
  let { property: s, start: n, end: o } = i, a = e.length, { compare: r, between: l, normalize: c } = hn(s), { start: h, end: d, loop: u, style: f } = pr(t, e, i), m = [], g = !1, p = null, b, x, v, y = () => l(n, v, b) && r(n, v) !== 0, _ = () => r(o, b) === 0 || l(o, v, b), M = () => g || y(), k = () => !g || _();
  for (let S = h, w = h;S <= d; ++S) {
    if (x = e[S % a], x.skip)
      continue;
    if (b = c(x[s]), b === v)
      continue;
    if (g = l(b, n, o), p === null && M())
      p = r(b, n) === 0 ? S : w;
    if (p !== null && k())
      m.push(Ls({
        start: p,
        end: S,
        loop: u,
        count: a,
        style: f
      })), p = null;
    w = S, v = b;
  }
  if (p !== null)
    m.push(Ls({
      start: p,
      end: d,
      loop: u,
      count: a,
      style: f
    }));
  return m;
}
function Yi(t, e) {
  let i = [], s = t.segments;
  for (let n = 0;n < s.length; n++) {
    let o = $i(s[n], t.points, e);
    if (o.length)
      i.push(...o);
  }
  return i;
}
function mr(t, e, i, s) {
  let n = 0, o = e - 1;
  if (i && !s)
    while (n < e && !t[n].skip)
      n++;
  while (n < e && t[n].skip)
    n++;
  if (n %= e, i)
    o += n;
  while (o > n && t[o % e].skip)
    o--;
  return o %= e, {
    start: n,
    end: o
  };
}
function br(t, e, i, s) {
  let n = t.length, o = [], a = e, r = t[e], l;
  for (l = e + 1;l <= i; ++l) {
    let c = t[l % n];
    if (c.skip || c.stop) {
      if (!r.skip)
        s = !1, o.push({
          start: e % n,
          end: (l - 1) % n,
          loop: s
        }), e = a = c.stop ? l : null;
    } else if (a = l, r.skip)
      e = l;
    r = c;
  }
  if (a !== null)
    o.push({
      start: e % n,
      end: a % n,
      loop: s
    });
  return o;
}
function dn(t, e) {
  let i = t.points, s = t.options.spanGaps, n = i.length;
  if (!n)
    return [];
  let o = !!t._loop, { start: a, end: r } = mr(i, n, o, s);
  if (s === !0)
    return Rs(t, [
      {
        start: a,
        end: r,
        loop: o
      }
    ], i, e);
  let l = r < a ? r + n : r, c = !!t._fullLoop && a === 0 && r === n - 1;
  return Rs(t, br(i, a, l, c), i, e);
}
function Rs(t, e, i, s) {
  if (!s || !s.setContext || !i)
    return e;
  return xr(t, e, i, s);
}
function xr(t, e, i, s) {
  let n = t._chart.getContext(), o = Es(t.options), { _datasetIndex: a, options: { spanGaps: r } } = t, l = i.length, c = [], h = o, d = e[0].start, u = d;
  function f(m, g, p, b) {
    let x = r ? -1 : 1;
    if (m === g)
      return;
    m += l;
    while (i[m % l].skip)
      m -= x;
    while (i[g % l].skip)
      g += x;
    if (m % l !== g % l)
      c.push({
        start: m % l,
        end: g % l,
        loop: p,
        style: b
      }), h = b, d = g % l;
  }
  for (let m of e) {
    d = r ? d : m.start;
    let g = i[d % l], p;
    for (u = d + 1;u <= m.end; u++) {
      let b = i[u % l];
      if (p = Es(s.setContext(bt(n, {
        type: "segment",
        p0: g,
        p1: b,
        p0DataIndex: (u - 1) % l,
        p1DataIndex: u % l,
        datasetIndex: a
      }))), _r(p, h))
        f(d, u - 1, m.loop, h);
      g = b, h = p;
    }
    if (d < u - 1)
      f(d, u - 1, m.loop, h);
  }
  return c;
}
function Es(t) {
  return {
    backgroundColor: t.backgroundColor,
    borderCapStyle: t.borderCapStyle,
    borderDash: t.borderDash,
    borderDashOffset: t.borderDashOffset,
    borderJoinStyle: t.borderJoinStyle,
    borderWidth: t.borderWidth,
    borderColor: t.borderColor
  };
}
function _r(t, e) {
  if (!e)
    return !1;
  let i = [], s = function(n, o) {
    if (!Ti(o))
      return o;
    if (!i.includes(o))
      i.push(o);
    return i.indexOf(o);
  };
  return JSON.stringify(t, s) !== JSON.stringify(e, s);
}
function Te(t, e, i) {
  return t.options.clip ? t[i] : e[i];
}
function yr(t, e) {
  let { xScale: i, yScale: s } = t;
  if (i && s)
    return {
      left: Te(i, e, "left"),
      right: Te(i, e, "right"),
      top: Te(s, e, "top"),
      bottom: Te(s, e, "bottom")
    };
  return e;
}
function Ui(t, e) {
  let i = e._clip;
  if (i.disabled)
    return !1;
  let s = yr(e, t.chartArea);
  return {
    left: i.left === !1 ? 0 : s.left - (i.left === !0 ? 0 : i.left),
    right: i.right === !1 ? t.width : s.right + (i.right === !0 ? 0 : i.right),
    top: i.top === !1 ? 0 : s.top - (i.top === !0 ? 0 : i.top),
    bottom: i.bottom === !1 ? t.height : s.bottom + (i.bottom === !0 ? 0 : i.bottom)
  };
}

// node_modules/chart.js/dist/chart.js
/*!
 * Chart.js v4.5.1
 * https://www.chartjs.org
 * (c) 2025 Chart.js Contributors
 * Released under the MIT License
 */

class uo {
  constructor() {
    this._request = null, this._charts = /* @__PURE__ */ new Map, this._running = !1, this._lastDate = void 0;
  }
  _notify(t, e, i, s) {
    let n = e.listeners[s], o = e.duration;
    n.forEach((a) => a({
      chart: t,
      initial: e.initial,
      numSteps: o,
      currentStep: Math.min(i - e.start, o)
    }));
  }
  _refresh() {
    if (this._request)
      return;
    this._running = !0, this._request = Di.call(window, () => {
      if (this._update(), this._request = null, this._running)
        this._refresh();
    });
  }
  _update(t = Date.now()) {
    let e = 0;
    if (this._charts.forEach((i, s) => {
      if (!i.running || !i.items.length)
        return;
      let n = i.items, o = n.length - 1, a = !1, r;
      for (;o >= 0; --o)
        if (r = n[o], r._active) {
          if (r._total > i.duration)
            i.duration = r._total;
          r.tick(t), a = !0;
        } else
          n[o] = n[n.length - 1], n.pop();
      if (a)
        s.draw(), this._notify(s, i, t, "progress");
      if (!n.length)
        i.running = !1, this._notify(s, i, t, "complete"), i.initial = !1;
      e += n.length;
    }), this._lastDate = t, e === 0)
      this._running = !1;
  }
  _getAnims(t) {
    let e = this._charts, i = e.get(t);
    if (!i)
      i = {
        running: !1,
        initial: !0,
        items: [],
        listeners: {
          complete: [],
          progress: []
        }
      }, e.set(t, i);
    return i;
  }
  listen(t, e, i) {
    this._getAnims(t).listeners[e].push(i);
  }
  add(t, e) {
    if (!e || !e.length)
      return;
    this._getAnims(t).items.push(...e);
  }
  has(t) {
    return this._getAnims(t).items.length > 0;
  }
  start(t) {
    let e = this._charts.get(t);
    if (!e)
      return;
    e.running = !0, e.start = Date.now(), e.duration = e.items.reduce((i, s) => Math.max(i, s._duration), 0), this._refresh();
  }
  running(t) {
    if (!this._running)
      return !1;
    let e = this._charts.get(t);
    if (!e || !e.running || !e.items.length)
      return !1;
    return !0;
  }
  stop(t) {
    let e = this._charts.get(t);
    if (!e || !e.items.length)
      return;
    let i = e.items, s = i.length - 1;
    for (;s >= 0; --s)
      i[s].cancel();
    e.items = [], this._notify(t, e, Date.now(), "complete");
  }
  remove(t) {
    return this._charts.delete(t);
  }
}
var xt = /* @__PURE__ */ new uo, un = "transparent", vr = {
  boolean(t, e, i) {
    return i > 0.5 ? e : t;
  },
  color(t, e, i) {
    let s = Li(t || un), n = s.valid && Li(e || un);
    return n && n.valid ? n.mix(s, i).hexString() : e;
  },
  number(t, e, i) {
    return t + (e - t) * i;
  }
};

class fo {
  constructor(t, e, i, s) {
    let n = e[i];
    s = Qt([
      t.to,
      s,
      n,
      t.from
    ]);
    let o = Qt([
      t.from,
      n,
      s
    ]);
    this._active = !0, this._fn = t.fn || vr[t.type || typeof o], this._easing = $t[t.easing] || $t.linear, this._start = Math.floor(Date.now() + (t.delay || 0)), this._duration = this._total = Math.floor(t.duration), this._loop = !!t.loop, this._target = e, this._prop = i, this._from = o, this._to = s, this._promises = void 0;
  }
  active() {
    return this._active;
  }
  update(t, e, i) {
    if (this._active) {
      this._notify(!1);
      let s = this._target[this._prop], n = i - this._start, o = this._duration - n;
      this._start = i, this._duration = Math.floor(Math.max(o, t.duration)), this._total += n, this._loop = !!t.loop, this._to = Qt([
        t.to,
        e,
        s,
        t.from
      ]), this._from = Qt([
        t.from,
        s,
        e
      ]);
    }
  }
  cancel() {
    if (this._active)
      this.tick(Date.now()), this._active = !1, this._notify(!1);
  }
  tick(t) {
    let e = t - this._start, i = this._duration, s = this._prop, n = this._from, o = this._loop, a = this._to, r;
    if (this._active = n !== a && (o || e < i), !this._active) {
      this._target[s] = a, this._notify(!0);
      return;
    }
    if (e < 0) {
      this._target[s] = n;
      return;
    }
    r = e / i % 2, r = o && r > 1 ? 2 - r : r, r = this._easing(Math.min(1, Math.max(0, r))), this._target[s] = this._fn(n, a, r);
  }
  wait() {
    let t = this._promises || (this._promises = []);
    return new Promise((e, i) => {
      t.push({
        res: e,
        rej: i
      });
    });
  }
  _notify(t) {
    let e = t ? "res" : "rej", i = this._promises || [];
    for (let s = 0;s < i.length; s++)
      i[s][e]();
  }
}

class us {
  constructor(t, e) {
    this._chart = t, this._properties = /* @__PURE__ */ new Map, this.configure(e);
  }
  configure(t) {
    if (!O(t))
      return;
    let e = Object.keys(F.animation), i = this._properties;
    Object.getOwnPropertyNames(t).forEach((s) => {
      let n = t[s];
      if (!O(n))
        return;
      let o = {};
      for (let a of e)
        o[a] = n[a];
      (E(n.properties) && n.properties || [
        s
      ]).forEach((a) => {
        if (a === s || !i.has(a))
          i.set(a, o);
      });
    });
  }
  _animateOptions(t, e) {
    let i = e.options, s = kr(t, i);
    if (!s)
      return [];
    let n = this._createAnimations(s, i);
    if (i.$shared)
      Mr(t.options.$animations, i).then(() => {
        t.options = i;
      }, () => {});
    return n;
  }
  _createAnimations(t, e) {
    let i = this._properties, s = [], n = t.$animations || (t.$animations = {}), o = Object.keys(e), a = Date.now(), r;
    for (r = o.length - 1;r >= 0; --r) {
      let l = o[r];
      if (l.charAt(0) === "$")
        continue;
      if (l === "options") {
        s.push(...this._animateOptions(t, e));
        continue;
      }
      let c = e[l], h = n[l], d = i.get(l);
      if (h)
        if (d && h.active()) {
          h.update(d, c, a);
          continue;
        } else
          h.cancel();
      if (!d || !d.duration) {
        t[l] = c;
        continue;
      }
      n[l] = h = new fo(d, t, l, c), s.push(h);
    }
    return s;
  }
  update(t, e) {
    if (this._properties.size === 0) {
      Object.assign(t, e);
      return;
    }
    let i = this._createAnimations(t, e);
    if (i.length)
      return xt.add(this._chart, i), !0;
  }
}
function Mr(t, e) {
  let i = [], s = Object.keys(e);
  for (let n = 0;n < s.length; n++) {
    let o = t[s[n]];
    if (o && o.active())
      i.push(o.wait());
  }
  return Promise.all(i);
}
function kr(t, e) {
  if (!e)
    return;
  let i = t.options;
  if (!i) {
    t.options = e;
    return;
  }
  if (i.$shared)
    t.options = i = Object.assign({}, i, {
      $shared: !1,
      $animations: {}
    });
  return i;
}
function fn(t, e) {
  let i = t && t.options || {}, s = i.reverse, n = i.min === void 0 ? e : 0, o = i.max === void 0 ? e : 0;
  return {
    start: s ? o : n,
    end: s ? n : o
  };
}
function wr(t, e, i) {
  if (i === !1)
    return !1;
  let s = fn(t, i), n = fn(e, i);
  return {
    top: n.end,
    right: s.end,
    bottom: n.start,
    left: s.start
  };
}
function Sr(t) {
  let e, i, s, n;
  if (O(t))
    e = t.top, i = t.right, s = t.bottom, n = t.left;
  else
    e = i = s = n = t;
  return {
    top: e,
    right: i,
    bottom: s,
    left: n,
    disabled: t === !1
  };
}
function go(t, e) {
  let i = [], s = t._getSortedDatasetMetas(e), n, o;
  for (n = 0, o = s.length;n < o; ++n)
    i.push(s[n].index);
  return i;
}
function gn(t, e, i, s = {}) {
  let n = t.keys, o = s.mode === "single", a, r, l, c;
  if (e === null)
    return;
  let h = !1;
  for (a = 0, r = n.length;a < r; ++a) {
    if (l = +n[a], l === i) {
      if (h = !0, s.all)
        continue;
      break;
    }
    if (c = t.values[l], W(c) && (o || e === 0 || ot(e) === ot(c)))
      e += c;
  }
  if (!h && !s.all)
    return 0;
  return e;
}
function Pr(t, e) {
  let { iScale: i, vScale: s } = e, n = i.axis === "x" ? "x" : "y", o = s.axis === "x" ? "x" : "y", a = Object.keys(t), r = Array(a.length), l, c, h;
  for (l = 0, c = a.length;l < c; ++l)
    h = a[l], r[l] = {
      [n]: h,
      [o]: t[h]
    };
  return r;
}
function Xi(t, e) {
  let i = t && t.options.stacked;
  return i || i === void 0 && e.stack !== void 0;
}
function Dr(t, e, i) {
  return `${t.id}.${e.id}.${i.stack || i.type}`;
}
function Ar(t) {
  let { min: e, max: i, minDefined: s, maxDefined: n } = t.getUserBounds();
  return {
    min: s ? e : Number.NEGATIVE_INFINITY,
    max: n ? i : Number.POSITIVE_INFINITY
  };
}
function Cr(t, e, i) {
  let s = t[e] || (t[e] = {});
  return s[i] || (s[i] = {});
}
function pn(t, e, i, s) {
  for (let n of e.getMatchingVisibleMetas(s).reverse()) {
    let o = t[n.index];
    if (i && o > 0 || !i && o < 0)
      return n.index;
  }
  return null;
}
function mn(t, e) {
  let { chart: i, _cachedMeta: s } = t, n = i._stacks || (i._stacks = {}), { iScale: o, vScale: a, index: r } = s, l = o.axis, c = a.axis, h = Dr(o, a, s), d = e.length, u;
  for (let f = 0;f < d; ++f) {
    let m = e[f], { [l]: g, [c]: p } = m, b = m._stacks || (m._stacks = {});
    u = b[c] = Cr(n, h, g), u[r] = p, u._top = pn(u, a, !0, s.type), u._bottom = pn(u, a, !1, s.type);
    let x = u._visualValues || (u._visualValues = {});
    x[r] = p;
  }
}
function Ki(t, e) {
  let i = t.scales;
  return Object.keys(i).filter((s) => i[s].axis === e).shift();
}
function Or(t, e) {
  return bt(t, {
    active: !1,
    dataset: void 0,
    datasetIndex: e,
    index: e,
    mode: "default",
    type: "dataset"
  });
}
function Tr(t, e, i) {
  return bt(t, {
    active: !1,
    dataIndex: e,
    parsed: void 0,
    raw: void 0,
    element: i,
    index: e,
    mode: "default",
    type: "data"
  });
}
function de(t, e) {
  let i = t.controller.index, s = t.vScale && t.vScale.axis;
  if (!s)
    return;
  e = e || t._parsed;
  for (let n of e) {
    let o = n._stacks;
    if (!o || o[s] === void 0 || o[s][i] === void 0)
      return;
    if (delete o[s][i], o[s]._visualValues !== void 0 && o[s]._visualValues[i] !== void 0)
      delete o[s]._visualValues[i];
  }
}
var qi = (t) => t === "reset" || t === "none", bn = (t, e) => e ? t : Object.assign({}, t), Lr = (t, e, i) => t && !e.hidden && e._stacked && {
  keys: go(i, !0),
  values: null
};

class yt {
  static defaults = {};
  static datasetElementType = null;
  static dataElementType = null;
  constructor(t, e) {
    this.chart = t, this._ctx = t.ctx, this.index = e, this._cachedDataOpts = {}, this._cachedMeta = this.getMeta(), this._type = this._cachedMeta.type, this.options = void 0, this._parsing = !1, this._data = void 0, this._objectData = void 0, this._sharedOptions = void 0, this._drawStart = void 0, this._drawCount = void 0, this.enableOptionSharing = !1, this.supportsDecimation = !1, this.$context = void 0, this._syncList = [], this.datasetElementType = new.target.datasetElementType, this.dataElementType = new.target.dataElementType, this.initialize();
  }
  initialize() {
    let t = this._cachedMeta;
    if (this.configure(), this.linkScales(), t._stacked = Xi(t.vScale, t), this.addElements(), this.options.fill && !this.chart.isPluginEnabled("filler"))
      console.warn("Tried to use the 'fill' option without the 'Filler' plugin enabled. Please import and register the 'Filler' plugin and make sure it is not disabled in the options");
  }
  updateIndex(t) {
    if (this.index !== t)
      de(this._cachedMeta);
    this.index = t;
  }
  linkScales() {
    let t = this.chart, e = this._cachedMeta, i = this.getDataset(), s = (h, d, u, f) => h === "x" ? d : h === "r" ? f : u, n = e.xAxisID = P(i.xAxisID, Ki(t, "x")), o = e.yAxisID = P(i.yAxisID, Ki(t, "y")), a = e.rAxisID = P(i.rAxisID, Ki(t, "r")), r = e.indexAxis, l = e.iAxisID = s(r, n, o, a), c = e.vAxisID = s(r, o, n, a);
    e.xScale = this.getScaleForId(n), e.yScale = this.getScaleForId(o), e.rScale = this.getScaleForId(a), e.iScale = this.getScaleForId(l), e.vScale = this.getScaleForId(c);
  }
  getDataset() {
    return this.chart.data.datasets[this.index];
  }
  getMeta() {
    return this.chart.getDatasetMeta(this.index);
  }
  getScaleForId(t) {
    return this.chart.scales[t];
  }
  _getOtherScale(t) {
    let e = this._cachedMeta;
    return t === e.iScale ? e.vScale : e.iScale;
  }
  reset() {
    this._update("reset");
  }
  _destroy() {
    let t = this._cachedMeta;
    if (this._data)
      Si(this._data, this);
    if (t._stacked)
      de(t);
  }
  _dataCheck() {
    let t = this.getDataset(), e = t.data || (t.data = []), i = this._data;
    if (O(e)) {
      let s = this._cachedMeta;
      this._data = Pr(e, s);
    } else if (i !== e) {
      if (i) {
        Si(i, this);
        let s = this._cachedMeta;
        de(s), s._parsed = [];
      }
      if (e && Object.isExtensible(e))
        Ys(e, this);
      this._syncList = [], this._data = e;
    }
  }
  addElements() {
    let t = this._cachedMeta;
    if (this._dataCheck(), this.datasetElementType)
      t.dataset = new this.datasetElementType;
  }
  buildOrUpdateElements(t) {
    let e = this._cachedMeta, i = this.getDataset(), s = !1;
    this._dataCheck();
    let n = e._stacked;
    if (e._stacked = Xi(e.vScale, e), e.stack !== i.stack)
      s = !0, de(e), e.stack = i.stack;
    if (this._resyncElements(t), s || n !== e._stacked)
      mn(this, e._parsed), e._stacked = Xi(e.vScale, e);
  }
  configure() {
    let t = this.chart.config, e = t.datasetScopeKeys(this._type), i = t.getOptionScopes(this.getDataset(), e, !0);
    this.options = t.createResolver(i, this.getContext()), this._parsing = this.options.parsing, this._cachedDataOpts = {};
  }
  parse(t, e) {
    let { _cachedMeta: i, _data: s } = this, { iScale: n, _stacked: o } = i, a = n.axis, r = t === 0 && e === s.length ? !0 : i._sorted, l = t > 0 && i._parsed[t - 1], c, h, d;
    if (this._parsing === !1)
      i._parsed = s, i._sorted = !0, d = s;
    else {
      if (E(s[t]))
        d = this.parseArrayData(i, s, t, e);
      else if (O(s[t]))
        d = this.parseObjectData(i, s, t, e);
      else
        d = this.parsePrimitiveData(i, s, t, e);
      let u = () => h[a] === null || l && h[a] < l[a];
      for (c = 0;c < e; ++c)
        if (i._parsed[c + t] = h = d[c], r) {
          if (u())
            r = !1;
          l = h;
        }
      i._sorted = r;
    }
    if (o)
      mn(this, d);
  }
  parsePrimitiveData(t, e, i, s) {
    let { iScale: n, vScale: o } = t, a = n.axis, r = o.axis, l = n.getLabels(), c = n === o, h = Array(s), d, u, f;
    for (d = 0, u = s;d < u; ++d)
      f = d + i, h[d] = {
        [a]: c || n.parse(l[f], f),
        [r]: o.parse(e[f], f)
      };
    return h;
  }
  parseArrayData(t, e, i, s) {
    let { xScale: n, yScale: o } = t, a = Array(s), r, l, c, h;
    for (r = 0, l = s;r < l; ++r)
      c = r + i, h = e[c], a[r] = {
        x: n.parse(h[0], c),
        y: o.parse(h[1], c)
      };
    return a;
  }
  parseObjectData(t, e, i, s) {
    let { xScale: n, yScale: o } = t, { xAxisKey: a = "x", yAxisKey: r = "y" } = this._parsing, l = Array(s), c, h, d, u;
    for (c = 0, h = s;c < h; ++c)
      d = c + i, u = e[d], l[c] = {
        x: n.parse(mt(u, a), d),
        y: o.parse(mt(u, r), d)
      };
    return l;
  }
  getParsed(t) {
    return this._cachedMeta._parsed[t];
  }
  getDataElement(t) {
    return this._cachedMeta.data[t];
  }
  applyStack(t, e, i) {
    let s = this.chart, n = this._cachedMeta, o = e[t.axis], a = {
      keys: go(s, !0),
      values: e._stacks[t.axis]._visualValues
    };
    return gn(a, o, n.index, {
      mode: i
    });
  }
  updateRangeFromParsed(t, e, i, s) {
    let n = i[e.axis], o = n === null ? NaN : n, a = s && i._stacks[e.axis];
    if (s && a)
      s.values = a, o = gn(s, n, this._cachedMeta.index);
    t.min = Math.min(t.min, o), t.max = Math.max(t.max, o);
  }
  getMinMax(t, e) {
    let i = this._cachedMeta, s = i._parsed, n = i._sorted && t === i.iScale, o = s.length, a = this._getOtherScale(t), r = Lr(e, i, this.chart), l = {
      min: Number.POSITIVE_INFINITY,
      max: Number.NEGATIVE_INFINITY
    }, { min: c, max: h } = Ar(a), d, u;
    function f() {
      u = s[d];
      let m = u[a.axis];
      return !W(u[t.axis]) || c > m || h < m;
    }
    for (d = 0;d < o; ++d) {
      if (f())
        continue;
      if (this.updateRangeFromParsed(l, t, u, r), n)
        break;
    }
    if (n)
      for (d = o - 1;d >= 0; --d) {
        if (f())
          continue;
        this.updateRangeFromParsed(l, t, u, r);
        break;
      }
    return l;
  }
  getAllParsedValues(t) {
    let e = this._cachedMeta._parsed, i = [], s, n, o;
    for (s = 0, n = e.length;s < n; ++s)
      if (o = e[s][t.axis], W(o))
        i.push(o);
    return i;
  }
  getMaxOverflow() {
    return !1;
  }
  getLabelAndValue(t) {
    let e = this._cachedMeta, { iScale: i, vScale: s } = e, n = this.getParsed(t);
    return {
      label: i ? "" + i.getLabelForValue(n[i.axis]) : "",
      value: s ? "" + s.getLabelForValue(n[s.axis]) : ""
    };
  }
  _update(t) {
    let e = this._cachedMeta;
    this.update(t || "default"), e._clip = Sr(P(this.options.clip, wr(e.xScale, e.yScale, this.getMaxOverflow())));
  }
  update(t) {}
  draw() {
    let t = this._ctx, e = this.chart, i = this._cachedMeta, s = i.data || [], n = e.chartArea, o = [], a = this._drawStart || 0, r = this._drawCount || s.length - a, l = this.options.drawActiveElementsOnTop, c;
    if (i.dataset)
      i.dataset.draw(t, n, a, r);
    for (c = a;c < a + r; ++c) {
      let h = s[c];
      if (h.hidden)
        continue;
      if (h.active && l)
        o.push(h);
      else
        h.draw(t, n);
    }
    for (c = 0;c < o.length; ++c)
      o[c].draw(t, n);
  }
  getStyle(t, e) {
    let i = e ? "active" : "default";
    return t === void 0 && this._cachedMeta.dataset ? this.resolveDatasetElementOptions(i) : this.resolveDataElementOptions(t || 0, i);
  }
  getContext(t, e, i) {
    let s = this.getDataset(), n;
    if (t >= 0 && t < this._cachedMeta.data.length) {
      let o = this._cachedMeta.data[t];
      n = o.$context || (o.$context = Tr(this.getContext(), t, o)), n.parsed = this.getParsed(t), n.raw = s.data[t], n.index = n.dataIndex = t;
    } else
      n = this.$context || (this.$context = Or(this.chart.getContext(), this.index)), n.dataset = s, n.index = n.datasetIndex = this.index;
    return n.active = !!e, n.mode = i, n;
  }
  resolveDatasetElementOptions(t) {
    return this._resolveElementOptions(this.datasetElementType.id, t);
  }
  resolveDataElementOptions(t, e) {
    return this._resolveElementOptions(this.dataElementType.id, e, t);
  }
  _resolveElementOptions(t, e = "default", i) {
    let s = e === "active", n = this._cachedDataOpts, o = t + "-" + e, a = n[o], r = this.enableOptionSharing && Kt(i);
    if (a)
      return bn(a, r);
    let l = this.chart.config, c = l.datasetElementScopeKeys(this._type, t), h = s ? [
      `${t}Hover`,
      "hover",
      t,
      ""
    ] : [
      t,
      ""
    ], d = l.getOptionScopes(this.getDataset(), c), u = Object.keys(F.elements[t]), f = () => this.getContext(i, s, e), m = l.resolveNamedOptions(d, u, f, h);
    if (m.$shared)
      m.$shared = r, n[o] = Object.freeze(bn(m, r));
    return m;
  }
  _resolveAnimations(t, e, i) {
    let s = this.chart, n = this._cachedDataOpts, o = `animation-${e}`, a = n[o];
    if (a)
      return a;
    let r;
    if (s.options.animation !== !1) {
      let c = this.chart.config, h = c.datasetAnimationScopeKeys(this._type, e), d = c.getOptionScopes(this.getDataset(), h);
      r = c.createResolver(d, this.getContext(t, i, e));
    }
    let l = new us(s, r && r.animations);
    if (r && r._cacheable)
      n[o] = Object.freeze(l);
    return l;
  }
  getSharedOptions(t) {
    if (!t.$shared)
      return;
    return this._sharedOptions || (this._sharedOptions = Object.assign({}, t));
  }
  includeOptions(t, e) {
    return !e || qi(t) || this.chart._animationsDisabled;
  }
  _getSharedOptions(t, e) {
    let i = this.resolveDataElementOptions(t, e), s = this._sharedOptions, n = this.getSharedOptions(i), o = this.includeOptions(e, n) || n !== s;
    return this.updateSharedOptions(n, e, i), {
      sharedOptions: n,
      includeOptions: o
    };
  }
  updateElement(t, e, i, s) {
    if (qi(s))
      Object.assign(t, i);
    else
      this._resolveAnimations(e, s).update(t, i);
  }
  updateSharedOptions(t, e, i) {
    if (t && !qi(e))
      this._resolveAnimations(void 0, e).update(t, i);
  }
  _setStyle(t, e, i, s) {
    t.active = s;
    let n = this.getStyle(e, s);
    this._resolveAnimations(e, i, s).update(t, {
      options: !s && this.getSharedOptions(n) || n
    });
  }
  removeHoverStyle(t, e, i) {
    this._setStyle(t, i, "active", !1);
  }
  setHoverStyle(t, e, i) {
    this._setStyle(t, i, "active", !0);
  }
  _removeDatasetHoverStyle() {
    let t = this._cachedMeta.dataset;
    if (t)
      this._setStyle(t, void 0, "active", !1);
  }
  _setDatasetHoverStyle() {
    let t = this._cachedMeta.dataset;
    if (t)
      this._setStyle(t, void 0, "active", !0);
  }
  _resyncElements(t) {
    let e = this._data, i = this._cachedMeta.data;
    for (let [a, r, l] of this._syncList)
      this[a](r, l);
    this._syncList = [];
    let s = i.length, n = e.length, o = Math.min(n, s);
    if (o)
      this.parse(0, o);
    if (n > s)
      this._insertElements(s, n - s, t);
    else if (n < s)
      this._removeElements(n, s - n);
  }
  _insertElements(t, e, i = !0) {
    let s = this._cachedMeta, n = s.data, o = t + e, a, r = (l) => {
      l.length += e;
      for (a = l.length - 1;a >= o; a--)
        l[a] = l[a - e];
    };
    r(n);
    for (a = t;a < o; ++a)
      n[a] = new this.dataElementType;
    if (this._parsing)
      r(s._parsed);
    if (this.parse(t, e), i)
      this.updateElements(n, t, e, "reset");
  }
  updateElements(t, e, i, s) {}
  _removeElements(t, e) {
    let i = this._cachedMeta;
    if (this._parsing) {
      let s = i._parsed.splice(t, e);
      if (i._stacked)
        de(i, s);
    }
    i.data.splice(t, e);
  }
  _sync(t) {
    if (this._parsing)
      this._syncList.push(t);
    else {
      let [e, i, s] = t;
      this[e](i, s);
    }
    this.chart._dataChanges.push([
      this.index,
      ...t
    ]);
  }
  _onDataPush() {
    let t = arguments.length;
    this._sync([
      "_insertElements",
      this.getDataset().data.length - t,
      t
    ]);
  }
  _onDataPop() {
    this._sync([
      "_removeElements",
      this._cachedMeta.data.length - 1,
      1
    ]);
  }
  _onDataShift() {
    this._sync([
      "_removeElements",
      0,
      1
    ]);
  }
  _onDataSplice(t, e) {
    if (e)
      this._sync([
        "_removeElements",
        t,
        e
      ]);
    let i = arguments.length - 2;
    if (i)
      this._sync([
        "_insertElements",
        t,
        i
      ]);
  }
  _onDataUnshift() {
    this._sync([
      "_insertElements",
      0,
      arguments.length
    ]);
  }
}
function Rr(t, e) {
  if (!t._cache.$bar) {
    let i = t.getMatchingVisibleMetas(e), s = [];
    for (let n = 0, o = i.length;n < o; n++)
      s = s.concat(i[n].controller.getAllParsedValues(t));
    t._cache.$bar = Pi(s.sort((n, o) => n - o));
  }
  return t._cache.$bar;
}
function Er(t) {
  let e = t.iScale, i = Rr(e, t.type), s = e._length, n, o, a, r, l = () => {
    if (a === 32767 || a === -32768)
      return;
    if (Kt(r))
      s = Math.min(s, Math.abs(a - r) || s);
    r = a;
  };
  for (n = 0, o = i.length;n < o; ++n)
    a = e.getPixelForValue(i[n]), l();
  r = void 0;
  for (n = 0, o = e.ticks.length;n < o; ++n)
    a = e.getPixelForTick(n), l();
  return s;
}
function Ir(t, e, i, s) {
  let n = i.barThickness, o, a;
  if (A(n))
    o = e.min * i.categoryPercentage, a = i.barPercentage;
  else
    o = n * s, a = 1;
  return {
    chunk: o / s,
    ratio: a,
    start: e.pixels[t] - o / 2
  };
}
function Fr(t, e, i, s) {
  let n = e.pixels, o = n[t], a = t > 0 ? n[t - 1] : null, r = t < n.length - 1 ? n[t + 1] : null, l = i.categoryPercentage;
  if (a === null)
    a = o - (r === null ? e.end - e.start : r - o);
  if (r === null)
    r = o + o - a;
  let c = o - (o - Math.min(a, r)) / 2 * l;
  return {
    chunk: Math.abs(r - a) / 2 * l / s,
    ratio: i.barPercentage,
    start: c
  };
}
function zr(t, e, i, s) {
  let n = i.parse(t[0], s), o = i.parse(t[1], s), a = Math.min(n, o), r = Math.max(n, o), l = a, c = r;
  if (Math.abs(a) > Math.abs(r))
    l = r, c = a;
  e[i.axis] = c, e._custom = {
    barStart: l,
    barEnd: c,
    start: n,
    end: o,
    min: a,
    max: r
  };
}
function po(t, e, i, s) {
  if (E(t))
    zr(t, e, i, s);
  else
    e[i.axis] = i.parse(t, s);
  return e;
}
function xn(t, e, i, s) {
  let { iScale: n, vScale: o } = t, a = n.getLabels(), r = n === o, l = [], c, h, d, u;
  for (c = i, h = i + s;c < h; ++c)
    u = e[c], d = {}, d[n.axis] = r || n.parse(a[c], c), l.push(po(u, d, o, c));
  return l;
}
function Gi(t) {
  return t && t.barStart !== void 0 && t.barEnd !== void 0;
}
function Br(t, e, i) {
  if (t !== 0)
    return ot(t);
  return (e.isHorizontal() ? 1 : -1) * (e.min >= i ? 1 : -1);
}
function Wr(t) {
  let e, i, s, n, o;
  if (t.horizontal)
    e = t.base > t.x, i = "left", s = "right";
  else
    e = t.base < t.y, i = "bottom", s = "top";
  if (e)
    n = "end", o = "start";
  else
    n = "start", o = "end";
  return {
    start: i,
    end: s,
    reverse: e,
    top: n,
    bottom: o
  };
}
function Nr(t, e, i, s) {
  let n = e.borderSkipped, o = {};
  if (!n) {
    t.borderSkipped = o;
    return;
  }
  if (n === !0) {
    t.borderSkipped = {
      top: !0,
      right: !0,
      bottom: !0,
      left: !0
    };
    return;
  }
  let { start: a, end: r, reverse: l, top: c, bottom: h } = Wr(t);
  if (n === "middle" && i)
    if (t.enableBorderRadius = !0, (i._top || 0) === s)
      n = c;
    else if ((i._bottom || 0) === s)
      n = h;
    else
      o[_n(h, a, r, l)] = !0, n = c;
  o[_n(n, a, r, l)] = !0, t.borderSkipped = o;
}
function _n(t, e, i, s) {
  if (s)
    t = Vr(t, e, i), t = yn(t, i, e);
  else
    t = yn(t, e, i);
  return t;
}
function Vr(t, e, i) {
  return t === e ? i : t === i ? e : t;
}
function yn(t, e, i) {
  return t === "start" ? e : t === "end" ? i : t;
}
function Hr(t, { inflateAmount: e }, i) {
  t.inflateAmount = e === "auto" ? i === 1 ? 0.33 : 0 : e;
}

class mo extends yt {
  static id = "bar";
  static defaults = {
    datasetElementType: !1,
    dataElementType: "bar",
    categoryPercentage: 0.8,
    barPercentage: 0.9,
    grouped: !0,
    animations: {
      numbers: {
        type: "number",
        properties: [
          "x",
          "y",
          "base",
          "width",
          "height"
        ]
      }
    }
  };
  static overrides = {
    scales: {
      _index_: {
        type: "category",
        offset: !0,
        grid: {
          offset: !0
        }
      },
      _value_: {
        type: "linear",
        beginAtZero: !0
      }
    }
  };
  parsePrimitiveData(t, e, i, s) {
    return xn(t, e, i, s);
  }
  parseArrayData(t, e, i, s) {
    return xn(t, e, i, s);
  }
  parseObjectData(t, e, i, s) {
    let { iScale: n, vScale: o } = t, { xAxisKey: a = "x", yAxisKey: r = "y" } = this._parsing, l = n.axis === "x" ? a : r, c = o.axis === "x" ? a : r, h = [], d, u, f, m;
    for (d = i, u = i + s;d < u; ++d)
      m = e[d], f = {}, f[n.axis] = n.parse(mt(m, l), d), h.push(po(mt(m, c), f, o, d));
    return h;
  }
  updateRangeFromParsed(t, e, i, s) {
    super.updateRangeFromParsed(t, e, i, s);
    let n = i._custom;
    if (n && e === this._cachedMeta.vScale)
      t.min = Math.min(t.min, n.min), t.max = Math.max(t.max, n.max);
  }
  getMaxOverflow() {
    return 0;
  }
  getLabelAndValue(t) {
    let e = this._cachedMeta, { iScale: i, vScale: s } = e, n = this.getParsed(t), o = n._custom, a = Gi(o) ? "[" + o.start + ", " + o.end + "]" : "" + s.getLabelForValue(n[s.axis]);
    return {
      label: "" + i.getLabelForValue(n[i.axis]),
      value: a
    };
  }
  initialize() {
    this.enableOptionSharing = !0, super.initialize();
    let t = this._cachedMeta;
    t.stack = this.getDataset().stack;
  }
  update(t) {
    let e = this._cachedMeta;
    this.updateElements(e.data, 0, e.data.length, t);
  }
  updateElements(t, e, i, s) {
    let n = s === "reset", { index: o, _cachedMeta: { vScale: a } } = this, r = a.getBasePixel(), l = a.isHorizontal(), c = this._getRuler(), { sharedOptions: h, includeOptions: d } = this._getSharedOptions(e, s);
    for (let u = e;u < e + i; u++) {
      let f = this.getParsed(u), m = n || A(f[a.axis]) ? {
        base: r,
        head: r
      } : this._calculateBarValuePixels(u), g = this._calculateBarIndexPixels(u, c), p = (f._stacks || {})[a.axis], b = {
        horizontal: l,
        base: m.base,
        enableBorderRadius: !p || Gi(f._custom) || o === p._top || o === p._bottom,
        x: l ? m.head : g.center,
        y: l ? g.center : m.head,
        height: l ? g.size : Math.abs(m.size),
        width: l ? Math.abs(m.size) : g.size
      };
      if (d)
        b.options = h || this.resolveDataElementOptions(u, t[u].active ? "active" : s);
      let x = b.options || t[u].options;
      Nr(b, x, p, o), Hr(b, x, c.ratio), this.updateElement(t[u], u, b, s);
    }
  }
  _getStacks(t, e) {
    let { iScale: i } = this._cachedMeta, s = i.getMatchingVisibleMetas(this._type).filter((c) => c.controller.options.grouped), n = i.options.stacked, o = [], a = this._cachedMeta.controller.getParsed(e), r = a && a[i.axis], l = (c) => {
      let h = c._parsed.find((u) => u[i.axis] === r), d = h && h[c.vScale.axis];
      if (A(d) || isNaN(d))
        return !0;
    };
    for (let c of s) {
      if (e !== void 0 && l(c))
        continue;
      if (n === !1 || o.indexOf(c.stack) === -1 || n === void 0 && c.stack === void 0)
        o.push(c.stack);
      if (c.index === t)
        break;
    }
    if (!o.length)
      o.push(void 0);
    return o;
  }
  _getStackCount(t) {
    return this._getStacks(void 0, t).length;
  }
  _getAxisCount() {
    return this._getAxis().length;
  }
  getFirstScaleIdForIndexAxis() {
    let t = this.chart.scales, e = this.chart.options.indexAxis;
    return Object.keys(t).filter((i) => t[i].axis === e).shift();
  }
  _getAxis() {
    let t = {}, e = this.getFirstScaleIdForIndexAxis();
    for (let i of this.chart.data.datasets)
      t[P(this.chart.options.indexAxis === "x" ? i.xAxisID : i.yAxisID, e)] = !0;
    return Object.keys(t);
  }
  _getStackIndex(t, e, i) {
    let s = this._getStacks(t, i), n = e !== void 0 ? s.indexOf(e) : -1;
    return n === -1 ? s.length - 1 : n;
  }
  _getRuler() {
    let t = this.options, e = this._cachedMeta, i = e.iScale, s = [], n, o;
    for (n = 0, o = e.data.length;n < o; ++n)
      s.push(i.getPixelForValue(this.getParsed(n)[i.axis], n));
    let a = t.barThickness;
    return {
      min: a || Er(e),
      pixels: s,
      start: i._startPixel,
      end: i._endPixel,
      stackCount: this._getStackCount(),
      scale: i,
      grouped: t.grouped,
      ratio: a ? 1 : t.categoryPercentage * t.barPercentage
    };
  }
  _calculateBarValuePixels(t) {
    let { _cachedMeta: { vScale: e, _stacked: i, index: s }, options: { base: n, minBarLength: o } } = this, a = n || 0, r = this.getParsed(t), l = r._custom, c = Gi(l), h = r[e.axis], d = 0, u = i ? this.applyStack(e, r, i) : h, f, m;
    if (u !== h)
      d = u - h, u = h;
    if (c) {
      if (h = l.barStart, u = l.barEnd - l.barStart, h !== 0 && ot(h) !== ot(l.barEnd))
        d = 0;
      d += h;
    }
    let g = !A(n) && !c ? n : d, p = e.getPixelForValue(g);
    if (this.chart.getDataVisibility(t))
      f = e.getPixelForValue(d + u);
    else
      f = p;
    if (m = f - p, Math.abs(m) < o) {
      if (m = Br(m, e, a) * o, h === a)
        p -= m / 2;
      let b = e.getPixelForDecimal(0), x = e.getPixelForDecimal(1), v = Math.min(b, x), y = Math.max(b, x);
      if (p = Math.max(Math.min(p, y), v), f = p + m, i && !c)
        r._stacks[e.axis]._visualValues[s] = e.getValueForPixel(f) - e.getValueForPixel(p);
    }
    if (p === e.getPixelForValue(a)) {
      let b = ot(m) * e.getLineWidthForValue(a) / 2;
      p += b, m -= b;
    }
    return {
      size: m,
      base: p,
      head: f,
      center: f + m / 2
    };
  }
  _calculateBarIndexPixels(t, e) {
    let i = e.scale, s = this.options, n = s.skipNull, o = P(s.maxBarThickness, 1 / 0), a, r, l = this._getAxisCount();
    if (e.grouped) {
      let c = n ? this._getStackCount(t) : e.stackCount, h = s.barThickness === "flex" ? Fr(t, e, s, c * l) : Ir(t, e, s, c * l), d = this.chart.options.indexAxis === "x" ? this.getDataset().xAxisID : this.getDataset().yAxisID, u = this._getAxis().indexOf(P(d, this.getFirstScaleIdForIndexAxis())), f = this._getStackIndex(this.index, this._cachedMeta.stack, n ? t : void 0) + u;
      a = h.start + h.chunk * f + h.chunk / 2, r = Math.min(o, h.chunk * h.ratio);
    } else
      a = i.getPixelForValue(this.getParsed(t)[i.axis], t), r = Math.min(o, e.min * e.ratio);
    return {
      base: a - r / 2,
      head: a + r / 2,
      center: a,
      size: r
    };
  }
  draw() {
    let t = this._cachedMeta, { vScale: e, data: i } = t, s = i.length, n = 0;
    for (;n < s; ++n)
      if (this.getParsed(n)[e.axis] !== null && !i[n].hidden)
        i[n].draw(this._ctx);
  }
}

class bo extends yt {
  static id = "bubble";
  static defaults = {
    datasetElementType: !1,
    dataElementType: "point",
    animations: {
      numbers: {
        type: "number",
        properties: [
          "x",
          "y",
          "borderWidth",
          "radius"
        ]
      }
    }
  };
  static overrides = {
    scales: {
      x: {
        type: "linear"
      },
      y: {
        type: "linear"
      }
    }
  };
  initialize() {
    this.enableOptionSharing = !0, super.initialize();
  }
  parsePrimitiveData(t, e, i, s) {
    let n = super.parsePrimitiveData(t, e, i, s);
    for (let o = 0;o < n.length; o++)
      n[o]._custom = this.resolveDataElementOptions(o + i).radius;
    return n;
  }
  parseArrayData(t, e, i, s) {
    let n = super.parseArrayData(t, e, i, s);
    for (let o = 0;o < n.length; o++) {
      let a = e[i + o];
      n[o]._custom = P(a[2], this.resolveDataElementOptions(o + i).radius);
    }
    return n;
  }
  parseObjectData(t, e, i, s) {
    let n = super.parseObjectData(t, e, i, s);
    for (let o = 0;o < n.length; o++) {
      let a = e[i + o];
      n[o]._custom = P(a && a.r && +a.r, this.resolveDataElementOptions(o + i).radius);
    }
    return n;
  }
  getMaxOverflow() {
    let t = this._cachedMeta.data, e = 0;
    for (let i = t.length - 1;i >= 0; --i)
      e = Math.max(e, t[i].size(this.resolveDataElementOptions(i)) / 2);
    return e > 0 && e;
  }
  getLabelAndValue(t) {
    let e = this._cachedMeta, i = this.chart.data.labels || [], { xScale: s, yScale: n } = e, o = this.getParsed(t), a = s.getLabelForValue(o.x), r = n.getLabelForValue(o.y), l = o._custom;
    return {
      label: i[t] || "",
      value: "(" + a + ", " + r + (l ? ", " + l : "") + ")"
    };
  }
  update(t) {
    let e = this._cachedMeta.data;
    this.updateElements(e, 0, e.length, t);
  }
  updateElements(t, e, i, s) {
    let n = s === "reset", { iScale: o, vScale: a } = this._cachedMeta, { sharedOptions: r, includeOptions: l } = this._getSharedOptions(e, s), c = o.axis, h = a.axis;
    for (let d = e;d < e + i; d++) {
      let u = t[d], f = !n && this.getParsed(d), m = {}, g = m[c] = n ? o.getPixelForDecimal(0.5) : o.getPixelForValue(f[c]), p = m[h] = n ? a.getBasePixel() : a.getPixelForValue(f[h]);
      if (m.skip = isNaN(g) || isNaN(p), l) {
        if (m.options = r || this.resolveDataElementOptions(d, u.active ? "active" : s), n)
          m.options.radius = 0;
      }
      this.updateElement(u, d, m, s);
    }
  }
  resolveDataElementOptions(t, e) {
    let i = this.getParsed(t), s = super.resolveDataElementOptions(t, e);
    if (s.$shared)
      s = Object.assign({}, s, {
        $shared: !1
      });
    let n = s.radius;
    if (e !== "active")
      s.radius = 0;
    return s.radius += P(i && i._custom, n), s;
  }
}
function jr(t, e, i) {
  let s = 1, n = 1, o = 0, a = 0;
  if (e < I) {
    let r = t, l = r + e, c = Math.cos(r), h = Math.sin(r), d = Math.cos(l), u = Math.sin(l), f = (v, y, _) => Gt(v, r, l, !0) ? 1 : Math.max(y, y * i, _, _ * i), m = (v, y, _) => Gt(v, r, l, !0) ? -1 : Math.min(y, y * i, _, _ * i), g = f(0, c, d), p = f(N, h, u), b = m(T, c, d), x = m(T + N, h, u);
    s = (g - b) / 2, n = (p - x) / 2, o = -(g + b) / 2, a = -(p + x) / 2;
  }
  return {
    ratioX: s,
    ratioY: n,
    offsetX: o,
    offsetY: a
  };
}

class ni extends yt {
  static id = "doughnut";
  static defaults = {
    datasetElementType: !1,
    dataElementType: "arc",
    animation: {
      animateRotate: !0,
      animateScale: !1
    },
    animations: {
      numbers: {
        type: "number",
        properties: [
          "circumference",
          "endAngle",
          "innerRadius",
          "outerRadius",
          "startAngle",
          "x",
          "y",
          "offset",
          "borderWidth",
          "spacing"
        ]
      }
    },
    cutout: "50%",
    rotation: 0,
    circumference: 360,
    radius: "100%",
    spacing: 0,
    indexAxis: "r"
  };
  static descriptors = {
    _scriptable: (t) => t !== "spacing",
    _indexable: (t) => t !== "spacing" && !t.startsWith("borderDash") && !t.startsWith("hoverBorderDash")
  };
  static overrides = {
    aspectRatio: 1,
    plugins: {
      legend: {
        labels: {
          generateLabels(t) {
            let e = t.data, { labels: { pointStyle: i, textAlign: s, color: n, useBorderRadius: o, borderRadius: a } } = t.legend.options;
            if (e.labels.length && e.datasets.length)
              return e.labels.map((r, l) => {
                let h = t.getDatasetMeta(0).controller.getStyle(l);
                return {
                  text: r,
                  fillStyle: h.backgroundColor,
                  fontColor: n,
                  hidden: !t.getDataVisibility(l),
                  lineDash: h.borderDash,
                  lineDashOffset: h.borderDashOffset,
                  lineJoin: h.borderJoinStyle,
                  lineWidth: h.borderWidth,
                  strokeStyle: h.borderColor,
                  textAlign: s,
                  pointStyle: i,
                  borderRadius: o && (a || h.borderRadius),
                  index: l
                };
              });
            return [];
          }
        },
        onClick(t, e, i) {
          i.chart.toggleDataVisibility(e.index), i.chart.update();
        }
      }
    }
  };
  constructor(t, e) {
    super(t, e);
    this.enableOptionSharing = !0, this.innerRadius = void 0, this.outerRadius = void 0, this.offsetX = void 0, this.offsetY = void 0;
  }
  linkScales() {}
  parse(t, e) {
    let i = this.getDataset().data, s = this._cachedMeta;
    if (this._parsing === !1)
      s._parsed = i;
    else {
      let n = (r) => +i[r];
      if (O(i[t])) {
        let { key: r = "value" } = this._parsing;
        n = (l) => +mt(i[l], r);
      }
      let o, a;
      for (o = t, a = t + e;o < a; ++o)
        s._parsed[o] = n(o);
    }
  }
  _getRotation() {
    return st(this.options.rotation - 90);
  }
  _getCircumference() {
    return st(this.options.circumference);
  }
  _getRotationExtents() {
    let t = I, e = -I;
    for (let i = 0;i < this.chart.data.datasets.length; ++i)
      if (this.chart.isDatasetVisible(i) && this.chart.getDatasetMeta(i).type === this._type) {
        let s = this.chart.getDatasetMeta(i).controller, n = s._getRotation(), o = s._getCircumference();
        t = Math.min(t, n), e = Math.max(e, n + o);
      }
    return {
      rotation: t,
      circumference: e - t
    };
  }
  update(t) {
    let e = this.chart, { chartArea: i } = e, s = this._cachedMeta, n = s.data, o = this.getMaxBorderWidth() + this.getMaxOffset(n) + this.options.spacing, a = Math.max((Math.min(i.width, i.height) - o) / 2, 0), r = Math.min(Fs(this.options.cutout, a), 1), l = this._getRingWeight(this.index), { circumference: c, rotation: h } = this._getRotationExtents(), { ratioX: d, ratioY: u, offsetX: f, offsetY: m } = jr(h, c, r), g = (i.width - o) / d, p = (i.height - o) / u, b = Math.max(Math.min(g, p) / 2, 0), x = _i(this.options.radius, b), v = Math.max(x * r, 0), y = (x - v) / this._getVisibleDatasetWeightTotal();
    this.offsetX = f * x, this.offsetY = m * x, s.total = this.calculateTotal(), this.outerRadius = x - y * this._getRingWeightOffset(this.index), this.innerRadius = Math.max(this.outerRadius - y * l, 0), this.updateElements(n, 0, n.length, t);
  }
  _circumference(t, e) {
    let i = this.options, s = this._cachedMeta, n = this._getCircumference();
    if (e && i.animation.animateRotate || !this.chart.getDataVisibility(t) || s._parsed[t] === null || s.data[t].hidden)
      return 0;
    return this.calculateCircumference(s._parsed[t] * n / I);
  }
  updateElements(t, e, i, s) {
    let n = s === "reset", o = this.chart, a = o.chartArea, l = o.options.animation, c = (a.left + a.right) / 2, h = (a.top + a.bottom) / 2, d = n && l.animateScale, u = d ? 0 : this.innerRadius, f = d ? 0 : this.outerRadius, { sharedOptions: m, includeOptions: g } = this._getSharedOptions(e, s), p = this._getRotation(), b;
    for (b = 0;b < e; ++b)
      p += this._circumference(b, n);
    for (b = e;b < e + i; ++b) {
      let x = this._circumference(b, n), v = t[b], y = {
        x: c + this.offsetX,
        y: h + this.offsetY,
        startAngle: p,
        endAngle: p + x,
        circumference: x,
        outerRadius: f,
        innerRadius: u
      };
      if (g)
        y.options = m || this.resolveDataElementOptions(b, v.active ? "active" : s);
      p += x, this.updateElement(v, b, y, s);
    }
  }
  calculateTotal() {
    let t = this._cachedMeta, e = t.data, i = 0, s;
    for (s = 0;s < e.length; s++) {
      let n = t._parsed[s];
      if (n !== null && !isNaN(n) && this.chart.getDataVisibility(s) && !e[s].hidden)
        i += Math.abs(n);
    }
    return i;
  }
  calculateCircumference(t) {
    let e = this._cachedMeta.total;
    if (e > 0 && !isNaN(t))
      return I * (Math.abs(t) / e);
    return 0;
  }
  getLabelAndValue(t) {
    let e = this._cachedMeta, i = this.chart, s = i.data.labels || [], n = Jt(e._parsed[t], i.options.locale);
    return {
      label: s[t] || "",
      value: n
    };
  }
  getMaxBorderWidth(t) {
    let e = 0, i = this.chart, s, n, o, a, r;
    if (!t) {
      for (s = 0, n = i.data.datasets.length;s < n; ++s)
        if (i.isDatasetVisible(s)) {
          o = i.getDatasetMeta(s), t = o.data, a = o.controller;
          break;
        }
    }
    if (!t)
      return 0;
    for (s = 0, n = t.length;s < n; ++s)
      if (r = a.resolveDataElementOptions(s), r.borderAlign !== "inner")
        e = Math.max(e, r.borderWidth || 0, r.hoverBorderWidth || 0);
    return e;
  }
  getMaxOffset(t) {
    let e = 0;
    for (let i = 0, s = t.length;i < s; ++i) {
      let n = this.resolveDataElementOptions(i);
      e = Math.max(e, n.offset || 0, n.hoverOffset || 0);
    }
    return e;
  }
  _getRingWeightOffset(t) {
    let e = 0;
    for (let i = 0;i < t; ++i)
      if (this.chart.isDatasetVisible(i))
        e += this._getRingWeight(i);
    return e;
  }
  _getRingWeight(t) {
    return Math.max(P(this.chart.data.datasets[t].weight, 1), 0);
  }
  _getVisibleDatasetWeightTotal() {
    return this._getRingWeightOffset(this.chart.data.datasets.length) || 1;
  }
}

class xo extends yt {
  static id = "line";
  static defaults = {
    datasetElementType: "line",
    dataElementType: "point",
    showLine: !0,
    spanGaps: !1
  };
  static overrides = {
    scales: {
      _index_: {
        type: "category"
      },
      _value_: {
        type: "linear"
      }
    }
  };
  initialize() {
    this.enableOptionSharing = !0, this.supportsDecimation = !0, super.initialize();
  }
  update(t) {
    let e = this._cachedMeta, { dataset: i, data: s = [], _dataset: n } = e, o = this.chart._animationsDisabled, { start: a, count: r } = Ci(e, s, o);
    if (this._drawStart = a, this._drawCount = r, Oi(e))
      a = 0, r = s.length;
    i._chart = this.chart, i._datasetIndex = this.index, i._decimated = !!n._decimated, i.points = s;
    let l = this.resolveDatasetElementOptions(t);
    if (!this.options.showLine)
      l.borderWidth = 0;
    l.segment = this.options.segment, this.updateElement(i, void 0, {
      animated: !o,
      options: l
    }, t), this.updateElements(s, a, r, t);
  }
  updateElements(t, e, i, s) {
    let n = s === "reset", { iScale: o, vScale: a, _stacked: r, _dataset: l } = this._cachedMeta, { sharedOptions: c, includeOptions: h } = this._getSharedOptions(e, s), d = o.axis, u = a.axis, { spanGaps: f, segment: m } = this.options, g = Ft(f) ? f : Number.POSITIVE_INFINITY, p = this.chart._animationsDisabled || n || s === "none", b = e + i, x = t.length, v = e > 0 && this.getParsed(e - 1);
    for (let y = 0;y < x; ++y) {
      let _ = t[y], M = p ? _ : {};
      if (y < e || y >= b) {
        M.skip = !0;
        continue;
      }
      let k = this.getParsed(y), S = A(k[u]), w = M[d] = o.getPixelForValue(k[d], y), C = M[u] = n || S ? a.getBasePixel() : a.getPixelForValue(r ? this.applyStack(a, k, r) : k[u], y);
      if (M.skip = isNaN(w) || isNaN(C) || S, M.stop = y > 0 && Math.abs(k[d] - v[d]) > g, m)
        M.parsed = k, M.raw = l.data[y];
      if (h)
        M.options = c || this.resolveDataElementOptions(y, _.active ? "active" : s);
      if (!p)
        this.updateElement(_, y, M, s);
      v = k;
    }
  }
  getMaxOverflow() {
    let t = this._cachedMeta, e = t.dataset, i = e.options && e.options.borderWidth || 0, s = t.data || [];
    if (!s.length)
      return i;
    let n = s[0].size(this.resolveDataElementOptions(0)), o = s[s.length - 1].size(this.resolveDataElementOptions(s.length - 1));
    return Math.max(i, n, o) / 2;
  }
  draw() {
    let t = this._cachedMeta;
    t.dataset.updateControlPoints(this.chart.chartArea, t.iScale.axis), super.draw();
  }
}

class fs extends yt {
  static id = "polarArea";
  static defaults = {
    dataElementType: "arc",
    animation: {
      animateRotate: !0,
      animateScale: !0
    },
    animations: {
      numbers: {
        type: "number",
        properties: [
          "x",
          "y",
          "startAngle",
          "endAngle",
          "innerRadius",
          "outerRadius"
        ]
      }
    },
    indexAxis: "r",
    startAngle: 0
  };
  static overrides = {
    aspectRatio: 1,
    plugins: {
      legend: {
        labels: {
          generateLabels(t) {
            let e = t.data;
            if (e.labels.length && e.datasets.length) {
              let { labels: { pointStyle: i, color: s } } = t.legend.options;
              return e.labels.map((n, o) => {
                let r = t.getDatasetMeta(0).controller.getStyle(o);
                return {
                  text: n,
                  fillStyle: r.backgroundColor,
                  strokeStyle: r.borderColor,
                  fontColor: s,
                  lineWidth: r.borderWidth,
                  pointStyle: i,
                  hidden: !t.getDataVisibility(o),
                  index: o
                };
              });
            }
            return [];
          }
        },
        onClick(t, e, i) {
          i.chart.toggleDataVisibility(e.index), i.chart.update();
        }
      }
    },
    scales: {
      r: {
        type: "radialLinear",
        angleLines: {
          display: !1
        },
        beginAtZero: !0,
        grid: {
          circular: !0
        },
        pointLabels: {
          display: !1
        },
        startAngle: 0
      }
    }
  };
  constructor(t, e) {
    super(t, e);
    this.innerRadius = void 0, this.outerRadius = void 0;
  }
  getLabelAndValue(t) {
    let e = this._cachedMeta, i = this.chart, s = i.data.labels || [], n = Jt(e._parsed[t].r, i.options.locale);
    return {
      label: s[t] || "",
      value: n
    };
  }
  parseObjectData(t, e, i, s) {
    return Wi.bind(this)(t, e, i, s);
  }
  update(t) {
    let e = this._cachedMeta.data;
    this._updateRadius(), this.updateElements(e, 0, e.length, t);
  }
  getMinMax() {
    let t = this._cachedMeta, e = {
      min: Number.POSITIVE_INFINITY,
      max: Number.NEGATIVE_INFINITY
    };
    return t.data.forEach((i, s) => {
      let n = this.getParsed(s).r;
      if (!isNaN(n) && this.chart.getDataVisibility(s)) {
        if (n < e.min)
          e.min = n;
        if (n > e.max)
          e.max = n;
      }
    }), e;
  }
  _updateRadius() {
    let t = this.chart, { chartArea: e, options: i } = t, s = Math.min(e.right - e.left, e.bottom - e.top), n = Math.max(s / 2, 0), o = Math.max(i.cutoutPercentage ? n / 100 * i.cutoutPercentage : 1, 0), a = (n - o) / t.getVisibleDatasetCount();
    this.outerRadius = n - a * this.index, this.innerRadius = this.outerRadius - a;
  }
  updateElements(t, e, i, s) {
    let n = s === "reset", o = this.chart, r = o.options.animation, l = this._cachedMeta.rScale, { xCenter: c, yCenter: h } = l, d = l.getIndexAngle(0) - 0.5 * T, u = d, f, m = 360 / this.countVisibleElements();
    for (f = 0;f < e; ++f)
      u += this._computeAngle(f, s, m);
    for (f = e;f < e + i; f++) {
      let g = t[f], p = u, b = u + this._computeAngle(f, s, m), x = o.getDataVisibility(f) ? l.getDistanceFromCenterForValue(this.getParsed(f).r) : 0;
      if (u = b, n) {
        if (r.animateScale)
          x = 0;
        if (r.animateRotate)
          p = b = d;
      }
      let v = {
        x: c,
        y: h,
        innerRadius: 0,
        outerRadius: x,
        startAngle: p,
        endAngle: b,
        options: this.resolveDataElementOptions(f, g.active ? "active" : s)
      };
      this.updateElement(g, f, v, s);
    }
  }
  countVisibleElements() {
    let t = this._cachedMeta, e = 0;
    return t.data.forEach((i, s) => {
      if (!isNaN(this.getParsed(s).r) && this.chart.getDataVisibility(s))
        e++;
    }), e;
  }
  _computeAngle(t, e, i) {
    return this.chart.getDataVisibility(t) ? st(this.resolveDataElementOptions(t, e).angle || i) : 0;
  }
}

class _o extends ni {
  static id = "pie";
  static defaults = {
    cutout: 0,
    rotation: 0,
    circumference: 360,
    radius: "100%"
  };
}

class yo extends yt {
  static id = "radar";
  static defaults = {
    datasetElementType: "line",
    dataElementType: "point",
    indexAxis: "r",
    showLine: !0,
    elements: {
      line: {
        fill: "start"
      }
    }
  };
  static overrides = {
    aspectRatio: 1,
    scales: {
      r: {
        type: "radialLinear"
      }
    }
  };
  getLabelAndValue(t) {
    let e = this._cachedMeta.vScale, i = this.getParsed(t);
    return {
      label: e.getLabels()[t],
      value: "" + e.getLabelForValue(i[e.axis])
    };
  }
  parseObjectData(t, e, i, s) {
    return Wi.bind(this)(t, e, i, s);
  }
  update(t) {
    let e = this._cachedMeta, i = e.dataset, s = e.data || [], n = e.iScale.getLabels();
    if (i.points = s, t !== "resize") {
      let o = this.resolveDatasetElementOptions(t);
      if (!this.options.showLine)
        o.borderWidth = 0;
      let a = {
        _loop: !0,
        _fullLoop: n.length === s.length,
        options: o
      };
      this.updateElement(i, void 0, a, t);
    }
    this.updateElements(s, 0, s.length, t);
  }
  updateElements(t, e, i, s) {
    let n = this._cachedMeta.rScale, o = s === "reset";
    for (let a = e;a < e + i; a++) {
      let r = t[a], l = this.resolveDataElementOptions(a, r.active ? "active" : s), c = n.getPointPositionForValue(a, this.getParsed(a).r), h = o ? n.xCenter : c.x, d = o ? n.yCenter : c.y, u = {
        x: h,
        y: d,
        angle: c.angle,
        skip: isNaN(h) || isNaN(d),
        options: l
      };
      this.updateElement(r, a, u, s);
    }
  }
}

class vo extends yt {
  static id = "scatter";
  static defaults = {
    datasetElementType: !1,
    dataElementType: "point",
    showLine: !1,
    fill: !1
  };
  static overrides = {
    interaction: {
      mode: "point"
    },
    scales: {
      x: {
        type: "linear"
      },
      y: {
        type: "linear"
      }
    }
  };
  getLabelAndValue(t) {
    let e = this._cachedMeta, i = this.chart.data.labels || [], { xScale: s, yScale: n } = e, o = this.getParsed(t), a = s.getLabelForValue(o.x), r = n.getLabelForValue(o.y);
    return {
      label: i[t] || "",
      value: "(" + a + ", " + r + ")"
    };
  }
  update(t) {
    let e = this._cachedMeta, { data: i = [] } = e, s = this.chart._animationsDisabled, { start: n, count: o } = Ci(e, i, s);
    if (this._drawStart = n, this._drawCount = o, Oi(e))
      n = 0, o = i.length;
    if (this.options.showLine) {
      if (!this.datasetElementType)
        this.addElements();
      let { dataset: a, _dataset: r } = e;
      a._chart = this.chart, a._datasetIndex = this.index, a._decimated = !!r._decimated, a.points = i;
      let l = this.resolveDatasetElementOptions(t);
      l.segment = this.options.segment, this.updateElement(a, void 0, {
        animated: !s,
        options: l
      }, t);
    } else if (this.datasetElementType)
      delete e.dataset, this.datasetElementType = !1;
    this.updateElements(i, n, o, t);
  }
  addElements() {
    let { showLine: t } = this.options;
    if (!this.datasetElementType && t)
      this.datasetElementType = this.chart.registry.getElement("line");
    super.addElements();
  }
  updateElements(t, e, i, s) {
    let n = s === "reset", { iScale: o, vScale: a, _stacked: r, _dataset: l } = this._cachedMeta, c = this.resolveDataElementOptions(e, s), h = this.getSharedOptions(c), d = this.includeOptions(s, h), u = o.axis, f = a.axis, { spanGaps: m, segment: g } = this.options, p = Ft(m) ? m : Number.POSITIVE_INFINITY, b = this.chart._animationsDisabled || n || s === "none", x = e > 0 && this.getParsed(e - 1);
    for (let v = e;v < e + i; ++v) {
      let y = t[v], _ = this.getParsed(v), M = b ? y : {}, k = A(_[f]), S = M[u] = o.getPixelForValue(_[u], v), w = M[f] = n || k ? a.getBasePixel() : a.getPixelForValue(r ? this.applyStack(a, _, r) : _[f], v);
      if (M.skip = isNaN(S) || isNaN(w) || k, M.stop = v > 0 && Math.abs(_[u] - x[u]) > p, g)
        M.parsed = _, M.raw = l.data[v];
      if (d)
        M.options = h || this.resolveDataElementOptions(v, y.active ? "active" : s);
      if (!b)
        this.updateElement(y, v, M, s);
      x = _;
    }
    this.updateSharedOptions(h, s, c);
  }
  getMaxOverflow() {
    let t = this._cachedMeta, e = t.data || [];
    if (!this.options.showLine) {
      let a = 0;
      for (let r = e.length - 1;r >= 0; --r)
        a = Math.max(a, e[r].size(this.resolveDataElementOptions(r)) / 2);
      return a > 0 && a;
    }
    let i = t.dataset, s = i.options && i.options.borderWidth || 0;
    if (!e.length)
      return s;
    let n = e[0].size(this.resolveDataElementOptions(0)), o = e[e.length - 1].size(this.resolveDataElementOptions(e.length - 1));
    return Math.max(s, n, o) / 2;
  }
}
var $r = /* @__PURE__ */ Object.freeze({
  __proto__: null,
  BarController: mo,
  BubbleController: bo,
  DoughnutController: ni,
  LineController: xo,
  PieController: _o,
  PolarAreaController: fs,
  RadarController: yo,
  ScatterController: vo
});
function Bt() {
  throw Error("This method is not implemented: Check that a complete date adapter is provided.");
}

class gs {
  static override(t) {
    Object.assign(gs.prototype, t);
  }
  options;
  constructor(t) {
    this.options = t || {};
  }
  init() {}
  formats() {
    return Bt();
  }
  parse() {
    return Bt();
  }
  format() {
    return Bt();
  }
  add() {
    return Bt();
  }
  diff() {
    return Bt();
  }
  startOf() {
    return Bt();
  }
  endOf() {
    return Bt();
  }
}
var Yr = {
  _date: gs
};
function Ur(t, e, i, s) {
  let { controller: n, data: o, _sorted: a } = t, r = n._cachedMeta.iScale, l = t.dataset ? t.dataset.options ? t.dataset.options.spanGaps : null : null;
  if (r && e === r.axis && e !== "r" && a && o.length) {
    let c = r._reversePixels ? Hs : at;
    if (!s) {
      let h = c(o, e, i);
      if (l) {
        let { vScale: d } = n._cachedMeta, { _parsed: u } = t, f = u.slice(0, h.lo + 1).reverse().findIndex((g) => !A(g[d.axis]));
        h.lo -= Math.max(0, f);
        let m = u.slice(h.hi).findIndex((g) => !A(g[d.axis]));
        h.hi += Math.max(0, m);
      }
      return h;
    } else if (n._sharedOptions) {
      let h = o[0], d = typeof h.getRange === "function" && h.getRange(e);
      if (d) {
        let u = c(o, e, i - d), f = c(o, e, i + d);
        return {
          lo: u.lo,
          hi: f.hi
        };
      }
    }
  }
  return {
    lo: 0,
    hi: o.length - 1
  };
}
function Me(t, e, i, s, n) {
  let o = t.getSortedVisibleDatasetMetas(), a = i[e];
  for (let r = 0, l = o.length;r < l; ++r) {
    let { index: c, data: h } = o[r], { lo: d, hi: u } = Ur(o[r], e, a, n);
    for (let f = d;f <= u; ++f) {
      let m = h[f];
      if (!m.skip)
        s(m, c, f);
    }
  }
}
function Xr(t) {
  let e = t.indexOf("x") !== -1, i = t.indexOf("y") !== -1;
  return function(s, n) {
    let o = e ? Math.abs(s.x - n.x) : 0, a = i ? Math.abs(s.y - n.y) : 0;
    return Math.sqrt(Math.pow(o, 2) + Math.pow(a, 2));
  };
}
function Ji(t, e, i, s, n) {
  let o = [];
  if (!n && !t.isPointInArea(e))
    return o;
  return Me(t, i, e, function(r, l, c) {
    if (!n && !rt(r, t.chartArea, 0))
      return;
    if (r.inRange(e.x, e.y, s))
      o.push({
        element: r,
        datasetIndex: l,
        index: c
      });
  }, !0), o;
}
function Kr(t, e, i, s) {
  let n = [];
  function o(a, r, l) {
    let { startAngle: c, endAngle: h } = a.getProps([
      "startAngle",
      "endAngle"
    ], s), { angle: d } = wi(a, {
      x: e.x,
      y: e.y
    });
    if (Gt(d, c, h))
      n.push({
        element: a,
        datasetIndex: r,
        index: l
      });
  }
  return Me(t, i, e, o), n;
}
function qr(t, e, i, s, n, o) {
  let a = [], r = Xr(i), l = Number.POSITIVE_INFINITY;
  function c(h, d, u) {
    let f = h.inRange(e.x, e.y, n);
    if (s && !f)
      return;
    let m = h.getCenterPoint(n);
    if (!(!!o || t.isPointInArea(m)) && !f)
      return;
    let p = r(e, m);
    if (p < l)
      a = [
        {
          element: h,
          datasetIndex: d,
          index: u
        }
      ], l = p;
    else if (p === l)
      a.push({
        element: h,
        datasetIndex: d,
        index: u
      });
  }
  return Me(t, i, e, c), a;
}
function Zi(t, e, i, s, n, o) {
  if (!o && !t.isPointInArea(e))
    return [];
  return i === "r" && !s ? Kr(t, e, i, n) : qr(t, e, i, s, n, o);
}
function vn(t, e, i, s, n) {
  let o = [], a = i === "x" ? "inXRange" : "inYRange", r = !1;
  if (Me(t, i, e, (l, c, h) => {
    if (l[a] && l[a](e[i], n))
      o.push({
        element: l,
        datasetIndex: c,
        index: h
      }), r = r || l.inRange(e.x, e.y, n);
  }), s && !r)
    return [];
  return o;
}
var Gr = {
  evaluateInteractionItems: Me,
  modes: {
    index(t, e, i, s) {
      let n = Ct(e, t), o = i.axis || "x", a = i.includeInvisible || !1, r = i.intersect ? Ji(t, n, o, s, a) : Zi(t, n, o, !1, s, a), l = [];
      if (!r.length)
        return [];
      return t.getSortedVisibleDatasetMetas().forEach((c) => {
        let h = r[0].index, d = c.data[h];
        if (d && !d.skip)
          l.push({
            element: d,
            datasetIndex: c.index,
            index: h
          });
      }), l;
    },
    dataset(t, e, i, s) {
      let n = Ct(e, t), o = i.axis || "xy", a = i.includeInvisible || !1, r = i.intersect ? Ji(t, n, o, s, a) : Zi(t, n, o, !1, s, a);
      if (r.length > 0) {
        let l = r[0].datasetIndex, c = t.getDatasetMeta(l).data;
        r = [];
        for (let h = 0;h < c.length; ++h)
          r.push({
            element: c[h],
            datasetIndex: l,
            index: h
          });
      }
      return r;
    },
    point(t, e, i, s) {
      let n = Ct(e, t), o = i.axis || "xy", a = i.includeInvisible || !1;
      return Ji(t, n, o, s, a);
    },
    nearest(t, e, i, s) {
      let n = Ct(e, t), o = i.axis || "xy", a = i.includeInvisible || !1;
      return Zi(t, n, o, i.intersect, s, a);
    },
    x(t, e, i, s) {
      let n = Ct(e, t);
      return vn(t, n, "x", i.intersect, s);
    },
    y(t, e, i, s) {
      let n = Ct(e, t);
      return vn(t, n, "y", i.intersect, s);
    }
  }
}, Mo = [
  "left",
  "top",
  "right",
  "bottom"
];
function ue(t, e) {
  return t.filter((i) => i.pos === e);
}
function Mn(t, e) {
  return t.filter((i) => Mo.indexOf(i.pos) === -1 && i.box.axis === e);
}
function fe(t, e) {
  return t.sort((i, s) => {
    let n = e ? s : i, o = e ? i : s;
    return n.weight === o.weight ? n.index - o.index : n.weight - o.weight;
  });
}
function Jr(t) {
  let e = [], i, s, n, o, a, r;
  for (i = 0, s = (t || []).length;i < s; ++i)
    n = t[i], { position: o, options: { stack: a, stackWeight: r = 1 } } = n, e.push({
      index: i,
      box: n,
      pos: o,
      horizontal: n.isHorizontal(),
      weight: n.weight,
      stack: a && o + a,
      stackWeight: r
    });
  return e;
}
function Zr(t) {
  let e = {};
  for (let i of t) {
    let { stack: s, pos: n, stackWeight: o } = i;
    if (!s || !Mo.includes(n))
      continue;
    let a = e[s] || (e[s] = {
      count: 0,
      placed: 0,
      weight: 0,
      size: 0
    });
    a.count++, a.weight += o;
  }
  return e;
}
function Qr(t, e) {
  let i = Zr(t), { vBoxMaxWidth: s, hBoxMaxHeight: n } = e, o, a, r;
  for (o = 0, a = t.length;o < a; ++o) {
    r = t[o];
    let { fullSize: l } = r.box, c = i[r.stack], h = c && r.stackWeight / c.weight;
    if (r.horizontal)
      r.width = h ? h * s : l && e.availableWidth, r.height = n;
    else
      r.width = s, r.height = h ? h * n : l && e.availableHeight;
  }
  return i;
}
function tl(t) {
  let e = Jr(t), i = fe(e.filter((c) => c.box.fullSize), !0), s = fe(ue(e, "left"), !0), n = fe(ue(e, "right")), o = fe(ue(e, "top"), !0), a = fe(ue(e, "bottom")), r = Mn(e, "x"), l = Mn(e, "y");
  return {
    fullSize: i,
    leftAndTop: s.concat(o),
    rightAndBottom: n.concat(l).concat(a).concat(r),
    chartArea: ue(e, "chartArea"),
    vertical: s.concat(n).concat(l),
    horizontal: o.concat(a).concat(r)
  };
}
function kn(t, e, i, s) {
  return Math.max(t[i], e[i]) + Math.max(t[s], e[s]);
}
function ko(t, e) {
  t.top = Math.max(t.top, e.top), t.left = Math.max(t.left, e.left), t.bottom = Math.max(t.bottom, e.bottom), t.right = Math.max(t.right, e.right);
}
function el(t, e, i, s) {
  let { pos: n, box: o } = i, a = t.maxPadding;
  if (!O(n)) {
    if (i.size)
      t[n] -= i.size;
    let d = s[i.stack] || {
      size: 0,
      count: 1
    };
    d.size = Math.max(d.size, i.horizontal ? o.height : o.width), i.size = d.size / d.count, t[n] += i.size;
  }
  if (o.getPadding)
    ko(a, o.getPadding());
  let r = Math.max(0, e.outerWidth - kn(a, t, "left", "right")), l = Math.max(0, e.outerHeight - kn(a, t, "top", "bottom")), c = r !== t.w, h = l !== t.h;
  return t.w = r, t.h = l, i.horizontal ? {
    same: c,
    other: h
  } : {
    same: h,
    other: c
  };
}
function il(t) {
  let e = t.maxPadding;
  function i(s) {
    let n = Math.max(e[s] - t[s], 0);
    return t[s] += n, n;
  }
  t.y += i("top"), t.x += i("left"), i("right"), i("bottom");
}
function sl(t, e) {
  let i = e.maxPadding;
  function s(n) {
    let o = {
      left: 0,
      top: 0,
      right: 0,
      bottom: 0
    };
    return n.forEach((a) => {
      o[a] = Math.max(e[a], i[a]);
    }), o;
  }
  return t ? s([
    "left",
    "right"
  ]) : s([
    "top",
    "bottom"
  ]);
}
function me(t, e, i, s) {
  let n = [], o, a, r, l, c, h;
  for (o = 0, a = t.length, c = 0;o < a; ++o) {
    r = t[o], l = r.box, l.update(r.width || e.w, r.height || e.h, sl(r.horizontal, e));
    let { same: d, other: u } = el(e, i, r, s);
    if (c |= d && n.length, h = h || u, !l.fullSize)
      n.push(r);
  }
  return c && me(n, e, i, s) || h;
}
function Xe(t, e, i, s, n) {
  t.top = i, t.left = e, t.right = e + s, t.bottom = i + n, t.width = s, t.height = n;
}
function wn(t, e, i, s) {
  let n = i.padding, { x: o, y: a } = e;
  for (let r of t) {
    let l = r.box, c = s[r.stack] || {
      count: 1,
      placed: 0,
      weight: 1
    }, h = r.stackWeight / c.weight || 1;
    if (r.horizontal) {
      let d = e.w * h, u = c.size || l.height;
      if (Kt(c.start))
        a = c.start;
      if (l.fullSize)
        Xe(l, n.left, a, i.outerWidth - n.right - n.left, u);
      else
        Xe(l, e.left + c.placed, a, d, u);
      c.start = a, c.placed += d, a = l.bottom;
    } else {
      let d = e.h * h, u = c.size || l.width;
      if (Kt(c.start))
        o = c.start;
      if (l.fullSize)
        Xe(l, o, n.top, u, i.outerHeight - n.bottom - n.top);
      else
        Xe(l, o, e.top + c.placed, u, d);
      c.start = o, c.placed += d, o = l.right;
    }
  }
  e.x = o, e.y = a;
}
var G = {
  addBox(t, e) {
    if (!t.boxes)
      t.boxes = [];
    e.fullSize = e.fullSize || !1, e.position = e.position || "top", e.weight = e.weight || 0, e._layers = e._layers || function() {
      return [
        {
          z: 0,
          draw(i) {
            e.draw(i);
          }
        }
      ];
    }, t.boxes.push(e);
  },
  removeBox(t, e) {
    let i = t.boxes ? t.boxes.indexOf(e) : -1;
    if (i !== -1)
      t.boxes.splice(i, 1);
  },
  configure(t, e, i) {
    e.fullSize = i.fullSize, e.position = i.position, e.weight = i.weight;
  },
  update(t, e, i, s) {
    if (!t)
      return;
    let n = q(t.options.layout.padding), o = Math.max(e - n.width, 0), a = Math.max(i - n.height, 0), r = tl(t.boxes), { vertical: l, horizontal: c } = r;
    L(t.boxes, (g) => {
      if (typeof g.beforeLayout === "function")
        g.beforeLayout();
    });
    let h = l.reduce((g, p) => p.box.options && p.box.options.display === !1 ? g : g + 1, 0) || 1, d = Object.freeze({
      outerWidth: e,
      outerHeight: i,
      padding: n,
      availableWidth: o,
      availableHeight: a,
      vBoxMaxWidth: o / 2 / h,
      hBoxMaxHeight: a / 2
    }), u = Object.assign({}, n);
    ko(u, q(s));
    let f = Object.assign({
      maxPadding: u,
      w: o,
      h: a,
      x: n.left,
      y: n.top
    }, n), m = Qr(l.concat(c), d);
    if (me(r.fullSize, f, d, m), me(l, f, d, m), me(c, f, d, m))
      me(l, f, d, m);
    il(f), wn(r.leftAndTop, f, d, m), f.x += f.w, f.y += f.h, wn(r.rightAndBottom, f, d, m), t.chartArea = {
      left: f.left,
      top: f.top,
      right: f.left + f.w,
      bottom: f.top + f.h,
      height: f.h,
      width: f.w
    }, L(r.chartArea, (g) => {
      let p = g.box;
      Object.assign(p, t.chartArea), p.update(f.w, f.h, {
        left: 0,
        top: 0,
        right: 0,
        bottom: 0
      });
    });
  }
};

class ps {
  acquireContext(t, e) {}
  releaseContext(t) {
    return !1;
  }
  addEventListener(t, e, i) {}
  removeEventListener(t, e, i) {}
  getDevicePixelRatio() {
    return 1;
  }
  getMaximumSize(t, e, i, s) {
    return e = Math.max(0, e || t.width), i = i || t.height, {
      width: e,
      height: Math.max(0, s ? Math.floor(e / s) : i)
    };
  }
  isAttached(t) {
    return !0;
  }
  updateConfig(t) {}
}

class wo extends ps {
  acquireContext(t) {
    return t && t.getContext && t.getContext("2d") || null;
  }
  updateConfig(t) {
    t.options.animation = !1;
  }
}
var Qe = "$chartjs", nl = {
  touchstart: "mousedown",
  touchmove: "mousemove",
  touchend: "mouseup",
  pointerenter: "mouseenter",
  pointerdown: "mousedown",
  pointermove: "mousemove",
  pointerup: "mouseup",
  pointerleave: "mouseout",
  pointerout: "mouseout"
}, Sn = (t) => t === null || t === "";
function ol(t, e) {
  let i = t.style, s = t.getAttribute("height"), n = t.getAttribute("width");
  if (t[Qe] = {
    initial: {
      height: s,
      width: n,
      style: {
        display: i.display,
        height: i.height,
        width: i.width
      }
    }
  }, i.display = i.display || "block", i.boxSizing = i.boxSizing || "border-box", Sn(n)) {
    let o = Vi(t, "width");
    if (o !== void 0)
      t.width = o;
  }
  if (Sn(s))
    if (t.style.height === "")
      t.height = t.width / (e || 2);
    else {
      let o = Vi(t, "height");
      if (o !== void 0)
        t.height = o;
    }
  return t;
}
var So = rn ? {
  passive: !0
} : !1;
function al(t, e, i) {
  if (t)
    t.addEventListener(e, i, So);
}
function rl(t, e, i) {
  if (t && t.canvas)
    t.canvas.removeEventListener(e, i, So);
}
function ll(t, e) {
  let i = nl[t.type] || t.type, { x: s, y: n } = Ct(t, e);
  return {
    type: i,
    chart: e,
    native: t,
    x: s !== void 0 ? s : null,
    y: n !== void 0 ? n : null
  };
}
function ei(t, e) {
  for (let i of t)
    if (i === e || i.contains(e))
      return !0;
}
function cl(t, e, i) {
  let s = t.canvas, n = new MutationObserver((o) => {
    let a = !1;
    for (let r of o)
      a = a || ei(r.addedNodes, s), a = a && !ei(r.removedNodes, s);
    if (a)
      i();
  });
  return n.observe(document, {
    childList: !0,
    subtree: !0
  }), n;
}
function hl(t, e, i) {
  let s = t.canvas, n = new MutationObserver((o) => {
    let a = !1;
    for (let r of o)
      a = a || ei(r.removedNodes, s), a = a && !ei(r.addedNodes, s);
    if (a)
      i();
  });
  return n.observe(document, {
    childList: !0,
    subtree: !0
  }), n;
}
var _e = /* @__PURE__ */ new Map, Pn = 0;
function Po() {
  let t = window.devicePixelRatio;
  if (t === Pn)
    return;
  Pn = t, _e.forEach((e, i) => {
    if (i.currentDevicePixelRatio !== t)
      e();
  });
}
function dl(t, e) {
  if (!_e.size)
    window.addEventListener("resize", Po);
  _e.set(t, e);
}
function ul(t) {
  if (_e.delete(t), !_e.size)
    window.removeEventListener("resize", Po);
}
function fl(t, e, i) {
  let s = t.canvas, n = s && Ye(s);
  if (!n)
    return;
  let o = Ai((r, l) => {
    let c = n.clientWidth;
    if (i(r, l), c < n.clientWidth)
      i();
  }, window), a = new ResizeObserver((r) => {
    let l = r[0], c = l.contentRect.width, h = l.contentRect.height;
    if (c === 0 && h === 0)
      return;
    o(c, h);
  });
  return a.observe(n), dl(t, o), a;
}
function Qi(t, e, i) {
  if (i)
    i.disconnect();
  if (e === "resize")
    ul(t);
}
function gl(t, e, i) {
  let s = t.canvas, n = Ai((o) => {
    if (t.ctx !== null)
      i(ll(o, t));
  }, t);
  return al(s, e, n), n;
}

class Do extends ps {
  acquireContext(t, e) {
    let i = t && t.getContext && t.getContext("2d");
    if (i && i.canvas === t)
      return ol(t, e), i;
    return null;
  }
  releaseContext(t) {
    let e = t.canvas;
    if (!e[Qe])
      return !1;
    let i = e[Qe].initial;
    [
      "height",
      "width"
    ].forEach((n) => {
      let o = i[n];
      if (A(o))
        e.removeAttribute(n);
      else
        e.setAttribute(n, o);
    });
    let s = i.style || {};
    return Object.keys(s).forEach((n) => {
      e.style[n] = s[n];
    }), e.width = e.width, delete e[Qe], !0;
  }
  addEventListener(t, e, i) {
    this.removeEventListener(t, e);
    let s = t.$proxies || (t.$proxies = {}), o = {
      attach: cl,
      detach: hl,
      resize: fl
    }[e] || gl;
    s[e] = o(t, e, i);
  }
  removeEventListener(t, e) {
    let i = t.$proxies || (t.$proxies = {}), s = i[e];
    if (!s)
      return;
    ({
      attach: Qi,
      detach: Qi,
      resize: Qi
    }[e] || rl)(t, e, s), i[e] = void 0;
  }
  getDevicePixelRatio() {
    return window.devicePixelRatio;
  }
  getMaximumSize(t, e, i, s) {
    return an(t, e, i, s);
  }
  isAttached(t) {
    let e = t && Ye(t);
    return !!(e && e.isConnected);
  }
}
function pl(t) {
  if (!$e() || typeof OffscreenCanvas < "u" && t instanceof OffscreenCanvas)
    return wo;
  return Do;
}

class ut {
  static defaults = {};
  static defaultRoutes = void 0;
  x;
  y;
  active = !1;
  options;
  $animations;
  tooltipPosition(t) {
    let { x: e, y: i } = this.getProps([
      "x",
      "y"
    ], t);
    return {
      x: e,
      y: i
    };
  }
  hasValue() {
    return Ft(this.x) && Ft(this.y);
  }
  getProps(t, e) {
    let i = this.$animations;
    if (!e || !i)
      return this;
    let s = {};
    return t.forEach((n) => {
      s[n] = i[n] && i[n].active() ? i[n]._to : this[n];
    }), s;
  }
}
function ml(t, e) {
  let i = t.options.ticks, s = bl(t), n = Math.min(i.maxTicksLimit || s, s), o = i.major.enabled ? _l(e) : [], a = o.length, r = o[0], l = o[a - 1], c = [];
  if (a > n)
    return yl(e, c, o, a / n), c;
  let h = xl(o, e, n);
  if (a > 0) {
    let d, u, f = a > 1 ? Math.round((l - r) / (a - 1)) : null;
    Ke(e, c, h, A(f) ? 0 : r - f, r);
    for (d = 0, u = a - 1;d < u; d++)
      Ke(e, c, h, o[d], o[d + 1]);
    return Ke(e, c, h, l, A(f) ? e.length : l + f), c;
  }
  return Ke(e, c, h), c;
}
function bl(t) {
  let e = t.options.offset, i = t._tickSize(), s = t._length / i + (e ? 0 : 1), n = t._maxLength / i;
  return Math.floor(Math.min(s, n));
}
function xl(t, e, i) {
  let s = vl(t), n = e.length / i;
  if (!s)
    return Math.max(n, 1);
  let o = Ws(s);
  for (let a = 0, r = o.length - 1;a < r; a++) {
    let l = o[a];
    if (l > n)
      return l;
  }
  return Math.max(n, 1);
}
function _l(t) {
  let e = [], i, s;
  for (i = 0, s = t.length;i < s; i++)
    if (t[i].major)
      e.push(i);
  return e;
}
function yl(t, e, i, s) {
  let n = 0, o = i[0], a;
  s = Math.ceil(s);
  for (a = 0;a < t.length; a++)
    if (a === o)
      e.push(t[a]), n++, o = i[n * s];
}
function Ke(t, e, i, s, n) {
  let o = P(s, 0), a = Math.min(P(n, t.length), t.length), r = 0, l, c, h;
  if (i = Math.ceil(i), n)
    l = n - s, i = l / Math.floor(l / i);
  h = o;
  while (h < 0)
    r++, h = Math.round(o + r * i);
  for (c = Math.max(o, 0);c < a; c++)
    if (c === h)
      e.push(t[c]), r++, h = Math.round(o + r * i);
}
function vl(t) {
  let e = t.length, i, s;
  if (e < 2)
    return !1;
  for (s = t[0], i = 1;i < e; ++i)
    if (t[i] - t[i - 1] !== s)
      return !1;
  return s;
}
var Ml = (t) => t === "left" ? "right" : t === "right" ? "left" : t, Dn = (t, e, i) => e === "top" || e === "left" ? t[e] + i : t[e] - i, An = (t, e) => Math.min(e || t, t);
function Cn(t, e) {
  let i = [], s = t.length / e, n = t.length, o = 0;
  for (;o < n; o += s)
    i.push(t[Math.floor(o)]);
  return i;
}
function kl(t, e, i) {
  let s = t.ticks.length, n = Math.min(e, s - 1), { _startPixel: o, _endPixel: a } = t, r = 0.000001, l = t.getPixelForTick(n), c;
  if (i) {
    if (s === 1)
      c = Math.max(l - o, a - l);
    else if (e === 0)
      c = (t.getPixelForTick(1) - l) / 2;
    else
      c = (l - t.getPixelForTick(n - 1)) / 2;
    if (l += n < e ? c : -c, l < o - 0.000001 || l > a + 0.000001)
      return;
  }
  return l;
}
function wl(t, e) {
  L(t, (i) => {
    let s = i.gc, n = s.length / 2, o;
    if (n > e) {
      for (o = 0;o < n; ++o)
        delete i.data[s[o]];
      s.splice(0, n);
    }
  });
}
function ge(t) {
  return t.drawTicks ? t.tickLength : 0;
}
function On(t, e) {
  if (!t.display)
    return 0;
  let i = H(t.font, e), s = q(t.padding);
  return (E(t.text) ? t.text.length : 1) * i.lineHeight + s.height;
}
function Sl(t, e) {
  return bt(t, {
    scale: e,
    type: "scale"
  });
}
function Pl(t, e, i) {
  return bt(t, {
    tick: i,
    index: e,
    type: "tick"
  });
}
function Dl(t, e, i) {
  let s = We(t);
  if (i && e !== "right" || !i && e === "right")
    s = Ml(s);
  return s;
}
function Al(t, e, i, s) {
  let { top: n, left: o, bottom: a, right: r, chart: l } = t, { chartArea: c, scales: h } = l, d = 0, u, f, m, g = a - n, p = r - o;
  if (t.isHorizontal()) {
    if (f = K(s, o, r), O(i)) {
      let b = Object.keys(i)[0], x = i[b];
      m = h[b].getPixelForValue(x) + g - e;
    } else if (i === "center")
      m = (c.bottom + c.top) / 2 + g - e;
    else
      m = Dn(t, i, e);
    u = r - o;
  } else {
    if (O(i)) {
      let b = Object.keys(i)[0], x = i[b];
      f = h[b].getPixelForValue(x) - p + e;
    } else if (i === "center")
      f = (c.left + c.right) / 2 - p + e;
    else
      f = Dn(t, i, e);
    m = K(s, a, n), d = i === "left" ? -N : N;
  }
  return {
    titleX: f,
    titleY: m,
    maxWidth: u,
    rotation: d
  };
}

class Nt extends ut {
  constructor(t) {
    super();
    this.id = t.id, this.type = t.type, this.options = void 0, this.ctx = t.ctx, this.chart = t.chart, this.top = void 0, this.bottom = void 0, this.left = void 0, this.right = void 0, this.width = void 0, this.height = void 0, this._margins = {
      left: 0,
      right: 0,
      top: 0,
      bottom: 0
    }, this.maxWidth = void 0, this.maxHeight = void 0, this.paddingTop = void 0, this.paddingBottom = void 0, this.paddingLeft = void 0, this.paddingRight = void 0, this.axis = void 0, this.labelRotation = void 0, this.min = void 0, this.max = void 0, this._range = void 0, this.ticks = [], this._gridLineItems = null, this._labelItems = null, this._labelSizes = null, this._length = 0, this._maxLength = 0, this._longestTextCache = {}, this._startPixel = void 0, this._endPixel = void 0, this._reversePixels = !1, this._userMax = void 0, this._userMin = void 0, this._suggestedMax = void 0, this._suggestedMin = void 0, this._ticksLength = 0, this._borderValue = 0, this._cache = {}, this._dataLimitsCached = !1, this.$context = void 0;
  }
  init(t) {
    this.options = t.setContext(this.getContext()), this.axis = t.axis, this._userMin = this.parse(t.min), this._userMax = this.parse(t.max), this._suggestedMin = this.parse(t.suggestedMin), this._suggestedMax = this.parse(t.suggestedMax);
  }
  parse(t, e) {
    return t;
  }
  getUserBounds() {
    let { _userMin: t, _userMax: e, _suggestedMin: i, _suggestedMax: s } = this;
    return t = J(t, Number.POSITIVE_INFINITY), e = J(e, Number.NEGATIVE_INFINITY), i = J(i, Number.POSITIVE_INFINITY), s = J(s, Number.NEGATIVE_INFINITY), {
      min: J(t, i),
      max: J(e, s),
      minDefined: W(t),
      maxDefined: W(e)
    };
  }
  getMinMax(t) {
    let { min: e, max: i, minDefined: s, maxDefined: n } = this.getUserBounds(), o;
    if (s && n)
      return {
        min: e,
        max: i
      };
    let a = this.getMatchingVisibleMetas();
    for (let r = 0, l = a.length;r < l; ++r) {
      if (o = a[r].controller.getMinMax(this, t), !s)
        e = Math.min(e, o.min);
      if (!n)
        i = Math.max(i, o.max);
    }
    return e = n && e > i ? i : e, i = s && e > i ? e : i, {
      min: J(e, J(i, e)),
      max: J(i, J(e, i))
    };
  }
  getPadding() {
    return {
      left: this.paddingLeft || 0,
      top: this.paddingTop || 0,
      right: this.paddingRight || 0,
      bottom: this.paddingBottom || 0
    };
  }
  getTicks() {
    return this.ticks;
  }
  getLabels() {
    let t = this.chart.data;
    return this.options.labels || (this.isHorizontal() ? t.xLabels : t.yLabels) || t.labels || [];
  }
  getLabelItems(t = this.chart.chartArea) {
    return this._labelItems || (this._labelItems = this._computeLabelItems(t));
  }
  beforeLayout() {
    this._cache = {}, this._dataLimitsCached = !1;
  }
  beforeUpdate() {
    R(this.options.beforeUpdate, [
      this
    ]);
  }
  update(t, e, i) {
    let { beginAtZero: s, grace: n, ticks: o } = this.options, a = o.sampleSize;
    if (this.beforeUpdate(), this.maxWidth = t, this.maxHeight = e, this._margins = i = Object.assign({
      left: 0,
      right: 0,
      top: 0,
      bottom: 0
    }, i), this.ticks = null, this._labelSizes = null, this._gridLineItems = null, this._labelItems = null, this.beforeSetDimensions(), this.setDimensions(), this.afterSetDimensions(), this._maxLength = this.isHorizontal() ? this.width + i.left + i.right : this.height + i.top + i.bottom, !this._dataLimitsCached)
      this.beforeDataLimits(), this.determineDataLimits(), this.afterDataLimits(), this._range = Qs(this, n, s), this._dataLimitsCached = !0;
    this.beforeBuildTicks(), this.ticks = this.buildTicks() || [], this.afterBuildTicks();
    let r = a < this.ticks.length;
    if (this._convertTicksToLabels(r ? Cn(this.ticks, a) : this.ticks), this.configure(), this.beforeCalculateLabelRotation(), this.calculateLabelRotation(), this.afterCalculateLabelRotation(), o.display && (o.autoSkip || o.source === "auto"))
      this.ticks = ml(this, this.ticks), this._labelSizes = null, this.afterAutoSkip();
    if (r)
      this._convertTicksToLabels(this.ticks);
    this.beforeFit(), this.fit(), this.afterFit(), this.afterUpdate();
  }
  configure() {
    let t = this.options.reverse, e, i;
    if (this.isHorizontal())
      e = this.left, i = this.right;
    else
      e = this.top, i = this.bottom, t = !t;
    this._startPixel = e, this._endPixel = i, this._reversePixels = t, this._length = i - e, this._alignToPixels = this.options.alignToPixels;
  }
  afterUpdate() {
    R(this.options.afterUpdate, [
      this
    ]);
  }
  beforeSetDimensions() {
    R(this.options.beforeSetDimensions, [
      this
    ]);
  }
  setDimensions() {
    if (this.isHorizontal())
      this.width = this.maxWidth, this.left = 0, this.right = this.width;
    else
      this.height = this.maxHeight, this.top = 0, this.bottom = this.height;
    this.paddingLeft = 0, this.paddingTop = 0, this.paddingRight = 0, this.paddingBottom = 0;
  }
  afterSetDimensions() {
    R(this.options.afterSetDimensions, [
      this
    ]);
  }
  _callHooks(t) {
    this.chart.notifyPlugins(t, this.getContext()), R(this.options[t], [
      this
    ]);
  }
  beforeDataLimits() {
    this._callHooks("beforeDataLimits");
  }
  determineDataLimits() {}
  afterDataLimits() {
    this._callHooks("afterDataLimits");
  }
  beforeBuildTicks() {
    this._callHooks("beforeBuildTicks");
  }
  buildTicks() {
    return [];
  }
  afterBuildTicks() {
    this._callHooks("afterBuildTicks");
  }
  beforeTickToLabelConversion() {
    R(this.options.beforeTickToLabelConversion, [
      this
    ]);
  }
  generateTickLabels(t) {
    let e = this.options.ticks, i, s, n;
    for (i = 0, s = t.length;i < s; i++)
      n = t[i], n.label = R(e.callback, [
        n.value,
        i,
        t
      ], this);
  }
  afterTickToLabelConversion() {
    R(this.options.afterTickToLabelConversion, [
      this
    ]);
  }
  beforeCalculateLabelRotation() {
    R(this.options.beforeCalculateLabelRotation, [
      this
    ]);
  }
  calculateLabelRotation() {
    let t = this.options, e = t.ticks, i = An(this.ticks.length, t.ticks.maxTicksLimit), s = e.minRotation || 0, n = e.maxRotation, o = s, a, r, l;
    if (!this._isVisible() || !e.display || s >= n || i <= 1 || !this.isHorizontal()) {
      this.labelRotation = s;
      return;
    }
    let c = this._getLabelSizes(), h = c.widest.width, d = c.highest.height, u = Y(this.chart.width - h, 0, this.maxWidth);
    if (a = t.offset ? this.maxWidth / i : u / (i - 1), h + 6 > a)
      a = u / (i - (t.offset ? 0.5 : 1)), r = this.maxHeight - ge(t.grid) - e.padding - On(t.title, this.chart.options.font), l = Math.sqrt(h * h + d * d), o = ze(Math.min(Math.asin(Y((c.highest.height + 6) / a, -1, 1)), Math.asin(Y(r / l, -1, 1)) - Math.asin(Y(d / l, -1, 1)))), o = Math.max(s, Math.min(n, o));
    this.labelRotation = o;
  }
  afterCalculateLabelRotation() {
    R(this.options.afterCalculateLabelRotation, [
      this
    ]);
  }
  afterAutoSkip() {}
  beforeFit() {
    R(this.options.beforeFit, [
      this
    ]);
  }
  fit() {
    let t = {
      width: 0,
      height: 0
    }, { chart: e, options: { ticks: i, title: s, grid: n } } = this, o = this._isVisible(), a = this.isHorizontal();
    if (o) {
      let r = On(s, e.options.font);
      if (a)
        t.width = this.maxWidth, t.height = ge(n) + r;
      else
        t.height = this.maxHeight, t.width = ge(n) + r;
      if (i.display && this.ticks.length) {
        let { first: l, last: c, widest: h, highest: d } = this._getLabelSizes(), u = i.padding * 2, f = st(this.labelRotation), m = Math.cos(f), g = Math.sin(f);
        if (a) {
          let p = i.mirror ? 0 : g * h.width + m * d.height;
          t.height = Math.min(this.maxHeight, t.height + p + u);
        } else {
          let p = i.mirror ? 0 : m * h.width + g * d.height;
          t.width = Math.min(this.maxWidth, t.width + p + u);
        }
        this._calculatePadding(l, c, g, m);
      }
    }
    if (this._handleMargins(), a)
      this.width = this._length = e.width - this._margins.left - this._margins.right, this.height = t.height;
    else
      this.width = t.width, this.height = this._length = e.height - this._margins.top - this._margins.bottom;
  }
  _calculatePadding(t, e, i, s) {
    let { ticks: { align: n, padding: o }, position: a } = this.options, r = this.labelRotation !== 0, l = a !== "top" && this.axis === "x";
    if (this.isHorizontal()) {
      let c = this.getPixelForTick(0) - this.left, h = this.right - this.getPixelForTick(this.ticks.length - 1), d = 0, u = 0;
      if (r)
        if (l)
          d = s * t.width, u = i * e.height;
        else
          d = i * t.height, u = s * e.width;
      else if (n === "start")
        u = e.width;
      else if (n === "end")
        d = t.width;
      else if (n !== "inner")
        d = t.width / 2, u = e.width / 2;
      this.paddingLeft = Math.max((d - c + o) * this.width / (this.width - c), 0), this.paddingRight = Math.max((u - h + o) * this.width / (this.width - h), 0);
    } else {
      let c = e.height / 2, h = t.height / 2;
      if (n === "start")
        c = 0, h = t.height;
      else if (n === "end")
        c = e.height, h = 0;
      this.paddingTop = c + o, this.paddingBottom = h + o;
    }
  }
  _handleMargins() {
    if (this._margins)
      this._margins.left = Math.max(this.paddingLeft, this._margins.left), this._margins.top = Math.max(this.paddingTop, this._margins.top), this._margins.right = Math.max(this.paddingRight, this._margins.right), this._margins.bottom = Math.max(this.paddingBottom, this._margins.bottom);
  }
  afterFit() {
    R(this.options.afterFit, [
      this
    ]);
  }
  isHorizontal() {
    let { axis: t, position: e } = this.options;
    return e === "top" || e === "bottom" || t === "x";
  }
  isFullSize() {
    return this.options.fullSize;
  }
  _convertTicksToLabels(t) {
    this.beforeTickToLabelConversion(), this.generateTickLabels(t);
    let e, i;
    for (e = 0, i = t.length;e < i; e++)
      if (A(t[e].label))
        t.splice(e, 1), i--, e--;
    this.afterTickToLabelConversion();
  }
  _getLabelSizes() {
    let t = this._labelSizes;
    if (!t) {
      let e = this.options.ticks.sampleSize, i = this.ticks;
      if (e < i.length)
        i = Cn(i, e);
      this._labelSizes = t = this._computeLabelSizes(i, i.length, this.options.ticks.maxTicksLimit);
    }
    return t;
  }
  _computeLabelSizes(t, e, i) {
    let { ctx: s, _longestTextCache: n } = this, o = [], a = [], r = Math.floor(e / An(e, i)), l = 0, c = 0, h, d, u, f, m, g, p, b, x, v, y;
    for (h = 0;h < e; h += r) {
      if (f = t[h].label, m = this._resolveTickFontOptions(h), s.font = g = m.string, p = n[g] = n[g] || {
        data: {},
        gc: []
      }, b = m.lineHeight, x = v = 0, !A(f) && !E(f))
        x = ae(s, p.data, p.gc, x, f), v = b;
      else if (E(f)) {
        for (d = 0, u = f.length;d < u; ++d)
          if (y = f[d], !A(y) && !E(y))
            x = ae(s, p.data, p.gc, x, y), v += b;
      }
      o.push(x), a.push(v), l = Math.max(x, l), c = Math.max(v, c);
    }
    wl(n, e);
    let _ = o.indexOf(l), M = a.indexOf(c), k = (S) => ({
      width: o[S] || 0,
      height: a[S] || 0
    });
    return {
      first: k(0),
      last: k(e - 1),
      widest: k(_),
      highest: k(M),
      widths: o,
      heights: a
    };
  }
  getLabelForValue(t) {
    return t;
  }
  getPixelForValue(t, e) {
    return NaN;
  }
  getValueForPixel(t) {}
  getPixelForTick(t) {
    let e = this.ticks;
    if (t < 0 || t > e.length - 1)
      return null;
    return this.getPixelForValue(e[t].value);
  }
  getPixelForDecimal(t) {
    if (this._reversePixels)
      t = 1 - t;
    let e = this._startPixel + t * this._length;
    return Vs(this._alignToPixels ? Pt(this.chart, e, 0) : e);
  }
  getDecimalForPixel(t) {
    let e = (t - this._startPixel) / this._length;
    return this._reversePixels ? 1 - e : e;
  }
  getBasePixel() {
    return this.getPixelForValue(this.getBaseValue());
  }
  getBaseValue() {
    let { min: t, max: e } = this;
    return t < 0 && e < 0 ? e : t > 0 && e > 0 ? t : 0;
  }
  getContext(t) {
    let e = this.ticks || [];
    if (t >= 0 && t < e.length) {
      let i = e[t];
      return i.$context || (i.$context = Pl(this.getContext(), t, i));
    }
    return this.$context || (this.$context = Sl(this.chart.getContext(), this));
  }
  _tickSize() {
    let t = this.options.ticks, e = st(this.labelRotation), i = Math.abs(Math.cos(e)), s = Math.abs(Math.sin(e)), n = this._getLabelSizes(), o = t.autoSkipPadding || 0, a = n ? n.widest.width + o : 0, r = n ? n.highest.height + o : 0;
    return this.isHorizontal() ? r * i > a * s ? a / i : r / s : r * s < a * i ? r / i : a / s;
  }
  _isVisible() {
    let t = this.options.display;
    if (t !== "auto")
      return !!t;
    return this.getMatchingVisibleMetas().length > 0;
  }
  _computeGridLineItems(t) {
    let e = this.axis, i = this.chart, s = this.options, { grid: n, position: o, border: a } = s, r = n.offset, l = this.isHorizontal(), h = this.ticks.length + (r ? 1 : 0), d = ge(n), u = [], f = a.setContext(this.getContext()), m = f.display ? f.width : 0, g = m / 2, p = function(B) {
      return Pt(i, B, m);
    }, b, x, v, y, _, M, k, S, w, C, D, V;
    if (o === "top")
      b = p(this.bottom), M = this.bottom - d, S = b - g, C = p(t.top) + g, V = t.bottom;
    else if (o === "bottom")
      b = p(this.top), C = t.top, V = p(t.bottom) - g, M = b + g, S = this.top + d;
    else if (o === "left")
      b = p(this.right), _ = this.right - d, k = b - g, w = p(t.left) + g, D = t.right;
    else if (o === "right")
      b = p(this.left), w = t.left, D = p(t.right) - g, _ = b + g, k = this.left + d;
    else if (e === "x") {
      if (o === "center")
        b = p((t.top + t.bottom) / 2 + 0.5);
      else if (O(o)) {
        let B = Object.keys(o)[0], j = o[B];
        b = p(this.chart.scales[B].getPixelForValue(j));
      }
      C = t.top, V = t.bottom, M = b + g, S = M + d;
    } else if (e === "y") {
      if (o === "center")
        b = p((t.left + t.right) / 2);
      else if (O(o)) {
        let B = Object.keys(o)[0], j = o[B];
        b = p(this.chart.scales[B].getPixelForValue(j));
      }
      _ = b - g, k = _ - d, w = t.left, D = t.right;
    }
    let tt = P(s.ticks.maxTicksLimit, h), z = Math.max(1, Math.ceil(h / tt));
    for (x = 0;x < h; x += z) {
      let B = this.getContext(x), j = n.setContext(B), et = a.setContext(B), { lineWidth: nt, color: U } = j, Se = et.dash || [], Vt = et.dashOffset, { tickWidth: ee, tickColor: Tt } = j, ie = j.tickBorderDash || [], Lt = j.tickBorderDashOffset;
      if (v = kl(this, x, r), v === void 0)
        continue;
      if (y = Pt(i, v, nt), l)
        _ = k = w = D = y;
      else
        M = S = C = V = y;
      u.push({
        tx1: _,
        ty1: M,
        tx2: k,
        ty2: S,
        x1: w,
        y1: C,
        x2: D,
        y2: V,
        width: nt,
        color: U,
        borderDash: Se,
        borderDashOffset: Vt,
        tickWidth: ee,
        tickColor: Tt,
        tickBorderDash: ie,
        tickBorderDashOffset: Lt
      });
    }
    return this._ticksLength = h, this._borderValue = b, u;
  }
  _computeLabelItems(t) {
    let e = this.axis, i = this.options, { position: s, ticks: n } = i, o = this.isHorizontal(), a = this.ticks, { align: r, crossAlign: l, padding: c, mirror: h } = n, d = ge(i.grid), u = d + c, f = h ? -c : u, m = -st(this.labelRotation), g = [], p, b, x, v, y, _, M, k, S, w, C, D, V = "middle";
    if (s === "top")
      _ = this.bottom - f, M = this._getXAxisLabelAlignment();
    else if (s === "bottom")
      _ = this.top + f, M = this._getXAxisLabelAlignment();
    else if (s === "left") {
      let z = this._getYAxisLabelAlignment(d);
      M = z.textAlign, y = z.x;
    } else if (s === "right") {
      let z = this._getYAxisLabelAlignment(d);
      M = z.textAlign, y = z.x;
    } else if (e === "x") {
      if (s === "center")
        _ = (t.top + t.bottom) / 2 + u;
      else if (O(s)) {
        let z = Object.keys(s)[0], B = s[z];
        _ = this.chart.scales[z].getPixelForValue(B) + u;
      }
      M = this._getXAxisLabelAlignment();
    } else if (e === "y") {
      if (s === "center")
        y = (t.left + t.right) / 2 - u;
      else if (O(s)) {
        let z = Object.keys(s)[0], B = s[z];
        y = this.chart.scales[z].getPixelForValue(B);
      }
      M = this._getYAxisLabelAlignment(d).textAlign;
    }
    if (e === "y") {
      if (r === "start")
        V = "top";
      else if (r === "end")
        V = "bottom";
    }
    let tt = this._getLabelSizes();
    for (p = 0, b = a.length;p < b; ++p) {
      x = a[p], v = x.label;
      let z = n.setContext(this.getContext(p));
      k = this.getPixelForTick(p) + n.labelOffset, S = this._resolveTickFontOptions(p), w = S.lineHeight, C = E(v) ? v.length : 1;
      let B = C / 2, { color: j, textStrokeColor: et, textStrokeWidth: nt } = z, U = M;
      if (o) {
        if (y = k, M === "inner")
          if (p === b - 1)
            U = !this.options.reverse ? "right" : "left";
          else if (p === 0)
            U = !this.options.reverse ? "left" : "right";
          else
            U = "center";
        if (s === "top")
          if (l === "near" || m !== 0)
            D = -C * w + w / 2;
          else if (l === "center")
            D = -tt.highest.height / 2 - B * w + w;
          else
            D = -tt.highest.height + w / 2;
        else if (l === "near" || m !== 0)
          D = w / 2;
        else if (l === "center")
          D = tt.highest.height / 2 - B * w;
        else
          D = tt.highest.height - C * w;
        if (h)
          D *= -1;
        if (m !== 0 && !z.showLabelBackdrop)
          y += w / 2 * Math.sin(m);
      } else
        _ = k, D = (1 - C) * w / 2;
      let Se;
      if (z.showLabelBackdrop) {
        let Vt = q(z.backdropPadding), ee = tt.heights[p], Tt = tt.widths[p], ie = D - Vt.top, Lt = 0 - Vt.left;
        switch (V) {
          case "middle":
            ie -= ee / 2;
            break;
          case "bottom":
            ie -= ee;
            break;
        }
        switch (M) {
          case "center":
            Lt -= Tt / 2;
            break;
          case "right":
            Lt -= Tt;
            break;
          case "inner":
            if (p === b - 1)
              Lt -= Tt;
            else if (p > 0)
              Lt -= Tt / 2;
            break;
        }
        Se = {
          left: Lt,
          top: ie,
          width: Tt + Vt.width,
          height: ee + Vt.height,
          color: z.backdropColor
        };
      }
      g.push({
        label: v,
        font: S,
        textOffset: D,
        options: {
          rotation: m,
          color: j,
          strokeColor: et,
          strokeWidth: nt,
          textAlign: U,
          textBaseline: V,
          translation: [
            y,
            _
          ],
          backdrop: Se
        }
      });
    }
    return g;
  }
  _getXAxisLabelAlignment() {
    let { position: t, ticks: e } = this.options;
    if (-st(this.labelRotation))
      return t === "top" ? "left" : "right";
    let s = "center";
    if (e.align === "start")
      s = "left";
    else if (e.align === "end")
      s = "right";
    else if (e.align === "inner")
      s = "inner";
    return s;
  }
  _getYAxisLabelAlignment(t) {
    let { position: e, ticks: { crossAlign: i, mirror: s, padding: n } } = this.options, o = this._getLabelSizes(), a = t + n, r = o.widest.width, l, c;
    if (e === "left")
      if (s)
        if (c = this.right + n, i === "near")
          l = "left";
        else if (i === "center")
          l = "center", c += r / 2;
        else
          l = "right", c += r;
      else if (c = this.right - a, i === "near")
        l = "right";
      else if (i === "center")
        l = "center", c -= r / 2;
      else
        l = "left", c = this.left;
    else if (e === "right")
      if (s)
        if (c = this.left + n, i === "near")
          l = "right";
        else if (i === "center")
          l = "center", c -= r / 2;
        else
          l = "left", c -= r;
      else if (c = this.left + a, i === "near")
        l = "left";
      else if (i === "center")
        l = "center", c += r / 2;
      else
        l = "right", c = this.right;
    else
      l = "right";
    return {
      textAlign: l,
      x: c
    };
  }
  _computeLabelArea() {
    if (this.options.ticks.mirror)
      return;
    let t = this.chart, e = this.options.position;
    if (e === "left" || e === "right")
      return {
        top: 0,
        left: this.left,
        bottom: t.height,
        right: this.right
      };
    if (e === "top" || e === "bottom")
      return {
        top: this.top,
        left: 0,
        bottom: this.bottom,
        right: t.width
      };
  }
  drawBackground() {
    let { ctx: t, options: { backgroundColor: e }, left: i, top: s, width: n, height: o } = this;
    if (e)
      t.save(), t.fillStyle = e, t.fillRect(i, s, n, o), t.restore();
  }
  getLineWidthForValue(t) {
    let e = this.options.grid;
    if (!this._isVisible() || !e.display)
      return 0;
    let s = this.ticks.findIndex((n) => n.value === t);
    if (s >= 0)
      return e.setContext(this.getContext(s)).lineWidth;
    return 0;
  }
  drawGrid(t) {
    let e = this.options.grid, i = this.ctx, s = this._gridLineItems || (this._gridLineItems = this._computeGridLineItems(t)), n, o, a = (r, l, c) => {
      if (!c.width || !c.color)
        return;
      i.save(), i.lineWidth = c.width, i.strokeStyle = c.color, i.setLineDash(c.borderDash || []), i.lineDashOffset = c.borderDashOffset, i.beginPath(), i.moveTo(r.x, r.y), i.lineTo(l.x, l.y), i.stroke(), i.restore();
    };
    if (e.display)
      for (n = 0, o = s.length;n < o; ++n) {
        let r = s[n];
        if (e.drawOnChartArea)
          a({
            x: r.x1,
            y: r.y1
          }, {
            x: r.x2,
            y: r.y2
          }, r);
        if (e.drawTicks)
          a({
            x: r.tx1,
            y: r.ty1
          }, {
            x: r.tx2,
            y: r.ty2
          }, {
            color: r.tickColor,
            width: r.tickWidth,
            borderDash: r.tickBorderDash,
            borderDashOffset: r.tickBorderDashOffset
          });
      }
  }
  drawBorder() {
    let { chart: t, ctx: e, options: { border: i, grid: s } } = this, n = i.setContext(this.getContext()), o = i.display ? n.width : 0;
    if (!o)
      return;
    let a = s.setContext(this.getContext(0)).lineWidth, r = this._borderValue, l, c, h, d;
    if (this.isHorizontal())
      l = Pt(t, this.left, o) - o / 2, c = Pt(t, this.right, a) + a / 2, h = d = r;
    else
      h = Pt(t, this.top, o) - o / 2, d = Pt(t, this.bottom, a) + a / 2, l = c = r;
    e.save(), e.lineWidth = n.width, e.strokeStyle = n.color, e.beginPath(), e.moveTo(l, h), e.lineTo(c, d), e.stroke(), e.restore();
  }
  drawLabels(t) {
    if (!this.options.ticks.display)
      return;
    let i = this.ctx, s = this._computeLabelArea();
    if (s)
      ce(i, s);
    let n = this.getLabelItems(t);
    for (let o of n) {
      let { options: a, font: r, label: l, textOffset: c } = o;
      Dt(i, l, 0, c, r, a);
    }
    if (s)
      he(i);
  }
  drawTitle() {
    let { ctx: t, options: { position: e, title: i, reverse: s } } = this;
    if (!i.display)
      return;
    let n = H(i.font), o = q(i.padding), a = i.align, r = n.lineHeight / 2;
    if (e === "bottom" || e === "center" || O(e)) {
      if (r += o.bottom, E(i.text))
        r += n.lineHeight * (i.text.length - 1);
    } else
      r += o.top;
    let { titleX: l, titleY: c, maxWidth: h, rotation: d } = Al(this, r, e, a);
    Dt(t, i.text, 0, 0, n, {
      color: i.color,
      maxWidth: h,
      rotation: d,
      textAlign: Dl(a, e, s),
      textBaseline: "middle",
      translation: [
        l,
        c
      ]
    });
  }
  draw(t) {
    if (!this._isVisible())
      return;
    this.drawBackground(), this.drawGrid(t), this.drawBorder(), this.drawTitle(), this.drawLabels(t);
  }
  _layers() {
    let t = this.options, e = t.ticks && t.ticks.z || 0, i = P(t.grid && t.grid.z, -1), s = P(t.border && t.border.z, 0);
    if (!this._isVisible() || this.draw !== Nt.prototype.draw)
      return [
        {
          z: e,
          draw: (n) => {
            this.draw(n);
          }
        }
      ];
    return [
      {
        z: i,
        draw: (n) => {
          this.drawBackground(), this.drawGrid(n), this.drawTitle();
        }
      },
      {
        z: s,
        draw: () => {
          this.drawBorder();
        }
      },
      {
        z: e,
        draw: (n) => {
          this.drawLabels(n);
        }
      }
    ];
  }
  getMatchingVisibleMetas(t) {
    let e = this.chart.getSortedVisibleDatasetMetas(), i = this.axis + "AxisID", s = [], n, o;
    for (n = 0, o = e.length;n < o; ++n) {
      let a = e[n];
      if (a[i] === this.id && (!t || a.type === t))
        s.push(a);
    }
    return s;
  }
  _resolveTickFontOptions(t) {
    let e = this.options.ticks.setContext(this.getContext(t));
    return H(e.font);
  }
  _maxDigits() {
    let t = this._resolveTickFontOptions(0).lineHeight;
    return (this.isHorizontal() ? this.width : this.height) / t;
  }
}

class be {
  constructor(t, e, i) {
    this.type = t, this.scope = e, this.override = i, this.items = Object.create(null);
  }
  isForType(t) {
    return Object.prototype.isPrototypeOf.call(this.type.prototype, t.prototype);
  }
  register(t) {
    let e = Object.getPrototypeOf(t), i;
    if (Tl(e))
      i = this.register(e);
    let s = this.items, n = t.id, o = this.scope + "." + n;
    if (!n)
      throw Error("class does not have id: " + t);
    if (n in s)
      return o;
    if (s[n] = t, Cl(t, o, i), this.override)
      F.override(t.id, t.overrides);
    return o;
  }
  get(t) {
    return this.items[t];
  }
  unregister(t) {
    let e = this.items, i = t.id, s = this.scope;
    if (i in e)
      delete e[i];
    if (s && i in F[s]) {
      if (delete F[s][i], this.override)
        delete St[i];
    }
  }
}
function Cl(t, e, i) {
  let s = Yt(Object.create(null), [
    i ? F.get(i) : {},
    F.get(e),
    t.defaults
  ]);
  if (F.set(e, s), t.defaultRoutes)
    Ol(e, t.defaultRoutes);
  if (t.descriptors)
    F.describe(e, t.descriptors);
}
function Ol(t, e) {
  Object.keys(e).forEach((i) => {
    let s = i.split("."), n = s.pop(), o = [
      t
    ].concat(s).join("."), a = e[i].split("."), r = a.pop(), l = a.join(".");
    F.route(o, n, l, r);
  });
}
function Tl(t) {
  return "id" in t && "defaults" in t;
}

class Ao {
  constructor() {
    this.controllers = new be(yt, "datasets", !0), this.elements = new be(ut, "elements"), this.plugins = new be(Object, "plugins"), this.scales = new be(Nt, "scales"), this._typedRegistries = [
      this.controllers,
      this.scales,
      this.elements
    ];
  }
  add(...t) {
    this._each("register", t);
  }
  remove(...t) {
    this._each("unregister", t);
  }
  addControllers(...t) {
    this._each("register", t, this.controllers);
  }
  addElements(...t) {
    this._each("register", t, this.elements);
  }
  addPlugins(...t) {
    this._each("register", t, this.plugins);
  }
  addScales(...t) {
    this._each("register", t, this.scales);
  }
  getController(t) {
    return this._get(t, this.controllers, "controller");
  }
  getElement(t) {
    return this._get(t, this.elements, "element");
  }
  getPlugin(t) {
    return this._get(t, this.plugins, "plugin");
  }
  getScale(t) {
    return this._get(t, this.scales, "scale");
  }
  removeControllers(...t) {
    this._each("unregister", t, this.controllers);
  }
  removeElements(...t) {
    this._each("unregister", t, this.elements);
  }
  removePlugins(...t) {
    this._each("unregister", t, this.plugins);
  }
  removeScales(...t) {
    this._each("unregister", t, this.scales);
  }
  _each(t, e, i) {
    [
      ...e
    ].forEach((s) => {
      let n = i || this._getRegistryForType(s);
      if (i || n.isForType(s) || n === this.plugins && s.id)
        this._exec(t, n, s);
      else
        L(s, (o) => {
          let a = i || this._getRegistryForType(o);
          this._exec(t, a, o);
        });
    });
  }
  _exec(t, e, i) {
    let s = Fe(t);
    R(i["before" + s], [], i), e[t](i), R(i["after" + s], [], i);
  }
  _getRegistryForType(t) {
    for (let e = 0;e < this._typedRegistries.length; e++) {
      let i = this._typedRegistries[e];
      if (i.isForType(t))
        return i;
    }
    return this.plugins;
  }
  _get(t, e, i) {
    let s = e.get(t);
    if (s === void 0)
      throw Error('"' + t + '" is not a registered ' + i + ".");
    return s;
  }
}
var dt = /* @__PURE__ */ new Ao;

class Co {
  constructor() {
    this._init = void 0;
  }
  notify(t, e, i, s) {
    if (e === "beforeInit")
      this._init = this._createDescriptors(t, !0), this._notify(this._init, t, "install");
    if (this._init === void 0)
      return;
    let n = s ? this._descriptors(t).filter(s) : this._descriptors(t), o = this._notify(n, t, e, i);
    if (e === "afterDestroy")
      this._notify(n, t, "stop"), this._notify(this._init, t, "uninstall"), this._init = void 0;
    return o;
  }
  _notify(t, e, i, s) {
    s = s || {};
    for (let n of t) {
      let o = n.plugin, a = o[i], r = [
        e,
        s,
        n.options
      ];
      if (R(a, r, o) === !1 && s.cancelable)
        return !1;
    }
    return !0;
  }
  invalidate() {
    if (!A(this._cache))
      this._oldCache = this._cache, this._cache = void 0;
  }
  _descriptors(t) {
    if (this._cache)
      return this._cache;
    let e = this._cache = this._createDescriptors(t);
    return this._notifyStateChanges(t), e;
  }
  _createDescriptors(t, e) {
    let i = t && t.config, s = P(i.options && i.options.plugins, {}), n = Ll(i);
    return s === !1 && !e ? [] : El(t, n, s, e);
  }
  _notifyStateChanges(t) {
    let e = this._oldCache || [], i = this._cache, s = (n, o) => n.filter((a) => !o.some((r) => a.plugin.id === r.plugin.id));
    this._notify(s(e, i), t, "stop"), this._notify(s(i, e), t, "start");
  }
}
function Ll(t) {
  let e = {}, i = [], s = Object.keys(dt.plugins.items);
  for (let o = 0;o < s.length; o++)
    i.push(dt.getPlugin(s[o]));
  let n = t.plugins || [];
  for (let o = 0;o < n.length; o++) {
    let a = n[o];
    if (i.indexOf(a) === -1)
      i.push(a), e[a.id] = !0;
  }
  return {
    plugins: i,
    localIds: e
  };
}
function Rl(t, e) {
  if (!e && t === !1)
    return null;
  if (t === !0)
    return {};
  return t;
}
function El(t, { plugins: e, localIds: i }, s, n) {
  let o = [], a = t.getContext();
  for (let r of e) {
    let l = r.id, c = Rl(s[l], n);
    if (c === null)
      continue;
    o.push({
      plugin: r,
      options: Il(t.config, {
        plugin: r,
        local: i[l]
      }, c, a)
    });
  }
  return o;
}
function Il(t, { plugin: e, local: i }, s, n) {
  let o = t.pluginScopeKeys(e), a = t.getOptionScopes(s, o);
  if (i && e.defaults)
    a.push(e.defaults);
  return t.createResolver(a, n, [
    ""
  ], {
    scriptable: !1,
    indexable: !1,
    allKeys: !0
  });
}
function ns(t, e) {
  let i = F.datasets[t] || {};
  return ((e.datasets || {})[t] || {}).indexAxis || e.indexAxis || i.indexAxis || "x";
}
function Fl(t, e) {
  let i = t;
  if (t === "_index_")
    i = e;
  else if (t === "_value_")
    i = e === "x" ? "y" : "x";
  return i;
}
function zl(t, e) {
  return t === e ? "_index_" : "_value_";
}
function Tn(t) {
  if (t === "x" || t === "y" || t === "r")
    return t;
}
function Bl(t) {
  if (t === "top" || t === "bottom")
    return "x";
  if (t === "left" || t === "right")
    return "y";
}
function os(t, ...e) {
  if (Tn(t))
    return t;
  for (let i of e) {
    let s = i.axis || Bl(i.position) || t.length > 1 && Tn(t[0].toLowerCase());
    if (s)
      return s;
  }
  throw Error(`Cannot determine type of '${t}' axis. Please provide 'axis' or 'position' option.`);
}
function Ln(t, e, i) {
  if (i[e + "AxisID"] === t)
    return {
      axis: e
    };
}
function Wl(t, e) {
  if (e.data && e.data.datasets) {
    let i = e.data.datasets.filter((s) => s.xAxisID === t || s.yAxisID === t);
    if (i.length)
      return Ln(t, "x", i[0]) || Ln(t, "y", i[0]);
  }
  return {};
}
function Nl(t, e) {
  let i = St[t.type] || {
    scales: {}
  }, s = e.scales || {}, n = ns(t.type, e), o = Object.create(null);
  return Object.keys(s).forEach((a) => {
    let r = s[a];
    if (!O(r))
      return console.error(`Invalid scale configuration for scale: ${a}`);
    if (r._proxy)
      return console.warn(`Ignoring resolver passed as options for scale: ${a}`);
    let l = os(a, r, Wl(a, t), F.scales[r.type]), c = zl(l, n), h = i.scales || {};
    o[a] = Xt(Object.create(null), [
      {
        axis: l
      },
      r,
      h[l],
      h[c]
    ]);
  }), t.data.datasets.forEach((a) => {
    let r = a.type || t.type, l = a.indexAxis || ns(r, e), h = (St[r] || {}).scales || {};
    Object.keys(h).forEach((d) => {
      let u = Fl(d, l), f = a[u + "AxisID"] || u;
      o[f] = o[f] || Object.create(null), Xt(o[f], [
        {
          axis: u
        },
        s[f],
        h[d]
      ]);
    });
  }), Object.keys(o).forEach((a) => {
    let r = o[a];
    Xt(r, [
      F.scales[r.type],
      F.scale
    ]);
  }), o;
}
function Oo(t) {
  let e = t.options || (t.options = {});
  e.plugins = P(e.plugins, {}), e.scales = Nl(t, e);
}
function To(t) {
  return t = t || {}, t.datasets = t.datasets || [], t.labels = t.labels || [], t;
}
function Vl(t) {
  return t = t || {}, t.data = To(t.data), Oo(t), t;
}
var Rn = /* @__PURE__ */ new Map, Lo = /* @__PURE__ */ new Set;
function qe(t, e) {
  let i = Rn.get(t);
  if (!i)
    i = e(), Rn.set(t, i), Lo.add(i);
  return i;
}
var pe = (t, e, i) => {
  let s = mt(e, i);
  if (s !== void 0)
    t.add(s);
};

class Ro {
  constructor(t) {
    this._config = Vl(t), this._scopeCache = /* @__PURE__ */ new Map, this._resolverCache = /* @__PURE__ */ new Map;
  }
  get platform() {
    return this._config.platform;
  }
  get type() {
    return this._config.type;
  }
  set type(t) {
    this._config.type = t;
  }
  get data() {
    return this._config.data;
  }
  set data(t) {
    this._config.data = To(t);
  }
  get options() {
    return this._config.options;
  }
  set options(t) {
    this._config.options = t;
  }
  get plugins() {
    return this._config.plugins;
  }
  update() {
    let t = this._config;
    this.clearCache(), Oo(t);
  }
  clearCache() {
    this._scopeCache.clear(), this._resolverCache.clear();
  }
  datasetScopeKeys(t) {
    return qe(t, () => [
      [
        `datasets.${t}`,
        ""
      ]
    ]);
  }
  datasetAnimationScopeKeys(t, e) {
    return qe(`${t}.transition.${e}`, () => [
      [
        `datasets.${t}.transitions.${e}`,
        `transitions.${e}`
      ],
      [
        `datasets.${t}`,
        ""
      ]
    ]);
  }
  datasetElementScopeKeys(t, e) {
    return qe(`${t}-${e}`, () => [
      [
        `datasets.${t}.elements.${e}`,
        `datasets.${t}`,
        `elements.${e}`,
        ""
      ]
    ]);
  }
  pluginScopeKeys(t) {
    let e = t.id, i = this.type;
    return qe(`${i}-plugin-${e}`, () => [
      [
        `plugins.${e}`,
        ...t.additionalOptionScopes || []
      ]
    ]);
  }
  _cachedScopes(t, e) {
    let i = this._scopeCache, s = i.get(t);
    if (!s || e)
      s = /* @__PURE__ */ new Map, i.set(t, s);
    return s;
  }
  getOptionScopes(t, e, i) {
    let { options: s, type: n } = this, o = this._cachedScopes(t, i), a = o.get(e);
    if (a)
      return a;
    let r = /* @__PURE__ */ new Set;
    e.forEach((c) => {
      if (t)
        r.add(t), c.forEach((h) => pe(r, t, h));
      c.forEach((h) => pe(r, s, h)), c.forEach((h) => pe(r, St[n] || {}, h)), c.forEach((h) => pe(r, F, h)), c.forEach((h) => pe(r, Ne, h));
    });
    let l = Array.from(r);
    if (l.length === 0)
      l.push(Object.create(null));
    if (Lo.has(e))
      o.set(e, l);
    return l;
  }
  chartOptionScopes() {
    let { options: t, type: e } = this;
    return [
      t,
      St[e] || {},
      F.datasets[e] || {},
      {
        type: e
      },
      F,
      Ne
    ];
  }
  resolveNamedOptions(t, e, i, s = [
    ""
  ]) {
    let n = {
      $shared: !0
    }, { resolver: o, subPrefixes: a } = En(this._resolverCache, t, s), r = o;
    if (jl(o, e)) {
      n.$shared = !1, i = gt(i) ? i() : i;
      let l = this.createResolver(t, i, a);
      r = It(o, i, l);
    }
    for (let l of e)
      n[l] = r[l];
    return n;
  }
  createResolver(t, e, i = [
    ""
  ], s) {
    let { resolver: n } = En(this._resolverCache, t, i);
    return O(e) ? It(n, e, void 0, s) : n;
  }
}
function En(t, e, i) {
  let s = t.get(e);
  if (!s)
    s = /* @__PURE__ */ new Map, t.set(e, s);
  let n = i.join(), o = s.get(n);
  if (!o)
    o = {
      resolver: je(e, i),
      subPrefixes: i.filter((r) => !r.toLowerCase().includes("hover"))
    }, s.set(n, o);
  return o;
}
var Hl = (t) => O(t) && Object.getOwnPropertyNames(t).some((e) => gt(t[e]));
function jl(t, e) {
  let { isScriptable: i, isIndexable: s } = Fi(t);
  for (let n of e) {
    let o = i(n), a = s(n), r = (a || o) && t[n];
    if (o && (gt(r) || Hl(r)) || a && E(r))
      return !0;
  }
  return !1;
}
var $l = "4.5.1", Yl = [
  "top",
  "bottom",
  "left",
  "right",
  "chartArea"
];
function In(t, e) {
  return t === "top" || t === "bottom" || Yl.indexOf(t) === -1 && e === "x";
}
function Fn(t, e) {
  return function(i, s) {
    return i[t] === s[t] ? i[e] - s[e] : i[t] - s[t];
  };
}
function zn(t) {
  let e = t.chart, i = e.options.animation;
  e.notifyPlugins("afterRender"), R(i && i.onComplete, [
    t
  ], e);
}
function Ul(t) {
  let e = t.chart, i = e.options.animation;
  R(i && i.onProgress, [
    t
  ], e);
}
function Eo(t) {
  if ($e() && typeof t === "string")
    t = document.getElementById(t);
  else if (t && t.length)
    t = t[0];
  if (t && t.canvas)
    t = t.canvas;
  return t;
}
var ti = {}, Bn = (t) => {
  let e = Eo(t);
  return Object.values(ti).filter((i) => i.canvas === e).pop();
};
function Xl(t, e, i) {
  let s = Object.keys(t);
  for (let n of s) {
    let o = +n;
    if (o >= e) {
      let a = t[n];
      if (delete t[n], i > 0 || o > e)
        t[o + i] = a;
    }
  }
}
function Kl(t, e, i, s) {
  if (!i || t.type === "mouseout")
    return null;
  if (s)
    return e;
  return t;
}

class ke {
  static defaults = F;
  static instances = ti;
  static overrides = St;
  static registry = dt;
  static version = $l;
  static getChart = Bn;
  static register(...t) {
    dt.add(...t), Wn();
  }
  static unregister(...t) {
    dt.remove(...t), Wn();
  }
  constructor(t, e) {
    let i = this.config = new Ro(e), s = Eo(t), n = Bn(s);
    if (n)
      throw Error("Canvas is already in use. Chart with ID '" + n.id + "' must be destroyed before the canvas with ID '" + n.canvas.id + "' can be reused.");
    let o = i.createResolver(i.chartOptionScopes(), this.getContext());
    this.platform = new (i.platform || pl(s)), this.platform.updateConfig(i);
    let a = this.platform.acquireContext(s, o.aspectRatio), r = a && a.canvas, l = r && r.height, c = r && r.width;
    if (this.id = Is(), this.ctx = a, this.canvas = r, this.width = c, this.height = l, this._options = o, this._aspectRatio = this.aspectRatio, this._layers = [], this._metasets = [], this._stacks = void 0, this.boxes = [], this.currentDevicePixelRatio = void 0, this.chartArea = void 0, this._active = [], this._lastEvent = void 0, this._listeners = {}, this._responsiveListeners = void 0, this._sortedMetasets = [], this.scales = {}, this._plugins = new Co, this.$proxies = {}, this._hiddenIndices = {}, this.attached = !1, this._animationsDisabled = void 0, this.$context = void 0, this._doResize = Us((h) => this.update(h), o.resizeDelay || 0), this._dataChanges = [], ti[this.id] = this, !a || !r) {
      console.error("Failed to create chart: can't acquire context from the given item");
      return;
    }
    if (xt.listen(this, "complete", zn), xt.listen(this, "progress", Ul), this._initialize(), this.attached)
      this.update();
  }
  get aspectRatio() {
    let { options: { aspectRatio: t, maintainAspectRatio: e }, width: i, height: s, _aspectRatio: n } = this;
    if (!A(t))
      return t;
    if (e && n)
      return n;
    return s ? i / s : null;
  }
  get data() {
    return this.config.data;
  }
  set data(t) {
    this.config.data = t;
  }
  get options() {
    return this._options;
  }
  set options(t) {
    this.config.options = t;
  }
  get registry() {
    return dt;
  }
  _initialize() {
    if (this.notifyPlugins("beforeInit"), this.options.responsive)
      this.resize();
    else
      Ni(this, this.options.devicePixelRatio);
    return this.bindEvents(), this.notifyPlugins("afterInit"), this;
  }
  clear() {
    return Ri(this.canvas, this.ctx), this;
  }
  stop() {
    return xt.stop(this), this;
  }
  resize(t, e) {
    if (!xt.running(this))
      this._resize(t, e);
    else
      this._resizeBeforeDraw = {
        width: t,
        height: e
      };
  }
  _resize(t, e) {
    let i = this.options, s = this.canvas, n = i.maintainAspectRatio && this.aspectRatio, o = this.platform.getMaximumSize(s, t, e, n), a = i.devicePixelRatio || this.platform.getDevicePixelRatio(), r = this.width ? "resize" : "attach";
    if (this.width = o.width, this.height = o.height, this._aspectRatio = this.aspectRatio, !Ni(this, a, !0))
      return;
    if (this.notifyPlugins("resize", {
      size: o
    }), R(i.onResize, [
      this,
      o
    ], this), this.attached) {
      if (this._doResize(r))
        this.render();
    }
  }
  ensureScalesHaveIDs() {
    let e = this.options.scales || {};
    L(e, (i, s) => {
      i.id = s;
    });
  }
  buildOrUpdateScales() {
    let t = this.options, e = t.scales, i = this.scales, s = Object.keys(i).reduce((o, a) => (o[a] = !1, o), {}), n = [];
    if (e)
      n = n.concat(Object.keys(e).map((o) => {
        let a = e[o], r = os(o, a), l = r === "r", c = r === "x";
        return {
          options: a,
          dposition: l ? "chartArea" : c ? "bottom" : "left",
          dtype: l ? "radialLinear" : c ? "category" : "linear"
        };
      }));
    L(n, (o) => {
      let a = o.options, r = a.id, l = os(r, a), c = P(a.type, o.dtype);
      if (a.position === void 0 || In(a.position, l) !== In(o.dposition))
        a.position = o.dposition;
      s[r] = !0;
      let h = null;
      if (r in i && i[r].type === c)
        h = i[r];
      else
        h = new (dt.getScale(c))({
          id: r,
          type: c,
          ctx: this.ctx,
          chart: this
        }), i[h.id] = h;
      h.init(a, t);
    }), L(s, (o, a) => {
      if (!o)
        delete i[a];
    }), L(i, (o) => {
      G.configure(this, o, o.options), G.addBox(this, o);
    });
  }
  _updateMetasets() {
    let t = this._metasets, e = this.data.datasets.length, i = t.length;
    if (t.sort((s, n) => s.index - n.index), i > e) {
      for (let s = e;s < i; ++s)
        this._destroyDatasetMeta(s);
      t.splice(e, i - e);
    }
    this._sortedMetasets = t.slice(0).sort(Fn("order", "index"));
  }
  _removeUnreferencedMetasets() {
    let { _metasets: t, data: { datasets: e } } = this;
    if (t.length > e.length)
      delete this._stacks;
    t.forEach((i, s) => {
      if (e.filter((n) => n === i._dataset).length === 0)
        this._destroyDatasetMeta(s);
    });
  }
  buildOrUpdateControllers() {
    let t = [], e = this.data.datasets, i, s;
    this._removeUnreferencedMetasets();
    for (i = 0, s = e.length;i < s; i++) {
      let n = e[i], o = this.getDatasetMeta(i), a = n.type || this.config.type;
      if (o.type && o.type !== a)
        this._destroyDatasetMeta(i), o = this.getDatasetMeta(i);
      if (o.type = a, o.indexAxis = n.indexAxis || ns(a, this.options), o.order = n.order || 0, o.index = i, o.label = "" + n.label, o.visible = this.isDatasetVisible(i), o.controller)
        o.controller.updateIndex(i), o.controller.linkScales();
      else {
        let r = dt.getController(a), { datasetElementType: l, dataElementType: c } = F.datasets[a];
        Object.assign(r, {
          dataElementType: dt.getElement(c),
          datasetElementType: l && dt.getElement(l)
        }), o.controller = new r(this, i), t.push(o.controller);
      }
    }
    return this._updateMetasets(), t;
  }
  _resetElements() {
    L(this.data.datasets, (t, e) => {
      this.getDatasetMeta(e).controller.reset();
    }, this);
  }
  reset() {
    this._resetElements(), this.notifyPlugins("reset");
  }
  update(t) {
    let e = this.config;
    e.update();
    let i = this._options = e.createResolver(e.chartOptionScopes(), this.getContext()), s = this._animationsDisabled = !i.animation;
    if (this._updateScales(), this._checkEventBindings(), this._updateHiddenIndices(), this._plugins.invalidate(), this.notifyPlugins("beforeUpdate", {
      mode: t,
      cancelable: !0
    }) === !1)
      return;
    let n = this.buildOrUpdateControllers();
    this.notifyPlugins("beforeElementsUpdate");
    let o = 0;
    for (let l = 0, c = this.data.datasets.length;l < c; l++) {
      let { controller: h } = this.getDatasetMeta(l), d = !s && n.indexOf(h) === -1;
      h.buildOrUpdateElements(d), o = Math.max(+h.getMaxOverflow(), o);
    }
    if (o = this._minPadding = i.layout.autoPadding ? o : 0, this._updateLayout(o), !s)
      L(n, (l) => {
        l.reset();
      });
    this._updateDatasets(t), this.notifyPlugins("afterUpdate", {
      mode: t
    }), this._layers.sort(Fn("z", "_idx"));
    let { _active: a, _lastEvent: r } = this;
    if (r)
      this._eventHandler(r, !0);
    else if (a.length)
      this._updateHoverStyles(a, a, !0);
    this.render();
  }
  _updateScales() {
    L(this.scales, (t) => {
      G.removeBox(this, t);
    }), this.ensureScalesHaveIDs(), this.buildOrUpdateScales();
  }
  _checkEventBindings() {
    let t = this.options, e = new Set(Object.keys(this._listeners)), i = new Set(t.events);
    if (!yi(e, i) || !!this._responsiveListeners !== t.responsive)
      this.unbindEvents(), this.bindEvents();
  }
  _updateHiddenIndices() {
    let { _hiddenIndices: t } = this, e = this._getUniformDataChanges() || [];
    for (let { method: i, start: s, count: n } of e) {
      let o = i === "_removeElements" ? -n : n;
      Xl(t, s, o);
    }
  }
  _getUniformDataChanges() {
    let t = this._dataChanges;
    if (!t || !t.length)
      return;
    this._dataChanges = [];
    let e = this.data.datasets.length, i = (n) => new Set(t.filter((o) => o[0] === n).map((o, a) => a + "," + o.splice(1).join(","))), s = i(0);
    for (let n = 1;n < e; n++)
      if (!yi(s, i(n)))
        return;
    return Array.from(s).map((n) => n.split(",")).map((n) => ({
      method: n[1],
      start: +n[2],
      count: +n[3]
    }));
  }
  _updateLayout(t) {
    if (this.notifyPlugins("beforeLayout", {
      cancelable: !0
    }) === !1)
      return;
    G.update(this, this.width, this.height, t);
    let e = this.chartArea, i = e.width <= 0 || e.height <= 0;
    this._layers = [], L(this.boxes, (s) => {
      if (i && s.position === "chartArea")
        return;
      if (s.configure)
        s.configure();
      this._layers.push(...s._layers());
    }, this), this._layers.forEach((s, n) => {
      s._idx = n;
    }), this.notifyPlugins("afterLayout");
  }
  _updateDatasets(t) {
    if (this.notifyPlugins("beforeDatasetsUpdate", {
      mode: t,
      cancelable: !0
    }) === !1)
      return;
    for (let e = 0, i = this.data.datasets.length;e < i; ++e)
      this.getDatasetMeta(e).controller.configure();
    for (let e = 0, i = this.data.datasets.length;e < i; ++e)
      this._updateDataset(e, gt(t) ? t({
        datasetIndex: e
      }) : t);
    this.notifyPlugins("afterDatasetsUpdate", {
      mode: t
    });
  }
  _updateDataset(t, e) {
    let i = this.getDatasetMeta(t), s = {
      meta: i,
      index: t,
      mode: e,
      cancelable: !0
    };
    if (this.notifyPlugins("beforeDatasetUpdate", s) === !1)
      return;
    i.controller._update(e), s.cancelable = !1, this.notifyPlugins("afterDatasetUpdate", s);
  }
  render() {
    if (this.notifyPlugins("beforeRender", {
      cancelable: !0
    }) === !1)
      return;
    if (xt.has(this)) {
      if (this.attached && !xt.running(this))
        xt.start(this);
    } else
      this.draw(), zn({
        chart: this
      });
  }
  draw() {
    let t;
    if (this._resizeBeforeDraw) {
      let { width: i, height: s } = this._resizeBeforeDraw;
      this._resizeBeforeDraw = null, this._resize(i, s);
    }
    if (this.clear(), this.width <= 0 || this.height <= 0)
      return;
    if (this.notifyPlugins("beforeDraw", {
      cancelable: !0
    }) === !1)
      return;
    let e = this._layers;
    for (t = 0;t < e.length && e[t].z <= 0; ++t)
      e[t].draw(this.chartArea);
    this._drawDatasets();
    for (;t < e.length; ++t)
      e[t].draw(this.chartArea);
    this.notifyPlugins("afterDraw");
  }
  _getSortedDatasetMetas(t) {
    let e = this._sortedMetasets, i = [], s, n;
    for (s = 0, n = e.length;s < n; ++s) {
      let o = e[s];
      if (!t || o.visible)
        i.push(o);
    }
    return i;
  }
  getSortedVisibleDatasetMetas() {
    return this._getSortedDatasetMetas(!0);
  }
  _drawDatasets() {
    if (this.notifyPlugins("beforeDatasetsDraw", {
      cancelable: !0
    }) === !1)
      return;
    let t = this.getSortedVisibleDatasetMetas();
    for (let e = t.length - 1;e >= 0; --e)
      this._drawDataset(t[e]);
    this.notifyPlugins("afterDatasetsDraw");
  }
  _drawDataset(t) {
    let e = this.ctx, i = {
      meta: t,
      index: t.index,
      cancelable: !0
    }, s = Ui(this, t);
    if (this.notifyPlugins("beforeDatasetDraw", i) === !1)
      return;
    if (s)
      ce(e, s);
    if (t.controller.draw(), s)
      he(e);
    i.cancelable = !1, this.notifyPlugins("afterDatasetDraw", i);
  }
  isPointInArea(t) {
    return rt(t, this.chartArea, this._minPadding);
  }
  getElementsAtEventForMode(t, e, i, s) {
    let n = Gr.modes[e];
    if (typeof n === "function")
      return n(this, t, i, s);
    return [];
  }
  getDatasetMeta(t) {
    let e = this.data.datasets[t], i = this._metasets, s = i.filter((n) => n && n._dataset === e).pop();
    if (!s)
      s = {
        type: null,
        data: [],
        dataset: null,
        controller: null,
        hidden: null,
        xAxisID: null,
        yAxisID: null,
        order: e && e.order || 0,
        index: t,
        _dataset: e,
        _parsed: [],
        _sorted: !1
      }, i.push(s);
    return s;
  }
  getContext() {
    return this.$context || (this.$context = bt(null, {
      chart: this,
      type: "chart"
    }));
  }
  getVisibleDatasetCount() {
    return this.getSortedVisibleDatasetMetas().length;
  }
  isDatasetVisible(t) {
    let e = this.data.datasets[t];
    if (!e)
      return !1;
    let i = this.getDatasetMeta(t);
    return typeof i.hidden === "boolean" ? !i.hidden : !e.hidden;
  }
  setDatasetVisibility(t, e) {
    let i = this.getDatasetMeta(t);
    i.hidden = !e;
  }
  toggleDataVisibility(t) {
    this._hiddenIndices[t] = !this._hiddenIndices[t];
  }
  getDataVisibility(t) {
    return !this._hiddenIndices[t];
  }
  _updateVisibility(t, e, i) {
    let s = i ? "show" : "hide", n = this.getDatasetMeta(t), o = n.controller._resolveAnimations(void 0, s);
    if (Kt(e))
      n.data[e].hidden = !i, this.update();
    else
      this.setDatasetVisibility(t, i), o.update(n, {
        visible: i
      }), this.update((a) => a.datasetIndex === t ? s : void 0);
  }
  hide(t, e) {
    this._updateVisibility(t, e, !1);
  }
  show(t, e) {
    this._updateVisibility(t, e, !0);
  }
  _destroyDatasetMeta(t) {
    let e = this._metasets[t];
    if (e && e.controller)
      e.controller._destroy();
    delete this._metasets[t];
  }
  _stop() {
    let t, e;
    this.stop(), xt.remove(this);
    for (t = 0, e = this.data.datasets.length;t < e; ++t)
      this._destroyDatasetMeta(t);
  }
  destroy() {
    this.notifyPlugins("beforeDestroy");
    let { canvas: t, ctx: e } = this;
    if (this._stop(), this.config.clearCache(), t)
      this.unbindEvents(), Ri(t, e), this.platform.releaseContext(e), this.canvas = null, this.ctx = null;
    delete ti[this.id], this.notifyPlugins("afterDestroy");
  }
  toBase64Image(...t) {
    return this.canvas.toDataURL(...t);
  }
  bindEvents() {
    if (this.bindUserEvents(), this.options.responsive)
      this.bindResponsiveEvents();
    else
      this.attached = !0;
  }
  bindUserEvents() {
    let t = this._listeners, e = this.platform, i = (n, o) => {
      e.addEventListener(this, n, o), t[n] = o;
    }, s = (n, o, a) => {
      n.offsetX = o, n.offsetY = a, this._eventHandler(n);
    };
    L(this.options.events, (n) => i(n, s));
  }
  bindResponsiveEvents() {
    if (!this._responsiveListeners)
      this._responsiveListeners = {};
    let t = this._responsiveListeners, e = this.platform, i = (r, l) => {
      e.addEventListener(this, r, l), t[r] = l;
    }, s = (r, l) => {
      if (t[r])
        e.removeEventListener(this, r, l), delete t[r];
    }, n = (r, l) => {
      if (this.canvas)
        this.resize(r, l);
    }, o, a = () => {
      s("attach", a), this.attached = !0, this.resize(), i("resize", n), i("detach", o);
    };
    if (o = () => {
      this.attached = !1, s("resize", n), this._stop(), this._resize(0, 0), i("attach", a);
    }, e.isAttached(this.canvas))
      a();
    else
      o();
  }
  unbindEvents() {
    L(this._listeners, (t, e) => {
      this.platform.removeEventListener(this, e, t);
    }), this._listeners = {}, L(this._responsiveListeners, (t, e) => {
      this.platform.removeEventListener(this, e, t);
    }), this._responsiveListeners = void 0;
  }
  updateHoverStyle(t, e, i) {
    let s = i ? "set" : "remove", n, o, a, r;
    if (e === "dataset")
      n = this.getDatasetMeta(t[0].datasetIndex), n.controller["_" + s + "DatasetHoverStyle"]();
    for (a = 0, r = t.length;a < r; ++a) {
      o = t[a];
      let l = o && this.getDatasetMeta(o.datasetIndex).controller;
      if (l)
        l[s + "HoverStyle"](o.element, o.datasetIndex, o.index);
    }
  }
  getActiveElements() {
    return this._active || [];
  }
  setActiveElements(t) {
    let e = this._active || [], i = t.map(({ datasetIndex: n, index: o }) => {
      let a = this.getDatasetMeta(n);
      if (!a)
        throw Error("No dataset found at index " + n);
      return {
        datasetIndex: n,
        element: a.data[o],
        index: o
      };
    });
    if (!re(i, e))
      this._active = i, this._lastEvent = null, this._updateHoverStyles(i, e);
  }
  notifyPlugins(t, e, i) {
    return this._plugins.notify(this, t, e, i);
  }
  isPluginEnabled(t) {
    return this._plugins._cache.filter((e) => e.plugin.id === t).length === 1;
  }
  _updateHoverStyles(t, e, i) {
    let s = this.options.hover, n = (r, l) => r.filter((c) => !l.some((h) => c.datasetIndex === h.datasetIndex && c.index === h.index)), o = n(e, t), a = i ? t : n(t, e);
    if (o.length)
      this.updateHoverStyle(o, s.mode, !1);
    if (a.length && s.mode)
      this.updateHoverStyle(a, s.mode, !0);
  }
  _eventHandler(t, e) {
    let i = {
      event: t,
      replay: e,
      cancelable: !0,
      inChartArea: this.isPointInArea(t)
    }, s = (o) => (o.options.events || this.options.events).includes(t.native.type);
    if (this.notifyPlugins("beforeEvent", i, s) === !1)
      return;
    let n = this._handleEvent(t, e, i.inChartArea);
    if (i.cancelable = !1, this.notifyPlugins("afterEvent", i, s), n || i.changed)
      this.render();
    return this;
  }
  _handleEvent(t, e, i) {
    let { _active: s = [], options: n } = this, o = e, a = this._getActiveElements(t, s, i, o), r = Bs(t), l = Kl(t, this._lastEvent, i, r);
    if (i) {
      if (this._lastEvent = null, R(n.onHover, [
        t,
        a,
        this
      ], this), r)
        R(n.onClick, [
          t,
          a,
          this
        ], this);
    }
    let c = !re(a, s);
    if (c || e)
      this._active = a, this._updateHoverStyles(a, s, e);
    return this._lastEvent = l, c;
  }
  _getActiveElements(t, e, i, s) {
    if (t.type === "mouseout")
      return [];
    if (!i)
      return e;
    let n = this.options.hover;
    return this.getElementsAtEventForMode(t, n.mode, n, s);
  }
}
function Wn() {
  return L(ke.instances, (t) => t._plugins.invalidate());
}
function ql(t, e, i) {
  let { startAngle: s, x: n, y: o, outerRadius: a, innerRadius: r, options: l } = e, { borderWidth: c, borderJoinStyle: h } = l, d = Math.min(c / a, X(s - i));
  if (t.beginPath(), t.arc(n, o, a - c / 2, s + d / 2, i - d / 2), r > 0) {
    let u = Math.min(c / r, X(s - i));
    t.arc(n, o, r + c / 2, i - u / 2, s + u / 2, !0);
  } else {
    let u = Math.min(c / 2, a * X(s - i));
    if (h === "round")
      t.arc(n, o, u, i - T / 2, s + T / 2, !0);
    else if (h === "bevel") {
      let f = 2 * u * u, m = -f * Math.cos(i + T / 2) + n, g = -f * Math.sin(i + T / 2) + o, p = f * Math.cos(s + T / 2) + n, b = f * Math.sin(s + T / 2) + o;
      t.lineTo(m, g), t.lineTo(p, b);
    }
  }
  t.closePath(), t.moveTo(0, 0), t.rect(0, 0, t.canvas.width, t.canvas.height), t.clip("evenodd");
}
function Gl(t, e, i) {
  let { startAngle: s, pixelMargin: n, x: o, y: a, outerRadius: r, innerRadius: l } = e, c = n / r;
  if (t.beginPath(), t.arc(o, a, r, s - c, i + c), l > n)
    c = n / l, t.arc(o, a, l, i + c, s - c, !0);
  else
    t.arc(o, a, n, i + N, s - N);
  t.closePath(), t.clip();
}
function Jl(t) {
  return He(t, [
    "outerStart",
    "outerEnd",
    "innerStart",
    "innerEnd"
  ]);
}
function Zl(t, e, i, s) {
  let n = Jl(t.options.borderRadius), o = (i - e) / 2, a = Math.min(o, s * e / 2), r = (l) => {
    let c = (i - Math.min(o, l)) * s / 2;
    return Y(l, 0, Math.min(o, c));
  };
  return {
    outerStart: r(n.outerStart),
    outerEnd: r(n.outerEnd),
    innerStart: Y(n.innerStart, 0, a),
    innerEnd: Y(n.innerEnd, 0, a)
  };
}
function te(t, e, i, s) {
  return {
    x: i + t * Math.cos(e),
    y: s + t * Math.sin(e)
  };
}
function ii(t, e, i, s, n, o) {
  let { x: a, y: r, startAngle: l, pixelMargin: c, innerRadius: h } = e, d = Math.max(e.outerRadius + s + i - c, 0), u = h > 0 ? h + s + i + c : 0, f = 0, m = n - l;
  if (s) {
    let B = h > 0 ? h - s : 0, j = d > 0 ? d - s : 0, et = (B + j) / 2, nt = et !== 0 ? m * et / (et + s) : m;
    f = (m - nt) / 2;
  }
  let g = Math.max(0.001, m * d - i / T) / d, p = (m - g) / 2, b = l + p + f, x = n - p - f, { outerStart: v, outerEnd: y, innerStart: _, innerEnd: M } = Zl(e, u, d, x - b), k = d - v, S = d - y, w = b + v / k, C = x - y / S, D = u + _, V = u + M, tt = b + _ / D, z = x - M / V;
  if (t.beginPath(), o) {
    let B = (w + C) / 2;
    if (t.arc(a, r, d, w, B), t.arc(a, r, d, B, C), y > 0) {
      let U = te(S, C, a, r);
      t.arc(U.x, U.y, y, C, x + N);
    }
    let j = te(V, x, a, r);
    if (t.lineTo(j.x, j.y), M > 0) {
      let U = te(V, z, a, r);
      t.arc(U.x, U.y, M, x + N, z + Math.PI);
    }
    let et = (x - M / u + (b + _ / u)) / 2;
    if (t.arc(a, r, u, x - M / u, et, !0), t.arc(a, r, u, et, b + _ / u, !0), _ > 0) {
      let U = te(D, tt, a, r);
      t.arc(U.x, U.y, _, tt + Math.PI, b - N);
    }
    let nt = te(k, b, a, r);
    if (t.lineTo(nt.x, nt.y), v > 0) {
      let U = te(k, w, a, r);
      t.arc(U.x, U.y, v, b - N, w);
    }
  } else {
    t.moveTo(a, r);
    let B = Math.cos(w) * d + a, j = Math.sin(w) * d + r;
    t.lineTo(B, j);
    let et = Math.cos(C) * d + a, nt = Math.sin(C) * d + r;
    t.lineTo(et, nt);
  }
  t.closePath();
}
function Ql(t, e, i, s, n) {
  let { fullCircles: o, startAngle: a, circumference: r } = e, l = e.endAngle;
  if (o) {
    ii(t, e, i, s, l, n);
    for (let c = 0;c < o; ++c)
      t.fill();
    if (!isNaN(r))
      l = a + (r % I || I);
  }
  return ii(t, e, i, s, l, n), t.fill(), l;
}
function tc(t, e, i, s, n) {
  let { fullCircles: o, startAngle: a, circumference: r, options: l } = e, { borderWidth: c, borderJoinStyle: h, borderDash: d, borderDashOffset: u, borderRadius: f } = l, m = l.borderAlign === "inner";
  if (!c)
    return;
  if (t.setLineDash(d || []), t.lineDashOffset = u, m)
    t.lineWidth = c * 2, t.lineJoin = h || "round";
  else
    t.lineWidth = c, t.lineJoin = h || "bevel";
  let g = e.endAngle;
  if (o) {
    ii(t, e, i, s, g, n);
    for (let p = 0;p < o; ++p)
      t.stroke();
    if (!isNaN(r))
      g = a + (r % I || I);
  }
  if (m)
    Gl(t, e, g);
  if (l.selfJoin && g - a >= T && f === 0 && h !== "miter")
    ql(t, e, g);
  if (!o)
    ii(t, e, i, s, g, n), t.stroke();
}

class Io extends ut {
  static id = "arc";
  static defaults = {
    borderAlign: "center",
    borderColor: "#fff",
    borderDash: [],
    borderDashOffset: 0,
    borderJoinStyle: void 0,
    borderRadius: 0,
    borderWidth: 2,
    offset: 0,
    spacing: 0,
    angle: void 0,
    circular: !0,
    selfJoin: !1
  };
  static defaultRoutes = {
    backgroundColor: "backgroundColor"
  };
  static descriptors = {
    _scriptable: !0,
    _indexable: (t) => t !== "borderDash"
  };
  circumference;
  endAngle;
  fullCircles;
  innerRadius;
  outerRadius;
  pixelMargin;
  startAngle;
  constructor(t) {
    super();
    if (this.options = void 0, this.circumference = void 0, this.startAngle = void 0, this.endAngle = void 0, this.innerRadius = void 0, this.outerRadius = void 0, this.pixelMargin = 0, this.fullCircles = 0, t)
      Object.assign(this, t);
  }
  inRange(t, e, i) {
    let s = this.getProps([
      "x",
      "y"
    ], i), { angle: n, distance: o } = wi(s, {
      x: t,
      y: e
    }), { startAngle: a, endAngle: r, innerRadius: l, outerRadius: c, circumference: h } = this.getProps([
      "startAngle",
      "endAngle",
      "innerRadius",
      "outerRadius",
      "circumference"
    ], i), d = (this.options.spacing + this.options.borderWidth) / 2, u = P(h, r - a), f = Gt(n, a, r) && a !== r, m = u >= I || f, g = ct(o, l + d, c + d);
    return m && g;
  }
  getCenterPoint(t) {
    let { x: e, y: i, startAngle: s, endAngle: n, innerRadius: o, outerRadius: a } = this.getProps([
      "x",
      "y",
      "startAngle",
      "endAngle",
      "innerRadius",
      "outerRadius"
    ], t), { offset: r, spacing: l } = this.options, c = (s + n) / 2, h = (o + a + l + r) / 2;
    return {
      x: e + Math.cos(c) * h,
      y: i + Math.sin(c) * h
    };
  }
  tooltipPosition(t) {
    return this.getCenterPoint(t);
  }
  draw(t) {
    let { options: e, circumference: i } = this, s = (e.offset || 0) / 4, n = (e.spacing || 0) / 2, o = e.circular;
    if (this.pixelMargin = e.borderAlign === "inner" ? 0.33 : 0, this.fullCircles = i > I ? Math.floor(i / I) : 0, i === 0 || this.innerRadius < 0 || this.outerRadius < 0)
      return;
    t.save();
    let a = (this.startAngle + this.endAngle) / 2;
    t.translate(Math.cos(a) * s, Math.sin(a) * s);
    let r = 1 - Math.sin(Math.min(T, i || 0)), l = s * r;
    t.fillStyle = e.backgroundColor, t.strokeStyle = e.borderColor, Ql(t, this, l, n, o), tc(t, this, l, n, o), t.restore();
  }
}
function Fo(t, e, i = e) {
  t.lineCap = P(i.borderCapStyle, e.borderCapStyle), t.setLineDash(P(i.borderDash, e.borderDash)), t.lineDashOffset = P(i.borderDashOffset, e.borderDashOffset), t.lineJoin = P(i.borderJoinStyle, e.borderJoinStyle), t.lineWidth = P(i.borderWidth, e.borderWidth), t.strokeStyle = P(i.borderColor, e.borderColor);
}
function ec(t, e, i) {
  t.lineTo(i.x, i.y);
}
function ic(t) {
  if (t.stepped)
    return Js;
  if (t.tension || t.cubicInterpolationMode === "monotone")
    return Zs;
  return ec;
}
function zo(t, e, i = {}) {
  let s = t.length, { start: n = 0, end: o = s - 1 } = i, { start: a, end: r } = e, l = Math.max(n, a), c = Math.min(o, r), h = n < a && o < a || n > r && o > r;
  return {
    count: s,
    start: l,
    loop: e.loop,
    ilen: c < l && !h ? s + c - l : c - l
  };
}
function sc(t, e, i, s) {
  let { points: n, options: o } = e, { count: a, start: r, loop: l, ilen: c } = zo(n, i, s), h = ic(o), { move: d = !0, reverse: u } = s || {}, f, m, g;
  for (f = 0;f <= c; ++f) {
    if (m = n[(r + (u ? c - f : f)) % a], m.skip)
      continue;
    else if (d)
      t.moveTo(m.x, m.y), d = !1;
    else
      h(t, g, m, u, o.stepped);
    g = m;
  }
  if (l)
    m = n[(r + (u ? c : 0)) % a], h(t, g, m, u, o.stepped);
  return !!l;
}
function nc(t, e, i, s) {
  let n = e.points, { count: o, start: a, ilen: r } = zo(n, i, s), { move: l = !0, reverse: c } = s || {}, h = 0, d = 0, u, f, m, g, p, b, x = (y) => (a + (c ? r - y : y)) % o, v = () => {
    if (g !== p)
      t.lineTo(h, p), t.lineTo(h, g), t.lineTo(h, b);
  };
  if (l)
    f = n[x(0)], t.moveTo(f.x, f.y);
  for (u = 0;u <= r; ++u) {
    if (f = n[x(u)], f.skip)
      continue;
    let y = f.x, _ = f.y, M = y | 0;
    if (M === m) {
      if (_ < g)
        g = _;
      else if (_ > p)
        p = _;
      h = (d * h + y) / ++d;
    } else
      v(), t.lineTo(y, _), m = M, d = 0, g = p = _;
    b = _;
  }
  v();
}
function as(t) {
  let e = t.options, i = e.borderDash && e.borderDash.length;
  return !t._decimated && !t._loop && !e.tension && e.cubicInterpolationMode !== "monotone" && !e.stepped && !i ? nc : sc;
}
function oc(t) {
  if (t.stepped)
    return ln;
  if (t.tension || t.cubicInterpolationMode === "monotone")
    return cn;
  return kt;
}
function ac(t, e, i, s) {
  let n = e._path;
  if (!n) {
    if (n = e._path = new Path2D, e.path(n, i, s))
      n.closePath();
  }
  Fo(t, e.options), t.stroke(n);
}
function rc(t, e, i, s) {
  let { segments: n, options: o } = e, a = as(e);
  for (let r of n) {
    if (Fo(t, o, r.style), t.beginPath(), a(t, e, r, {
      start: i,
      end: i + s - 1
    }))
      t.closePath();
    t.stroke();
  }
}
var lc = typeof Path2D === "function";
function cc(t, e, i, s) {
  if (lc && !e.options.segment)
    ac(t, e, i, s);
  else
    rc(t, e, i, s);
}

class we extends ut {
  static id = "line";
  static defaults = {
    borderCapStyle: "butt",
    borderDash: [],
    borderDashOffset: 0,
    borderJoinStyle: "miter",
    borderWidth: 3,
    capBezierPoints: !0,
    cubicInterpolationMode: "default",
    fill: !1,
    spanGaps: !1,
    stepped: !1,
    tension: 0
  };
  static defaultRoutes = {
    backgroundColor: "backgroundColor",
    borderColor: "borderColor"
  };
  static descriptors = {
    _scriptable: !0,
    _indexable: (t) => t !== "borderDash" && t !== "fill"
  };
  constructor(t) {
    super();
    if (this.animated = !0, this.options = void 0, this._chart = void 0, this._loop = void 0, this._fullLoop = void 0, this._path = void 0, this._points = void 0, this._segments = void 0, this._decimated = !1, this._pointsUpdated = !1, this._datasetIndex = void 0, t)
      Object.assign(this, t);
  }
  updateControlPoints(t, e) {
    let i = this.options;
    if ((i.tension || i.cubicInterpolationMode === "monotone") && !i.stepped && !this._pointsUpdated) {
      let s = i.spanGaps ? this._loop : this._fullLoop;
      on(this._points, i, t, s, e), this._pointsUpdated = !0;
    }
  }
  set points(t) {
    this._points = t, delete this._segments, delete this._path, this._pointsUpdated = !1;
  }
  get points() {
    return this._points;
  }
  get segments() {
    return this._segments || (this._segments = dn(this, this.options.segment));
  }
  first() {
    let t = this.segments, e = this.points;
    return t.length && e[t[0].start];
  }
  last() {
    let t = this.segments, e = this.points, i = t.length;
    return i && e[t[i - 1].end];
  }
  interpolate(t, e) {
    let i = this.options, s = t[e], n = this.points, o = Yi(this, {
      property: e,
      start: s,
      end: s
    });
    if (!o.length)
      return;
    let a = [], r = oc(i), l, c;
    for (l = 0, c = o.length;l < c; ++l) {
      let { start: h, end: d } = o[l], u = n[h], f = n[d];
      if (u === f) {
        a.push(u);
        continue;
      }
      let m = Math.abs((s - u[e]) / (f[e] - u[e])), g = r(u, f, m, i.stepped);
      g[e] = t[e], a.push(g);
    }
    return a.length === 1 ? a[0] : a;
  }
  pathSegment(t, e, i) {
    return as(this)(t, this, e, i);
  }
  path(t, e, i) {
    let s = this.segments, n = as(this), o = this._loop;
    e = e || 0, i = i || this.points.length - e;
    for (let a of s)
      o &= n(t, this, a, {
        start: e,
        end: e + i - 1
      });
    return !!o;
  }
  draw(t, e, i, s) {
    let n = this.options || {};
    if ((this.points || []).length && n.borderWidth)
      t.save(), cc(t, this, i, s), t.restore();
    if (this.animated)
      this._pointsUpdated = !1, this._path = void 0;
  }
}
function Nn(t, e, i, s) {
  let n = t.options, { [i]: o } = t.getProps([
    i
  ], s);
  return Math.abs(e - o) < n.radius + n.hitRadius;
}

class Bo extends ut {
  static id = "point";
  parsed;
  skip;
  stop;
  static defaults = {
    borderWidth: 1,
    hitRadius: 1,
    hoverBorderWidth: 1,
    hoverRadius: 4,
    pointStyle: "circle",
    radius: 3,
    rotation: 0
  };
  static defaultRoutes = {
    backgroundColor: "backgroundColor",
    borderColor: "borderColor"
  };
  constructor(t) {
    super();
    if (this.options = void 0, this.parsed = void 0, this.skip = void 0, this.stop = void 0, t)
      Object.assign(this, t);
  }
  inRange(t, e, i) {
    let s = this.options, { x: n, y: o } = this.getProps([
      "x",
      "y"
    ], i);
    return Math.pow(t - n, 2) + Math.pow(e - o, 2) < Math.pow(s.hitRadius + s.radius, 2);
  }
  inXRange(t, e) {
    return Nn(this, t, "x", e);
  }
  inYRange(t, e) {
    return Nn(this, t, "y", e);
  }
  getCenterPoint(t) {
    let { x: e, y: i } = this.getProps([
      "x",
      "y"
    ], t);
    return {
      x: e,
      y: i
    };
  }
  size(t) {
    t = t || this.options || {};
    let e = t.radius || 0;
    e = Math.max(e, e && t.hoverRadius || 0);
    let i = e && t.borderWidth || 0;
    return (e + i) * 2;
  }
  draw(t, e) {
    let i = this.options;
    if (this.skip || i.radius < 0.1 || !rt(this, e, this.size(i) / 2))
      return;
    t.strokeStyle = i.borderColor, t.lineWidth = i.borderWidth, t.fillStyle = i.backgroundColor, Ve(t, i, this.x, this.y);
  }
  getRange() {
    let t = this.options || {};
    return t.radius + t.hitRadius;
  }
}
function Wo(t, e) {
  let { x: i, y: s, base: n, width: o, height: a } = t.getProps([
    "x",
    "y",
    "base",
    "width",
    "height"
  ], e), r, l, c, h, d;
  if (t.horizontal)
    d = a / 2, r = Math.min(i, n), l = Math.max(i, n), c = s - d, h = s + d;
  else
    d = o / 2, r = i - d, l = i + d, c = Math.min(s, n), h = Math.max(s, n);
  return {
    left: r,
    top: c,
    right: l,
    bottom: h
  };
}
function Ot(t, e, i, s) {
  return t ? 0 : Y(e, i, s);
}
function hc(t, e, i) {
  let s = t.options.borderWidth, n = t.borderSkipped, o = Ii(s);
  return {
    t: Ot(n.top, o.top, 0, i),
    r: Ot(n.right, o.right, 0, e),
    b: Ot(n.bottom, o.bottom, 0, i),
    l: Ot(n.left, o.left, 0, e)
  };
}
function dc(t, e, i) {
  let { enableBorderRadius: s } = t.getProps([
    "enableBorderRadius"
  ]), n = t.options.borderRadius, o = At(n), a = Math.min(e, i), r = t.borderSkipped, l = s || O(n);
  return {
    topLeft: Ot(!l || r.top || r.left, o.topLeft, 0, a),
    topRight: Ot(!l || r.top || r.right, o.topRight, 0, a),
    bottomLeft: Ot(!l || r.bottom || r.left, o.bottomLeft, 0, a),
    bottomRight: Ot(!l || r.bottom || r.right, o.bottomRight, 0, a)
  };
}
function uc(t) {
  let e = Wo(t), i = e.right - e.left, s = e.bottom - e.top, n = hc(t, i / 2, s / 2), o = dc(t, i / 2, s / 2);
  return {
    outer: {
      x: e.left,
      y: e.top,
      w: i,
      h: s,
      radius: o
    },
    inner: {
      x: e.left + n.l,
      y: e.top + n.t,
      w: i - n.l - n.r,
      h: s - n.t - n.b,
      radius: {
        topLeft: Math.max(0, o.topLeft - Math.max(n.t, n.l)),
        topRight: Math.max(0, o.topRight - Math.max(n.t, n.r)),
        bottomLeft: Math.max(0, o.bottomLeft - Math.max(n.b, n.l)),
        bottomRight: Math.max(0, o.bottomRight - Math.max(n.b, n.r))
      }
    }
  };
}
function ts(t, e, i, s) {
  let n = e === null, o = i === null, r = t && !(n && o) && Wo(t, s);
  return r && (n || ct(e, r.left, r.right)) && (o || ct(i, r.top, r.bottom));
}
function fc(t) {
  return t.topLeft || t.topRight || t.bottomLeft || t.bottomRight;
}
function gc(t, e) {
  t.rect(e.x, e.y, e.w, e.h);
}
function es(t, e, i = {}) {
  let s = t.x !== i.x ? -e : 0, n = t.y !== i.y ? -e : 0, o = (t.x + t.w !== i.x + i.w ? e : 0) - s, a = (t.y + t.h !== i.y + i.h ? e : 0) - n;
  return {
    x: t.x + s,
    y: t.y + n,
    w: t.w + o,
    h: t.h + a,
    radius: t.radius
  };
}

class No extends ut {
  static id = "bar";
  static defaults = {
    borderSkipped: "start",
    borderWidth: 0,
    borderRadius: 0,
    inflateAmount: "auto",
    pointStyle: void 0
  };
  static defaultRoutes = {
    backgroundColor: "backgroundColor",
    borderColor: "borderColor"
  };
  constructor(t) {
    super();
    if (this.options = void 0, this.horizontal = void 0, this.base = void 0, this.width = void 0, this.height = void 0, this.inflateAmount = void 0, t)
      Object.assign(this, t);
  }
  draw(t) {
    let { inflateAmount: e, options: { borderColor: i, backgroundColor: s } } = this, { inner: n, outer: o } = uc(this), a = fc(o.radius) ? Zt : gc;
    if (t.save(), o.w !== n.w || o.h !== n.h)
      t.beginPath(), a(t, es(o, e, n)), t.clip(), a(t, es(n, -e, o)), t.fillStyle = i, t.fill("evenodd");
    t.beginPath(), a(t, es(n, e)), t.fillStyle = s, t.fill(), t.restore();
  }
  inRange(t, e, i) {
    return ts(this, t, e, i);
  }
  inXRange(t, e) {
    return ts(this, t, null, e);
  }
  inYRange(t, e) {
    return ts(this, null, t, e);
  }
  getCenterPoint(t) {
    let { x: e, y: i, base: s, horizontal: n } = this.getProps([
      "x",
      "y",
      "base",
      "horizontal"
    ], t);
    return {
      x: n ? (e + s) / 2 : e,
      y: n ? i : (i + s) / 2
    };
  }
  getRange(t) {
    return t === "x" ? this.width / 2 : this.height / 2;
  }
}
var pc = /* @__PURE__ */ Object.freeze({
  __proto__: null,
  ArcElement: Io,
  BarElement: No,
  LineElement: we,
  PointElement: Bo
}), rs = [
  "rgb(54, 162, 235)",
  "rgb(255, 99, 132)",
  "rgb(255, 159, 64)",
  "rgb(255, 205, 86)",
  "rgb(75, 192, 192)",
  "rgb(153, 102, 255)",
  "rgb(201, 203, 207)"
], Vn = /* @__PURE__ */ rs.map((t) => t.replace("rgb(", "rgba(").replace(")", ", 0.5)"));
function Vo(t) {
  return rs[t % rs.length];
}
function Ho(t) {
  return Vn[t % Vn.length];
}
function mc(t, e) {
  return t.borderColor = Vo(e), t.backgroundColor = Ho(e), ++e;
}
function bc(t, e) {
  return t.backgroundColor = t.data.map(() => Vo(e++)), e;
}
function xc(t, e) {
  return t.backgroundColor = t.data.map(() => Ho(e++)), e;
}
function _c(t) {
  let e = 0;
  return (i, s) => {
    let n = t.getDatasetMeta(s).controller;
    if (n instanceof ni)
      e = bc(i, e);
    else if (n instanceof fs)
      e = xc(i, e);
    else if (n)
      e = mc(i, e);
  };
}
function Hn(t) {
  let e;
  for (e in t)
    if (t[e].borderColor || t[e].backgroundColor)
      return !0;
  return !1;
}
function yc(t) {
  return t && (t.borderColor || t.backgroundColor);
}
function vc() {
  return F.borderColor !== "rgba(0,0,0,0.1)" || F.backgroundColor !== "rgba(0,0,0,0.1)";
}
var Mc = {
  id: "colors",
  defaults: {
    enabled: !0,
    forceOverride: !1
  },
  beforeLayout(t, e, i) {
    if (!i.enabled)
      return;
    let { data: { datasets: s }, options: n } = t.config, { elements: o } = n, a = Hn(s) || yc(n) || o && Hn(o) || vc();
    if (!i.forceOverride && a)
      return;
    let r = _c(t);
    s.forEach(r);
  }
};
function kc(t, e, i, s, n) {
  let o = n.samples || s;
  if (o >= i)
    return t.slice(e, e + i);
  let a = [], r = (i - 2) / (o - 2), l = 0, c = e + i - 1, h = e, d, u, f, m, g;
  a[l++] = t[h];
  for (d = 0;d < o - 2; d++) {
    let p = 0, b = 0, x, v = Math.floor((d + 1) * r) + 1 + e, y = Math.min(Math.floor((d + 2) * r) + 1, i) + e, _ = y - v;
    for (x = v;x < y; x++)
      p += t[x].x, b += t[x].y;
    p /= _, b /= _;
    let M = Math.floor(d * r) + 1 + e, k = Math.min(Math.floor((d + 1) * r) + 1, i) + e, { x: S, y: w } = t[h];
    f = m = -1;
    for (x = M;x < k; x++)
      if (m = 0.5 * Math.abs((S - p) * (t[x].y - w) - (S - t[x].x) * (b - w)), m > f)
        f = m, u = t[x], g = x;
    a[l++] = u, h = g;
  }
  return a[l++] = t[c], a;
}
function wc(t, e, i, s) {
  let n = 0, o = 0, a, r, l, c, h, d, u, f, m, g, p = [], b = e + i - 1, x = t[e].x, y = t[b].x - x;
  for (a = e;a < e + i; ++a) {
    r = t[a], l = (r.x - x) / y * s, c = r.y;
    let _ = l | 0;
    if (_ === h) {
      if (c < m)
        m = c, d = a;
      else if (c > g)
        g = c, u = a;
      n = (o * n + r.x) / ++o;
    } else {
      let M = a - 1;
      if (!A(d) && !A(u)) {
        let k = Math.min(d, u), S = Math.max(d, u);
        if (k !== f && k !== M)
          p.push({
            ...t[k],
            x: n
          });
        if (S !== f && S !== M)
          p.push({
            ...t[S],
            x: n
          });
      }
      if (a > 0 && M !== f)
        p.push(t[M]);
      p.push(r), h = _, o = 0, m = g = c, d = u = f = a;
    }
  }
  return p;
}
function jo(t) {
  if (t._decimated) {
    let e = t._data;
    delete t._decimated, delete t._data, Object.defineProperty(t, "data", {
      configurable: !0,
      enumerable: !0,
      writable: !0,
      value: e
    });
  }
}
function jn(t) {
  t.data.datasets.forEach((e) => {
    jo(e);
  });
}
function Sc(t, e) {
  let i = e.length, s = 0, n, { iScale: o } = t, { min: a, max: r, minDefined: l, maxDefined: c } = o.getUserBounds();
  if (l)
    s = Y(at(e, o.axis, a).lo, 0, i - 1);
  if (c)
    n = Y(at(e, o.axis, r).hi + 1, s, i) - s;
  else
    n = i - s;
  return {
    start: s,
    count: n
  };
}
var Pc = {
  id: "decimation",
  defaults: {
    algorithm: "min-max",
    enabled: !1
  },
  beforeElementsUpdate: (t, e, i) => {
    if (!i.enabled) {
      jn(t);
      return;
    }
    let s = t.width;
    t.data.datasets.forEach((n, o) => {
      let { _data: a, indexAxis: r } = n, l = t.getDatasetMeta(o), c = a || n.data;
      if (Qt([
        r,
        t.options.indexAxis
      ]) === "y")
        return;
      if (!l.controller.supportsDecimation)
        return;
      let h = t.scales[l.xAxisID];
      if (h.type !== "linear" && h.type !== "time")
        return;
      if (t.options.parsing)
        return;
      let { start: d, count: u } = Sc(l, c), f = i.threshold || 4 * s;
      if (u <= f) {
        jo(n);
        return;
      }
      if (A(a))
        n._data = c, delete n.data, Object.defineProperty(n, "data", {
          configurable: !0,
          enumerable: !0,
          get: function() {
            return this._decimated;
          },
          set: function(g) {
            this._data = g;
          }
        });
      let m;
      switch (i.algorithm) {
        case "lttb":
          m = kc(c, d, u, s, i);
          break;
        case "min-max":
          m = wc(c, d, u, s);
          break;
        default:
          throw Error(`Unsupported decimation algorithm '${i.algorithm}'`);
      }
      n._decimated = m;
    });
  },
  destroy(t) {
    jn(t);
  }
};
function Dc(t, e, i) {
  let { segments: s, points: n } = t, o = e.points, a = [];
  for (let r of s) {
    let { start: l, end: c } = r;
    c = oi(l, c, n);
    let h = ls(i, n[l], n[c], r.loop);
    if (!e.segments) {
      a.push({
        source: r,
        target: h,
        start: n[l],
        end: n[c]
      });
      continue;
    }
    let d = Yi(e, h);
    for (let u of d) {
      let f = ls(i, o[u.start], o[u.end], u.loop), m = $i(r, n, f);
      for (let g of m)
        a.push({
          source: g,
          target: u,
          start: {
            [i]: $n(h, f, "start", Math.max)
          },
          end: {
            [i]: $n(h, f, "end", Math.min)
          }
        });
    }
  }
  return a;
}
function ls(t, e, i, s) {
  if (s)
    return;
  let n = e[t], o = i[t];
  if (t === "angle")
    n = X(n), o = X(o);
  return {
    property: t,
    start: n,
    end: o
  };
}
function Ac(t, e) {
  let { x: i = null, y: s = null } = t || {}, n = e.points, o = [];
  return e.segments.forEach(({ start: a, end: r }) => {
    r = oi(a, r, n);
    let l = n[a], c = n[r];
    if (s !== null)
      o.push({
        x: l.x,
        y: s
      }), o.push({
        x: c.x,
        y: s
      });
    else if (i !== null)
      o.push({
        x: i,
        y: l.y
      }), o.push({
        x: i,
        y: c.y
      });
  }), o;
}
function oi(t, e, i) {
  for (;e > t; e--) {
    let s = i[e];
    if (!isNaN(s.x) && !isNaN(s.y))
      break;
  }
  return e;
}
function $n(t, e, i, s) {
  if (t && e)
    return s(t[i], e[i]);
  return t ? t[i] : e ? e[i] : 0;
}
function $o(t, e) {
  let i = [], s = !1;
  if (E(t))
    s = !0, i = t;
  else
    i = Ac(t, e);
  return i.length ? new we({
    points: i,
    options: {
      tension: 0
    },
    _loop: s,
    _fullLoop: s
  }) : null;
}
function Yn(t) {
  return t && t.fill !== !1;
}
function Cc(t, e, i) {
  let n = t[e].fill, o = [
    e
  ], a;
  if (!i)
    return n;
  while (n !== !1 && o.indexOf(n) === -1) {
    if (!W(n))
      return n;
    if (a = t[n], !a)
      return !1;
    if (a.visible)
      return n;
    o.push(n), n = a.fill;
  }
  return !1;
}
function Oc(t, e, i) {
  let s = Ec(t);
  if (O(s))
    return isNaN(s.value) ? !1 : s;
  let n = parseFloat(s);
  if (W(n) && Math.floor(n) === n)
    return Tc(s[0], e, n, i);
  return [
    "origin",
    "start",
    "end",
    "stack",
    "shape"
  ].indexOf(s) >= 0 && s;
}
function Tc(t, e, i, s) {
  if (t === "-" || t === "+")
    i = e + i;
  if (i === e || i < 0 || i >= s)
    return !1;
  return i;
}
function Lc(t, e) {
  let i = null;
  if (t === "start")
    i = e.bottom;
  else if (t === "end")
    i = e.top;
  else if (O(t))
    i = e.getPixelForValue(t.value);
  else if (e.getBasePixel)
    i = e.getBasePixel();
  return i;
}
function Rc(t, e, i) {
  let s;
  if (t === "start")
    s = i;
  else if (t === "end")
    s = e.options.reverse ? e.min : e.max;
  else if (O(t))
    s = t.value;
  else
    s = e.getBaseValue();
  return s;
}
function Ec(t) {
  let e = t.options, i = e.fill, s = P(i && i.target, i);
  if (s === void 0)
    s = !!e.backgroundColor;
  if (s === !1 || s === null)
    return !1;
  if (s === !0)
    return "origin";
  return s;
}
function Ic(t) {
  let { scale: e, index: i, line: s } = t, n = [], { segments: o, points: a } = s, r = Fc(e, i);
  r.push($o({
    x: null,
    y: e.bottom
  }, s));
  for (let l = 0;l < o.length; l++) {
    let c = o[l];
    for (let h = c.start;h <= c.end; h++)
      zc(n, a[h], r);
  }
  return new we({
    points: n,
    options: {}
  });
}
function Fc(t, e) {
  let i = [], s = t.getMatchingVisibleMetas("line");
  for (let n = 0;n < s.length; n++) {
    let o = s[n];
    if (o.index === e)
      break;
    if (!o.hidden)
      i.unshift(o.dataset);
  }
  return i;
}
function zc(t, e, i) {
  let s = [];
  for (let n = 0;n < i.length; n++) {
    let o = i[n], { first: a, last: r, point: l } = Bc(o, e, "x");
    if (!l || a && r)
      continue;
    if (a)
      s.unshift(l);
    else if (t.push(l), !r)
      break;
  }
  t.push(...s);
}
function Bc(t, e, i) {
  let s = t.interpolate(e, i);
  if (!s)
    return {};
  let n = s[i], { segments: o, points: a } = t, r = !1, l = !1;
  for (let c = 0;c < o.length; c++) {
    let h = o[c], d = a[h.start][i], u = a[h.end][i];
    if (ct(n, d, u)) {
      r = n === d, l = n === u;
      break;
    }
  }
  return {
    first: r,
    last: l,
    point: s
  };
}

class ms {
  constructor(t) {
    this.x = t.x, this.y = t.y, this.radius = t.radius;
  }
  pathSegment(t, e, i) {
    let { x: s, y: n, radius: o } = this;
    return e = e || {
      start: 0,
      end: I
    }, t.arc(s, n, o, e.end, e.start, !0), !i.bounds;
  }
  interpolate(t) {
    let { x: e, y: i, radius: s } = this, n = t.angle;
    return {
      x: e + Math.cos(n) * s,
      y: i + Math.sin(n) * s,
      angle: n
    };
  }
}
function Wc(t) {
  let { chart: e, fill: i, line: s } = t;
  if (W(i))
    return Nc(e, i);
  if (i === "stack")
    return Ic(t);
  if (i === "shape")
    return !0;
  let n = Vc(t);
  if (n instanceof ms)
    return n;
  return $o(n, s);
}
function Nc(t, e) {
  let i = t.getDatasetMeta(e);
  return i && t.isDatasetVisible(e) ? i.dataset : null;
}
function Vc(t) {
  if ((t.scale || {}).getPointPositionForValue)
    return jc(t);
  return Hc(t);
}
function Hc(t) {
  let { scale: e = {}, fill: i } = t, s = Lc(i, e);
  if (W(s)) {
    let n = e.isHorizontal();
    return {
      x: n ? s : null,
      y: n ? null : s
    };
  }
  return null;
}
function jc(t) {
  let { scale: e, fill: i } = t, s = e.options, n = e.getLabels().length, o = s.reverse ? e.max : e.min, a = Rc(i, e, o), r = [];
  if (s.grid.circular) {
    let l = e.getPointPositionForValue(0, o);
    return new ms({
      x: l.x,
      y: l.y,
      radius: e.getDistanceFromCenterForValue(a)
    });
  }
  for (let l = 0;l < n; ++l)
    r.push(e.getPointPositionForValue(l, a));
  return r;
}
function is(t, e, i) {
  let s = Wc(e), { chart: n, index: o, line: a, scale: r, axis: l } = e, c = a.options, { fill: h, backgroundColor: d } = c, { above: u = d, below: f = d } = h || {}, m = n.getDatasetMeta(o), g = Ui(n, m);
  if (s && a.points.length)
    ce(t, i), $c(t, {
      line: a,
      target: s,
      above: u,
      below: f,
      area: i,
      scale: r,
      axis: l,
      clip: g
    }), he(t);
}
function $c(t, e) {
  let { line: i, target: s, above: n, below: o, area: a, scale: r, clip: l } = e, c = i._loop ? "angle" : e.axis;
  t.save();
  let h = o;
  if (o !== n) {
    if (c === "x")
      Un(t, s, a.top), ss(t, {
        line: i,
        target: s,
        color: n,
        scale: r,
        property: c,
        clip: l
      }), t.restore(), t.save(), Un(t, s, a.bottom);
    else if (c === "y")
      Xn(t, s, a.left), ss(t, {
        line: i,
        target: s,
        color: o,
        scale: r,
        property: c,
        clip: l
      }), t.restore(), t.save(), Xn(t, s, a.right), h = n;
  }
  ss(t, {
    line: i,
    target: s,
    color: h,
    scale: r,
    property: c,
    clip: l
  }), t.restore();
}
function Un(t, e, i) {
  let { segments: s, points: n } = e, o = !0, a = !1;
  t.beginPath();
  for (let r of s) {
    let { start: l, end: c } = r, h = n[l], d = n[oi(l, c, n)];
    if (o)
      t.moveTo(h.x, h.y), o = !1;
    else
      t.lineTo(h.x, i), t.lineTo(h.x, h.y);
    if (a = !!e.pathSegment(t, r, {
      move: a
    }), a)
      t.closePath();
    else
      t.lineTo(d.x, i);
  }
  t.lineTo(e.first().x, i), t.closePath(), t.clip();
}
function Xn(t, e, i) {
  let { segments: s, points: n } = e, o = !0, a = !1;
  t.beginPath();
  for (let r of s) {
    let { start: l, end: c } = r, h = n[l], d = n[oi(l, c, n)];
    if (o)
      t.moveTo(h.x, h.y), o = !1;
    else
      t.lineTo(i, h.y), t.lineTo(h.x, h.y);
    if (a = !!e.pathSegment(t, r, {
      move: a
    }), a)
      t.closePath();
    else
      t.lineTo(i, d.y);
  }
  t.lineTo(i, e.first().y), t.closePath(), t.clip();
}
function ss(t, e) {
  let { line: i, target: s, property: n, color: o, scale: a, clip: r } = e, l = Dc(i, s, n);
  for (let { source: c, target: h, start: d, end: u } of l) {
    let { style: { backgroundColor: f = o } = {} } = c, m = s !== !0;
    t.save(), t.fillStyle = f, Yc(t, a, r, m && ls(n, d, u)), t.beginPath();
    let g = !!i.pathSegment(t, c), p;
    if (m) {
      if (g)
        t.closePath();
      else
        Kn(t, s, u, n);
      let b = !!s.pathSegment(t, h, {
        move: g,
        reverse: !0
      });
      if (p = g && b, !p)
        Kn(t, s, d, n);
    }
    t.closePath(), t.fill(p ? "evenodd" : "nonzero"), t.restore();
  }
}
function Yc(t, e, i, s) {
  let n = e.chart.chartArea, { property: o, start: a, end: r } = s || {};
  if (o === "x" || o === "y") {
    let l, c, h, d;
    if (o === "x")
      l = a, c = n.top, h = r, d = n.bottom;
    else
      l = n.left, c = a, h = n.right, d = r;
    if (t.beginPath(), i)
      l = Math.max(l, i.left), h = Math.min(h, i.right), c = Math.max(c, i.top), d = Math.min(d, i.bottom);
    t.rect(l, c, h - l, d - c), t.clip();
  }
}
function Kn(t, e, i, s) {
  let n = e.interpolate(i, s);
  if (n)
    t.lineTo(n.x, n.y);
}
var Uc = {
  id: "filler",
  afterDatasetsUpdate(t, e, i) {
    let s = (t.data.datasets || []).length, n = [], o, a, r, l;
    for (a = 0;a < s; ++a) {
      if (o = t.getDatasetMeta(a), r = o.dataset, l = null, r && r.options && r instanceof we)
        l = {
          visible: t.isDatasetVisible(a),
          index: a,
          fill: Oc(r, a, s),
          chart: t,
          axis: o.controller.options.indexAxis,
          scale: o.vScale,
          line: r
        };
      o.$filler = l, n.push(l);
    }
    for (a = 0;a < s; ++a) {
      if (l = n[a], !l || l.fill === !1)
        continue;
      l.fill = Cc(n, a, i.propagate);
    }
  },
  beforeDraw(t, e, i) {
    let s = i.drawTime === "beforeDraw", n = t.getSortedVisibleDatasetMetas(), o = t.chartArea;
    for (let a = n.length - 1;a >= 0; --a) {
      let r = n[a].$filler;
      if (!r)
        continue;
      if (r.line.updateControlPoints(o, r.axis), s && r.fill)
        is(t.ctx, r, o);
    }
  },
  beforeDatasetsDraw(t, e, i) {
    if (i.drawTime !== "beforeDatasetsDraw")
      return;
    let s = t.getSortedVisibleDatasetMetas();
    for (let n = s.length - 1;n >= 0; --n) {
      let o = s[n].$filler;
      if (Yn(o))
        is(t.ctx, o, t.chartArea);
    }
  },
  beforeDatasetDraw(t, e, i) {
    let s = e.meta.$filler;
    if (!Yn(s) || i.drawTime !== "beforeDatasetDraw")
      return;
    is(t.ctx, s, t.chartArea);
  },
  defaults: {
    propagate: !0,
    drawTime: "beforeDatasetDraw"
  }
}, qn = (t, e) => {
  let { boxHeight: i = e, boxWidth: s = e } = t;
  if (t.usePointStyle)
    i = Math.min(i, e), s = t.pointStyleWidth || Math.min(s, e);
  return {
    boxWidth: s,
    boxHeight: i,
    itemHeight: Math.max(e, i)
  };
}, Xc = (t, e) => t !== null && e !== null && t.datasetIndex === e.datasetIndex && t.index === e.index;

class cs extends ut {
  constructor(t) {
    super();
    this._added = !1, this.legendHitBoxes = [], this._hoveredItem = null, this.doughnutMode = !1, this.chart = t.chart, this.options = t.options, this.ctx = t.ctx, this.legendItems = void 0, this.columnSizes = void 0, this.lineWidths = void 0, this.maxHeight = void 0, this.maxWidth = void 0, this.top = void 0, this.bottom = void 0, this.left = void 0, this.right = void 0, this.height = void 0, this.width = void 0, this._margins = void 0, this.position = void 0, this.weight = void 0, this.fullSize = void 0;
  }
  update(t, e, i) {
    this.maxWidth = t, this.maxHeight = e, this._margins = i, this.setDimensions(), this.buildLabels(), this.fit();
  }
  setDimensions() {
    if (this.isHorizontal())
      this.width = this.maxWidth, this.left = this._margins.left, this.right = this.width;
    else
      this.height = this.maxHeight, this.top = this._margins.top, this.bottom = this.height;
  }
  buildLabels() {
    let t = this.options.labels || {}, e = R(t.generateLabels, [
      this.chart
    ], this) || [];
    if (t.filter)
      e = e.filter((i) => t.filter(i, this.chart.data));
    if (t.sort)
      e = e.sort((i, s) => t.sort(i, s, this.chart.data));
    if (this.options.reverse)
      e.reverse();
    this.legendItems = e;
  }
  fit() {
    let { options: t, ctx: e } = this;
    if (!t.display) {
      this.width = this.height = 0;
      return;
    }
    let i = t.labels, s = H(i.font), n = s.size, o = this._computeTitleHeight(), { boxWidth: a, itemHeight: r } = qn(i, n), l, c;
    if (e.font = s.string, this.isHorizontal())
      l = this.maxWidth, c = this._fitRows(o, n, a, r) + 10;
    else
      c = this.maxHeight, l = this._fitCols(o, s, a, r) + 10;
    this.width = Math.min(l, t.maxWidth || this.maxWidth), this.height = Math.min(c, t.maxHeight || this.maxHeight);
  }
  _fitRows(t, e, i, s) {
    let { ctx: n, maxWidth: o, options: { labels: { padding: a } } } = this, r = this.legendHitBoxes = [], l = this.lineWidths = [
      0
    ], c = s + a, h = t;
    n.textAlign = "left", n.textBaseline = "middle";
    let d = -1, u = -c;
    return this.legendItems.forEach((f, m) => {
      let g = i + e / 2 + n.measureText(f.text).width;
      if (m === 0 || l[l.length - 1] + g + 2 * a > o)
        h += c, l[l.length - (m > 0 ? 0 : 1)] = 0, u += c, d++;
      r[m] = {
        left: 0,
        top: u,
        row: d,
        width: g,
        height: s
      }, l[l.length - 1] += g + a;
    }), h;
  }
  _fitCols(t, e, i, s) {
    let { ctx: n, maxHeight: o, options: { labels: { padding: a } } } = this, r = this.legendHitBoxes = [], l = this.columnSizes = [], c = o - t, h = a, d = 0, u = 0, f = 0, m = 0;
    return this.legendItems.forEach((g, p) => {
      let { itemWidth: b, itemHeight: x } = Kc(i, e, n, g, s);
      if (p > 0 && u + x + 2 * a > c)
        h += d + a, l.push({
          width: d,
          height: u
        }), f += d + a, m++, d = u = 0;
      r[p] = {
        left: f,
        top: u,
        col: m,
        width: b,
        height: x
      }, d = Math.max(d, b), u += x + a;
    }), h += d, l.push({
      width: d,
      height: u
    }), h;
  }
  adjustHitBoxes() {
    if (!this.options.display)
      return;
    let t = this._computeTitleHeight(), { legendHitBoxes: e, options: { align: i, labels: { padding: s }, rtl: n } } = this, o = zt(n, this.left, this.width);
    if (this.isHorizontal()) {
      let a = 0, r = K(i, this.left + s, this.right - this.lineWidths[a]);
      for (let l of e) {
        if (a !== l.row)
          a = l.row, r = K(i, this.left + s, this.right - this.lineWidths[a]);
        l.top += this.top + t + s, l.left = o.leftForLtr(o.x(r), l.width), r += l.width + s;
      }
    } else {
      let a = 0, r = K(i, this.top + t + s, this.bottom - this.columnSizes[a].height);
      for (let l of e) {
        if (l.col !== a)
          a = l.col, r = K(i, this.top + t + s, this.bottom - this.columnSizes[a].height);
        l.top = r, l.left += this.left + s, l.left = o.leftForLtr(o.x(l.left), l.width), r += l.height + s;
      }
    }
  }
  isHorizontal() {
    return this.options.position === "top" || this.options.position === "bottom";
  }
  draw() {
    if (this.options.display) {
      let t = this.ctx;
      ce(t, this), this._draw(), he(t);
    }
  }
  _draw() {
    let { options: t, columnSizes: e, lineWidths: i, ctx: s } = this, { align: n, labels: o } = t, a = F.color, r = zt(t.rtl, this.left, this.width), l = H(o.font), { padding: c } = o, h = l.size, d = h / 2, u;
    this.drawTitle(), s.textAlign = r.textAlign("left"), s.textBaseline = "middle", s.lineWidth = 0.5, s.font = l.string;
    let { boxWidth: f, boxHeight: m, itemHeight: g } = qn(o, h), p = function(_, M, k) {
      if (isNaN(f) || f <= 0 || isNaN(m) || m < 0)
        return;
      s.save();
      let S = P(k.lineWidth, 1);
      if (s.fillStyle = P(k.fillStyle, a), s.lineCap = P(k.lineCap, "butt"), s.lineDashOffset = P(k.lineDashOffset, 0), s.lineJoin = P(k.lineJoin, "miter"), s.lineWidth = S, s.strokeStyle = P(k.strokeStyle, a), s.setLineDash(P(k.lineDash, [])), o.usePointStyle) {
        let w = {
          radius: m * Math.SQRT2 / 2,
          pointStyle: k.pointStyle,
          rotation: k.rotation,
          borderWidth: S
        }, C = r.xPlus(_, f / 2), D = M + d;
        Ei(s, w, C, D, o.pointStyleWidth && f);
      } else {
        let w = M + Math.max((h - m) / 2, 0), C = r.leftForLtr(_, f), D = At(k.borderRadius);
        if (s.beginPath(), Object.values(D).some((V) => V !== 0))
          Zt(s, {
            x: C,
            y: w,
            w: f,
            h: m,
            radius: D
          });
        else
          s.rect(C, w, f, m);
        if (s.fill(), S !== 0)
          s.stroke();
      }
      s.restore();
    }, b = function(_, M, k) {
      Dt(s, k.text, _, M + g / 2, l, {
        strikethrough: k.hidden,
        textAlign: r.textAlign(k.textAlign)
      });
    }, x = this.isHorizontal(), v = this._computeTitleHeight();
    if (x)
      u = {
        x: K(n, this.left + c, this.right - i[0]),
        y: this.top + c + v,
        line: 0
      };
    else
      u = {
        x: this.left + c,
        y: K(n, this.top + v + c, this.bottom - e[0].height),
        line: 0
      };
    Hi(this.ctx, t.textDirection);
    let y = g + c;
    this.legendItems.forEach((_, M) => {
      s.strokeStyle = _.fontColor, s.fillStyle = _.fontColor;
      let k = s.measureText(_.text).width, S = r.textAlign(_.textAlign || (_.textAlign = o.textAlign)), w = f + d + k, C = u.x, D = u.y;
      if (r.setWidth(this.width), x) {
        if (M > 0 && C + w + c > this.right)
          D = u.y += y, u.line++, C = u.x = K(n, this.left + c, this.right - i[u.line]);
      } else if (M > 0 && D + y > this.bottom)
        C = u.x = C + e[u.line].width + c, u.line++, D = u.y = K(n, this.top + v + c, this.bottom - e[u.line].height);
      let V = r.x(C);
      if (p(V, D, _), C = Xs(S, C + f + d, x ? C + w : this.right, t.rtl), b(r.x(C), D, _), x)
        u.x += w + c;
      else if (typeof _.text !== "string") {
        let tt = l.lineHeight;
        u.y += Yo(_, tt) + c;
      } else
        u.y += y;
    }), ji(this.ctx, t.textDirection);
  }
  drawTitle() {
    let t = this.options, e = t.title, i = H(e.font), s = q(e.padding);
    if (!e.display)
      return;
    let n = zt(t.rtl, this.left, this.width), o = this.ctx, a = e.position, r = i.size / 2, l = s.top + r, c, h = this.left, d = this.width;
    if (this.isHorizontal())
      d = Math.max(...this.lineWidths), c = this.top + l, h = K(t.align, h, this.right - d);
    else {
      let f = this.columnSizes.reduce((m, g) => Math.max(m, g.height), 0);
      c = l + K(t.align, this.top, this.bottom - f - t.labels.padding - this._computeTitleHeight());
    }
    let u = K(a, h, h + d);
    o.textAlign = n.textAlign(We(a)), o.textBaseline = "middle", o.strokeStyle = e.color, o.fillStyle = e.color, o.font = i.string, Dt(o, e.text, u, c, i);
  }
  _computeTitleHeight() {
    let t = this.options.title, e = H(t.font), i = q(t.padding);
    return t.display ? e.lineHeight + i.height : 0;
  }
  _getLegendItemAt(t, e) {
    let i, s, n;
    if (ct(t, this.left, this.right) && ct(e, this.top, this.bottom)) {
      n = this.legendHitBoxes;
      for (i = 0;i < n.length; ++i)
        if (s = n[i], ct(t, s.left, s.left + s.width) && ct(e, s.top, s.top + s.height))
          return this.legendItems[i];
    }
    return null;
  }
  handleEvent(t) {
    let e = this.options;
    if (!Jc(t.type, e))
      return;
    let i = this._getLegendItemAt(t.x, t.y);
    if (t.type === "mousemove" || t.type === "mouseout") {
      let s = this._hoveredItem, n = Xc(s, i);
      if (s && !n)
        R(e.onLeave, [
          t,
          s,
          this
        ], this);
      if (this._hoveredItem = i, i && !n)
        R(e.onHover, [
          t,
          i,
          this
        ], this);
    } else if (i)
      R(e.onClick, [
        t,
        i,
        this
      ], this);
  }
}
function Kc(t, e, i, s, n) {
  let o = qc(s, t, e, i), a = Gc(n, s, e.lineHeight);
  return {
    itemWidth: o,
    itemHeight: a
  };
}
function qc(t, e, i, s) {
  let n = t.text;
  if (n && typeof n !== "string")
    n = n.reduce((o, a) => o.length > a.length ? o : a);
  return e + i.size / 2 + s.measureText(n).width;
}
function Gc(t, e, i) {
  let s = t;
  if (typeof e.text !== "string")
    s = Yo(e, i);
  return s;
}
function Yo(t, e) {
  let i = t.text ? t.text.length : 0;
  return e * i;
}
function Jc(t, e) {
  if ((t === "mousemove" || t === "mouseout") && (e.onHover || e.onLeave))
    return !0;
  if (e.onClick && (t === "click" || t === "mouseup"))
    return !0;
  return !1;
}
var Zc = {
  id: "legend",
  _element: cs,
  start(t, e, i) {
    let s = t.legend = new cs({
      ctx: t.ctx,
      options: i,
      chart: t
    });
    G.configure(t, s, i), G.addBox(t, s);
  },
  stop(t) {
    G.removeBox(t, t.legend), delete t.legend;
  },
  beforeUpdate(t, e, i) {
    let s = t.legend;
    G.configure(t, s, i), s.options = i;
  },
  afterUpdate(t) {
    let e = t.legend;
    e.buildLabels(), e.adjustHitBoxes();
  },
  afterEvent(t, e) {
    if (!e.replay)
      t.legend.handleEvent(e.event);
  },
  defaults: {
    display: !0,
    position: "top",
    align: "center",
    fullSize: !0,
    reverse: !1,
    weight: 1000,
    onClick(t, e, i) {
      let s = e.datasetIndex, n = i.chart;
      if (n.isDatasetVisible(s))
        n.hide(s), e.hidden = !0;
      else
        n.show(s), e.hidden = !1;
    },
    onHover: null,
    onLeave: null,
    labels: {
      color: (t) => t.chart.options.color,
      boxWidth: 40,
      padding: 10,
      generateLabels(t) {
        let e = t.data.datasets, { labels: { usePointStyle: i, pointStyle: s, textAlign: n, color: o, useBorderRadius: a, borderRadius: r } } = t.legend.options;
        return t._getSortedDatasetMetas().map((l) => {
          let c = l.controller.getStyle(i ? 0 : void 0), h = q(c.borderWidth);
          return {
            text: e[l.index].label,
            fillStyle: c.backgroundColor,
            fontColor: o,
            hidden: !l.visible,
            lineCap: c.borderCapStyle,
            lineDash: c.borderDash,
            lineDashOffset: c.borderDashOffset,
            lineJoin: c.borderJoinStyle,
            lineWidth: (h.width + h.height) / 4,
            strokeStyle: c.borderColor,
            pointStyle: s || c.pointStyle,
            rotation: c.rotation,
            textAlign: n || c.textAlign,
            borderRadius: a && (r || c.borderRadius),
            datasetIndex: l.index
          };
        }, this);
      }
    },
    title: {
      color: (t) => t.chart.options.color,
      display: !1,
      position: "center",
      text: ""
    }
  },
  descriptors: {
    _scriptable: (t) => !t.startsWith("on"),
    labels: {
      _scriptable: (t) => ![
        "generateLabels",
        "filter",
        "sort"
      ].includes(t)
    }
  }
};

class ai extends ut {
  constructor(t) {
    super();
    this.chart = t.chart, this.options = t.options, this.ctx = t.ctx, this._padding = void 0, this.top = void 0, this.bottom = void 0, this.left = void 0, this.right = void 0, this.width = void 0, this.height = void 0, this.position = void 0, this.weight = void 0, this.fullSize = void 0;
  }
  update(t, e) {
    let i = this.options;
    if (this.left = 0, this.top = 0, !i.display) {
      this.width = this.height = this.right = this.bottom = 0;
      return;
    }
    this.width = this.right = t, this.height = this.bottom = e;
    let s = E(i.text) ? i.text.length : 1;
    this._padding = q(i.padding);
    let n = s * H(i.font).lineHeight + this._padding.height;
    if (this.isHorizontal())
      this.height = n;
    else
      this.width = n;
  }
  isHorizontal() {
    let t = this.options.position;
    return t === "top" || t === "bottom";
  }
  _drawArgs(t) {
    let { top: e, left: i, bottom: s, right: n, options: o } = this, a = o.align, r = 0, l, c, h;
    if (this.isHorizontal())
      c = K(a, i, n), h = e + t, l = n - i;
    else {
      if (o.position === "left")
        c = i + t, h = K(a, s, e), r = T * -0.5;
      else
        c = n - t, h = K(a, e, s), r = T * 0.5;
      l = s - e;
    }
    return {
      titleX: c,
      titleY: h,
      maxWidth: l,
      rotation: r
    };
  }
  draw() {
    let t = this.ctx, e = this.options;
    if (!e.display)
      return;
    let i = H(e.font), n = i.lineHeight / 2 + this._padding.top, { titleX: o, titleY: a, maxWidth: r, rotation: l } = this._drawArgs(n);
    Dt(t, e.text, 0, 0, i, {
      color: e.color,
      maxWidth: r,
      rotation: l,
      textAlign: We(e.align),
      textBaseline: "middle",
      translation: [
        o,
        a
      ]
    });
  }
}
function Qc(t, e) {
  let i = new ai({
    ctx: t.ctx,
    options: e,
    chart: t
  });
  G.configure(t, i, e), G.addBox(t, i), t.titleBlock = i;
}
var th = {
  id: "title",
  _element: ai,
  start(t, e, i) {
    Qc(t, i);
  },
  stop(t) {
    let e = t.titleBlock;
    G.removeBox(t, e), delete t.titleBlock;
  },
  beforeUpdate(t, e, i) {
    let s = t.titleBlock;
    G.configure(t, s, i), s.options = i;
  },
  defaults: {
    align: "center",
    display: !1,
    font: {
      weight: "bold"
    },
    fullSize: !0,
    padding: 10,
    position: "top",
    text: "",
    weight: 2000
  },
  defaultRoutes: {
    color: "color"
  },
  descriptors: {
    _scriptable: !0,
    _indexable: !1
  }
}, Ge = /* @__PURE__ */ new WeakMap, eh = {
  id: "subtitle",
  start(t, e, i) {
    let s = new ai({
      ctx: t.ctx,
      options: i,
      chart: t
    });
    G.configure(t, s, i), G.addBox(t, s), Ge.set(t, s);
  },
  stop(t) {
    G.removeBox(t, Ge.get(t)), Ge.delete(t);
  },
  beforeUpdate(t, e, i) {
    let s = Ge.get(t);
    G.configure(t, s, i), s.options = i;
  },
  defaults: {
    align: "center",
    display: !1,
    font: {
      weight: "normal"
    },
    fullSize: !0,
    padding: 0,
    position: "top",
    text: "",
    weight: 1500
  },
  defaultRoutes: {
    color: "color"
  },
  descriptors: {
    _scriptable: !0,
    _indexable: !1
  }
}, xe = {
  average(t) {
    if (!t.length)
      return !1;
    let e, i, s = /* @__PURE__ */ new Set, n = 0, o = 0;
    for (e = 0, i = t.length;e < i; ++e) {
      let r = t[e].element;
      if (r && r.hasValue()) {
        let l = r.tooltipPosition();
        s.add(l.x), n += l.y, ++o;
      }
    }
    if (o === 0 || s.size === 0)
      return !1;
    return {
      x: [
        ...s
      ].reduce((r, l) => r + l) / s.size,
      y: n / o
    };
  },
  nearest(t, e) {
    if (!t.length)
      return !1;
    let { x: i, y: s } = e, n = Number.POSITIVE_INFINITY, o, a, r;
    for (o = 0, a = t.length;o < a; ++o) {
      let l = t[o].element;
      if (l && l.hasValue()) {
        let c = l.getCenterPoint(), h = Ee(e, c);
        if (h < n)
          n = h, r = l;
      }
    }
    if (r) {
      let l = r.tooltipPosition();
      i = l.x, s = l.y;
    }
    return {
      x: i,
      y: s
    };
  }
};
function ht(t, e) {
  if (e)
    if (E(e))
      Array.prototype.push.apply(t, e);
    else
      t.push(e);
  return t;
}
function _t(t) {
  if ((typeof t === "string" || t instanceof String) && t.indexOf(`
`) > -1)
    return t.split(`
`);
  return t;
}
function ih(t, e) {
  let { element: i, datasetIndex: s, index: n } = e, o = t.getDatasetMeta(s).controller, { label: a, value: r } = o.getLabelAndValue(n);
  return {
    chart: t,
    label: a,
    parsed: o.getParsed(n),
    raw: t.data.datasets[s].data[n],
    formattedValue: r,
    dataset: o.getDataset(),
    dataIndex: n,
    datasetIndex: s,
    element: i
  };
}
function Gn(t, e) {
  let i = t.chart.ctx, { body: s, footer: n, title: o } = t, { boxWidth: a, boxHeight: r } = e, l = H(e.bodyFont), c = H(e.titleFont), h = H(e.footerFont), d = o.length, u = n.length, f = s.length, m = q(e.padding), g = m.height, p = 0, b = s.reduce((y, _) => y + _.before.length + _.lines.length + _.after.length, 0);
  if (b += t.beforeBody.length + t.afterBody.length, d)
    g += d * c.lineHeight + (d - 1) * e.titleSpacing + e.titleMarginBottom;
  if (b) {
    let y = e.displayColors ? Math.max(r, l.lineHeight) : l.lineHeight;
    g += f * y + (b - f) * l.lineHeight + (b - 1) * e.bodySpacing;
  }
  if (u)
    g += e.footerMarginTop + u * h.lineHeight + (u - 1) * e.footerSpacing;
  let x = 0, v = function(y) {
    p = Math.max(p, i.measureText(y).width + x);
  };
  return i.save(), i.font = c.string, L(t.title, v), i.font = l.string, L(t.beforeBody.concat(t.afterBody), v), x = e.displayColors ? a + 2 + e.boxPadding : 0, L(s, (y) => {
    L(y.before, v), L(y.lines, v), L(y.after, v);
  }), x = 0, i.font = h.string, L(t.footer, v), i.restore(), p += m.width, {
    width: p,
    height: g
  };
}
function sh(t, e) {
  let { y: i, height: s } = e;
  if (i < s / 2)
    return "top";
  else if (i > t.height - s / 2)
    return "bottom";
  return "center";
}
function nh(t, e, i, s) {
  let { x: n, width: o } = s, a = i.caretSize + i.caretPadding;
  if (t === "left" && n + o + a > e.width)
    return !0;
  if (t === "right" && n - o - a < 0)
    return !0;
}
function oh(t, e, i, s) {
  let { x: n, width: o } = i, { width: a, chartArea: { left: r, right: l } } = t, c = "center";
  if (s === "center")
    c = n <= (r + l) / 2 ? "left" : "right";
  else if (n <= o / 2)
    c = "left";
  else if (n >= a - o / 2)
    c = "right";
  if (nh(c, t, e, i))
    c = "center";
  return c;
}
function Jn(t, e, i) {
  let s = i.yAlign || e.yAlign || sh(t, i);
  return {
    xAlign: i.xAlign || e.xAlign || oh(t, e, i, s),
    yAlign: s
  };
}
function ah(t, e) {
  let { x: i, width: s } = t;
  if (e === "right")
    i -= s;
  else if (e === "center")
    i -= s / 2;
  return i;
}
function rh(t, e, i) {
  let { y: s, height: n } = t;
  if (e === "top")
    s += i;
  else if (e === "bottom")
    s -= n + i;
  else
    s -= n / 2;
  return s;
}
function Zn(t, e, i, s) {
  let { caretSize: n, caretPadding: o, cornerRadius: a } = t, { xAlign: r, yAlign: l } = i, c = n + o, { topLeft: h, topRight: d, bottomLeft: u, bottomRight: f } = At(a), m = ah(e, r), g = rh(e, l, c);
  if (l === "center") {
    if (r === "left")
      m += c;
    else if (r === "right")
      m -= c;
  } else if (r === "left")
    m -= Math.max(h, u) + n;
  else if (r === "right")
    m += Math.max(d, f) + n;
  return {
    x: Y(m, 0, s.width - e.width),
    y: Y(g, 0, s.height - e.height)
  };
}
function Je(t, e, i) {
  let s = q(i.padding);
  return e === "center" ? t.x + t.width / 2 : e === "right" ? t.x + t.width - s.right : t.x + s.left;
}
function Qn(t) {
  return ht([], _t(t));
}
function lh(t, e, i) {
  return bt(t, {
    tooltip: e,
    tooltipItems: i,
    type: "tooltip"
  });
}
function to(t, e) {
  let i = e && e.dataset && e.dataset.tooltip && e.dataset.tooltip.callbacks;
  return i ? t.override(i) : t;
}
var Uo = {
  beforeTitle: lt,
  title(t) {
    if (t.length > 0) {
      let e = t[0], i = e.chart.data.labels, s = i ? i.length : 0;
      if (this && this.options && this.options.mode === "dataset")
        return e.dataset.label || "";
      else if (e.label)
        return e.label;
      else if (s > 0 && e.dataIndex < s)
        return i[e.dataIndex];
    }
    return "";
  },
  afterTitle: lt,
  beforeBody: lt,
  beforeLabel: lt,
  label(t) {
    if (this && this.options && this.options.mode === "dataset")
      return t.label + ": " + t.formattedValue || t.formattedValue;
    let e = t.dataset.label || "";
    if (e)
      e += ": ";
    let i = t.formattedValue;
    if (!A(i))
      e += i;
    return e;
  },
  labelColor(t) {
    let i = t.chart.getDatasetMeta(t.datasetIndex).controller.getStyle(t.dataIndex);
    return {
      borderColor: i.borderColor,
      backgroundColor: i.backgroundColor,
      borderWidth: i.borderWidth,
      borderDash: i.borderDash,
      borderDashOffset: i.borderDashOffset,
      borderRadius: 0
    };
  },
  labelTextColor() {
    return this.options.bodyColor;
  },
  labelPointStyle(t) {
    let i = t.chart.getDatasetMeta(t.datasetIndex).controller.getStyle(t.dataIndex);
    return {
      pointStyle: i.pointStyle,
      rotation: i.rotation
    };
  },
  afterLabel: lt,
  afterBody: lt,
  beforeFooter: lt,
  footer: lt,
  afterFooter: lt
};
function Z(t, e, i, s) {
  let n = t[e].call(i, s);
  if (typeof n > "u")
    return Uo[e].call(i, s);
  return n;
}

class hs extends ut {
  static positioners = xe;
  constructor(t) {
    super();
    this.opacity = 0, this._active = [], this._eventPosition = void 0, this._size = void 0, this._cachedAnimations = void 0, this._tooltipItems = [], this.$animations = void 0, this.$context = void 0, this.chart = t.chart, this.options = t.options, this.dataPoints = void 0, this.title = void 0, this.beforeBody = void 0, this.body = void 0, this.afterBody = void 0, this.footer = void 0, this.xAlign = void 0, this.yAlign = void 0, this.x = void 0, this.y = void 0, this.height = void 0, this.width = void 0, this.caretX = void 0, this.caretY = void 0, this.labelColors = void 0, this.labelPointStyles = void 0, this.labelTextColors = void 0;
  }
  initialize(t) {
    this.options = t, this._cachedAnimations = void 0, this.$context = void 0;
  }
  _resolveAnimations() {
    let t = this._cachedAnimations;
    if (t)
      return t;
    let e = this.chart, i = this.options.setContext(this.getContext()), s = i.enabled && e.options.animation && i.animations, n = new us(this.chart, s);
    if (s._cacheable)
      this._cachedAnimations = Object.freeze(n);
    return n;
  }
  getContext() {
    return this.$context || (this.$context = lh(this.chart.getContext(), this, this._tooltipItems));
  }
  getTitle(t, e) {
    let { callbacks: i } = e, s = Z(i, "beforeTitle", this, t), n = Z(i, "title", this, t), o = Z(i, "afterTitle", this, t), a = [];
    return a = ht(a, _t(s)), a = ht(a, _t(n)), a = ht(a, _t(o)), a;
  }
  getBeforeBody(t, e) {
    return Qn(Z(e.callbacks, "beforeBody", this, t));
  }
  getBody(t, e) {
    let { callbacks: i } = e, s = [];
    return L(t, (n) => {
      let o = {
        before: [],
        lines: [],
        after: []
      }, a = to(i, n);
      ht(o.before, _t(Z(a, "beforeLabel", this, n))), ht(o.lines, Z(a, "label", this, n)), ht(o.after, _t(Z(a, "afterLabel", this, n))), s.push(o);
    }), s;
  }
  getAfterBody(t, e) {
    return Qn(Z(e.callbacks, "afterBody", this, t));
  }
  getFooter(t, e) {
    let { callbacks: i } = e, s = Z(i, "beforeFooter", this, t), n = Z(i, "footer", this, t), o = Z(i, "afterFooter", this, t), a = [];
    return a = ht(a, _t(s)), a = ht(a, _t(n)), a = ht(a, _t(o)), a;
  }
  _createItems(t) {
    let e = this._active, i = this.chart.data, s = [], n = [], o = [], a = [], r, l;
    for (r = 0, l = e.length;r < l; ++r)
      a.push(ih(this.chart, e[r]));
    if (t.filter)
      a = a.filter((c, h, d) => t.filter(c, h, d, i));
    if (t.itemSort)
      a = a.sort((c, h) => t.itemSort(c, h, i));
    return L(a, (c) => {
      let h = to(t.callbacks, c);
      s.push(Z(h, "labelColor", this, c)), n.push(Z(h, "labelPointStyle", this, c)), o.push(Z(h, "labelTextColor", this, c));
    }), this.labelColors = s, this.labelPointStyles = n, this.labelTextColors = o, this.dataPoints = a, a;
  }
  update(t, e) {
    let i = this.options.setContext(this.getContext()), s = this._active, n, o = [];
    if (!s.length) {
      if (this.opacity !== 0)
        n = {
          opacity: 0
        };
    } else {
      let a = xe[i.position].call(this, s, this._eventPosition);
      o = this._createItems(i), this.title = this.getTitle(o, i), this.beforeBody = this.getBeforeBody(o, i), this.body = this.getBody(o, i), this.afterBody = this.getAfterBody(o, i), this.footer = this.getFooter(o, i);
      let r = this._size = Gn(this, i), l = Object.assign({}, a, r), c = Jn(this.chart, i, l), h = Zn(i, l, c, this.chart);
      this.xAlign = c.xAlign, this.yAlign = c.yAlign, n = {
        opacity: 1,
        x: h.x,
        y: h.y,
        width: r.width,
        height: r.height,
        caretX: a.x,
        caretY: a.y
      };
    }
    if (this._tooltipItems = o, this.$context = void 0, n)
      this._resolveAnimations().update(this, n);
    if (t && i.external)
      i.external.call(this, {
        chart: this.chart,
        tooltip: this,
        replay: e
      });
  }
  drawCaret(t, e, i, s) {
    let n = this.getCaretPosition(t, i, s);
    e.lineTo(n.x1, n.y1), e.lineTo(n.x2, n.y2), e.lineTo(n.x3, n.y3);
  }
  getCaretPosition(t, e, i) {
    let { xAlign: s, yAlign: n } = this, { caretSize: o, cornerRadius: a } = i, { topLeft: r, topRight: l, bottomLeft: c, bottomRight: h } = At(a), { x: d, y: u } = t, { width: f, height: m } = e, g, p, b, x, v, y;
    if (n === "center") {
      if (v = u + m / 2, s === "left")
        g = d, p = g - o, x = v + o, y = v - o;
      else
        g = d + f, p = g + o, x = v - o, y = v + o;
      b = g;
    } else {
      if (s === "left")
        p = d + Math.max(r, c) + o;
      else if (s === "right")
        p = d + f - Math.max(l, h) - o;
      else
        p = this.caretX;
      if (n === "top")
        x = u, v = x - o, g = p - o, b = p + o;
      else
        x = u + m, v = x + o, g = p + o, b = p - o;
      y = x;
    }
    return {
      x1: g,
      x2: p,
      x3: b,
      y1: x,
      y2: v,
      y3: y
    };
  }
  drawTitle(t, e, i) {
    let s = this.title, n = s.length, o, a, r;
    if (n) {
      let l = zt(i.rtl, this.x, this.width);
      t.x = Je(this, i.titleAlign, i), e.textAlign = l.textAlign(i.titleAlign), e.textBaseline = "middle", o = H(i.titleFont), a = i.titleSpacing, e.fillStyle = i.titleColor, e.font = o.string;
      for (r = 0;r < n; ++r)
        if (e.fillText(s[r], l.x(t.x), t.y + o.lineHeight / 2), t.y += o.lineHeight + a, r + 1 === n)
          t.y += i.titleMarginBottom - a;
    }
  }
  _drawColorBox(t, e, i, s, n) {
    let o = this.labelColors[i], a = this.labelPointStyles[i], { boxHeight: r, boxWidth: l } = n, c = H(n.bodyFont), h = Je(this, "left", n), d = s.x(h), u = r < c.lineHeight ? (c.lineHeight - r) / 2 : 0, f = e.y + u;
    if (n.usePointStyle) {
      let m = {
        radius: Math.min(l, r) / 2,
        pointStyle: a.pointStyle,
        rotation: a.rotation,
        borderWidth: 1
      }, g = s.leftForLtr(d, l) + l / 2, p = f + r / 2;
      t.strokeStyle = n.multiKeyBackground, t.fillStyle = n.multiKeyBackground, Ve(t, m, g, p), t.strokeStyle = o.borderColor, t.fillStyle = o.backgroundColor, Ve(t, m, g, p);
    } else {
      t.lineWidth = O(o.borderWidth) ? Math.max(...Object.values(o.borderWidth)) : o.borderWidth || 1, t.strokeStyle = o.borderColor, t.setLineDash(o.borderDash || []), t.lineDashOffset = o.borderDashOffset || 0;
      let m = s.leftForLtr(d, l), g = s.leftForLtr(s.xPlus(d, 1), l - 2), p = At(o.borderRadius);
      if (Object.values(p).some((b) => b !== 0))
        t.beginPath(), t.fillStyle = n.multiKeyBackground, Zt(t, {
          x: m,
          y: f,
          w: l,
          h: r,
          radius: p
        }), t.fill(), t.stroke(), t.fillStyle = o.backgroundColor, t.beginPath(), Zt(t, {
          x: g,
          y: f + 1,
          w: l - 2,
          h: r - 2,
          radius: p
        }), t.fill();
      else
        t.fillStyle = n.multiKeyBackground, t.fillRect(m, f, l, r), t.strokeRect(m, f, l, r), t.fillStyle = o.backgroundColor, t.fillRect(g, f + 1, l - 2, r - 2);
    }
    t.fillStyle = this.labelTextColors[i];
  }
  drawBody(t, e, i) {
    let { body: s } = this, { bodySpacing: n, bodyAlign: o, displayColors: a, boxHeight: r, boxWidth: l, boxPadding: c } = i, h = H(i.bodyFont), d = h.lineHeight, u = 0, f = zt(i.rtl, this.x, this.width), m = function(k) {
      e.fillText(k, f.x(t.x + u), t.y + d / 2), t.y += d + n;
    }, g = f.textAlign(o), p, b, x, v, y, _, M;
    e.textAlign = o, e.textBaseline = "middle", e.font = h.string, t.x = Je(this, g, i), e.fillStyle = i.bodyColor, L(this.beforeBody, m), u = a && g !== "right" ? o === "center" ? l / 2 + c : l + 2 + c : 0;
    for (v = 0, _ = s.length;v < _; ++v) {
      if (p = s[v], b = this.labelTextColors[v], e.fillStyle = b, L(p.before, m), x = p.lines, a && x.length)
        this._drawColorBox(e, t, v, f, i), d = Math.max(h.lineHeight, r);
      for (y = 0, M = x.length;y < M; ++y)
        m(x[y]), d = h.lineHeight;
      L(p.after, m);
    }
    u = 0, d = h.lineHeight, L(this.afterBody, m), t.y -= n;
  }
  drawFooter(t, e, i) {
    let s = this.footer, n = s.length, o, a;
    if (n) {
      let r = zt(i.rtl, this.x, this.width);
      t.x = Je(this, i.footerAlign, i), t.y += i.footerMarginTop, e.textAlign = r.textAlign(i.footerAlign), e.textBaseline = "middle", o = H(i.footerFont), e.fillStyle = i.footerColor, e.font = o.string;
      for (a = 0;a < n; ++a)
        e.fillText(s[a], r.x(t.x), t.y + o.lineHeight / 2), t.y += o.lineHeight + i.footerSpacing;
    }
  }
  drawBackground(t, e, i, s) {
    let { xAlign: n, yAlign: o } = this, { x: a, y: r } = t, { width: l, height: c } = i, { topLeft: h, topRight: d, bottomLeft: u, bottomRight: f } = At(s.cornerRadius);
    if (e.fillStyle = s.backgroundColor, e.strokeStyle = s.borderColor, e.lineWidth = s.borderWidth, e.beginPath(), e.moveTo(a + h, r), o === "top")
      this.drawCaret(t, e, i, s);
    if (e.lineTo(a + l - d, r), e.quadraticCurveTo(a + l, r, a + l, r + d), o === "center" && n === "right")
      this.drawCaret(t, e, i, s);
    if (e.lineTo(a + l, r + c - f), e.quadraticCurveTo(a + l, r + c, a + l - f, r + c), o === "bottom")
      this.drawCaret(t, e, i, s);
    if (e.lineTo(a + u, r + c), e.quadraticCurveTo(a, r + c, a, r + c - u), o === "center" && n === "left")
      this.drawCaret(t, e, i, s);
    if (e.lineTo(a, r + h), e.quadraticCurveTo(a, r, a + h, r), e.closePath(), e.fill(), s.borderWidth > 0)
      e.stroke();
  }
  _updateAnimationTarget(t) {
    let e = this.chart, i = this.$animations, s = i && i.x, n = i && i.y;
    if (s || n) {
      let o = xe[t.position].call(this, this._active, this._eventPosition);
      if (!o)
        return;
      let a = this._size = Gn(this, t), r = Object.assign({}, o, this._size), l = Jn(e, t, r), c = Zn(t, r, l, e);
      if (s._to !== c.x || n._to !== c.y)
        this.xAlign = l.xAlign, this.yAlign = l.yAlign, this.width = a.width, this.height = a.height, this.caretX = o.x, this.caretY = o.y, this._resolveAnimations().update(this, c);
    }
  }
  _willRender() {
    return !!this.opacity;
  }
  draw(t) {
    let e = this.options.setContext(this.getContext()), i = this.opacity;
    if (!i)
      return;
    this._updateAnimationTarget(e);
    let s = {
      width: this.width,
      height: this.height
    }, n = {
      x: this.x,
      y: this.y
    };
    i = Math.abs(i) < 0.001 ? 0 : i;
    let o = q(e.padding), a = this.title.length || this.beforeBody.length || this.body.length || this.afterBody.length || this.footer.length;
    if (e.enabled && a)
      t.save(), t.globalAlpha = i, this.drawBackground(n, t, s, e), Hi(t, e.textDirection), n.y += o.top, this.drawTitle(n, t, e), this.drawBody(n, t, e), this.drawFooter(n, t, e), ji(t, e.textDirection), t.restore();
  }
  getActiveElements() {
    return this._active || [];
  }
  setActiveElements(t, e) {
    let i = this._active, s = t.map(({ datasetIndex: a, index: r }) => {
      let l = this.chart.getDatasetMeta(a);
      if (!l)
        throw Error("Cannot find a dataset at index " + a);
      return {
        datasetIndex: a,
        element: l.data[r],
        index: r
      };
    }), n = !re(i, s), o = this._positionChanged(s, e);
    if (n || o)
      this._active = s, this._eventPosition = e, this._ignoreReplayEvents = !0, this.update(!0);
  }
  handleEvent(t, e, i = !0) {
    if (e && this._ignoreReplayEvents)
      return !1;
    this._ignoreReplayEvents = !1;
    let s = this.options, n = this._active || [], o = this._getActiveElements(t, n, e, i), a = this._positionChanged(o, t), r = e || !re(o, n) || a;
    if (r) {
      if (this._active = o, s.enabled || s.external)
        this._eventPosition = {
          x: t.x,
          y: t.y
        }, this.update(!0, e);
    }
    return r;
  }
  _getActiveElements(t, e, i, s) {
    let n = this.options;
    if (t.type === "mouseout")
      return [];
    if (!s)
      return e.filter((a) => this.chart.data.datasets[a.datasetIndex] && this.chart.getDatasetMeta(a.datasetIndex).controller.getParsed(a.index) !== void 0);
    let o = this.chart.getElementsAtEventForMode(t, n.mode, n, i);
    if (n.reverse)
      o.reverse();
    return o;
  }
  _positionChanged(t, e) {
    let { caretX: i, caretY: s, options: n } = this, o = xe[n.position].call(this, t, e);
    return o !== !1 && (i !== o.x || s !== o.y);
  }
}
var ch = {
  id: "tooltip",
  _element: hs,
  positioners: xe,
  afterInit(t, e, i) {
    if (i)
      t.tooltip = new hs({
        chart: t,
        options: i
      });
  },
  beforeUpdate(t, e, i) {
    if (t.tooltip)
      t.tooltip.initialize(i);
  },
  reset(t, e, i) {
    if (t.tooltip)
      t.tooltip.initialize(i);
  },
  afterDraw(t) {
    let e = t.tooltip;
    if (e && e._willRender()) {
      let i = {
        tooltip: e
      };
      if (t.notifyPlugins("beforeTooltipDraw", {
        ...i,
        cancelable: !0
      }) === !1)
        return;
      e.draw(t.ctx), t.notifyPlugins("afterTooltipDraw", i);
    }
  },
  afterEvent(t, e) {
    if (t.tooltip) {
      let i = e.replay;
      if (t.tooltip.handleEvent(e.event, i, e.inChartArea))
        e.changed = !0;
    }
  },
  defaults: {
    enabled: !0,
    external: null,
    position: "average",
    backgroundColor: "rgba(0,0,0,0.8)",
    titleColor: "#fff",
    titleFont: {
      weight: "bold"
    },
    titleSpacing: 2,
    titleMarginBottom: 6,
    titleAlign: "left",
    bodyColor: "#fff",
    bodySpacing: 2,
    bodyFont: {},
    bodyAlign: "left",
    footerColor: "#fff",
    footerSpacing: 2,
    footerMarginTop: 6,
    footerFont: {
      weight: "bold"
    },
    footerAlign: "left",
    padding: 6,
    caretPadding: 2,
    caretSize: 5,
    cornerRadius: 6,
    boxHeight: (t, e) => e.bodyFont.size,
    boxWidth: (t, e) => e.bodyFont.size,
    multiKeyBackground: "#fff",
    displayColors: !0,
    boxPadding: 0,
    borderColor: "rgba(0,0,0,0)",
    borderWidth: 0,
    animation: {
      duration: 400,
      easing: "easeOutQuart"
    },
    animations: {
      numbers: {
        type: "number",
        properties: [
          "x",
          "y",
          "width",
          "height",
          "caretX",
          "caretY"
        ]
      },
      opacity: {
        easing: "linear",
        duration: 200
      }
    },
    callbacks: Uo
  },
  defaultRoutes: {
    bodyFont: "font",
    footerFont: "font",
    titleFont: "font"
  },
  descriptors: {
    _scriptable: (t) => t !== "filter" && t !== "itemSort" && t !== "external",
    _indexable: !1,
    callbacks: {
      _scriptable: !1,
      _indexable: !1
    },
    animation: {
      _fallback: !1
    },
    animations: {
      _fallback: "animation"
    }
  },
  additionalOptionScopes: [
    "interaction"
  ]
}, hh = /* @__PURE__ */ Object.freeze({
  __proto__: null,
  Colors: Mc,
  Decimation: Pc,
  Filler: Uc,
  Legend: Zc,
  SubTitle: eh,
  Title: th,
  Tooltip: ch
}), dh = (t, e, i, s) => {
  if (typeof e === "string")
    i = t.push(e) - 1, s.unshift({
      index: i,
      label: e
    });
  else if (isNaN(e))
    i = null;
  return i;
};
function uh(t, e, i, s) {
  let n = t.indexOf(e);
  if (n === -1)
    return dh(t, e, i, s);
  let o = t.lastIndexOf(e);
  return n !== o ? i : n;
}
var fh = (t, e) => t === null ? null : Y(Math.round(t), 0, e);
function eo(t) {
  let e = this.getLabels();
  if (t >= 0 && t < e.length)
    return e[t];
  return t;
}

class Xo extends Nt {
  static id = "category";
  static defaults = {
    ticks: {
      callback: eo
    }
  };
  constructor(t) {
    super(t);
    this._startValue = void 0, this._valueRange = 0, this._addedLabels = [];
  }
  init(t) {
    let e = this._addedLabels;
    if (e.length) {
      let i = this.getLabels();
      for (let { index: s, label: n } of e)
        if (i[s] === n)
          i.splice(s, 1);
      this._addedLabels = [];
    }
    super.init(t);
  }
  parse(t, e) {
    if (A(t))
      return null;
    let i = this.getLabels();
    return e = isFinite(e) && i[e] === t ? e : uh(i, t, P(e, t), this._addedLabels), fh(e, i.length - 1);
  }
  determineDataLimits() {
    let { minDefined: t, maxDefined: e } = this.getUserBounds(), { min: i, max: s } = this.getMinMax(!0);
    if (this.options.bounds === "ticks") {
      if (!t)
        i = 0;
      if (!e)
        s = this.getLabels().length - 1;
    }
    this.min = i, this.max = s;
  }
  buildTicks() {
    let t = this.min, e = this.max, i = this.options.offset, s = [], n = this.getLabels();
    n = t === 0 && e === n.length - 1 ? n : n.slice(t, e + 1), this._valueRange = Math.max(n.length - (i ? 0 : 1), 1), this._startValue = this.min - (i ? 0.5 : 0);
    for (let o = t;o <= e; o++)
      s.push({
        value: o
      });
    return s;
  }
  getLabelForValue(t) {
    return eo.call(this, t);
  }
  configure() {
    if (super.configure(), !this.isHorizontal())
      this._reversePixels = !this._reversePixels;
  }
  getPixelForValue(t) {
    if (typeof t !== "number")
      t = this.parse(t);
    return t === null ? NaN : this.getPixelForDecimal((t - this._startValue) / this._valueRange);
  }
  getPixelForTick(t) {
    let e = this.ticks;
    if (t < 0 || t > e.length - 1)
      return null;
    return this.getPixelForValue(e[t].value);
  }
  getValueForPixel(t) {
    return Math.round(this._startValue + this.getDecimalForPixel(t) * this._valueRange);
  }
  getBasePixel() {
    return this.bottom;
  }
}
function gh(t, e) {
  let i = [], s = 0.00000000000001, { bounds: n, step: o, min: a, max: r, precision: l, count: c, maxTicks: h, maxDigits: d, includeBounds: u } = t, f = o || 1, m = h - 1, { min: g, max: p } = e, b = !A(a), x = !A(r), v = !A(c), y = (p - g) / (d + 1), _ = vi((p - g) / m / f) * f, M, k, S, w;
  if (_ < 0.00000000000001 && !b && !x)
    return [
      {
        value: g
      },
      {
        value: p
      }
    ];
  if (w = Math.ceil(p / _) - Math.floor(g / _), w > m)
    _ = vi(w * _ / m / f) * f;
  if (!A(l))
    M = Math.pow(10, l), _ = Math.ceil(_ * M) / M;
  if (n === "ticks")
    k = Math.floor(g / _) * _, S = Math.ceil(p / _) * _;
  else
    k = g, S = p;
  if (b && x && o && Ns((r - a) / o, _ / 1000))
    w = Math.round(Math.min((r - a) / _, h)), _ = (r - a) / w, k = a, S = r;
  else if (v)
    k = b ? a : k, S = x ? r : S, w = c - 1, _ = (S - k) / w;
  else if (w = (S - k) / _, qt(w, Math.round(w), _ / 1000))
    w = Math.round(w);
  else
    w = Math.ceil(w);
  let C = Math.max(ki(_), ki(k));
  M = Math.pow(10, A(l) ? C : l), k = Math.round(k * M) / M, S = Math.round(S * M) / M;
  let D = 0;
  if (b) {
    if (u && k !== a) {
      if (i.push({
        value: a
      }), k < a)
        D++;
      if (qt(Math.round((k + D * _) * M) / M, a, io(a, y, t)))
        D++;
    } else if (k < a)
      D++;
  }
  for (;D < w; ++D) {
    let V = Math.round((k + D * _) * M) / M;
    if (x && V > r)
      break;
    i.push({
      value: V
    });
  }
  if (x && u && S !== r)
    if (i.length && qt(i[i.length - 1].value, r, io(r, y, t)))
      i[i.length - 1].value = r;
    else
      i.push({
        value: r
      });
  else if (!x || S === r)
    i.push({
      value: S
    });
  return i;
}
function io(t, e, { horizontal: i, minRotation: s }) {
  let n = st(s), o = (i ? Math.sin(n) : Math.cos(n)) || 0.001, a = 0.75 * e * ("" + t).length;
  return Math.min(e / o, a);
}

class ye extends Nt {
  constructor(t) {
    super(t);
    this.start = void 0, this.end = void 0, this._startValue = void 0, this._endValue = void 0, this._valueRange = 0;
  }
  parse(t, e) {
    if (A(t))
      return null;
    if ((typeof t === "number" || t instanceof Number) && !isFinite(+t))
      return null;
    return +t;
  }
  handleTickRangeOptions() {
    let { beginAtZero: t } = this.options, { minDefined: e, maxDefined: i } = this.getUserBounds(), { min: s, max: n } = this, o = (r) => s = e ? s : r, a = (r) => n = i ? n : r;
    if (t) {
      let r = ot(s), l = ot(n);
      if (r < 0 && l < 0)
        a(0);
      else if (r > 0 && l > 0)
        o(0);
    }
    if (s === n) {
      let r = n === 0 ? 1 : Math.abs(n * 0.05);
      if (a(n + r), !t)
        o(s - r);
    }
    this.min = s, this.max = n;
  }
  getTickLimit() {
    let t = this.options.ticks, { maxTicksLimit: e, stepSize: i } = t, s;
    if (i) {
      if (s = Math.ceil(this.max / i) - Math.floor(this.min / i) + 1, s > 1000)
        console.warn(`scales.${this.id}.ticks.stepSize: ${i} would result generating up to ${s} ticks. Limiting to 1000.`), s = 1000;
    } else
      s = this.computeTickLimit(), e = e || 11;
    if (e)
      s = Math.min(e, s);
    return s;
  }
  computeTickLimit() {
    return Number.POSITIVE_INFINITY;
  }
  buildTicks() {
    let t = this.options, e = t.ticks, i = this.getTickLimit();
    i = Math.max(2, i);
    let s = {
      maxTicks: i,
      bounds: t.bounds,
      min: t.min,
      max: t.max,
      precision: e.precision,
      step: e.stepSize,
      count: e.count,
      maxDigits: this._maxDigits(),
      horizontal: this.isHorizontal(),
      minRotation: e.minRotation || 0,
      includeBounds: e.includeBounds !== !1
    }, n = this._range || this, o = gh(s, n);
    if (t.bounds === "ticks")
      Mi(o, this, "value");
    if (t.reverse)
      o.reverse(), this.start = this.max, this.end = this.min;
    else
      this.start = this.min, this.end = this.max;
    return o;
  }
  configure() {
    let t = this.ticks, e = this.min, i = this.max;
    if (super.configure(), this.options.offset && t.length) {
      let s = (i - e) / Math.max(t.length - 1, 1) / 2;
      e -= s, i += s;
    }
    this._startValue = e, this._endValue = i, this._valueRange = i - e;
  }
  getLabelForValue(t) {
    return Jt(t, this.chart.options.locale, this.options.ticks.format);
  }
}

class Ko extends ye {
  static id = "linear";
  static defaults = {
    ticks: {
      callback: le.formatters.numeric
    }
  };
  determineDataLimits() {
    let { min: t, max: e } = this.getMinMax(!0);
    this.min = W(t) ? t : 0, this.max = W(e) ? e : 1, this.handleTickRangeOptions();
  }
  computeTickLimit() {
    let t = this.isHorizontal(), e = t ? this.width : this.height, i = st(this.options.ticks.minRotation), s = (t ? Math.sin(i) : Math.cos(i)) || 0.001, n = this._resolveTickFontOptions(0);
    return Math.ceil(e / Math.min(40, n.lineHeight / s));
  }
  getPixelForValue(t) {
    return t === null ? NaN : this.getPixelForDecimal((t - this._startValue) / this._valueRange);
  }
  getValueForPixel(t) {
    return this._startValue + this.getDecimalForPixel(t) * this._valueRange;
  }
}
var ve = (t) => Math.floor(pt(t)), Wt = (t, e) => Math.pow(10, ve(t) + e);
function so(t) {
  return t / Math.pow(10, ve(t)) === 1;
}
function no(t, e, i) {
  let s = Math.pow(10, i), n = Math.floor(t / s);
  return Math.ceil(e / s) - n;
}
function ph(t, e) {
  let i = e - t, s = ve(i);
  while (no(t, e, s) > 10)
    s++;
  while (no(t, e, s) < 10)
    s--;
  return Math.min(s, ve(t));
}
function mh(t, { min: e, max: i }) {
  e = J(t.min, e);
  let s = [], n = ve(e), o = ph(e, i), a = o < 0 ? Math.pow(10, Math.abs(o)) : 1, r = Math.pow(10, o), l = n > o ? Math.pow(10, n) : 0, c = Math.round((e - l) * a) / a, h = Math.floor((e - l) / r / 10) * r * 10, d = Math.floor((c - h) / Math.pow(10, o)), u = J(t.min, Math.round((l + h + d * Math.pow(10, o)) * a) / a);
  while (u < i) {
    if (s.push({
      value: u,
      major: so(u),
      significand: d
    }), d >= 10)
      d = d < 15 ? 15 : 20;
    else
      d++;
    if (d >= 20)
      o++, d = 2, a = o >= 0 ? 1 : a;
    u = Math.round((l + h + d * Math.pow(10, o)) * a) / a;
  }
  let f = J(t.max, u);
  return s.push({
    value: f,
    major: so(f),
    significand: d
  }), s;
}

class qo extends Nt {
  static id = "logarithmic";
  static defaults = {
    ticks: {
      callback: le.formatters.logarithmic,
      major: {
        enabled: !0
      }
    }
  };
  constructor(t) {
    super(t);
    this.start = void 0, this.end = void 0, this._startValue = void 0, this._valueRange = 0;
  }
  parse(t, e) {
    let i = ye.prototype.parse.apply(this, [
      t,
      e
    ]);
    if (i === 0) {
      this._zero = !0;
      return;
    }
    return W(i) && i > 0 ? i : null;
  }
  determineDataLimits() {
    let { min: t, max: e } = this.getMinMax(!0);
    if (this.min = W(t) ? Math.max(0, t) : null, this.max = W(e) ? Math.max(0, e) : null, this.options.beginAtZero)
      this._zero = !0;
    if (this._zero && this.min !== this._suggestedMin && !W(this._userMin))
      this.min = t === Wt(this.min, 0) ? Wt(this.min, -1) : Wt(this.min, 0);
    this.handleTickRangeOptions();
  }
  handleTickRangeOptions() {
    let { minDefined: t, maxDefined: e } = this.getUserBounds(), i = this.min, s = this.max, n = (a) => i = t ? i : a, o = (a) => s = e ? s : a;
    if (i === s)
      if (i <= 0)
        n(1), o(10);
      else
        n(Wt(i, -1)), o(Wt(s, 1));
    if (i <= 0)
      n(Wt(s, -1));
    if (s <= 0)
      o(Wt(i, 1));
    this.min = i, this.max = s;
  }
  buildTicks() {
    let t = this.options, e = {
      min: this._userMin,
      max: this._userMax
    }, i = mh(e, this);
    if (t.bounds === "ticks")
      Mi(i, this, "value");
    if (t.reverse)
      i.reverse(), this.start = this.max, this.end = this.min;
    else
      this.start = this.min, this.end = this.max;
    return i;
  }
  getLabelForValue(t) {
    return t === void 0 ? "0" : Jt(t, this.chart.options.locale, this.options.ticks.format);
  }
  configure() {
    let t = this.min;
    super.configure(), this._startValue = pt(t), this._valueRange = pt(this.max) - pt(t);
  }
  getPixelForValue(t) {
    if (t === void 0 || t === 0)
      t = this.min;
    if (t === null || isNaN(t))
      return NaN;
    return this.getPixelForDecimal(t === this.min ? 0 : (pt(t) - this._startValue) / this._valueRange);
  }
  getValueForPixel(t) {
    let e = this.getDecimalForPixel(t);
    return Math.pow(10, this._startValue + e * this._valueRange);
  }
}
function ds(t) {
  let e = t.ticks;
  if (e.display && t.display) {
    let i = q(e.backdropPadding);
    return P(e.font && e.font.size, F.font.size) + i.height;
  }
  return 0;
}
function bh(t, e, i) {
  return i = E(i) ? i : [
    i
  ], {
    w: Gs(t, e.string, i),
    h: i.length * e.lineHeight
  };
}
function oo(t, e, i, s, n) {
  if (t === s || t === n)
    return {
      start: e - i / 2,
      end: e + i / 2
    };
  else if (t < s || t > n)
    return {
      start: e - i,
      end: e
    };
  return {
    start: e,
    end: e + i
  };
}
function xh(t) {
  let e = {
    l: t.left + t._padding.left,
    r: t.right - t._padding.right,
    t: t.top + t._padding.top,
    b: t.bottom - t._padding.bottom
  }, i = Object.assign({}, e), s = [], n = [], o = t._pointLabels.length, a = t.options.pointLabels, r = a.centerPointLabels ? T / o : 0;
  for (let l = 0;l < o; l++) {
    let c = a.setContext(t.getPointLabelContext(l));
    n[l] = c.padding;
    let h = t.getPointPosition(l, t.drawingArea + n[l], r), d = H(c.font), u = bh(t.ctx, d, t._pointLabels[l]);
    s[l] = u;
    let f = X(t.getIndexAngle(l) + r), m = Math.round(ze(f)), g = oo(m, h.x, u.w, 0, 180), p = oo(m, h.y, u.h, 90, 270);
    _h(i, e, f, g, p);
  }
  t.setCenterPoint(e.l - i.l, i.r - e.r, e.t - i.t, i.b - e.b), t._pointLabelItems = Mh(t, s, n);
}
function _h(t, e, i, s, n) {
  let o = Math.abs(Math.sin(i)), a = Math.abs(Math.cos(i)), r = 0, l = 0;
  if (s.start < e.l)
    r = (e.l - s.start) / o, t.l = Math.min(t.l, e.l - r);
  else if (s.end > e.r)
    r = (s.end - e.r) / o, t.r = Math.max(t.r, e.r + r);
  if (n.start < e.t)
    l = (e.t - n.start) / a, t.t = Math.min(t.t, e.t - l);
  else if (n.end > e.b)
    l = (n.end - e.b) / a, t.b = Math.max(t.b, e.b + l);
}
function yh(t, e, i) {
  let s = t.drawingArea, { extra: n, additionalAngle: o, padding: a, size: r } = i, l = t.getPointPosition(e, s + n + a, o), c = Math.round(ze(X(l.angle + N))), h = Sh(l.y, r.h, c), d = kh(c), u = wh(l.x, r.w, d);
  return {
    visible: !0,
    x: l.x,
    y: h,
    textAlign: d,
    left: u,
    top: h,
    right: u + r.w,
    bottom: h + r.h
  };
}
function vh(t, e) {
  if (!e)
    return !0;
  let { left: i, top: s, right: n, bottom: o } = t;
  return !(rt({
    x: i,
    y: s
  }, e) || rt({
    x: i,
    y: o
  }, e) || rt({
    x: n,
    y: s
  }, e) || rt({
    x: n,
    y: o
  }, e));
}
function Mh(t, e, i) {
  let s = [], n = t._pointLabels.length, o = t.options, { centerPointLabels: a, display: r } = o.pointLabels, l = {
    extra: ds(o) / 2,
    additionalAngle: a ? T / n : 0
  }, c;
  for (let h = 0;h < n; h++) {
    l.padding = i[h], l.size = e[h];
    let d = yh(t, h, l);
    if (s.push(d), r === "auto") {
      if (d.visible = vh(d, c), d.visible)
        c = d;
    }
  }
  return s;
}
function kh(t) {
  if (t === 0 || t === 180)
    return "center";
  else if (t < 180)
    return "left";
  return "right";
}
function wh(t, e, i) {
  if (i === "right")
    t -= e;
  else if (i === "center")
    t -= e / 2;
  return t;
}
function Sh(t, e, i) {
  if (i === 90 || i === 270)
    t -= e / 2;
  else if (i > 270 || i < 90)
    t -= e;
  return t;
}
function Ph(t, e, i) {
  let { left: s, top: n, right: o, bottom: a } = i, { backdropColor: r } = e;
  if (!A(r)) {
    let l = At(e.borderRadius), c = q(e.backdropPadding);
    t.fillStyle = r;
    let h = s - c.left, d = n - c.top, u = o - s + c.width, f = a - n + c.height;
    if (Object.values(l).some((m) => m !== 0))
      t.beginPath(), Zt(t, {
        x: h,
        y: d,
        w: u,
        h: f,
        radius: l
      }), t.fill();
    else
      t.fillRect(h, d, u, f);
  }
}
function Dh(t, e) {
  let { ctx: i, options: { pointLabels: s } } = t;
  for (let n = e - 1;n >= 0; n--) {
    let o = t._pointLabelItems[n];
    if (!o.visible)
      continue;
    let a = s.setContext(t.getPointLabelContext(n));
    Ph(i, a, o);
    let r = H(a.font), { x: l, y: c, textAlign: h } = o;
    Dt(i, t._pointLabels[n], l, c + r.lineHeight / 2, r, {
      color: a.color,
      textAlign: h,
      textBaseline: "middle"
    });
  }
}
function Go(t, e, i, s) {
  let { ctx: n } = t;
  if (i)
    n.arc(t.xCenter, t.yCenter, e, 0, I);
  else {
    let o = t.getPointPosition(0, e);
    n.moveTo(o.x, o.y);
    for (let a = 1;a < s; a++)
      o = t.getPointPosition(a, e), n.lineTo(o.x, o.y);
  }
}
function Ah(t, e, i, s, n) {
  let o = t.ctx, a = e.circular, { color: r, lineWidth: l } = e;
  if (!a && !s || !r || !l || i < 0)
    return;
  o.save(), o.strokeStyle = r, o.lineWidth = l, o.setLineDash(n.dash || []), o.lineDashOffset = n.dashOffset, o.beginPath(), Go(t, i, a, s), o.closePath(), o.stroke(), o.restore();
}
function Ch(t, e, i) {
  return bt(t, {
    label: i,
    index: e,
    type: "pointLabel"
  });
}

class Jo extends ye {
  static id = "radialLinear";
  static defaults = {
    display: !0,
    animate: !0,
    position: "chartArea",
    angleLines: {
      display: !0,
      lineWidth: 1,
      borderDash: [],
      borderDashOffset: 0
    },
    grid: {
      circular: !1
    },
    startAngle: 0,
    ticks: {
      showLabelBackdrop: !0,
      callback: le.formatters.numeric
    },
    pointLabels: {
      backdropColor: void 0,
      backdropPadding: 2,
      display: !0,
      font: {
        size: 10
      },
      callback(t) {
        return t;
      },
      padding: 5,
      centerPointLabels: !1
    }
  };
  static defaultRoutes = {
    "angleLines.color": "borderColor",
    "pointLabels.color": "color",
    "ticks.color": "color"
  };
  static descriptors = {
    angleLines: {
      _fallback: "grid"
    }
  };
  constructor(t) {
    super(t);
    this.xCenter = void 0, this.yCenter = void 0, this.drawingArea = void 0, this._pointLabels = [], this._pointLabelItems = [];
  }
  setDimensions() {
    let t = this._padding = q(ds(this.options) / 2), e = this.width = this.maxWidth - t.width, i = this.height = this.maxHeight - t.height;
    this.xCenter = Math.floor(this.left + e / 2 + t.left), this.yCenter = Math.floor(this.top + i / 2 + t.top), this.drawingArea = Math.floor(Math.min(e, i) / 2);
  }
  determineDataLimits() {
    let { min: t, max: e } = this.getMinMax(!1);
    this.min = W(t) && !isNaN(t) ? t : 0, this.max = W(e) && !isNaN(e) ? e : 0, this.handleTickRangeOptions();
  }
  computeTickLimit() {
    return Math.ceil(this.drawingArea / ds(this.options));
  }
  generateTickLabels(t) {
    ye.prototype.generateTickLabels.call(this, t), this._pointLabels = this.getLabels().map((e, i) => {
      let s = R(this.options.pointLabels.callback, [
        e,
        i
      ], this);
      return s || s === 0 ? s : "";
    }).filter((e, i) => this.chart.getDataVisibility(i));
  }
  fit() {
    let t = this.options;
    if (t.display && t.pointLabels.display)
      xh(this);
    else
      this.setCenterPoint(0, 0, 0, 0);
  }
  setCenterPoint(t, e, i, s) {
    this.xCenter += Math.floor((t - e) / 2), this.yCenter += Math.floor((i - s) / 2), this.drawingArea -= Math.min(this.drawingArea / 2, Math.max(t, e, i, s));
  }
  getIndexAngle(t) {
    let e = I / (this._pointLabels.length || 1), i = this.options.startAngle || 0;
    return X(t * e + st(i));
  }
  getDistanceFromCenterForValue(t) {
    if (A(t))
      return NaN;
    let e = this.drawingArea / (this.max - this.min);
    if (this.options.reverse)
      return (this.max - t) * e;
    return (t - this.min) * e;
  }
  getValueForDistanceFromCenter(t) {
    if (A(t))
      return NaN;
    let e = t / (this.drawingArea / (this.max - this.min));
    return this.options.reverse ? this.max - e : this.min + e;
  }
  getPointLabelContext(t) {
    let e = this._pointLabels || [];
    if (t >= 0 && t < e.length) {
      let i = e[t];
      return Ch(this.getContext(), t, i);
    }
  }
  getPointPosition(t, e, i = 0) {
    let s = this.getIndexAngle(t) - N + i;
    return {
      x: Math.cos(s) * e + this.xCenter,
      y: Math.sin(s) * e + this.yCenter,
      angle: s
    };
  }
  getPointPositionForValue(t, e) {
    return this.getPointPosition(t, this.getDistanceFromCenterForValue(e));
  }
  getBasePosition(t) {
    return this.getPointPositionForValue(t || 0, this.getBaseValue());
  }
  getPointLabelPosition(t) {
    let { left: e, top: i, right: s, bottom: n } = this._pointLabelItems[t];
    return {
      left: e,
      top: i,
      right: s,
      bottom: n
    };
  }
  drawBackground() {
    let { backgroundColor: t, grid: { circular: e } } = this.options;
    if (t) {
      let i = this.ctx;
      i.save(), i.beginPath(), Go(this, this.getDistanceFromCenterForValue(this._endValue), e, this._pointLabels.length), i.closePath(), i.fillStyle = t, i.fill(), i.restore();
    }
  }
  drawGrid() {
    let t = this.ctx, e = this.options, { angleLines: i, grid: s, border: n } = e, o = this._pointLabels.length, a, r, l;
    if (e.pointLabels.display)
      Dh(this, o);
    if (s.display)
      this.ticks.forEach((c, h) => {
        if (h !== 0 || h === 0 && this.min < 0) {
          r = this.getDistanceFromCenterForValue(c.value);
          let d = this.getContext(h), u = s.setContext(d), f = n.setContext(d);
          Ah(this, u, r, o, f);
        }
      });
    if (i.display) {
      t.save();
      for (a = o - 1;a >= 0; a--) {
        let c = i.setContext(this.getPointLabelContext(a)), { color: h, lineWidth: d } = c;
        if (!d || !h)
          continue;
        t.lineWidth = d, t.strokeStyle = h, t.setLineDash(c.borderDash), t.lineDashOffset = c.borderDashOffset, r = this.getDistanceFromCenterForValue(e.reverse ? this.min : this.max), l = this.getPointPosition(a, r), t.beginPath(), t.moveTo(this.xCenter, this.yCenter), t.lineTo(l.x, l.y), t.stroke();
      }
      t.restore();
    }
  }
  drawBorder() {}
  drawLabels() {
    let t = this.ctx, e = this.options, i = e.ticks;
    if (!i.display)
      return;
    let s = this.getIndexAngle(0), n, o;
    t.save(), t.translate(this.xCenter, this.yCenter), t.rotate(s), t.textAlign = "center", t.textBaseline = "middle", this.ticks.forEach((a, r) => {
      if (r === 0 && this.min >= 0 && !e.reverse)
        return;
      let l = i.setContext(this.getContext(r)), c = H(l.font);
      if (n = this.getDistanceFromCenterForValue(this.ticks[r].value), l.showLabelBackdrop) {
        t.font = c.string, o = t.measureText(a.label).width, t.fillStyle = l.backdropColor;
        let h = q(l.backdropPadding);
        t.fillRect(-o / 2 - h.left, -n - c.size / 2 - h.top, o + h.width, c.size + h.height);
      }
      Dt(t, a.label, 0, -n, c, {
        color: l.color,
        strokeColor: l.textStrokeColor,
        strokeWidth: l.textStrokeWidth
      });
    }), t.restore();
  }
  drawTitle() {}
}
var ri = {
  millisecond: {
    common: !0,
    size: 1,
    steps: 1000
  },
  second: {
    common: !0,
    size: 1000,
    steps: 60
  },
  minute: {
    common: !0,
    size: 60000,
    steps: 60
  },
  hour: {
    common: !0,
    size: 3600000,
    steps: 24
  },
  day: {
    common: !0,
    size: 86400000,
    steps: 30
  },
  week: {
    common: !1,
    size: 604800000,
    steps: 4
  },
  month: {
    common: !0,
    size: 2628000000,
    steps: 12
  },
  quarter: {
    common: !1,
    size: 7884000000,
    steps: 4
  },
  year: {
    common: !0,
    size: 31540000000
  }
}, Q = /* @__PURE__ */ Object.keys(ri);
function ao(t, e) {
  return t - e;
}
function ro(t, e) {
  if (A(e))
    return null;
  let i = t._adapter, { parser: s, round: n, isoWeekday: o } = t._parseOpts, a = e;
  if (typeof s === "function")
    a = s(a);
  if (!W(a))
    a = typeof s === "string" ? i.parse(a, s) : i.parse(a);
  if (a === null)
    return null;
  if (n)
    a = n === "week" && (Ft(o) || o === !0) ? i.startOf(a, "isoWeek", o) : i.startOf(a, n);
  return +a;
}
function lo(t, e, i, s) {
  let n = Q.length;
  for (let o = Q.indexOf(t);o < n - 1; ++o) {
    let a = ri[Q[o]], r = a.steps ? a.steps : Number.MAX_SAFE_INTEGER;
    if (a.common && Math.ceil((i - e) / (r * a.size)) <= s)
      return Q[o];
  }
  return Q[n - 1];
}
function Oh(t, e, i, s, n) {
  for (let o = Q.length - 1;o >= Q.indexOf(i); o--) {
    let a = Q[o];
    if (ri[a].common && t._adapter.diff(n, s, a) >= e - 1)
      return a;
  }
  return Q[i ? Q.indexOf(i) : 0];
}
function Th(t) {
  for (let e = Q.indexOf(t) + 1, i = Q.length;e < i; ++e)
    if (ri[Q[e]].common)
      return Q[e];
}
function co(t, e, i) {
  if (!i)
    t[e] = !0;
  else if (i.length) {
    let { lo: s, hi: n } = Be(i, e), o = i[s] >= e ? i[s] : i[n];
    t[o] = !0;
  }
}
function Lh(t, e, i, s) {
  let n = t._adapter, o = +n.startOf(e[0].value, s), a = e[e.length - 1].value, r, l;
  for (r = o;r <= a; r = +n.add(r, 1, s))
    if (l = i[r], l >= 0)
      e[l].major = !0;
  return e;
}
function ho(t, e, i) {
  let s = [], n = {}, o = e.length, a, r;
  for (a = 0;a < o; ++a)
    r = e[a], n[r] = a, s.push({
      value: r,
      major: !1
    });
  return o === 0 || !i ? s : Lh(t, s, n, i);
}

class si extends Nt {
  static id = "time";
  static defaults = {
    bounds: "data",
    adapters: {},
    time: {
      parser: !1,
      unit: !1,
      round: !1,
      isoWeekday: !1,
      minUnit: "millisecond",
      displayFormats: {}
    },
    ticks: {
      source: "auto",
      callback: !1,
      major: {
        enabled: !1
      }
    }
  };
  constructor(t) {
    super(t);
    this._cache = {
      data: [],
      labels: [],
      all: []
    }, this._unit = "day", this._majorUnit = void 0, this._offsets = {}, this._normalized = !1, this._parseOpts = void 0;
  }
  init(t, e = {}) {
    let i = t.time || (t.time = {}), s = this._adapter = new Yr._date(t.adapters.date);
    s.init(e), Xt(i.displayFormats, s.formats()), this._parseOpts = {
      parser: i.parser,
      round: i.round,
      isoWeekday: i.isoWeekday
    }, super.init(t), this._normalized = e.normalized;
  }
  parse(t, e) {
    if (t === void 0)
      return null;
    return ro(this, t);
  }
  beforeLayout() {
    super.beforeLayout(), this._cache = {
      data: [],
      labels: [],
      all: []
    };
  }
  determineDataLimits() {
    let t = this.options, e = this._adapter, i = t.time.unit || "day", { min: s, max: n, minDefined: o, maxDefined: a } = this.getUserBounds();
    function r(l) {
      if (!o && !isNaN(l.min))
        s = Math.min(s, l.min);
      if (!a && !isNaN(l.max))
        n = Math.max(n, l.max);
    }
    if (!o || !a) {
      if (r(this._getLabelBounds()), t.bounds !== "ticks" || t.ticks.source !== "labels")
        r(this.getMinMax(!1));
    }
    s = W(s) && !isNaN(s) ? s : +e.startOf(Date.now(), i), n = W(n) && !isNaN(n) ? n : +e.endOf(Date.now(), i) + 1, this.min = Math.min(s, n - 1), this.max = Math.max(s + 1, n);
  }
  _getLabelBounds() {
    let t = this.getLabelTimestamps(), { POSITIVE_INFINITY: e, NEGATIVE_INFINITY: i } = Number;
    if (t.length)
      e = t[0], i = t[t.length - 1];
    return {
      min: e,
      max: i
    };
  }
  buildTicks() {
    let t = this.options, { time: e, ticks: i } = t, s = i.source === "labels" ? this.getLabelTimestamps() : this._generate();
    if (t.bounds === "ticks" && s.length)
      this.min = this._userMin || s[0], this.max = this._userMax || s[s.length - 1];
    let n = this.min, o = this.max, a = js(s, n, o);
    if (this._unit = e.unit || (i.autoSkip ? lo(e.minUnit, this.min, this.max, this._getLabelCapacity(n)) : Oh(this, a.length, e.minUnit, this.min, this.max)), this._majorUnit = !i.major.enabled || this._unit === "year" ? void 0 : Th(this._unit), this.initOffsets(s), t.reverse)
      a.reverse();
    return ho(this, a, this._majorUnit);
  }
  afterAutoSkip() {
    if (this.options.offsetAfterAutoskip)
      this.initOffsets(this.ticks.map((t) => +t.value));
  }
  initOffsets(t = []) {
    let e = 0, i = 0, s, n;
    if (this.options.offset && t.length) {
      if (s = this.getDecimalForValue(t[0]), t.length === 1)
        e = 1 - s;
      else
        e = (this.getDecimalForValue(t[1]) - s) / 2;
      if (n = this.getDecimalForValue(t[t.length - 1]), t.length === 1)
        i = n;
      else
        i = (n - this.getDecimalForValue(t[t.length - 2])) / 2;
    }
    let o = t.length < 3 ? 0.5 : 0.25;
    e = Y(e, 0, o), i = Y(i, 0, o), this._offsets = {
      start: e,
      end: i,
      factor: 1 / (e + 1 + i)
    };
  }
  _generate() {
    let t = this._adapter, e = this.min, i = this.max, s = this.options, n = s.time, o = n.unit || lo(n.minUnit, e, i, this._getLabelCapacity(e)), a = P(s.ticks.stepSize, 1), r = o === "week" ? n.isoWeekday : !1, l = Ft(r) || r === !0, c = {}, h = e, d, u;
    if (l)
      h = +t.startOf(h, "isoWeek", r);
    if (h = +t.startOf(h, l ? "day" : o), t.diff(i, e, o) > 1e5 * a)
      throw Error(e + " and " + i + " are too far apart with stepSize of " + a + " " + o);
    let f = s.ticks.source === "data" && this.getDataTimestamps();
    for (d = h, u = 0;d < i; d = +t.add(d, a, o), u++)
      co(c, d, f);
    if (d === i || s.bounds === "ticks" || u === 1)
      co(c, d, f);
    return Object.keys(c).sort(ao).map((m) => +m);
  }
  getLabelForValue(t) {
    let e = this._adapter, i = this.options.time;
    if (i.tooltipFormat)
      return e.format(t, i.tooltipFormat);
    return e.format(t, i.displayFormats.datetime);
  }
  format(t, e) {
    let s = this.options.time.displayFormats, n = this._unit, o = e || s[n];
    return this._adapter.format(t, o);
  }
  _tickFormatFunction(t, e, i, s) {
    let n = this.options, o = n.ticks.callback;
    if (o)
      return R(o, [
        t,
        e,
        i
      ], this);
    let a = n.time.displayFormats, r = this._unit, l = this._majorUnit, c = r && a[r], h = l && a[l], d = i[e], u = l && h && d && d.major;
    return this._adapter.format(t, s || (u ? h : c));
  }
  generateTickLabels(t) {
    let e, i, s;
    for (e = 0, i = t.length;e < i; ++e)
      s = t[e], s.label = this._tickFormatFunction(s.value, e, t);
  }
  getDecimalForValue(t) {
    return t === null ? NaN : (t - this.min) / (this.max - this.min);
  }
  getPixelForValue(t) {
    let e = this._offsets, i = this.getDecimalForValue(t);
    return this.getPixelForDecimal((e.start + i) * e.factor);
  }
  getValueForPixel(t) {
    let e = this._offsets, i = this.getDecimalForPixel(t) / e.factor - e.end;
    return this.min + i * (this.max - this.min);
  }
  _getLabelSize(t) {
    let e = this.options.ticks, i = this.ctx.measureText(t).width, s = st(this.isHorizontal() ? e.maxRotation : e.minRotation), n = Math.cos(s), o = Math.sin(s), a = this._resolveTickFontOptions(0).size;
    return {
      w: i * n + a * o,
      h: i * o + a * n
    };
  }
  _getLabelCapacity(t) {
    let e = this.options.time, i = e.displayFormats, s = i[e.unit] || i.millisecond, n = this._tickFormatFunction(t, 0, ho(this, [
      t
    ], this._majorUnit), s), o = this._getLabelSize(n), a = Math.floor(this.isHorizontal() ? this.width / o.w : this.height / o.h) - 1;
    return a > 0 ? a : 1;
  }
  getDataTimestamps() {
    let t = this._cache.data || [], e, i;
    if (t.length)
      return t;
    let s = this.getMatchingVisibleMetas();
    if (this._normalized && s.length)
      return this._cache.data = s[0].controller.getAllParsedValues(this);
    for (e = 0, i = s.length;e < i; ++e)
      t = t.concat(s[e].controller.getAllParsedValues(this));
    return this._cache.data = this.normalize(t);
  }
  getLabelTimestamps() {
    let t = this._cache.labels || [], e, i;
    if (t.length)
      return t;
    let s = this.getLabels();
    for (e = 0, i = s.length;e < i; ++e)
      t.push(ro(this, s[e]));
    return this._cache.labels = this._normalized ? t : this.normalize(t);
  }
  normalize(t) {
    return Pi(t.sort(ao));
  }
}
function Ze(t, e, i) {
  let s = 0, n = t.length - 1, o, a, r, l;
  if (i) {
    if (e >= t[s].pos && e <= t[n].pos)
      ({ lo: s, hi: n } = at(t, "pos", e));
    ({ pos: o, time: r } = t[s]), { pos: a, time: l } = t[n];
  } else {
    if (e >= t[s].time && e <= t[n].time)
      ({ lo: s, hi: n } = at(t, "time", e));
    ({ time: o, pos: r } = t[s]), { time: a, pos: l } = t[n];
  }
  let c = a - o;
  return c ? r + (l - r) * (e - o) / c : r;
}

class Zo extends si {
  static id = "timeseries";
  static defaults = si.defaults;
  constructor(t) {
    super(t);
    this._table = [], this._minPos = void 0, this._tableRange = void 0;
  }
  initOffsets() {
    let t = this._getTimestampsForTable(), e = this._table = this.buildLookupTable(t);
    this._minPos = Ze(e, this.min), this._tableRange = Ze(e, this.max) - this._minPos, super.initOffsets(t);
  }
  buildLookupTable(t) {
    let { min: e, max: i } = this, s = [], n = [], o, a, r, l, c;
    for (o = 0, a = t.length;o < a; ++o)
      if (l = t[o], l >= e && l <= i)
        s.push(l);
    if (s.length < 2)
      return [
        {
          time: e,
          pos: 0
        },
        {
          time: i,
          pos: 1
        }
      ];
    for (o = 0, a = s.length;o < a; ++o)
      if (c = s[o + 1], r = s[o - 1], l = s[o], Math.round((c + r) / 2) !== l)
        n.push({
          time: l,
          pos: o / (a - 1)
        });
    return n;
  }
  _generate() {
    let t = this.min, e = this.max, i = super.getDataTimestamps();
    if (!i.includes(t) || !i.length)
      i.splice(0, 0, t);
    if (!i.includes(e) || i.length === 1)
      i.push(e);
    return i.sort((s, n) => s - n);
  }
  _getTimestampsForTable() {
    let t = this._cache.all || [];
    if (t.length)
      return t;
    let e = this.getDataTimestamps(), i = this.getLabelTimestamps();
    if (e.length && i.length)
      t = this.normalize(e.concat(i));
    else
      t = e.length ? e : i;
    return t = this._cache.all = t, t;
  }
  getDecimalForValue(t) {
    return (Ze(this._table, t) - this._minPos) / this._tableRange;
  }
  getValueForPixel(t) {
    let e = this._offsets, i = this.getDecimalForPixel(t) / e.factor - e.end;
    return Ze(this._table, i * this._tableRange + this._minPos, !0);
  }
}
var Rh = /* @__PURE__ */ Object.freeze({
  __proto__: null,
  CategoryScale: Xo,
  LinearScale: Ko,
  LogarithmicScale: qo,
  RadialLinearScale: Jo,
  TimeScale: si,
  TimeSeriesScale: Zo
}), Qo = [
  $r,
  pc,
  hh,
  Rh
];

// src/nb-win-probability-chart.js
ke.register(...Qo);
var ta = window.matchMedia("(prefers-color-scheme: light)");
function ea() {
  let t = getComputedStyle(document.documentElement), e = (i) => t.getPropertyValue(i).trim();
  return {
    legend: e("--color-text-secondary"),
    tick: e("--color-text-muted"),
    grid: e("--color-border")
  };
}

class bs extends HTMLElement {
  static observedAttributes = [
    "data",
    "home-color",
    "draw-color",
    "away-color",
    "home-abbr",
    "away-abbr"
  ];
  connectedCallback() {
    if (ta.addEventListener("change", this.applyTheme), !this.canvas)
      this.canvas = document.createElement("canvas");
    if (this.canvas.parentNode !== this)
      this.appendChild(this.canvas);
    let t = this.getAttribute("race-id"), e = this.getAttribute("sport");
    if (t && e && !this.getAttribute("data"))
      this._loadRaceData(t, e);
    else if (this.render(), !this.chart)
      queueMicrotask(() => this.isConnected && this.render());
  }
  applyTheme = () => {
    if (!this.chart)
      return;
    let t = ea(), { options: e } = this.chart;
    e.plugins.legend.labels.color = t.legend;
    for (let i of ["x", "y"])
      e.scales[i].ticks.color = t.tick, e.scales[i].grid.color = t.grid;
    this.chart.update("none");
  };
  disconnectedCallback() {
    ta.removeEventListener("change", this.applyTheme), queueMicrotask(() => {
      if (!this.isConnected && this.chart)
        this.chart.destroy(), this.chart = null, this.canvas?.remove(), this.canvas = null;
    });
  }
  attributeChangedCallback() {
    if (this.canvas)
      this.render();
  }
  async refresh() {
    let t = this.getAttribute("race-id"), e = this.getAttribute("sport");
    if (t && e) {
      await this._loadRaceData(t, e);
      return;
    }
    let i = this.getAttribute("game-id");
    if (i)
      try {
        let s = await fetch(`/api/games/${i}/odds`, {
          cache: "no-store"
        });
        if (!s.ok)
          return;
        let n = await s.json();
        this.setAttribute("data", JSON.stringify(n.oddsHistory || []));
      } catch {}
  }
  async _loadRaceData(t, e) {
    if (this._loadPromise)
      return this._loadPromise;
    return this._loadPromise = (async () => {
      try {
        let i = await fetch(`/api/${e}/race/${t}/chart`, {
          cache: "no-store"
        });
        if (!i.ok)
          return;
        let s = await i.json();
        if (s.length >= 2)
          this.setAttribute("data", JSON.stringify(s));
      } catch {} finally {
        this._loadPromise = null;
      }
    })(), this._loadPromise;
  }
  render() {
    let t = this.getAttribute("data");
    if (!t)
      return;
    let e = JSON.parse(t);
    if (!e || e.length < 2)
      return;
    this.snapshots = e;
    let i = this.getAttribute("home-color") || "#3b82f6", s = this.getAttribute("draw-color") || "#f59e0b", n = this.getAttribute("away-color") || "#ef4444", o = this.getAttribute("home-abbr") || "Home", a = this.getAttribute("draw-abbr") || "Draw", r = this.getAttribute("away-abbr") || "Away", l = e.map((g) => {
      if (g.label)
        return g.label;
      if (g.timestamp)
        return new Date(g.timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
      return "";
    }), c = e.map((g) => (g.homeWinProbability ?? g.winProbability ?? 0.5) * 100), h = e.some((g) => g.drawWinProbability != null && g.awayWinProbability != null), d = !h && e.some((g) => g.awayWinProbability != null), u = (g, p, b, x = !1) => ({
      label: g,
      data: p,
      borderColor: b,
      backgroundColor: `${b}20`,
      fill: x,
      tension: 0.3,
      pointRadius: 0,
      borderWidth: 2
    }), f = h ? [
      u(o, c, i),
      u(a, e.map((g) => (g.drawWinProbability ?? 0) * 100), s),
      u(r, e.map((g) => (g.awayWinProbability ?? 0) * 100), n)
    ] : d ? [
      u(o, e.map((g) => g.homeWinProbability == null ? null : g.homeWinProbability * 100), i),
      u(r, e.map((g) => g.awayWinProbability == null ? null : g.awayWinProbability * 100), n)
    ] : [
      u(this.hasAttribute("home-abbr") ? `${o} win %` : "Win %", c, i, !0)
    ];
    if (this.chart) {
      this.chart.data.labels = l, this.chart.data.datasets = f, this.chart.update("none");
      return;
    }
    let m = ea();
    this.chart = new ke(this.canvas, {
      type: "line",
      data: {
        labels: l,
        datasets: f
      },
      options: {
        responsive: !0,
        maintainAspectRatio: !1,
        plugins: {
          legend: {
            display: h || d || this.hasAttribute("home-abbr"),
            labels: { color: m.legend, usePointStyle: !0, boxWidth: 8 }
          },
          tooltip: {
            callbacks: {
              title: (g) => {
                let p = this.snapshots[g[0].dataIndex], b = g[0].label || "";
                if (p.homeScore != null && p.awayScore != null)
                  return `${b} · ${r} ${p.awayScore} - ${o} ${p.homeScore}`;
                return b;
              },
              label: (g) => {
                if (this.snapshots[g.dataIndex].isProbabilityBound) {
                  let b = g.parsed.y > 99 ? ">99%" : "<1%";
                  return `${g.dataset.label}: ${b}`;
                }
                return `${g.dataset.label}: ${g.parsed.y.toFixed(1)}%`;
              }
            }
          }
        },
        scales: {
          x: {
            display: !0,
            ticks: { color: m.tick, maxTicksLimit: 6 },
            grid: { color: m.grid }
          },
          y: {
            display: !0,
            min: 0,
            max: 100,
            ticks: {
              color: m.tick,
              callback: (g) => `${g}%`,
              stepSize: 25
            },
            grid: { color: m.grid }
          }
        },
        interaction: { intersect: !1, mode: "index" }
      }
    });
  }
}
customElements.define("nb-win-probability-chart", bs);
export {
  li as NbGameHighlights,
  ci as NbRaceLeaderboard,
  di as NbSparkline,
  bs as NbWinProbabilityChart
};
