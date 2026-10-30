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

  await page.reload();
  await expect(html).toHaveAttribute('data-theme', 'lcars');

  await page.getByRole('button', { name: /Account menu/ }).click();
  await page.getByRole('menuitemradio', { name: /Default/ }).click();
  await expect(html).not.toHaveAttribute('data-theme');
});
