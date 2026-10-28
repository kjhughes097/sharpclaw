import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export default function globalTeardown() {
  if (process.env.E2E_BASE_URL || process.env.E2E_KEEP_STACK) return;
  const script = fileURLToPath(new URL('./stack.sh', import.meta.url));
  execFileSync('bash', [script, 'stop'], { stdio: 'inherit' });
}
