import { admin, expect, login, messages, openNewThread, PASSWORD, send, test, USER } from './fixtures';

test.describe('authentication', () => {
  test('login failure, success and logout @core', async ({ page }) => {
    await page.goto('/chats');
    await expect(page).toHaveURL(/\/login/);
    await page.getByLabel('Username').fill(USER);
    await page.getByLabel('Password').fill('wrong password');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toHaveText('Invalid username or password.');

    await page.getByLabel('Password').fill(PASSWORD);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page).toHaveURL(/\/chats$/);

    await page.goto('/account');
    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/login$/);
    await page.goto('/agents');
    await expect(page).toHaveURL(/\/login\?next=%2Fagents/);
  });

  test('a revoked session returns to login without leftovers', async ({ page, request }) => {
    await login(page);
    await page.goto('/agents');
    await expect(page.getByRole('link', { name: 'assistant' })).toBeVisible();
    // Disabling the user revokes every session; re-enable for the other tests.
    expect((await admin(request).patch(`/api/users/${USER}`, { disabled: true })).ok()).toBeTruthy();
    expect((await admin(request).patch(`/api/users/${USER}`, { disabled: false })).ok()).toBeTruthy();
    await page.getByRole('link', { name: 'Audit' }).first().click();
    await expect(page).toHaveURL(/\/login\?next=%2Faudit/);
    await expect(page.getByRole('link', { name: 'assistant' })).toHaveCount(0);
  });
});

test.describe('chat', () => {
  test('streams a reply progressively @core', async ({ page, request }) => {
    await login(page);
    await openNewThread(page, request, 'Streaming');
    await send(page, 'slow please');
    const bubble = messages(page).getByText(/This reply streams/);
    await expect(bubble).toBeVisible();
    const partial = (await bubble.textContent()) ?? '';
    await expect(messages(page).getByText(/The end\./)).toBeVisible({ timeout: 30_000 });
    expect(partial.length).toBeLessThan(140);
  });

  test('a reload in the middle of a reply resumes it without duplication', async ({ page, request }) => {
    await login(page);
    await openNewThread(page, request, 'Reload');
    await send(page, 'slow again');
    await expect(messages(page).getByText(/This reply streams slowly so/)).toBeVisible();
    await page.reload();
    await expect(messages(page).getByText(/This reply streams/)).toBeVisible();
    await expect(messages(page).getByText(/The end\./)).toBeVisible({ timeout: 30_000 });
    await expect(messages(page).getByText(/This reply streams/)).toHaveCount(1);
    const text =
      (await messages(page)
        .getByText(/This reply streams/)
        .textContent()) ?? '';
    expect(text.match(/streams slowly/g)).toHaveLength(1);
  });

  test('offline and online reconnects without losing or duplicating text', async ({
    page,
    request,
    context,
  }) => {
    await login(page);
    await openNewThread(page, request, 'Offline');
    await send(page, 'slow offline');
    await expect(messages(page).getByText(/This reply streams/)).toBeVisible();
    await context.setOffline(true);
    await page.waitForTimeout(1500);
    await context.setOffline(false);
    await expect(messages(page).getByText(/The end\./)).toBeVisible({ timeout: 30_000 });
    const text =
      (await messages(page)
        .getByText(/This reply streams/)
        .textContent()) ?? '';
    expect(text.match(/The end\./g)).toHaveLength(1);
    expect(text.match(/streams slowly/g)).toHaveLength(1);
  });

  test('markdown is safe: no script, no remote image, no javascript: link', async ({ page, request }) => {
    await login(page);
    await openNewThread(page, request, 'Markdown');
    await send(page, 'markdown please');
    await expect(messages(page).locator('strong', { hasText: 'bold' })).toBeVisible();
    await expect(messages(page).getByRole('link', { name: /\[image: leak\]/ })).toBeVisible();
    await expect(page.locator('main img')).toHaveCount(0);
    await expect(messages(page).getByRole('link', { name: 'bad' })).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined();
  });

  test('banners: disabled agent and budget used up', async ({ page, request }) => {
    await login(page);
    await openNewThread(page, request, 'Banners');
    const api = admin(request);
    await api.patch('/api/agents/assistant', { enabled: false });
    await api.patch('/api/agents/assistant', { dailyBudgetUsd: 0 });
    try {
      await page.reload();
      await expect(
        page.getByText('This agent is disabled. Messages will queue until it is enabled.'),
      ).toBeVisible();
      await expect(page.getByText(/Daily budget used up\. Resets at/)).toBeVisible();
    } finally {
      await api.patch('/api/agents/assistant', { enabled: true, dailyBudgetUsd: 5 });
    }
  });

  test('a thread past its context window shows the dropped-turns banner', async ({ page, request }) => {
    test.setTimeout(90_000);
    await login(page);
    await openNewThread(page, request, 'Context');
    const api = admin(request);
    await api.patch('/api/agents/assistant', { contextWindowTokens: 1000 });
    try {
      for (let i = 0; i < 3; i++) {
        await send(page, `turn ${i} ` + 'x'.repeat(1400));
        await expect(messages(page).getByText('Hello from the fake provider.')).toHaveCount(i + 1, {
          timeout: 20_000,
        });
      }
      await page.reload();
      await expect(
        page.getByText(/The model no longer sees the oldest \d+ turns? of this conversation\./),
      ).toBeVisible();
      await expect(page.getByText(/\/ 1\.0k tokens/)).toBeVisible();
    } finally {
      await api.patch('/api/agents/assistant', { contextWindowTokens: 32000 });
    }
  });
});
