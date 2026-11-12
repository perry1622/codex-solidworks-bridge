"""Verify lightweight constraint evidence against real, read-only CAD assemblies."""
from pathlib import Path
import argparse
import hashlib
import json
import uuid
from bridge_rpc import Bridge,step


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--package',required=True)
    parser.add_argument('--pid',type=int,required=True)
    parser.add_argument('--assembly',required=True)
    parser.add_argument('--locked-assembly',required=True)
    parser.add_argument('--report',required=True)
    args=parser.parse_args();report={'tests':{},'cad_files_saved':False,'transcript':[]}
    destination=Path(args.report).resolve();destination.parent.mkdir(parents=True,exist_ok=True)
    files=[Path(p).resolve() for p in (args.assembly,args.locked_assembly)]
    hashes={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
    def check(name,condition):
        assert condition,name
        report['tests'][name]='passed'
    b=None;opened=[]
    try:
        with Bridge(args.package,pid=2147483647,state=destination.parent/'diagnostic-state') as diagnostic:
            status=diagnostic.ok('sw_status',{})['data']
            check('unavailable_target_explained',status['connection']=='disconnected' and status['connection_diagnostic']['reason']=='no_matching_com_registration')
            check('running_processes_reported',args.pid in status['connection_diagnostic']['running_pids'])
            report['transcript'].extend(diagnostic.transcript)
        b=Bridge(args.package,write=True,pid=args.pid,state=destination.parent/'inspect-state',output=destination.parent)
        check('actual_connection',b.ok('sw_status',{})['data']['connection']=='connected')
        enums=b.ok('sw_api_catalog',{'operation':'describe','type_name':'swConstrainedStatus_e'})['data']['items']
        native={v['name']:v['value'] for v in enums}
        inspections=[]
        for index,path in enumerate(files):
            before=b.ok('sw_status',{})['data']['documents']
            assert not any(d['path'] and Path(d['path']).resolve()==path for d in before),'Test assembly must not already be open'
            job,_=b.batch([step('open','application','ISldWorks','OpenDoc6',str(path),2,3,'',None,None)])
            result=job['api_results'][0]
            check('open_read_only_'+str(index),result['return_value'] is not None and result['out_parameters']['Errors']==0)
            opened.append(path)
            status=b.ok('sw_status',{})
            doc=next(d for d in status['data']['documents'] if d['path'] and Path(d['path']).resolve()==path)
            check('read_only_confirmed_'+str(index),doc['read_only'])
            inspection=b.ok('sw_inspect',{'session_id':status['session_id'],'document_id':doc['document_id'],
                'scopes':['summary','constraints','mates','checks']})['data']
            rows=inspection['details']['component_constraints'];mates=inspection['mates']
            check('all_components_read_'+str(index),len(rows)==inspection['details']['component_count']==14)
            check('native_constraint_names_'+str(index),all(native.get(c['constraint_status_name'])==c['constraint_status'] for c in rows))
            check('mate_evidence_explicit_'+str(index),all(isinstance(m['suppressed'],bool) and isinstance(m['error_code'],int) for m in mates))
            check('motion_not_inferred_'+str(index),any(c['code']=='MOTION' and c['state']=='not_run' for c in inspection['checks']))
            inspections.append(inspection)
            b.batch([step('close','application','ISldWorks','CloseDoc',path.name)])
            opened.remove(path)
        def moving(data):
            prefixes=('Isolated_Gear_','InputGear_m2_','OutputGear_m2_','InputShaft_d','OutputShaft_d')
            return [c for c in data['details']['component_constraints'] if c['name'].startswith(prefixes)]
        free,locked=map(moving,inspections)
        check('movable_delivery_distinguished',len(free)==4 and all(not c['fixed'] and c['constraint_status']==native['swUnderConstrained'] for c in free))
        check('original_angle_lock_detected',len(locked)==4 and all(c['constraint_status']==native['swFullyConstrained'] for c in locked))
        check('input_angle_suppression_visible',next(m for m in inspections[0]['mates'] if m['role']=='InputAngle')['suppressed'] and not next(m for m in inspections[1]['mates'] if m['role']=='InputAngle')['suppressed'])
        check('source_assemblies_unchanged',all(hashlib.sha256(Path(p).read_bytes()).hexdigest()==h for p,h in hashes.items()))
        report['inspections']=inspections
    except Exception as exc:report['failure']=str(exc)
    finally:
        if b is not None:
            for path in opened:
                try:b.batch([step('close','application','ISldWorks','CloseDoc',path.name)])
                except RuntimeError as exc:report['cleanup_error']=str(exc)
            report['transcript'].extend(b.transcript);b.close()
        report['passed']=len(report['tests']);report['failed']=int('failure' in report)
        destination.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        print(json.dumps({k:v for k,v in report.items() if k not in ('transcript','inspections')},ensure_ascii=True))
    if report['failed']:raise SystemExit(1)


if __name__=='__main__':main()
