"""Integration checks against the deployed isolated Python worker and real FFmpeg.

Run with a Python interpreter; --deployment points at an unpacked app/extraction
folder containing Payload/. Artifacts are created only in a temporary directory.
"""
import argparse
import json
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--deployment', type=Path, required=True)
    args = parser.parse_args()
    worker = args.deployment / 'Payload/Worker/worker.py'
    python = args.deployment / 'Payload/Runtime/python.exe'
    assert worker.is_file() and python.is_file(), 'Expected deployed worker/runtime'
    tools = {key: shutil.which(key) for key in ('ffmpeg', 'ffprobe')}
    assert all(tools.values()), 'FFmpeg and ffprobe must be on PATH'
    launch = [str(python), '-I', '-B', '-c',
              "import runpy,sys;sys.path.insert(0,sys.argv[1]);runpy.run_path(sys.argv[2],run_name='__main__')",
              str(worker.parent), str(worker)]

    def request(value, cancel=False):
        with subprocess.Popen(launch, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE, text=True, encoding='utf-8') as child:
            child.stdin.write(json.dumps(dict(value, tools=tools)) + '\n')
            child.stdin.flush()
            if cancel:
                child.stdin.write('cancel\n')
                child.stdin.flush()
            events = []
            lines = queue.Queue()
            def consume():
                for line in child.stdout:
                    lines.put(line)
                lines.put(None)
            threading.Thread(target=consume, daemon=True).start()
            while True:
                try:
                    line = lines.get(timeout=60)
                except queue.Empty:
                    child.kill()
                    raise AssertionError('Worker did not finish within 60 seconds')
                if line is None:
                    break
                events.append(json.loads(line))
            child.wait(timeout=10)
            error = child.stderr.read()
            assert not error, error
            return events

    def probe(path):
        j = json.loads(subprocess.check_output([tools['ffprobe'], '-v', 'error',
            '-show_streams', '-of', 'json', str(path)], text=True, encoding='utf-8'))
        return next(s for s in j['streams'] if s['codec_type'] == 'video'), j['streams']

    with tempfile.TemporaryDirectory(prefix='LumaRecode-runtime-') as tmp:
        folder = Path(tmp)
        source = folder / '测试 source.mp4'
        subprocess.run([tools['ffmpeg'], '-v', 'error', '-f', 'lavfi', '-i',
            'testsrc2=size=320x180:rate=24:duration=2', '-f', 'lavfi', '-i',
            'sine=frequency=440:sample_rate=48000:duration=2', '-c:v', 'libx265', '-pix_fmt', 'yuv420p10le',
            '-x265-params', 'colorprim=bt709:transfer=bt709:colormatrix=bt709',
            '-color_primaries', 'bt709', '-color_trc', 'bt709', '-colorspace', 'bt709',
            '-c:a', 'aac', '-shortest', str(source)], check=True)
        events = request({'action': 'probe', 'path': str(source)})
        media = next(e['data'] for e in events if e['event'] == 'media')
        assert media['kind'] == 'sdr' and media['audio'] and media['component_depths'] == [10, 10, 10], media
        print('PASS isolated bundled Python: probe, Unicode path, video/audio bit depth', flush=True)
        cases = [
            ('sdr-share', {'codec': 'hevc'}, 'yuv420p', 'bt709'),
            ('sdr-archive-source', {'purpose': 'archive'}, 'yuv420p10le', 'bt709'),
            *[(f'log-{profile}', {'output': 'hdr', 'transform': 'log-hdr',
               'source_kind': 'log', 'profile': profile}, 'yuv420p10le', 'arib-std-b67')
              for profile in ('milog', 'pocket3', 'samsung')],
        ]
        hlg = None
        for name, options, pixel, transfer in cases:
            output = folder / (name + ('.mkv' if options.get('purpose') == 'archive' else '.mp4'))
            events = request({'action': 'convert', 'path': str(source),
                              'output': str(output), 'options': options})
            errors = [e['data'] for e in events if e['event'] == 'error']
            assert not errors and any(e['event'] == 'done' for e in events), errors or events[-2:]
            v, streams = probe(output)
            assert v['pix_fmt'] == pixel and v['color_transfer'] == transfer, v
            assert any(s['codec_type'] == 'audio' for s in streams)
            assert any(e['event'] == 'progress' for e in events), 'Missing progress protocol'
            if transfer == 'arib-std-b67':
                assert v['color_primaries'] == 'bt2020' and v['color_space'] == 'bt2020nc'
                hlg = output
            print(f'PASS {name}: real encode, {pixel}, {transfer}, audio, progress', flush=True)
        output = folder / 'hdr-to-sdr.mp4'
        events = request({'action': 'convert', 'path': str(hlg), 'output': str(output),
                          'options': {'output': 'sdr', 'transform': 'hdr-sdr'}})
        assert any(e['event'] == 'done' for e in events), events[-3:]
        v, _ = probe(output)
        assert v['color_transfer'] == 'bt709' and v['pix_fmt'] == 'yuv420p'
        print('PASS HLG to SDR: Vulkan/libplacebo and verified Rec.709 8-bit output', flush=True)
        output = folder / 'cancelled.mkv'
        before_cancel = set(folder.iterdir())
        events = request({'action': 'convert', 'path': str(source), 'output': str(output),
                          'options': {'purpose': 'archive', 'codec': 'av1'}}, cancel=True)
        assert any(e['event'] == 'cancelled' for e in events) and not output.exists(), events[-3:]
        assert set(folder.iterdir()) == before_cancel, 'Cancelled temporary file retained'
        print('PASS cancellation and temporary output cleanup', flush=True)
        events = request({'action': 'probe', 'path': str(folder/'missing.mp4')})
        assert any(e['event'] == 'error' for e in events)
        print('PASS structured worker error for missing source', flush=True)


if __name__ == '__main__':
    main()
