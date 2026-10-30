// Regenerates the API types from the committed OpenAPI snapshot and fails if they differ from
// the committed src/api/schema.d.ts (3.3 spec section 7).
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';

const target = 'src/api/schema.d.ts';
const before = readFileSync(target, 'utf8');
execFileSync('npm', ['run', '--silent', 'gen:api'], { stdio: 'inherit' });
const after = readFileSync(target, 'utf8');
if (before !== after) {
  console.error(
    `${target} was out of date with contracts/openapi/sharpclaw-client.v1.json. It has been regenerated; commit it.`,
  );
  process.exit(1);
}
console.log(`${target} matches the OpenAPI snapshot.`);
