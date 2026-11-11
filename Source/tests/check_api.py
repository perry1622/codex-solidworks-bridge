"""Exercise real Interop metadata and, with --pid, the actual local SW COM server."""
from pathlib import Path
import argparse
import json
import traceback
import uuid
from bridge_rpc import Bridge, step, result_ref

parser = argparse.ArgumentParser()
parser.add_argument('--package', required=True)
parser.add_argument('--report', required=True)
parser.add_argument('--pid', type=int)
options = parser.parse_args()
report = {'tests': {}, 'transcript': [], 'cad_geometry_created': False}
destination = Path(options.report).resolve()
destination.parent.mkdir(parents=True, exist_ok=True)

def passed(name, assertion=True):
    assert assertion, name
    report['tests'][name] = 'passed'

def values(job):
    return {x['id']: x['return_value'] for x in job['api_results']}

try:
    with Bridge(options.package, pid=options.pid) as b:
        names = {x['name'] for x in b.rpc('tools/list')['tools']}
        passed('tool_discovery', names == {'sw_status', 'sw_inspect', 'sw_submit', 'sw_job', 'sw_api_catalog', 'sw_api_object'})
        summary = b.ok('sw_api_catalog', {'operation': 'summary'})['data']
        report['catalog'] = summary
        passed('official_metadata_catalog', summary['interface_count'] > 500 and summary['enum_count'] > 500)
        described = b.ok('sw_api_catalog', {'operation': 'describe', 'type_name': 'ISldWorks', 'member': 'OpenDoc6'})['data']['items']
        method = next(m for m in described if m['name'] == 'OpenDoc6')
        passed('official_ref_out_signature', [p['name'] for p in method['parameters'] if p['is_out']] == ['Errors', 'Warnings'])
        enums = b.ok('sw_api_catalog', {'operation': 'describe', 'type_name': 'swDocumentTypes_e'})['data']['items']
        passed('official_enum_values', next(x['value'] for x in enums if x['name'] == 'swDocPART') == 1)
        search = b.ok('sw_api_catalog', {'operation': 'search', 'type_name': 'IFeatureManager', 'query': 'FeatureExtrusion3'})['data']
        passed('method_search', any(x['method']['name'] == 'FeatureExtrusion3' for x in search['items']))
        page = b.ok('sw_api_catalog', {'operation': 'types', 'offset': 1, 'limit': 3})['data']
        passed('catalog_pagination', len(page['items']) == 3 and page['offset'] == 1)
        raw = b.ok('sw_api_catalog', {'operation': 'describe', 'type_name': 'IMathPoint', 'member': 'IArrayData'})['data']['items']
        passed('raw_buffer_limit_reported', all(not x['transport_supported'] for x in raw))
        for name, bad in [('negative_offset', {'operation': 'types', 'offset': -1}),
                          ('unknown_interface', {'operation': 'describe', 'type_name': 'System.IO.File'}),
                          ('missing_search_query', {'operation': 'search'}),
                          ('unknown_catalog_field', {'operation': 'summary', 'unexpected': True})]:
            passed(name, b.tool('sw_api_catalog', bad)['status'] == 'error')
        reject = b.tool('sw_submit', {'schema_version': '1.0', 'request_id': str(uuid.uuid4()),
            'session_id': str(uuid.uuid4()), 'expected_documents': [], 'execution': 'execute',
            'output_dir': str(b.output), 'action': {'type': 'api_batch', 'steps': [step('read', 'application', 'ISldWorks', 'RevisionNumber')]}})
        passed('read_only_api_call_rejected', reject['status'] == 'error' and reject['error']['code'] == 'UNSUPPORTED_ACTION')
        report['transcript'].extend(b.transcript)
    if options.pid:
        with Bridge(options.package, write=True, pid=options.pid) as b:
            status = b.ok('sw_status', {})
            passed('actual_solidworks_connection', status['data']['connection'] == 'connected')
            report['instance'] = status['data']['instance']
            steps = [step('math', 'application', 'ISldWorks', 'GetMathUtility'),
                     step('point', result_ref('math'), 'IMathUtility', 'CreatePoint', {'$array': 'double', 'items': [1., 2., 3.]}),
                     step('initial', result_ref('point'), 'IMathPoint', 'ArrayData', operation='get'),
                     step('update', result_ref('point'), 'IMathPoint', 'ArrayData', {'$array': 'double', 'items': [4., 5., 6.]}, operation='set'),
                     step('updated', result_ref('point'), 'IMathPoint', 'ArrayData', operation='get')]
            plan, _ = b.batch(steps, execution='validate')
            passed('api_batch_validate', plan['valid'])
            job, submitted = b.batch(steps)
            returned = values(job)
            passed('actual_com_objects_and_typed_array', returned['initial'] == [1, 2, 3])
            passed('actual_property_set_and_read', returned['updated'] == [4, 5, 6])
            object_id = returned['point']['object_id']
            retained, _ = b.batch([step('value', {'$object': object_id}, 'IMathPoint', 'ArrayData', operation='get')])
            passed('object_reference_across_batches', values(retained)['value'] == [4, 5, 6])
            capture = step('array', {'$object': object_id}, 'IMathPoint', 'ArrayData', operation='get')
            capture['return_mode'] = 'reference'
            captured, _ = b.batch([capture])
            array_id = values(captured)['array']['object_id']
            session = b.ok('sw_status', {})['session_id']
            page = b.ok('sw_api_object', {'session_id': session, 'object_id': array_id, 'operation': 'read', 'offset': 1, 'limit': 2})
            passed('actual_api_array_pagination', page['data']['items'] == [5, 6] and page['data']['array_length'] == 3)
            replay = b.ok('sw_submit', submitted)
            passed('idempotent_request_replay', replay['data']['job_id'] == job['job_id'])
            conflict = json.loads(json.dumps(submitted)); conflict['action']['steps'][0]['member'] = 'RevisionNumber'
            rejection = b.tool('sw_submit', conflict)
            passed('changed_request_id_conflict', rejection['error']['code'] == 'REQUEST_ID_CONFLICT')
            opened, _ = b.batch([step('open', 'application', 'ISldWorks', 'OpenDoc6',
                str(destination.parent / 'intentionally_nonexistent.sldprt'), {'$enum': 'swDocumentTypes_e.swDocPART'},
                {'$enum': 'swOpenDocOptions_e.swOpenDocOptions_Silent'}, '', None, None)])
            output = opened['api_results'][0]
            passed('actual_ref_out_values', output['return_value'] is None and output['out_parameters']['Errors'] != 0)
            for name, changes in [('invalid_enum_rejected_before_call', step('bad', 'application', 'ISldWorks', 'GetUserPreferenceStringValue', {'$enum': 'swDocumentTypes_e.missing'})),
                                  ('arbitrary_managed_type_rejected', step('bad', 'application', 'System.IO.File', 'Delete', 'unused')),
                                  ('fractional_int_rejected', step('bad', 'application', 'ISldWorks', 'GetUserPreferenceStringValue', 1.5)),
                                  ('forward_reference_rejected', step('bad', result_ref('later'), 'IMathPoint', 'ArrayData', operation='get')),
                                  ('raw_buffer_invocation_rejected', step('bad', {'$object': object_id}, 'IMathPoint', 'IArrayData', operation='get'))]:
                malformed = json.loads(json.dumps(submitted)); malformed['request_id'] = str(uuid.uuid4()); malformed['action']['steps'] = [changes]
                rejected = b.tool('sw_submit', malformed)
                passed(name, rejected['status'] == 'error' and rejected['error']['code'] in ('VALIDATION_FAILED', 'UNSUPPORTED_ACTION'))
            session = b.ok('sw_status', {})['session_id']
            released = b.ok('sw_api_object', {'session_id': session, 'object_id': object_id, 'operation': 'release'})
            passed('transport_object_release', released['data']['released'])
            invalid = b.tool('sw_api_object', {'session_id': session, 'object_id': object_id, 'operation': 'read'})
            passed('released_object_rejected', invalid['status'] == 'error')
            passed('no_automatic_success_claim', job['changes']['model_state'] == 'unknown' and all(not x['cad_result_verified'] for x in job['api_results']))
            report['transcript'].extend(b.transcript)
except Exception as exc:
    report['failure'] = str(exc)
    report['traceback'] = traceback.format_exc()
finally:
    report['passed'] = sum(v == 'passed' for v in report['tests'].values())
    report['failed'] = int('failure' in report)
    destination.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k not in ('transcript', 'traceback')}, ensure_ascii=True))
    if report['failed']:
        raise SystemExit(1)
