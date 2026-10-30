import { expect, login, test } from './fixtures';

test('the LCARS theme applies from the user menu and survives a reload', async ({ page }) => {
  await login(page);
  const html = page.locator('html');
  await expect(html).not.toHaveAttribute('data-theme');

  await page.getByRole('button', { name: /Account menu/ }).click();
  await page.getByRole('menuitemradio', { name: /LCARS/ }).click();
  await expect(html).toHaveAttribute('data-theme', 'lcars');
  // The token swap reached the page: the field is black.
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(0, 0, 0)');
  // LCARS has its own frame, and its bundled font loads from the gateway (the guard fails on any
  // other origin or CSP violation).
  await expect(page.getByText('SharpClaw / Overview')).toBeVisible();
  await expect
    .poll(() => page.evaluate(async () => (await document.fonts.ready, document.fonts.check('16px Antonio'))))
    .toBe(true);

  await page.reload();
  await expect(html).toHaveAttribute('data-theme', 'lcars');

  await page.getByRole('button', { name: /Account menu/ }).click();
  await page.getByRole('menuitemradio', { name: /Default/ }).click();
  await expect(html).not.toHaveAttribute('data-theme');
  await expect(page.getByText('SharpClaw 3')).toBeVisible();
});
