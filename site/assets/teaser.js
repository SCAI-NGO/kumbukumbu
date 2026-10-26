// The full page: a flower opens its section below the garden; a card grows into a sheet and shrinks back.
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
const section = document.getElementById('section');
const plants = [...document.querySelectorAll('.plant')];
const announcer = document.getElementById('announcer');

function openSection(name) {
  const template = document.getElementById('tpl-' + name);
  if (!template) return;
  for (const plant of plants) {
    const on = plant.dataset.section === name;
    plant.classList.toggle('open', on);
    plant.setAttribute('aria-expanded', on);
  }
  section.replaceChildren(template.content.cloneNode(true));
  const heading = section.querySelector('h2');
  heading.id = 'section-title';
  section.removeAttribute('aria-label');
  section.setAttribute('aria-labelledby', 'section-title');
  announcer.textContent = 'Showing: ' + heading.textContent;
  for (const card of section.querySelectorAll('.moment')) {
    card.addEventListener('click', () => popOut(card));
    // Enter opens on keydown, Space on keyup, as native buttons do. Opening on Space's keydown would move
    // focus to "close" before the keyup, and browsers that click a button on keyup (Firefox) would close it.
    card.addEventListener('keydown', e => {
      if (e.key === 'Enter') { e.preventDefault(); popOut(card); }
      else if (e.key === ' ') e.preventDefault();
    });
    card.addEventListener('keyup', e => { if (e.key === ' ') { e.preventDefault(); popOut(card); } });
  }
  // On a phone the section starts below the fold; bring its top into view.
  if (section.getBoundingClientRect().top > innerHeight * 0.75)
    section.scrollIntoView({ behavior: reducedMotion.matches ? 'auto' : 'smooth', block: 'start' });
}

for (const plant of plants) plant.addEventListener('click', () => openSection(plant.dataset.section));

// The card grows from where it sits into a centred sheet over a soft veil (FLIP), and shrinks back to the
// same spot when closed. `busy` stays set until a close has finished; a card asked for meanwhile opens after it.
let busy = false;
let queued = null;
let closingSheet = false;

function popOut(card) {
  if (busy) { if (closingSheet) queued = card; return; }
  busy = true;
  const instant = reducedMotion.matches;

  const veil = document.createElement('div');
  veil.className = 'veil';
  const sheet = card.cloneNode(true);
  sheet.classList.add('pop');
  sheet.removeAttribute('tabindex');
  sheet.removeAttribute('aria-haspopup');
  sheet.setAttribute('role', 'dialog');
  sheet.setAttribute('aria-modal', 'true');
  sheet.querySelector('b').id = 'sheet-title';
  sheet.setAttribute('aria-labelledby', 'sheet-title');
  const closeButton = document.createElement('button');
  closeButton.className = 'close';
  closeButton.type = 'button';
  closeButton.setAttribute('aria-label', 'Close');
  closeButton.textContent = 'close ×';
  sheet.prepend(closeButton);

  // Centred, at most 600 px wide; placed again on resize, e.g. when a phone is turned.
  const place = () => {
    const width = Math.min(600, innerWidth - 32);
    sheet.style.width = width + 'px';
    sheet.style.left = (innerWidth - width) / 2 + 'px';
    sheet.style.top = '0px';
    sheet.style.top = Math.max(16, (innerHeight - sheet.offsetHeight) / 2) + 'px';
  };
  document.documentElement.classList.add('sheet-open');
  document.body.append(veil, sheet);
  place();
  addEventListener('resize', place);

  // Measured at both ends, so a resize while the sheet is open still shrinks it to the card's spot.
  const invert = () => {
    const from = card.getBoundingClientRect();
    const to = sheet.getBoundingClientRect();
    return `translate(${from.left - to.left}px, ${from.top - to.top}px) scale(${from.width / to.width}, ${from.height / to.height})`;
  };
  const start = invert();
  card.style.visibility = 'hidden';
  if (instant) veil.classList.add('on');
  else {
    requestAnimationFrame(() => veil.classList.add('on'));
    sheet.animate([{ transform: start, opacity: .6 }, { transform: 'none', opacity: 1 }],
      { duration: 480, easing: 'cubic-bezier(.3,1.25,.5,1)' });
  }
  closeButton.focus({ preventScroll: true });

  const finish = () => {
    sheet.remove();
    veil.remove();
    document.documentElement.classList.remove('sheet-open');
    card.style.visibility = '';
    card.focus({ preventScroll: true });
    busy = false;
    closingSheet = false;
    if (queued) { const next = queued; queued = null; if (next.isConnected) popOut(next); }
  };
  const close = () => {
    if (closingSheet) return;
    closingSheet = true;
    removeEventListener('keydown', onKey, true);
    removeEventListener('resize', place);
    veil.classList.remove('on');
    if (instant) { finish(); return; }
    sheet.getAnimations().forEach(a => a.cancel());
    sheet.animate([{ transform: 'none', opacity: 1 }, { transform: invert(), opacity: .6 }],
      { duration: 340, easing: 'ease-in', fill: 'forwards' }).finished.then(finish);
  };
  const onKey = e => {
    if (e.key === 'Escape') { e.preventDefault(); close(); }
    else if (e.key === 'Tab') {
      // Keep focus inside the sheet while it is open.
      const focusable = [...sheet.querySelectorAll('button, a[href]')];
      const first = focusable[0], last = focusable[focusable.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
      else if (!sheet.contains(document.activeElement)) { e.preventDefault(); first.focus(); }
    }
  };
  veil.addEventListener('click', close);
  sheet.addEventListener('click', e => { if (!e.target.closest('a')) close(); });
  addEventListener('keydown', onKey, true);
}

// For review screenshots: #open=helps opens a section, #open=helps/2 also pops its second card.
const debug = location.hash.match(/^#open=(\w+)(?:\/(\d+))?$/);
if (debug) {
  openSection(debug[1]);
  const card = debug[2] && section.querySelectorAll('.moment')[debug[2] - 1];
  if (card) setTimeout(() => popOut(card), 800);
}
