"""Real single-EXE CLI integration tests, including shell pipelines and CTRL+BREAK."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import shutil
import signal
import subprocess
import sys
import tempfile
import threading

sys.stdout.reconfigure(encoding='utf-8')


def ctrl_break_case(exe, source, output):
    child = subprocess.Popen([str(exe), 'convert', str(source), '-o', str(output),
        '--preset', 'archive-av1', '--no-settings', '--accept-terms', '--json'],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8',
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
    lines = queue.Queue()
    def read():
        for line in child.stdout: lines.put(line)
        lines.put(None)
    threading.Thread(target=read, daemon=True).start()
    events, signalled = [], False
    try:
        while True:
            line = lines.get(timeout=40)
            if line is None: break
            event = json.loads(line); events.append(event)
            if event['event'] == 'start' and not signalled:
                child.send_signal(signal.CTRL_BREAK_EVENT); signalled = True
        child.wait(timeout=20)
        assert signalled and child.returncode == 130, (child.returncode, events, child.stderr.read())
        assert any(e['event'] == 'cancelled' for e in events), events
        assert not output.exists() and not list(output.parent.glob('.video-converter-*'))
    finally:
        if child.poll() is None: child.kill()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--ctrl-break-child', nargs=2, type=Path)
    args = parser.parse_args(); exe = args.exe.resolve()
    if args.ctrl_break_child:
        ctrl_break_case(exe, *args.ctrl_break_child); return
    count = 0
    def call(*words, status=0, raw=False):
        nonlocal count
        result = subprocess.run([str(exe), *map(str, words)], capture_output=True,
                                text=True, encoding='utf-8', timeout=90)
        assert result.returncode == status, (words, result.returncode, result.stdout[-4000:], result.stderr[-4000:])
        count += 1
        return result if raw else json.loads(result.stdout)
    def events(*words, status=0):
        result = call(*words, '--no-settings', '--accept-terms', '--json', status=status, raw=True)
        assert not result.stderr, result.stderr
        return [json.loads(line) for line in result.stdout.splitlines()]
    assert 'FrameFlow.exe' in call('--help', raw=True).stdout
    assert '3.7.1' in call('--version', raw=True).stdout
    assert '同一个 EXE' in call('--lang', 'zh', '--help', raw=True).stdout
    assert len(call('--json', 'presets')['presets']) == 7
    assert {p['id'] for p in call('profiles', '--json')['profiles']} == {'milog','pocket3','samsung'}
    assert call('license', '--json', '--no-settings')['project_license'] == 'MIT'
    assert call('doctor', '--no-settings', '--json', status=6)['data']['code'] == 'terms_not_accepted'
    assert call('unknown', '--json', status=2)['data']['code'] == 'invalid_arguments'
    assert call('doctor', '--ffmpeg', 'missing-ffmpeg.exe', '--no-settings', '--accept-terms', '--json', status=3)['data']['code'] == 'tool_unavailable'
    assert call('doctor', '--no-settings', '--accept-terms', '--json')['encoders']['hevc_qsv']
    with tempfile.TemporaryDirectory(prefix='FrameFlow-CLI-') as temporary:
        folder = Path(temporary)
        source = folder/'中文 & source.mp4'
        subprocess.run([shutil.which('ffmpeg'), '-v','error','-f','lavfi','-i',
            'testsrc2=size=640x360:rate=24:duration=3','-f','lavfi','-i',
            'sine=frequency=440:sample_rate=48000:duration=3','-c:v','libx265','-preset','ultrafast',
            '-pix_fmt','yuv420p10le','-x265-params','colorprim=bt709:transfer=bt709:colormatrix=bt709:log-level=error',
            '-c:a','aac','-shortest',str(source)],check=True)
        source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
        media = call('probe', source, '--no-settings', '--accept-terms','--json')
        assert media['kind']=='sdr' and media['component_depths']==[10]*3 and media['audio']
        plan = events('plan', source, '--preset','archive-hevc','--sampling','420')
        assert plan[0]['data']['pixel_format']=='yuv420p10le'
        events('plan',source,'--preset','archive-hevc','--sampling','422',status=2)
        events('plan',source,'--preset','archive-hevc','--sampling','444',status=2)
        plan = events('plan', source, '--preset','archive-hevc','--color-range','full','--sampling','420')
        assert plan[0]['data']['pixel_format']=='yuv420p10le'
        assert plan[0]['data']['expected_colors']['color_range']=='pc'
        plan = events('plan',source,'--preset','archive-avc')
        assert plan[0]['data']['pixel_format']=='yuv420p10le'
        source8=folder/'8bit420.mp4'
        subprocess.run([shutil.which('ffmpeg'),'-v','error','-i',str(source),'-map','0:v',
            '-c:v','libx264','-pix_fmt','yuv420p','-color_primaries','bt709','-color_trc','bt709',
            '-colorspace','bt709',str(source8)],check=True)
        plan=events('plan',source8,'--preset','archive-hevc')
        assert plan[0]['data']['pixel_format']=='yuv420p' and plan[0]['data']['options']['bit_depth']=='8'
        events('plan',source8,'--preset','archive-hevc','--bit-depth','10',status=2)
        events('plan', source, '--audio', 'flac', status=2)
        events('plan', source, '--input-kind', 'sdr', '--transform', 'log-sdr', '--profile', 'milog', status=2)
        pcm=folder/'pcm.mkv'
        subprocess.run([shutil.which('ffmpeg'), '-v','error','-i',str(source),'-f','lavfi','-i',
            'anoisesrc=r=48000:d=0.5:seed=7','-map','0:v','-map','1:a','-c:v','copy','-c:a','pcm_s24le',
            '-t','0.5',str(pcm)],check=True)
        flac=folder/'flac.mkv'
        result=events('convert',pcm,'--preset','archive-hevc','--audio','flac','--color-range','full','-o',flac)
        assert result[-1]['data']['completed']==1
        converted=call('probe',flac,'--no-settings','--accept-terms','--json')
        assert converted['audio'][0]['codec_name']=='flac' and converted['video']['color_range']=='pc'
        assert not list(folder.glob('*_SDR*')), 'Dry-run created an output video'
        for profile in ('milog','pocket3','samsung'):
            plan = events('plan',source,'--input-kind','log','--profile',profile)
            assert plan[0]['data']['options']['transform']=='log-hdr'
        result = events('convert',source,'--range','hdr',status=2)
        assert result[-1]['data']['code']=='invalid_options'
        events('convert',source,'-o',source,'--overwrite',status=5)
        config=folder/'config.json';config.write_text(json.dumps({'preset':'archive-hevc','sampling':'422'}))
        plan=events('plan',source,'--config',config,'--preset','share-hevc','--sampling','420')
        assert plan[0]['data']['options']['purpose']=='share'
        config.write_text(json.dumps({'preset':'archive-hevc','bit_depth':8}))
        plan=events('plan',source,'--config',config)
        assert plan[0]['data']['pixel_format']=='yuv420p'
        output=folder/'share output.mp4';log=folder/'events.jsonl'
        result=events('convert',source,'-o',output,'--log',log)
        assert output.is_file() and any(e['event']=='progress' and 'fps' in e['data'] for e in result)
        assert result[-1]['data']==dict(completed=1,skipped=0,failed=0)
        assert all(json.loads(line)['schema_version']==1 for line in log.read_text(encoding='utf-8').splitlines())
        events('convert',source,'-o',output,status=5)
        result=events('convert',source,'-o',output,'--skip-existing')
        assert result[-1]['data']['skipped']==1
        events('convert',source,'-o',output,'--overwrite')
        old_hash=hashlib.sha256(output.read_bytes()).hexdigest()
        bad=folder/'bad-ffmpeg.cmd';bad.write_text('@exit /b 1\n')
        events('convert',source,'-o',output,'--overwrite','--ffmpeg',bad,status=1)
        assert hashlib.sha256(output.read_bytes()).hexdigest()==old_hash
        assert not list(folder.glob('.video-converter-*'))
        tree=folder/'input';(tree/'nested').mkdir(parents=True)
        shutil.copyfile(source,tree/'a.mp4');shutil.copyfile(source,tree/'nested/b.mp4')
        result=events('convert',tree,'--recursive','--output-dir',folder/'batch')
        assert result[-1]['data']['completed']==2 and (folder/'batch/nested/b_SDR_hevc_share.mp4').is_file()
        result=events('convert',folder/'missing.mp4',source,'--continue-on-error','--output-dir',folder/'continue',status=1)
        assert result[-1]['data']==dict(completed=1,skipped=0,failed=1)
        events('convert',source,'--log',source,status=5)
        events('convert',source,'--log',folder/'absent/log.jsonl',status=5)
        spaced=folder/'程序 path';spaced.mkdir()
        shutil.copyfile(exe,spaced/'FrameFlow.exe')
        quoted=subprocess.run([str(spaced/'FrameFlow.exe'),'probe',str(source),'--no-settings','--accept-terms','--json'],capture_output=True,text=True,encoding='utf-8',timeout=30)
        assert quoted.returncode==0 and json.loads(quoted.stdout)['kind']=='sdr', quoted.stderr
        subprocess.run([shutil.which('pwsh'),'-NoProfile','-File',str(Path(__file__).with_name('pipeline.ps1')),'-Launcher',str(exe),'-Video',str(source)],check=True,timeout=30)
        # Run a helper in a hidden real console; target process group receives CTRL+BREAK.
        startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
        subprocess.run([sys.executable,__file__,'--exe',str(exe),'--ctrl-break-child',str(source),str(folder/'cancel.mkv')],
            creationflags=subprocess.CREATE_NEW_CONSOLE,startupinfo=startup,check=True,timeout=70)
        subprocess.run([sys.executable,str(Path(__file__).with_name('pwsh_cancel.py')),'--exe',str(exe)],check=True,timeout=100)
        assert hashlib.sha256(source.read_bytes()).hexdigest()==source_hash
        count+=6
    print(f'PASS {count} single-EXE CLI integration scenarios, real encoding, PowerShell 7 pipelines, Ctrl+C/Break, atomic overwrite, batch, JSON and source preservation.')


if __name__=='__main__':main()
