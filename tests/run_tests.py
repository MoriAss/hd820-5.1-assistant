"""Offline regressions; never invokes production Main or starts audio hardware.

Run on Windows: python tests/run_tests.py --ffmpeg C:\\tools\\ffmpeg.exe
Test executables and generated signals stay in test-results (gitignored).
"""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import time

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
TESTS = ROOT / 'tests'
SRC = ROOT / 'src'
CSC = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
REFS = ['System', 'System.Core', 'Microsoft.CSharp', 'System.Windows.Forms', 'System.Drawing', 'System.Xml', 'System.Web.Extensions']
SOURCES = list(SRC.glob('*.cs')) + list((SRC / 'encoder').glob('*.cs')) + list((SRC / 'reviewer').glob('*.cs'))

def run(args, timeout=120, **kwargs):
    return subprocess.run([str(s) for s in args], check=True, capture_output=True, timeout=timeout, **kwargs)

def bursts(path):
    data = path.read_bytes()
    assert data and len(data) % 6144 == 0, 'incomplete carrier'
    for pos in range(0, len(data), 6144):
        pa, pb, pc, bits = struct.unpack_from('<HHHH', data, pos)
        assert (pa, pb, pc & 31) == (0xF872, 0x4E1F, 1)
        assert 128 <= bits <= (6144-8)*8 and bits % 16 == 0
        assert data[pos+8:pos+10] == b'\x77\x0b'
        header = data[pos+13]
        size_code = header & 63
        rates = [32,40,48,56,64,80,96,112,128,160,192,224,256,320,384,448,512,576,640]
        assert header >> 6 == 0 and size_code <= 37 and data[pos+12] >> 3 <= 8
        assert bits == rates[size_code//2] * 4 * 8, 'AC3 header/carrier payload mismatch'
        assert not any(data[pos+8+bits//8:pos+6144]), 'nonzero carrier padding'
    return len(data) // 6144

def decode(ffmpeg, path):
    bursts(path)
    value = run([ffmpeg, '-v', 'error', '-f', 'spdif', '-i', path,
                 '-c:a', 'pcm_f32le', '-f', 'f32le', 'pipe:1'])
    assert not value.stderr, value.stderr.decode(errors='replace')
    samples = np.frombuffer(value.stdout, '<f4').reshape(-1, 6).astype(float)
    assert len(samples) and np.isfinite(samples).all()
    return samples

def amplitude(samples, hz):
    t = np.arange(len(samples)) / 48000
    return float(np.hypot(2*np.mean(samples*np.sin(2*np.pi*hz*t)),
                         2*np.mean(samples*np.cos(2*np.pi*hz*t))))

def roundtrip(ffmpeg, output):
    samples = decode(ffmpeg, output / 'channels.spdif')
    for ch, frequency in enumerate([401,503,607,61,701,809]):
        section = samples[ch*48000+9600:ch*48000+33600]
        assert len(section) == 24000
        rms = np.sqrt((section**2).mean(axis=0))
        peak = np.fft.rfftfreq(len(section), 1/48000)[np.argmax(abs(np.fft.rfft(section[:, ch]*np.hanning(len(section)))))]
        assert np.argmax(rms) == ch and abs(peak-frequency) <= 2.5
        assert abs(rms[ch] / (.15 / np.sqrt(2)) - 1) < .03, 'unexpected gain change'
        assert np.max(np.delete(rms, ch)) / rms[ch] < 0.01
    bass = decode(ffmpeg, output / 'bass.spdif')[48000:96000]
    assert len(bass) == 48000
    for ch, hz in zip([0,1,2,4,5],[401,503,607,809,907]):
        assert abs(amplitude(bass[:,ch],60)-.10) < .002
        assert abs(amplitude(bass[:,ch],hz)-.04) < .002
        assert amplitude(bass[:,ch],30) < .0001
    expected = .05 / np.sqrt(1+(60/80)**8)
    assert abs(amplitude(bass[:,3],60)/expected-1) < .03
    assert abs(amplitude(bass[:,3],30)-.03) < .001
    drop = decode(ffmpeg, output / 'drop.spdif')[1536:-1536]
    assert len(drop) >= 3072
    for ch, hz in enumerate([125,250,375,62.5,500,625]):
        assert abs(amplitude(drop[:,ch],hz)-.01) < .0003
    silence = decode(ffmpeg, SRC / 'encoder/silence-ac3-640k.spdif')
    assert len(silence) == 1536 and np.max(abs(silence)) < 1e-5
    return {'channels': 6, 'channelIsolation': True, 'bassAndNativeLfe': True,
            'overflowChannelAlignment': True, 'silenceBelowMinus100DbFs': True}

def main():
    p = argparse.ArgumentParser()
    p.add_argument('--ffmpeg', default=shutil.which('ffmpeg'))
    p.add_argument('--output', type=Path)
    p.add_argument('--assembly', type=Path, help='Already built production candidate; never executed by Main')
    a = p.parse_args()
    if not a.ffmpeg or not Path(a.ffmpeg).is_file():
        p.error('Pass --ffmpeg or put ffmpeg.exe on PATH')
    output = (a.output or ROOT / 'test-results' / dt.datetime.now().strftime('%Y%m%d-%H%M%S')).resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {'startedAt': dt.datetime.now(dt.timezone.utc).isoformat(), 'status': 'running', 'tests': [],
              'limits': ['Offline only; hardware and speaker latency/continuity are not verified.',
                         'UI fixtures do not call production Main, startup setter or audio Start.']}
    ffmpeg = Path(a.ffmpeg).resolve()
    assembly = a.assembly.resolve() if a.assembly else output / 'app/HD820游戏5.1助手.exe'
    try:
        if not a.assembly:
            run(['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',ROOT/'build.ps1','-OutputRoot',assembly.parent])
        report['assemblySha256'] = hashlib.sha256(assembly.read_bytes()).hexdigest()
        report['ffmpegVersion'] = run([ffmpeg,'-version']).stdout.decode(errors='replace').splitlines()[0]
        report['sourceHashes'] = {str(s.relative_to(ROOT)).replace('\\','/'): hashlib.sha256(s.read_bytes()).hexdigest() for s in SOURCES}
        cases = [
            ('ChannelMapperHarness', 'Hd820GameBridge.ChannelMapperHarness', []),
            ('ClockSynchronizerHarness', 'ClockSynchronizerHarness', []),
            ('MeterHarness', 'MeterHarness', []),
            ('EncoderHarness', 'EncoderHarness', [ffmpeg,output/'channels.spdif']),
            ('BassManagerHarness', 'BassManagerHarness', [ffmpeg,output/'bass.spdif']),
            ('EncoderDropHarness', 'EncoderDropHarness', [ffmpeg,output/'drop.spdif']),
            ('EncoderLifecycleHarness', 'EncoderLifecycleHarness', [ffmpeg]),
            ('StopAndCarrierHarness', 'StopAndCarrierHarness', [assembly,output/'carrier.json']),
            ('TrayRestoreHarness', 'TrayRestoreHarness', [assembly,output/'tray-restore.json']),
            ('TrayLifecycleHarness', 'TrayLifecycleHarness', [assembly]),
            ('DeepAudioRegressionHarness', 'DeepAudioRegressionHarness', [SRC/'encoder/silence-ac3-640k.spdif']),
            ('UiLifecycleHarness', 'UiLifecycleHarness', [output/'ui-regression.json',SRC/'encoder/silence-ac3-640k.spdif']),
        ]
        # Additional reviewed regression fixtures are declared explicitly in this manifest.
        manifest = TESTS / 'extra-tests.json'
        if manifest.exists():
            for item in json.loads(manifest.read_text(encoding='utf-8-sig')):
                substitutions = {'assembly': str(assembly),'output': str(output),'ffmpeg': str(ffmpeg)}
                cases.append((item['file'], item['main'], [s.format(**substitutions) for s in item.get('args',[])]))
        for file, entry, args in cases:
            started = time.monotonic()
            leaf = 'tray_lifecycle_fixture_20261004' if Path(file).stem == 'TrayLifecycleHarness' else Path(file).stem
            fixture_output = output / leaf
            fixture_output.mkdir(exist_ok=True)
            exe = fixture_output / (Path(file).stem + '.exe')
            fixture = TESTS / (file if file.endswith('.cs') else file+'.cs')
            run([CSC,'/nologo','/optimize+','/platform:x64','/target:exe','/langversion:5','/codepage:65001',
                 '/main:'+entry,'/out:'+str(exe), *('/r:'+r+'.dll' for r in REFS), *SOURCES, fixture])
            result = run([exe,*args], timeout=180, cwd=fixture_output)
            stdout = result.stdout.decode('utf-8-sig', errors='replace')
            (output/(Path(file).stem+'.txt')).write_text(stdout,encoding='utf-8')
            report['tests'].append({'name':Path(file).stem, 'status':'passed', 'seconds':round(time.monotonic()-started,3)})
            print('PASS '+Path(file).stem, flush=True)
        report['roundtrip'] = roundtrip(ffmpeg, output)
        report['status'] = 'passed'
    except Exception as e:
        report['status'] = 'failed'
        report['error'] = str(e)
        if isinstance(e,subprocess.CalledProcessError):
            report['stdout'] = e.stdout.decode(errors='replace')
            report['stderr'] = e.stderr.decode(errors='replace')
        raise
    finally:
        (output/'summary.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print('RESULT '+str(output/'summary.json'), flush=True)

if __name__ == '__main__':
    main()
