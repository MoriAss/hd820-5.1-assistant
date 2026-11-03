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
import re
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

def run(args, timeout=120, check=True, **kwargs):
    return subprocess.run([str(s) for s in args], check=check, capture_output=True, timeout=timeout, **kwargs)

def text_output(value):
    return value.decode('utf-8-sig', errors='replace') if isinstance(value, bytes) else (value or '')

# Successful pipe completion is not proof that FFmpeg accepted every PCM packet.
FFMPEG_ERROR = re.compile(
    r'\b(?:invalid|error|failed|failure|corrupt(?:ed|ion)?|truncated|non[- ]finite|nan|infinity)\b',
    re.IGNORECASE)

def validate_fixture_diagnostics(name, stdout, stderr):
    failures = []
    if FFMPEG_ERROR.search(stderr):
        failures.append('process stderr: ' + stderr.strip())
    try:
        payload = json.loads(stdout)
    except (ValueError, TypeError):
        payload = None
    if name == 'EncoderHarness':
        if not isinstance(payload, dict):
            failures.append('EncoderHarness did not produce its required JSON result')
        else:
            if payload.get('completed') is not True:
                failures.append('encoder completed must be true')
            if not isinstance(payload.get('bursts'), (int, float)) or payload['bursts'] <= 0:
                failures.append('encoder bursts must be positive')
    def visit(value, location='result'):
        if isinstance(value, dict):
            for key, item in value.items():
                label = str(key).lower()
                if label in ('error', 'lasterror') and item:
                    failures.append(location + '.' + str(key) + ': ' + str(item))
                if label in ('stderr', 'ffmpegstderr') and isinstance(item, str) and FFMPEG_ERROR.search(item):
                    failures.append(location + '.' + str(key) + ': ' + item.strip())
                visit(item, location + '.' + str(key))
        elif isinstance(value, list):
            for at, item in enumerate(value):
                visit(item, location + '[' + str(at) + ']')
    if payload is not None:
        visit(payload)
    # Also catch FFmpeg's diagnostic line when a fixture prints it outside JSON.
    if re.search(r'\binvalid\s+pcm\b|invalid data found when processing input', stdout, re.IGNORECASE):
        failures.append('stdout contains a PCM/input decoding error')
    if failures:
        raise AssertionError(name + ' reported FFmpeg/fixture errors despite its exit status: ' + '; '.join(failures))

def run_fixture(name, exe, arguments, fixture_output, output):
    try:
        result = run([exe, *arguments], timeout=180, cwd=fixture_output, check=False)
    except subprocess.TimeoutExpired as error:
        (output / (name + '.txt')).write_text(text_output(error.stdout), encoding='utf-8')
        (output / (name + '.stderr.txt')).write_text(text_output(error.stderr), encoding='utf-8')
        raise
    stdout, stderr = text_output(result.stdout), text_output(result.stderr)
    # Preserve both streams even for nonzero exits or a diagnostic assertion.
    (output / (name + '.txt')).write_text(stdout, encoding='utf-8')
    (output / (name + '.stderr.txt')).write_text(stderr, encoding='utf-8')
    if result.returncode:
        raise subprocess.CalledProcessError(result.returncode, result.args, result.stdout, result.stderr)
    validate_fixture_diagnostics(name, stdout, stderr)
    return result

def bursts(path):
    data = path.read_bytes()
    assert data and len(data) % 6144 == 0, 'incomplete carrier'
    for pos in range(0, len(data), 6144):
        pa, pb, pc, bits = struct.unpack_from('<HHHH', data, pos)
        assert (pa, pb, pc & 31) == (0xF872, 0x4E1F, 1), 'invalid IEC61937 header/type at byte %d: %r' % (pos, (pa,pb,pc))
        assert 128 <= bits <= (6144-8)*8 and bits % 16 == 0, 'invalid payload bit count at byte %d: %d' % (pos,bits)
        assert data[pos+8:pos+10] == b'\x77\x0b', 'invalid word-swapped AC3 sync at byte %d' % pos
        header = data[pos+13]
        size_code = header & 63
        rates = [32,40,48,56,64,80,96,112,128,160,192,224,256,320,384,448,512,576,640]
        assert header >> 6 == 0 and size_code <= 37 and data[pos+12] >> 3 <= 8, 'unsupported AC3 sample rate/frame code/bsid at byte %d' % pos
        assert bits == rates[size_code//2] * 4 * 8, 'AC3 header/carrier payload mismatch at byte %d' % pos
        assert not any(data[pos+8+bits//8:pos+6144]), 'nonzero carrier padding at byte %d' % pos
    return len(data) // 6144

def decode(ffmpeg, path):
    bursts(path)
    value = run([ffmpeg, '-v', 'error', '-f', 'spdif', '-i', path,
                 '-c:a', 'pcm_f32le', '-f', 'f32le', 'pipe:1'])
    assert not value.stderr, value.stderr.decode(errors='replace')
    assert len(value.stdout) % 24 == 0, 'decoded PCM byte count is not aligned to six float32 channels: ' + str(len(value.stdout))
    samples = np.frombuffer(value.stdout, '<f4').reshape(-1, 6).astype(float)
    assert len(samples) and np.isfinite(samples).all(), 'decoded PCM is empty or contains NaN/Infinity: ' + str(path)
    return samples

def amplitude(samples, hz):
    t = np.arange(len(samples)) / 48000
    return float(np.hypot(2*np.mean(samples*np.sin(2*np.pi*hz*t)),
                         2*np.mean(samples*np.cos(2*np.pi*hz*t))))

def channel_measurements(samples):
    def finite(value):
        return float(value) if np.isfinite(value) else None
    result = {'frames': len(samples), 'channelOrder': ['FL', 'FR', 'C', 'LFE', 'SL', 'SR']}
    if not len(samples):
        return result
    rms, peak = np.sqrt((samples**2).mean(axis=0)), np.max(abs(samples), axis=0)
    result.update(rmsLinear=rms.tolist(), peakLinear=peak.tolist(),
                  rmsDbFs=[finite(20*np.log10(s)) if s > 0 else None for s in rms],
                  peakDbFs=[finite(20*np.log10(s)) if s > 0 else None for s in peak],
                  dominantFrequencyHz=[float(np.fft.rfftfreq(len(samples), 1/48000)[np.argmax(abs(np.fft.rfft(samples[:, ch]*np.hanning(len(samples)))))]) for ch in range(6)])
    return result

def roundtrip(ffmpeg, output):
    evidence_path = output / 'roundtrip-evidence.json'
    evidence = {'status': 'running', 'sampleRateHz': 48000, 'measurements': {}, 'assertions': []}
    def check(name, passed, actual, expected):
        evidence['assertions'].append({'name': name, 'passed': bool(passed), 'actual': actual, 'expected': expected})
    def measured(name, samples):
        value = channel_measurements(samples)
        evidence['measurements'][name] = value
        return value
    def inspect_channels(name, path):
        evidence['stage'] = name
        samples = decode(ffmpeg, path)
        measured(name + '.full', samples)
        for ch, frequency in enumerate([401,503,607,61,701,809]):
            section = samples[ch*48000+9600:ch*48000+33600]
            value = measured(name + '.window' + str(ch), section)
            label = name + '.channel' + str(ch)
            check(label + '.windowFrames', len(section) == 24000, len(section), {'equal': 24000})
            if not len(section):
                continue
            rms = np.asarray(value['rmsLinear'])
            peak_hz = value['dominantFrequencyHz'][ch]
            relative_gain_error = abs(rms[ch] / (.15 / np.sqrt(2)) - 1)
            leakage = float(np.max(np.delete(rms, ch)) / rms[ch]) if rms[ch] > 0 else None
            check(label + '.activeChannel', np.argmax(rms) == ch, int(np.argmax(rms)), {'equal': ch})
            check(label + '.frequencyHz', abs(peak_hz-frequency) <= 2.5, peak_hz, {'target': frequency, 'absoluteToleranceAtMost': 2.5})
            check(label + '.gainRelativeError', relative_gain_error < .03, float(relative_gain_error), {'lessThan': .03, 'expectedRms': float(.15 / np.sqrt(2))})
            check(label + '.leakageRatio', leakage is not None and leakage < .01, leakage, {'lessThan': .01})
    try:
        inspect_channels('channels', output / 'channels.spdif')
        for encoding in ('utf8-bom', 'utf8-no-bom'):
            inspect_channels(encoding, output / 'encoder-preamble' / (encoding + '.spdif'))
        evidence['stage'] = 'bass'
        bass = decode(ffmpeg, output / 'bass.spdif')[48000:96000]
        measured('bassWindow', bass)
        check('bass.windowFrames', len(bass) == 48000, len(bass), {'equal': 48000})
        if len(bass):
            for ch, hz in zip([0,1,2,4,5],[401,503,607,809,907]):
                for frequency, target, tolerance in [(60,.10,.002),(hz,.04,.002),(30,0,.0001)]:
                    actual = amplitude(bass[:,ch],frequency)
                    check('bass.channel%d.amplitude%dHz' % (ch,frequency), abs(actual-target) < tolerance, actual, {'target': target, 'absoluteToleranceLessThan': tolerance})
            expected = .05 / np.sqrt(1+(60/80)**8)
            actual = amplitude(bass[:,3],60)
            check('bass.LFE.amplitude60Hz', abs(actual/expected-1) < .03, actual, {'target': float(expected), 'relativeToleranceLessThan': .03})
            actual = amplitude(bass[:,3],30)
            check('bass.LFE.native30Hz', abs(actual-.03) < .001, actual, {'target': .03, 'absoluteToleranceLessThan': .001})
        evidence['stage'] = 'drop'
        drop = decode(ffmpeg, output / 'drop.spdif')[1536:-1536]
        measured('dropWindow', drop)
        check('drop.minimumFrames', len(drop) >= 3072, len(drop), {'atLeast': 3072})
        if len(drop):
            for ch, hz in enumerate([125,250,375,62.5,500,625]):
                actual = amplitude(drop[:,ch],hz)
                check('drop.channel%d.amplitudeHz' % ch, abs(actual-.01) < .0003, actual, {'frequencyHz': hz, 'target': .01, 'absoluteToleranceLessThan': .0003})
        evidence['stage'] = 'silence'
        silence = decode(ffmpeg, SRC / 'encoder/silence-ac3-640k.spdif')
        measured('silence', silence)
        check('silence.frames', len(silence) == 1536, len(silence), {'equal': 1536})
        actual = float(np.max(abs(silence)))
        check('silence.peak', actual < 1e-5, actual, {'lessThan': 1e-5})
        failures = [item for item in evidence['assertions'] if not item['passed']]
        if failures:
            evidence['stage'] = 'assertions'
            raise AssertionError('roundtrip failed %d assertion(s); first=%s actual=%r expected=%r; full measurements: %s' %
                                 (len(failures), failures[0]['name'], failures[0]['actual'], failures[0]['expected'], evidence_path))
        evidence['status'] = 'passed'
        return {'channels': 6, 'channelIsolation': True, 'bassAndNativeLfe': True,
                'overflowChannelAlignment': True, 'silenceBelowMinus100DbFs': True,
                'utf8PreambleRoundtrips': ['utf8-bom', 'utf8-no-bom'],
                'measurementsFile': evidence_path.name}
    except Exception as error:
        evidence['status'] = 'failed'
        evidence['error'] = type(error).__name__ + ': ' + str(error)
        if isinstance(error, subprocess.CalledProcessError):
            evidence['decoderStderr'] = text_output(error.stderr)
        raise
    finally:
        (evidence_path).write_text(json.dumps(evidence, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf-8')

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
                substitutions = {'assembly': str(assembly),'output': str(output),'ffmpeg': str(ffmpeg),'src': str(SRC)}
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
            run_fixture(Path(file).stem, exe, args, fixture_output, output)
            report['tests'].append({'name':Path(file).stem, 'status':'passed', 'seconds':round(time.monotonic()-started,3)})
            print('PASS '+Path(file).stem, flush=True)
        report['roundtrip'] = roundtrip(ffmpeg, output)
        report['status'] = 'passed'
    except Exception as e:
        report['status'] = 'failed'
        report['error'] = str(e)
        if (output/'roundtrip-evidence.json').is_file():
            report['roundtripEvidenceFile'] = 'roundtrip-evidence.json'
        if isinstance(e,(subprocess.CalledProcessError,subprocess.TimeoutExpired)):
            report['stdout'] = text_output(e.stdout)
            report['stderr'] = text_output(e.stderr)
        raise
    finally:
        (output/'summary.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print('RESULT '+str(output/'summary.json'), flush=True)

if __name__ == '__main__':
    main()
