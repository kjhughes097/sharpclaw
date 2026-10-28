import { admin, expect, login, messages, openNewThread, seedWorkspaceFile, send, test } from './fixtures';

test.describe('tools and approvals', () => {
  test.beforeEach(() => seedWorkspaceFile('notes/old.md'));

  test('approve: the card appears in the thread and the inbox, then the turn continues @core', async ({
    page,
    request,
  }) => {
    await login(page);
    await openNewThread(page, request, 'Approve');
    await send(page, 'delete the note');
    const card = page.getByRole('region', { name: 'Approval for workspace_delete' });
    await expect(card).toBeVisible({ timeout: 20_000 });
    await expect(card.getByText('risk: high')).toBeVisible();
    await expect(page).toHaveTitle('(1) SharpClaw');

    await page.goto('/approvals');
    await expect(page.getByRole('region', { name: 'Approval for workspace_delete' })).toBeVisible();
    await page.goBack();

    await page
      .getByRole('region', { name: 'Approval for workspace_delete' })
      .getByRole('button', { name: 'Approve' })
      .click();
    await expect(messages(page).getByText('The tool step is finished.')).toBeVisible({ timeout: 20_000 });
    await expect(page).toHaveTitle('SharpClaw');
    await messages(page)
      .getByRole('button', { name: /workspace_delete/ })
      .click();
    await expect(page.getByText('"path": "notes/old.md"').first()).toBeVisible();
    await expect(page.getByText('"ok": true')).toBeVisible();
  });

  test('a critical-risk call needs a second confirmation', async ({ page, request }) => {
    // Reading a file makes the turn untrusted, which raises the delete from high to critical.
    seedWorkspaceFile('notes/crit.md', 'critical note\n');
    await login(page);
    await openNewThread(page, request, 'Critical');
    await send(page, 'read then delete the note');
    const card = page.getByRole('region', { name: 'Approval for workspace_delete' });
    await expect(card.getByText('risk: critical')).toBeVisible({ timeout: 20_000 });

    await card.getByRole('button', { name: 'Approve' }).click();
    await expect(card.getByRole('alert')).toHaveText('This is a critical-risk action. Approve it?');
    await card.getByRole('button', { name: 'Cancel' }).click();
    await expect(card.getByRole('alert')).toHaveCount(0);

    await card.getByRole('button', { name: 'Approve' }).click();
    await card.getByRole('button', { name: 'Yes, approve' }).click();
    await expect(messages(page).getByText('The tool step is finished.')).toBeVisible({ timeout: 20_000 });
    const recent = await (await admin(request).get('/api/approvals?limit=1')).json();
    expect(recent.approvals[0]).toMatchObject({
      status: 'approved',
      risk: 'critical',
      decidedByUser: 'kevin',
    });
  });

  test('deny is reported to the agent', async ({ page, request }) => {
    await login(page);
    await openNewThread(page, request, 'Deny');
    await send(page, 'delete the note please');
    const card = page.getByRole('region', { name: 'Approval for workspace_delete' });
    await card.getByRole('button', { name: 'Deny' }).click({ timeout: 20_000 });
    await expect(messages(page).getByText('The tool step is finished.')).toBeVisible({ timeout: 20_000 });
    const recent = await (await admin(request).get('/api/approvals?limit=5')).json();
    expect(recent.approvals[0].status).toBe('denied');
  });

  test('a decision in one tab clears the card in the other', async ({ page, request, context }) => {
    await login(page);
    const threadId = await openNewThread(page, request, 'Two tabs');
    const other = await context.newPage();
    await other.goto('/approvals');
    await send(page, 'delete the note now');
    await expect(page.getByRole('region', { name: 'Approval for workspace_delete' })).toBeVisible({
      timeout: 20_000,
    });
    const inbox = other.getByRole('region', { name: 'Approval for workspace_delete' });
    await expect(inbox).toBeVisible();
    await inbox.getByRole('button', { name: 'Approve' }).click();
    await expect(messages(page).getByText('The tool step is finished.')).toBeVisible({ timeout: 20_000 });
    await expect(other.getByText('Nothing is waiting for you.')).toBeVisible();
    expect(threadId).toBeTruthy();
    await other.close();
  });

  test('an unanswered approval expires and is reported', async ({ page, request }) => {
    test.setTimeout(90_000);
    await login(page);
    await openNewThread(page, request, 'Expiry');
    await send(page, 'delete the note later');
    await expect(page.getByRole('region', { name: 'Approval for workspace_delete' })).toBeVisible({
      timeout: 20_000,
    });
    // The e2e stack uses a 25 s approval timeout.
    await expect(messages(page).getByText('The tool step is finished.')).toBeVisible({ timeout: 60_000 });
    const recent = await (await admin(request).get('/api/approvals?limit=5')).json();
    expect(recent.approvals[0].status).toBe('expired');
  });
});

test.describe('agents, audit and trace', () => {
  test('create an agent, see the token once, revoke a token', async ({ page }) => {
    await login(page);
    await page.goto('/agents');
    await page.getByRole('button', { name: /New agent/ }).click();
    const name = `e2e-${Date.now().toString(36)}`;
    await page.getByLabel(/Name/).fill(name);
    await page.getByLabel(/Model/).fill('fake/model');
    await page.getByLabel(/Daily budget/).fill('1');
    await page.getByRole('button', { name: /^Create/ }).click();
    const reveal = page.getByRole('dialog');
    await expect(reveal.getByText(/will not be shown again/)).toBeVisible();
    const token = (await reveal.locator('code').textContent())!.trim();
    expect(token.length).toBeGreaterThan(20);
    await reveal.getByLabel(/I've saved it/).check();
    await reveal.getByRole('button', { name: /Close|Done/ }).click();
    await expect(page.getByText(token)).toHaveCount(0);

    // Exact: the new agent's tab shows the name capitalised.
    await page.getByRole('link', { name, exact: true }).click();
    await page.getByRole('button', { name: 'Revoke' }).first().click();
    await page
      .getByRole('dialog')
      .getByRole('button', { name: /Revoke/ })
      .click();
    await expect(page.getByText(/revoked/i).first()).toBeVisible();
  });

  test('the audit page and the trace panel show the turn', async ({ page, request }) => {
    await login(page);
    await openNewThread(page, request, 'Trace');
    await send(page, 'hello there');
    await expect(messages(page).getByText('Hello from the fake provider.')).toBeVisible({ timeout: 20_000 });
    await page.getByRole('button', { name: 'Chat menu' }).click();
    await page.getByRole('menuitem', { name: 'Trace' }).click();
    const trace = page.getByRole('complementary', { name: 'Trace' });
    await expect(trace.getByRole('heading', { name: 'Trace' })).toBeVisible();
    await expect(trace.getByText(/done/i).first()).toBeVisible();
    await page.keyboard.press('Escape');

    await page.goto('/audit?tab=llm');
    await expect(page.getByText('fake/model').first()).toBeVisible();
    await page.goto('/audit?tab=tools');
    await expect(page.getByText('workspace_delete').first()).toBeVisible();
  });
});
