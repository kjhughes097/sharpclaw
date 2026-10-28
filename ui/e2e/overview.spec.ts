import { admin, expect, login, test } from './fixtures';

test.describe('overview', () => {
  test('a message sent elsewhere appears on the agent card and in today’s total without a reload @core', async ({
    page,
    request,
  }) => {
    await login(page, '/overview');
    const card = page.getByRole('region', { name: 'Agent assistant' });
    await expect(card).toBeVisible();
    const before = await page.getByRole('img', { name: /^Today:/ }).getAttribute('aria-label');

    // Another client (the CLI, say) starts a thread and sends a message.
    const api = admin(request);
    const created = await api.post('/api/threads', { agentId: 'assistant', title: 'From elsewhere' });
    const { thread } = (await created.json()) as { thread: { id: string } };
    const marker = `overview ping ${Date.now()}`;
    expect((await api.post(`/api/threads/${thread.id}/messages`, { content: marker })).ok()).toBeTruthy();

    await expect(card.getByRole('link', { name: new RegExp(marker) })).toBeVisible({ timeout: 20_000 });
    await expect(card.getByRole('link', { name: /Hello from the fake provider/ }).first()).toBeVisible({
      timeout: 20_000,
    });
    await expect(page.getByRole('img', { name: /^Today:/ })).not.toHaveAttribute('aria-label', before!, {
      timeout: 20_000,
    });

    await card.getByRole('link', { name: new RegExp(marker) }).click();
    await expect(page).toHaveURL(new RegExp(`/prompts/${thread.id}$`));
  });
});

test.describe('layout', () => {
  test('only the panes scroll, never the page itself', async ({ page, request }) => {
    await login(page, '/overview');
    const api = admin(request);
    const { thread } = (await (
      await api.post('/api/threads', { agentId: 'assistant', title: 'Scroll' })
    ).json()) as {
      thread: { id: string };
    };
    for (let i = 0; i < 8; i++)
      await api.post(`/api/threads/${thread.id}/messages`, { content: `line ${i}` });

    for (const path of ['/overview', `/prompts/${thread.id}`, '/agents/assistant', '/audit', '/agents']) {
      await page.goto(path);
      await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible();
      await expect
        .poll(() => page.evaluate(() => document.scrollingElement!.scrollHeight - window.innerHeight), {
          message: `${path} scrolls the page`,
        })
        .toBeLessThanOrEqual(0);
    }
  });
});
