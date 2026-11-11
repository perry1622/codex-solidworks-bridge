"""Small stdlib MCP client for development tests; never launches or kills SolidWorks."""
from pathlib import Path
import json
import queue
import subprocess
import threading
import time
import uuid


class Bridge:
    def __init__(self, package, *, write=False, pid=None, state=None, output=None, libraries=()):
        self.package = Path(package).resolve()
        self.state = Path(state or self.package / 'state' / uuid.uuid4().hex).resolve()
        self.output = Path(output or self.package / 'models').resolve()
        command = [str(self.package / 'SwBridge.exe'), '--attach-only', '--state-root', str(self.state),
                   '--output-root', str(self.output)]
        if write:
            command.append('--allow-write')
        if pid is not None:
            command += ['--pid', str(pid)]
        for library in libraries:
            command += ['--api-library', str(Path(library).resolve())]
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding='utf-8',
                                        creationflags=subprocess.CREATE_NO_WINDOW)
        self.lines, self.errors, self.transcript = queue.Queue(), [], []
        self.sequence = 0
        def read():
            for line in self.process.stdout:
                self.lines.put(line)
            self.lines.put(None)
        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=lambda: self.errors.extend(self.process.stderr.read().splitlines()), daemon=True).start()
        self.rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
                               'clientInfo': {'name': 'sw-api-development-client', 'version': '0.4.0'}})
        self.rpc('notifications/initialized', {}, notification=True)

    def rpc(self, method, params=None, *, notification=False):
        self.sequence += 1
        message = {'jsonrpc': '2.0', 'method': method, 'params': params or {}}
        if not notification:
            message['id'] = self.sequence
        self.process.stdin.write(json.dumps(message, ensure_ascii=False) + '\n')
        self.process.stdin.flush()
        if notification:
            return
        line = self.lines.get(timeout=45)
        if line is None:
            raise RuntimeError('Bridge exited: ' + '\n'.join(self.errors))
        response = json.loads(line)
        if response.get('id') != self.sequence:
            raise RuntimeError('MCP response ID mismatch')
        self.transcript.append({'request': message, 'response': response})
        if 'error' in response:
            raise RuntimeError(str(response['error']))
        return response['result']

    def tool(self, name, arguments):
        result = self.rpc('tools/call', {'name': name, 'arguments': arguments})
        value = json.loads(result['content'][0]['text'])
        if value != result.get('structuredContent', value):
            raise RuntimeError('Structured/text result mismatch')
        return value

    def ok(self, name, arguments):
        value = self.tool(name, arguments)
        if value['status'] == 'error':
            raise RuntimeError(str(value['error']))
        return value

    def batch(self, steps, *, request_id=None, execution='execute', release=()):
        status = self.ok('sw_status', {})
        if status['data']['connection'] != 'connected':
            raise RuntimeError('SolidWorks connection: ' + status['data']['connection'])
        action = {'type': 'api_batch', 'steps': steps,
                  'expected_active_document_id': status['data'].get('active_document_id')}
        if release:
            action['release_objects'] = list(release)
        args = {'schema_version': '1.0', 'request_id': request_id or str(uuid.uuid4()),
                'session_id': status['session_id'], 'expected_documents':
                [{'document_id': d['document_id'], 'revision': d['revision']} for d in status['data']['documents']],
                'execution': execution, 'output_dir': str(self.output), 'action': action}
        accepted = self.ok('sw_submit', args)
        if execution == 'validate':
            return accepted['data'], args
        started = time.monotonic()
        job = accepted['data']
        while job['state'] in ('queued', 'running', 'cancel_requested'):
            if time.monotonic() - started > 120:
                raise TimeoutError('API job still running: ' + job['job_id'])
            time.sleep(.15)
            job = self.ok('sw_job', {'session_id': status['session_id'], 'job_id': job['job_id'], 'operation': 'get'})['data']
        if job['state'] != 'succeeded':
            raise RuntimeError(json.dumps(job, ensure_ascii=False))
        return job, args

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.process.kill()  # Only the bridge owned by this client.
            self.process.wait(timeout=5)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def step(identifier, target, interface, member, *arguments, operation='call'):
    return {'id': identifier, 'target': target, 'interface': interface, 'member': member,
            'operation': operation, 'arguments': list(arguments)}


def result_ref(identifier, *path):
    return {'$ref': identifier, **({'path': list(path)} if path else {})}
