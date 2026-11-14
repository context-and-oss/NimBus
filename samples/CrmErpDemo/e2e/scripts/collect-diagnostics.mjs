import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export function redact(text, key) {
  if (key) text = text.replaceAll(key, '[REDACTED]');
  return text
    .replace(/((?:Password|Pwd|SharedAccessKey)\s*=\s*)(?:"[^"]*"|'[^']*'|[^;\s"']+)/gi, '$1[REDACTED]')
    .replace(/("(?:password|E2E__Key|SharedAccessKey)"\s*:\s*")[^"]*/gi, '$1[REDACTED]');
}

// A full suite run is ~8 minutes and the chattiest resource emits roughly six lines a
// second, so the previous 300-line tail only ever covered the last ~50 seconds - the
// window holding a first-minute failure was always discarded before upload. Keep a bound
// so a runaway resource cannot fill the artifact, but make it wide enough to cover a whole
// run. Timestamps let a log line be lined up with a test's start time in results.json.
const TAIL_LINES = process.env.E2E_LOG_TAIL_LINES ?? '20000';

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const appHost = process.env.E2E_APPHOST;
  if (!appHost) throw new Error('E2E_APPHOST must identify the demo AppHost.');
  mkdirSync('artifacts/logs', { recursive: true });
  // 'servicebus-emulator' is the emulator process. 'servicebus' is only its connection string
  // and logs nothing useful: collecting it instead hid a broker-side failure for ten nights.
  for (const resource of ['servicebus-emulator', 'provisioner', 'crm-api', 'erp-api', 'crm-adapter',
    'erp-adapter', 'resolver', 'nimbus-ops', 'dataplatform-adapter', 'agent-zone', 'enrichment-agent',
    'hub-nordic', 'hub-dach', 'hub-unrouted']) {
    let output;
    try {
      output = execFileSync(process.platform === 'win32' ? 'aspire.exe' : 'aspire',
        ['logs', resource, '--apphost', appHost, '--tail', TAIL_LINES, '--timestamps', '--non-interactive'],
        { encoding: 'utf8', timeout: 60_000, maxBuffer: 64 * 1024 * 1024, windowsHide: true, stdio: 'pipe' });
    } catch (error) {
      output = `Log collection failed for ${resource}.\n${error.stdout ?? ''}\n${error.stderr ?? ''}`;
    }
    writeFileSync(`artifacts/logs/${resource}.log`, redact(output, process.env.E2E__Key));
  }
}
