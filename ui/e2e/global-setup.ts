import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export default function globalSetup() {
  if (process.env.E2E_BASE_URL) return;
  const script = fileURLToPath(new URL('./stack.sh', import.meta.url));
  execFileSync('bash', [script, 'start'], { stdio: 'inherit' });
}
