from pathlib import Path
import argparse
import datetime
import json
import queue
import subprocess
import threading
import traceback
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--package', required=True)
parser.add_argument('--report', required=True)
parser.add_argument('--state-root', required=True)
parser.add_argument('--pid', type=int)
args = parser.parse_args()
package = Path(args.package).resolve()
report_path = Path(args.report).resolve()
report_path.parent.mkdir(parents=True, exist_ok=True)
command = [str(package / 'SwBridge.exe'), '--attach-only', '--state-root', str(Path(args.state_root).resolve()),
           '--output-root', str(package / 'models')]
if args.pid:
    command += ['--pid', str(args.pid)]
process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           encoding='utf-8', cwd=package, creationflags=subprocess.CREATE_NO_WINDOW)
lines = queue.Queue()
errors = []
transcript = []
def reader():
    for line in process.stdout:
        lines.put(line)
    lines.put(None)
threading.Thread(target=reader, daemon=True).start()
threading.Thread(target=lambda: errors.extend(process.stderr.read().splitlines()), daemon=True).start()
sequence = 0
def send(method, params=None, notify=False):
    global sequence
    sequence += 1
    request = {'jsonrpc': '2.0', 'method': method, 'params': params or {}}
    if not notify:
        request['id'] = sequence
    process.stdin.write(json.dumps(request, ensure_ascii=False) + '\n')
    process.stdin.flush()
    if notify:
        return None
    line = lines.get(timeout=35)
    if line is None:
        raise RuntimeError('Bridge exited before replying: ' + '\n'.join(errors))
    response = json.loads(line)
    if response.get('id') != sequence:
        raise AssertionError('Response ID mismatch')
    transcript.append({'request': request, 'response': response})
    return response
def tool(name, arguments):
    response = send('tools/call', {'name': name, 'arguments': arguments})
    if 'error' in response:
        raise RuntimeError(str(response['error']))
    output = response['result']
    content = output.get('structuredContent') or json.loads(output['content'][0]['text'])
    assert content == json.loads(output['content'][0]['text'])
    return content

report = {'attempted_at': datetime.datetime.now(datetime.timezone(datetime.timedelta(hours=8))).isoformat(),
          'bridge': str(package / 'SwBridge.exe'), 'mode': 'read_only_attach_only',
          'tests': {}, 'transcript': transcript, 'stderr': errors,
          'cad_write_operations_attempted': False, 'global_configuration_changed': False}
try:
    init = send('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
                              'clientInfo': {'name': 'isolated-bridge-connection-check', 'version': '1.0'}})
    assert init['result']['protocolVersion'] == '2025-06-18'
    send('notifications/initialized', notify=True)
    report['tests']['mcp_initialize'] = 'pass'
    tools = send('tools/list')['result']['tools']
    assert {t['name'] for t in tools} == {'sw_status', 'sw_inspect', 'sw_submit', 'sw_job', 'sw_api_catalog', 'sw_api_object'}
    report['tests']['tool_discovery'] = 'pass'
    assert send('ping')['result'] == {}
    report['tests']['ping'] = 'pass'
    status = tool('sw_status', {})
    report['status'] = status
    report['tests']['cad_connection'] = status.get('data', {}).get('connection', 'error') if status['status'] == 'ok' else 'error'
    if status['status'] == 'ok' and status['data']['connection'] == 'connected':
        assert status['data']['capabilities']['actions'] == []
        documents = status['data']['documents']
        report['tests']['document_enumeration'] = 'pass'
        chosen = next((d for d in documents if d['document_type'] == 'part'), documents[0] if documents else None)
        if chosen:
            inspection = tool('sw_inspect', {'session_id': status['session_id'],
                'document_id': chosen['document_id'], 'scopes': ['summary', 'geometry', 'parameters', 'references', 'checks']})
            report['inspection'] = inspection
            report['tests']['document_inspection'] = 'pass' if inspection['status'] == 'ok' else 'error'
        else:
            report['tests']['document_inspection'] = 'not_run_no_open_documents'
        stale = tool('sw_inspect', {'session_id': str(uuid.uuid4()), 'document_id': 'stale'})
        assert stale['status'] == 'error' and stale['error']['code'] == 'SW_DISCONNECTED'
        report['tests']['stale_session_rejected'] = 'pass'
    rejected = tool('sw_submit', {'schema_version': '1.0', 'request_id': str(uuid.uuid4()),
        'session_id': status.get('session_id') or str(uuid.uuid4()), 'execution': 'execute',
        'expected_documents': [], 'output_dir': str(package / 'models'),
        'action': {'type': 'export_document', 'document_id': 'unused', 'format': 'step'}})
    assert rejected['status'] == 'error' and rejected['error']['code'] == 'UNSUPPORTED_ACTION'
    report['tests']['read_only_write_rejected'] = 'pass'
    invalid = tool('sw_status', {'unexpected': True})
    assert invalid['status'] == 'error' and invalid['error']['code'] == 'VALIDATION_FAILED'
    report['tests']['unknown_fields_rejected'] = 'pass'
    unknown = send('tools/call', {'name': 'not_a_tool', 'arguments': {}})
    assert unknown['error']['code'] == -32602
    report['tests']['unknown_tool_rejected'] = 'pass'
except Exception as exc:
    report['failure'] = str(exc)
    report['traceback'] = traceback.format_exc()
finally:
    process.stdin.close()
    try:
        process.wait(timeout=12)
    except subprocess.TimeoutExpired:
        process.kill()  # Only this test's owned bridge process, never SolidWorks.
        process.wait(timeout=5)
        report['bridge_process_stopped_after_timeout'] = True
    report['bridge_exit_code'] = process.returncode
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    compact = {k: v for k, v in report.items() if k not in ['transcript', 'traceback', 'status', 'inspection']}
    if report.get('status', {}).get('status') == 'ok':
        compact['instance'] = report['status']['data']['instance']
        compact['document_count'] = len(report['status']['data']['documents'])
    compact['report'] = str(report_path)
    print(json.dumps(compact, ensure_ascii=True))
    if 'failure' in report:
        raise SystemExit(1)
