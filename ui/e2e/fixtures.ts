import { test as base, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

export const USER = process.env.E2E_USER ?? 'kevin';
export const PASSWORD = process.env.E2E_PASSWORD ?? 'e2e password 1234';
export const ADMIN_KEY = process.env.E2E_ADMIN_KEY ?? 'e2e-admin-key';
const STATE = process.env.E2E_STATE_DIR ?? join(process.env.TMPDIR ?? '/tmp', 'sharpclaw-e2e');

/** Puts a file in the e2e agent's workspace (the isolated stack only). */
export function seedWorkspaceFile(path: string, content = 'note\n') {
  const full = join(STATE, 'workspace', path);
  mkdirSync(join(full, '..'), { recursive: true });
  writeFileSync(full, content);
}

/**
 * Every test checks the two global guarantees (3.3 spec sections 3 and 11): every request the page
 * makes is same-origin, and the console shows no CSP violation.
 */
export const test = base.extend<{ guard: void }>({
  guard: [
    async ({ page, baseURL }, use) => {
      const origin = new URL(baseURL!).origin;
      const foreign: string[] = [];
      const violations: string[] = [];
      page.on('request', (request) => {
        const url = request.url();
        if (!url.startsWith('data:') && !url.startsWith('blob:') && new URL(url).origin !== origin)
          foreign.push(url);
      });
      page.on('console', (message) => {
        if (/Content Security Policy|Refused to/i.test(message.text())) violations.push(message.text());
      });
      await use();
      expect(foreign, 'requests to other origins').toEqual([]);
      expect(violations, 'CSP violations').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };

export async function login(page: Page, next = '/chats', user = USER, password = PASSWORD) {
  await page.goto(next);
  await page.getByLabel('Username').fill(user);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).not.toHaveURL(/\/login/);
  await expect(
    page
      .getByText(/Live|Reconnecting/)
      .filter({ visible: true })
      .first(),
  ).toBeVisible();
}

export function admin(request: APIRequestContext) {
  const headers = { Authorization: `Bearer ${ADMIN_KEY}` };
  return {
    get: (path: string) => request.get(path, { headers }),
    post: (path: string, data?: unknown) => request.post(path, { headers, data }),
    patch: (path: string, data: unknown) => request.patch(path, { headers, data }),
  };
}

/** Creates a thread for the e2e agent through the API and opens it. */
export async function openNewThread(page: Page, request: APIRequestContext, title: string) {
  const res = await admin(request).post('/api/threads', { agentId: 'assistant', title });
  expect(res.ok()).toBeTruthy();
  const { thread } = (await res.json()) as { thread: { id: string } };
  await page.goto(`/chats/${thread.id}`);
  await expect(page.getByRole('textbox', { name: 'Message' })).toBeVisible();
  return thread.id;
}

export async function send(page: Page, text: string) {
  await page.getByRole('textbox', { name: 'Message' }).fill(text);
  await page.getByRole('button', { name: 'Send' }).click();
}

/** The message list (the thread list repeats previews, so scope text lookups here). */
export function messages(page: Page) {
  return page.getByTestId('message-scroll');
}
