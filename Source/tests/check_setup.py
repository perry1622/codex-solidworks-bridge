"""Verify installation against isolated profiles, never the user's configuration."""
from pathlib import Path
import argparse
import json
import shutil
import subprocess
import tempfile
import tomllib
from bridge_rpc import Bridge


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--package', required=True)
    parser.add_argument('--report', required=True)
    options = parser.parse_args()
    package = Path(options.package).resolve()
    report = Path(options.report).resolve()
    report.parent.mkdir(parents=True, exist_ok=True)
    tests = {}
    with tempfile.TemporaryDirectory(prefix='setup-', dir=report.parent) as temporary:
        assert Path(temporary).resolve().is_relative_to(report.parent.resolve())
        profile = Path(temporary) / 'profile with spaces'
        config = profile / 'config.toml'
        profile.mkdir()
        original = 'model = "keep-model"\n[mcp_servers.existing]\ncommand = "keep.exe"\n'
        config.write_text(original, encoding='utf-8')

        def install(*arguments):
            return subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                '-File', str(package / 'Install-CodexBridge.ps1'), '-ConfigPath', str(config),
                *arguments], capture_output=True, text=True, encoding='utf-8', errors='replace')

        result = install('-AllowWrite', '-SolidWorksPid', '12345')
        assert result.returncode == 0, result.stderr
        data = tomllib.loads(config.read_text(encoding='utf-8'))
        server = data['mcp_servers']['solidworks_local_01a1cb2e']
        assert data['model'] == 'keep-model' and data['mcp_servers']['existing']['command'] == 'keep.exe'
        assert Path(server['command']) == package / 'Bridge/SwBridge.exe'
        assert '--allow-write' in server['args'] and server['args'][-2:] == ['--pid', '12345']
        assert any(path.read_text(encoding='utf-8') == original for path in profile.glob('*.bak'))
        tests['install_preserves_settings_and_backup'] = 'passed'
        assert install().returncode == 0
        data = tomllib.loads(config.read_text(encoding='utf-8'))
        assert set(data['mcp_servers']) == {'existing', 'solidworks_local_01a1cb2e'}
        assert '--allow-write' not in data['mcp_servers']['solidworks_local_01a1cb2e']['args']
        assert '--pid' not in data['mcp_servers']['solidworks_local_01a1cb2e']['args']
        tests['reinstall_updates_only_managed_service'] = 'passed'
        for table in ('solidworks_local_01a1cb2e', '"solidworks_local_01a1cb2e"'):
            text = original + '\n[mcp_servers.' + table + ']\ncommand = "manual.exe"\n'
            config.write_text(text, encoding='utf-8')
            assert install().returncode != 0 and config.read_text(encoding='utf-8') == text
        tests['unmanaged_service_conflict_preserves_file'] = 'passed'
        config.write_text(original + '# BEGIN solidworks-local-bridge\n', encoding='utf-8')
        before = config.read_bytes()
        assert install().returncode != 0 and config.read_bytes() == before
        tests['malformed_block_preserves_file'] = 'passed'
        config.unlink()
        assert install('-WhatIf').returncode == 0 and not config.exists()
        tests['whatif_leaves_profile_untouched'] = 'passed'
        clone = Path(temporary) / 'cloned-package'
        manifest = json.loads((package / 'manifest.json').read_text(encoding='utf-8'))
        for name in [row['path'] for row in manifest['files']] + ['manifest.json']:
            target = clone / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(package / name, target)
        (clone / '.git').mkdir()
        (clone / '.git/HEAD').write_text('ref: refs/heads/main\n', encoding='utf-8')
        verified = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
            '-File', str(clone / 'Verify-Package.ps1')], capture_output=True, text=True)
        assert verified.returncode == 0 and json.loads(verified.stdout)['status'] == 'passed', verified.stderr
        tests['git_checkout_metadata_is_not_packaged'] = 'passed'
        with Bridge(package / 'Bridge', state=Path(temporary) / 'bridge-state') as bridge:
            instructions = bridge.transcript[0]['response']['result']['instructions']
            assert str(package / 'AGENTS.md') in instructions
            assert 'Release unused object references' in instructions
        tests['mcp_handoff_locates_actual_toolkit'] = 'passed'
    report.write_text(json.dumps({'status': 'passed', 'tests': tests,
        'global_configuration_changed': False}, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'status': 'passed', 'tests': len(tests)}))


if __name__ == '__main__':
    main()
