import { admin, expect, login, test } from './fixtures';

test.describe('account', () => {
  test('changing the password: wrong current is refused, other sessions end, the new one works', async ({
    page,
    request,
    browser,
  }) => {
    // A user of its own, so the shared e2e user's password never changes.
    const user = `acct${Date.now() % 1_000_000}`;
    const first = 'first password 1234';
    const second = 'second password 5678';
    expect((await admin(request).post('/api/users', { name: user, password: first })).ok()).toBeTruthy();

    // Another browser signed in as the same user: its session should end with the change.
    const other = await browser.newContext();
    const otherPage = await other.newPage();
    await login(otherPage, '/prompts', user, first);

    await login(page, '/account', user, first);
    await page.getByLabel('Current password').fill('not my password 0000');
    await page.getByLabel('New password', { exact: true }).fill(second);
    await page.getByLabel('Confirm new password').fill(second);
    await page.getByRole('button', { name: 'Change password' }).click();
    await expect(page.getByRole('alert').filter({ hasText: /password/i })).toBeVisible();
    await expect(page).toHaveURL(/\/account/);

    await page.getByLabel('Current password').fill(first);
    await page.getByLabel('New password', { exact: true }).fill(second);
    await page.getByLabel('Confirm new password').fill(second);
    await page.getByRole('button', { name: 'Change password' }).click();
    await expect(page.getByText('Password changed.').first()).toBeVisible();

    await otherPage.goto('/prompts');
    await expect(otherPage).toHaveURL(/\/login/);
    await other.close();

    await page.getByRole('button', { name: 'Sign out' }).first().click();
    await expect(page).toHaveURL(/\/login/);
    await login(page, '/prompts', user, second);
  });
});
