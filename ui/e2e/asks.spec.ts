import { expect, login, messages, openNewThread, send, test } from './fixtures';

test.describe('agent to agent (3.5)', () => {
  test('assistant asks helper; each thread links to the other', async ({ page, request }) => {
    await login(page);
    const origin = await openNewThread(page, request, 'Ask the helper');
    await send(page, 'ask the helper');
    await expect(messages(page).getByText('The helper says the answer is 4.')).toBeVisible({
      timeout: 30_000,
    });

    // The ask_agent row links to the helper's linked thread.
    await messages(page).getByRole('link', { name: 'Open the conversation with helper' }).click();
    await expect(page).not.toHaveURL(new RegExp(`/prompts/${origin}$`));
    await expect(messages(page).getByText(/from assistant \(agent\)/i)).toBeVisible();
    await expect(messages(page).getByText('What is 2+2?')).toBeVisible();
    await expect(messages(page).getByText('The answer is 4.')).toBeVisible();

    // And the linked thread's header links back to the asking thread.
    await page
      .locator('header span', { hasText: 'asked by' })
      .getByRole('link', { name: 'assistant' })
      .click();
    await expect(page).toHaveURL(new RegExp(`/prompts/${origin}$`));
  });
});
