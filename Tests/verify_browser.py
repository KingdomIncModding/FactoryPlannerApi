"""Real Chromium integration: cross-port ES modules, changes, reconnect, cancellation."""
import asyncio
import functools
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import subprocess
import threading
from playwright.async_api import async_playwright

PROJECT = Path(__file__).resolve().parents[1]
DLL = PROJECT / 'Tests/bin/Release/net10.0/ApiTests.dll'

class QuietHandler(SimpleHTTPRequestHandler):
    def log_message(self, *_): pass

async def main():
    subprocess.run(['dotnet', 'build', str(PROJECT/'Tests/ApiTests.csproj'), '-c', 'Release', '--nologo'], check=True, capture_output=True)
    web = ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(QuietHandler, directory=str(PROJECT/'Web')))
    thread = threading.Thread(target=web.serve_forever, daemon=True); thread.start()
    process = None
    def launch(port=0):
        proc = subprocess.Popen(['dotnet', str(DLL), '--fixture', str(port)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        line = proc.stdout.readline()
        if not line: raise RuntimeError(proc.stderr.read())
        return proc, json.loads(line)['port']
    def command(value):
        process.stdin.write(value+'\n'); process.stdin.flush()
        assert process.stdout.readline().strip() == 'ok'
    def stop_fixture():
        if process and process.poll() is None:
            process.stdin.write('quit\n'); process.stdin.flush(); process.wait(timeout=5)
    try:
        process, port = launch()
        async with async_playwright() as playwright:
            browser = await playwright.chromium.launch(headless=True)
            page = await browser.new_page()
            errors = []; page.on('pageerror', lambda error: errors.append(str(error)))
            await page.goto(f'http://127.0.0.1:{web.server_port}/')
            await page.evaluate('''async base => {
                const { FactoryApiClient } = await import(base + '/api/v1/client.js');
                const defaultClient = new FactoryApiClient(base);
                await defaultClient.catalog();
                await defaultClient.snapshot(['session']);
                window.snapshots = []; window.statuses = []; window.requestCount = 0;
                window.api = new FactoryApiClient(base, { interval: 100, fetchImpl: (...args) => { window.requestCount++; return fetch(...args); } });
                window.watch = api.watch({ sections: ['session','research','factory'],
                    onSnapshot: snapshot => snapshots.push(snapshot), onStatus: status => statuses.push(status.connected) });
            }''', f'http://127.0.0.1:{port}')
            await page.wait_for_function('snapshots.length > 0')
            first_instance = await page.evaluate('snapshots.at(-1).instanceId')
            assert await page.evaluate('Object.keys(snapshots.at(-1).sections).length') == 3
            command('publish')
            await page.wait_for_function('snapshots.at(-1).sections.research.data.value === 2')
            command('reset')
            await page.wait_for_function("snapshots.at(-1).sessionId === 'fixture-b'")
            command('fault')
            await page.wait_for_function('statuses.at(-1) === false')
            count_during_fault = await page.evaluate('snapshots.length')
            await asyncio.sleep(.3)
            assert await page.evaluate('snapshots.length') == count_during_fault
            command('recover')
            await page.wait_for_function("statuses.at(-1) === true && snapshots.at(-1).sessionId === 'fixture-a'")
            stop_fixture()
            await page.wait_for_function('statuses.at(-1) === false')
            process, same_port = launch(port)
            assert same_port == port
            await page.wait_for_function('previous => snapshots.at(-1).instanceId !== previous && statuses.at(-1) === true', arg=first_instance)
            # Native browser fetch of an exposed ETag triggers and passes CORS preflight.
            conditional = await page.evaluate('''async base => {
                const first = await fetch(base + '/api/v1/catalog');
                const tag = first.headers.get('ETag');
                const again = await fetch(base + '/api/v1/catalog', { headers: { 'If-None-Match': tag } });
                return { tag, status: again.status };
            }''', f'http://127.0.0.1:{port}')
            assert conditional['tag'] and conditional['status'] == 304
            await page.evaluate('async () => { watch.stop(); await watch.done; }')
            requests = await page.evaluate('requestCount')
            await asyncio.sleep(.35)
            assert requests == await page.evaluate('requestCount')
            assert not errors, errors
            report = {'passed': True, 'nativeDefaultFetch': True, 'crossPortModuleImport': True, 'initialSnapshot': True, 'selection': True,
                      'changeDelivery': True, 'kingdomSwitch': True, 'captureFailureRecovery': True,
                      'processRestartReconnect': True, 'conditionalCorsRequest': True,
                      'stopCancelsPolling': True, 'pageErrors': errors}
            (PROJECT/'Verification').mkdir(exist_ok=True)
            (PROJECT/'Verification/browser-tests.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
            print(json.dumps(report, indent=2))
            await browser.close()
    finally:
        stop_fixture(); web.shutdown(); web.server_close()

if __name__ == '__main__': asyncio.run(main())
