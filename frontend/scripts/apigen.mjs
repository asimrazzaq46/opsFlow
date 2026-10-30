// Regenerates the Angular REST client from the backend OpenAPI spec (no running server needed).
//   npm run apigen        -> rebuild spec + regenerate src/app/core/api/generated
//   npm run apigen:check  -> fail if committed spec/client are outdated (changes nothing)
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const apiProject = resolve(root, '../backend/src/OpsFlow.Api');
const specDir = join(root, 'openapi');
const specFile = 'OpsFlow.Api.json';
const outDir = join(root, 'src/app/core/api/generated');
const check = process.argv.includes('--check');

function run(cmd, args) {
  const r = spawnSync(cmd, args, { cwd: root, stdio: 'inherit' });
  if (r.status !== 0) {
    console.error(`\napigen FAILED: "${cmd} ${args.join(' ')}" exited with ${r.status ?? r.error}`);
    process.exit(1);
  }
}

function files(dir, base = dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? files(join(dir, e.name), base) : [join(dir, e.name).slice(base.length)],
  );
}

function sameDir(a, b) {
  const fa = existsSync(a) ? files(a) : [];
  const fb = existsSync(b) ? files(b) : [];
  return (
    fa.length === fb.length &&
    fa.every((f) => fb.includes(f) && readFileSync(join(a, f), 'utf8') === readFileSync(join(b, f), 'utf8'))
  );
}

const work = check ? mkdtempSync(join(tmpdir(), 'opsflow-apigen-')) : root;
const targetSpecDir = check ? join(work, 'openapi') : specDir;
const targetOut = check ? join(work, 'generated') : outDir;

try {
  run('dotnet', [
    'build', apiProject, '--no-incremental', '-nologo', '-v', 'q',
    // UPDATED: optional alag build configuration (jab IDE ne default bin folders lock kiye hon): APIGEN_CONFIGURATION=Verify
    ...(process.env.APIGEN_CONFIGURATION ? ['-c', process.env.APIGEN_CONFIGURATION] : []),
    '-p:OpenApiGenerateDocuments=true', `-p:OpenApiSpecOutputDir=${targetSpecDir}`,
  ]);
  if (!existsSync(join(targetSpecDir, specFile))) {
    console.error(`apigen FAILED: ${specFile} was not produced by the .NET build.`);
    process.exit(1);
  }
  run(process.execPath, [join(root, 'node_modules/ng-openapi-gen/lib/index.js'), '--config', 'ng-openapi-gen.json', '--input', join(targetSpecDir, specFile), '--output', targetOut]);

  if (check) {
    const upToDate =
      readFileSync(join(targetSpecDir, specFile), 'utf8') === readFileSync(join(specDir, specFile), 'utf8') &&
      sameDir(targetOut, outDir);
    if (!upToDate) {
      console.error('\nGenerated API client is OUTDATED. Run: npm run apigen');
      process.exit(1);
    }
    console.log('Generated API client is up to date.');
  }
} finally {
  if (check) rmSync(work, { recursive: true, force: true });
}
