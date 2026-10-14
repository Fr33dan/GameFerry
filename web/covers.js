// Front covers for the game lists, shown straight from GameTDB (https://www.gametdb.com), as the desktop app shows
// them. A page may show GameTDB's images but not read them (GameTDB sends no CORS headers), so the web version
// shows covers without putting any on the card.

const ART = 'https://art.gametdb.com/wii/cover';

/** A transparent pixel: an image showing it is just its frame, where a missing image would show a broken-image icon. */
const NONE = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';

/** Regions that are one country, whose art may only be in that country's language. */
const COUNTRY = { D: 'DE', F: 'FR', H: 'NL', I: 'IT', R: 'RU', S: 'ES', U: 'AU', V: 'DK' };

/**
 * The languages GameTDB may file a game's art under, likeliest first, from the region letter of its ID. This is the
 * order USB Loader GX searches in, as in the desktop app (GameTdbArt.Languages).
 * @param {string} id6
 */
export function languages(id6) {
  const region = (id6[3] ?? '').toUpperCase();
  const list = [];
  if (region === 'E' || region === 'N') list.push('US');
  else if (region === 'J') list.push('JA');
  else if (region === 'K' || region === 'Q' || region === 'T') list.push('KO');
  else if (region === 'W') list.push('ZH');
  else list.push('EN');
  if (region in COUNTRY) list.push(COUNTRY[/** @type {keyof typeof COUNTRY} */ (region)]);
  // X, Y and Z are used for special releases on both sides of the Atlantic.
  if (region === 'X' || region === 'Y' || region === 'Z') list.push('US');
  list.push('EN');
  if (region === 'V') list.push('FI', 'SE');
  return [...new Set(list)];
}

/** @param {string} id6 */
export function coverUrls(id6) {
  return languages(id6).map((language) => `${ART}/${language}/${id6}.png`);
}

/**
 * Shows a game's front cover in an image, trying each language in turn. An image left without a cover, or given no
 * game, shows as an empty frame. Showing the same game again does nothing, so rows can be refreshed freely.
 * @param {HTMLImageElement} image
 * @param {string | null | undefined} id6
 */
export function showCover(image, id6) {
  const id = id6 && /^[0-9A-Za-z]{6}$/.test(id6) ? id6.toUpperCase() : '';
  if (image.dataset.id === id) return;
  image.dataset.id = id;
  const urls = id ? coverUrls(id) : [];
  let next = 0;
  image.onerror = () => {
    if (next < urls.length) {
      image.src = urls[next++];
    } else {
      image.onerror = null;
      image.src = NONE;
    }
  };
  image.src = urls.length ? urls[next++] : NONE;
}

/** A cover image for a list row: decorative, the size of a row, and only fetched once it is scrolled into view. */
export function coverImage() {
  const image = document.createElement('img');
  image.className = 'cover';
  image.alt = '';
  image.loading = 'lazy';
  image.decoding = 'async';
  image.width = 30;
  image.height = 42;
  image.src = NONE;
  return image;
}
