/**
 * Copies text without the async Clipboard API, which needs a secure context and so is unavailable
 * over plain HTTP on the LAN (3.3 spec section 3). Callers must also show the value selectable.
 */
export function copyText(text: string): boolean {
  const area = document.createElement('textarea');
  area.value = text;
  area.setAttribute('readonly', '');
  // Off-screen through the CSSOM (allowed by the CSP), so the page doesn't jump.
  area.style.position = 'fixed';
  area.style.top = '-1000px';
  area.style.opacity = '0';
  document.body.appendChild(area);
  const previous = document.activeElement as HTMLElement | null;
  area.select();
  area.setSelectionRange(0, text.length);
  let ok = false;
  try {
    ok = document.execCommand('copy');
  } catch {
    ok = false;
  }
  document.body.removeChild(area);
  previous?.focus?.();
  return ok;
}

/** Selects an element's text so the user can copy it by hand when copying fails. */
export function selectContents(element: HTMLElement | null) {
  if (!element) return;
  const selection = window.getSelection();
  if (!selection) return;
  const range = document.createRange();
  range.selectNodeContents(element);
  selection.removeAllRanges();
  selection.addRange(range);
}
