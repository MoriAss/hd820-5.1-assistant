"""Build a verified portable ZIP from a tested binary and a public allowlist.

No third-party executable, personal setting, log or routing file is included.
Python standard library only. Run after tests/run_tests.py, on Windows.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def package(build, validation, output, version):
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Use a numeric x.y.z version')
    summary = json.loads(validation.read_text(encoding='utf-8-sig'))
    if summary.get('status') != 'passed' or not summary.get('tests') or not all(t['status'] == 'passed' for t in summary['tests']):
        raise ValueError('A completed passing regression summary is required')
    payload = {}
    for name in ('HD820游戏5.1助手.exe', 'silence-ac3-640k.spdif'):
        payload[name] = (build / name).read_bytes()
    binary_sha = sha(payload['HD820游戏5.1助手.exe'])
    if binary_sha != summary['assemblySha256']:
        raise ValueError('This binary is not the tested binary')
    sources = {p.relative_to(ROOT).as_posix(): sha(p.read_bytes()) for p in (ROOT/'src').rglob('*.cs')}
    if sources != summary['sourceHashes']:
        raise ValueError('Source changed since testing; rerun tests on the final build')
    bound = [ROOT/'build.ps1',ROOT/'src/encoder/silence-ac3-640k.spdif'] + [p for p in (ROOT/'tests').rglob('*') if p.is_file() and p.suffix in {'.cs','.py','.json','.txt'}]
    if {p.relative_to(ROOT).as_posix(): sha(p.read_bytes()) for p in bound} != summary.get('validationInputHashes'):
        raise ValueError('Test definitions, build script or silent carrier changed since testing')
    if payload['silence-ac3-640k.spdif'] != (ROOT/'src/encoder/silence-ac3-640k.spdif').read_bytes():
        raise ValueError('Silent carrier does not match tested source')
    env = dict(os.environ, HD820_PACKAGE_EXE=str((build/'HD820游戏5.1助手.exe').resolve()))
    binary_version = subprocess.check_output(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command',
        '[Diagnostics.FileVersionInfo]::GetVersionInfo($env:HD820_PACKAGE_EXE).FileVersion'], env=env).decode().strip()
    if binary_version != version + '.0':
        raise ValueError('Binary version mismatch: ' + binary_version)
    for name in ('LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md', '开始使用.html'):
        payload[name] = (ROOT/name).read_bytes()
    for path in sorted((ROOT/'docs').rglob('*')):
        if path.is_file() and path.suffix.lower() in {'.md', '.json', '.svg', '.png'}:
            payload[path.relative_to(ROOT).as_posix()] = path.read_bytes()
    manifest = {'version': version, 'binarySha256': binary_sha, 'sourceHashes': sources,
        'validation': {'status': summary['status'], 'groups': [t['name'] for t in summary['tests']],
                       'scope': 'Offline regressions; not proof of hardware continuity or latency.'},
        'files': {name: sha(data) for name, data in sorted(payload.items())}}
    payload['release-manifest.json'] = (json.dumps(manifest, ensure_ascii=False, indent=2)+'\n').encode('utf-8')
    output.mkdir(parents=True, exist_ok=True)
    dest = output / ('hd820-5.1-assistant-v'+version+'-windows-x64.zip')
    checksums = output / ('hd820-5.1-assistant-v'+version+'-SHA256SUMS.txt')
    if dest.exists() or checksums.exists():
        raise FileExistsError('Refusing to overwrite an existing release')
    # Fixed metadata + stable order: packaging the same bytes produces the same ZIP.
    with zipfile.ZipFile(dest, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, data in sorted(payload.items()):
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            z.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    with zipfile.ZipFile(dest) as z:
        if z.testzip() is not None or set(z.namelist()) != set(payload) or any(z.read(n) != data for n, data in payload.items()):
            raise ValueError('ZIP integrity check failed')
    checksums.write_text(sha(dest.read_bytes())+'  '+dest.name+'\n', encoding='ascii')
    return {'path': str(dest), 'sha256': sha(dest.read_bytes()), 'binarySha256': binary_sha, 'files': len(payload)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', required=True)
    parser.add_argument('--build-dir', type=Path, default=ROOT/'dist')
    parser.add_argument('--validation', type=Path, required=True)
    parser.add_argument('--output-dir', type=Path, default=ROOT/'release')
    args = parser.parse_args()
    print(json.dumps(package(args.build_dir.resolve(), args.validation.resolve(), args.output_dir.resolve(), args.version)))
