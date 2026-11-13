"""Exercise the actual PowerShell 7 pipeline and Ctrl+C in a hidden console.

The isolated console prevents the generated control event reaching the user's
terminal. This also checks JSON paths under the legacy Chinese output code page.
"""
import argparse
import base64
import ctypes
import hashlib
import json
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time


def exercise(exe, source, output):
    kernel = ctypes.windll.kernel32
    assert kernel.SetConsoleOutputCP(936)
    subprocess.run([shutil.which('pwsh'), '-NoProfile', '-File',
        str(Path(__file__).with_name('pipeline.ps1')), '-Launcher', str(exe), '-Video', str(source)],
        check=True, timeout=30)
    quote = lambda value: "'" + str(value).replace("'", "''") + "'"
    script = ("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
        f"& {quote(exe)} convert {quote(source)} -o {quote(output)} "
        "--preset archive-av1 --overwrite --no-settings --accept-terms --json | ForEach-Object { $_ }")
    encoded = base64.b64encode(script.encode('utf-16-le')).decode()
    process = subprocess.Popen([shutil.which('pwsh'), '-NoProfile', '-EncodedCommand', encoded],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8')
    # Ignore Ctrl+C in the controller only, after spawning the tested process.
    assert kernel.SetConsoleCtrlHandler(None, True)
    lines = queue.Queue()
    def read():
        try:
            for line in process.stdout: lines.put(line)
        finally:
            lines.put(None)
    threading.Thread(target=read, daemon=True).start()
    signalled = False
    old_hash = hashlib.sha256(output.read_bytes()).hexdigest()
    events = []
    try:
        while True:
            line = lines.get(timeout=50)
            if line is None: break
            event = json.loads(line); events.append(event)
            if event['event'] == 'progress' and not signalled:
                assert kernel.GenerateConsoleCtrlEvent(0, 0)
                signalled = True
        process.wait(timeout=20)
        deadline = time.monotonic() + 20
        while list(output.parent.glob('.video-converter-*')) and time.monotonic() < deadline:
            time.sleep(.1)
        assert signalled and process.returncode != 0, (signalled, process.returncode, events, process.stderr.read())
        assert hashlib.sha256(output.read_bytes()).hexdigest() == old_hash
        assert not list(output.parent.glob('.video-converter-*'))
        print('PASS PowerShell 7 Ctrl+C: stopped pipeline, cleaned temporary files, preserved existing output.')
    finally:
        if process.poll() is None: process.kill()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--child', nargs=2, type=Path)
    args = parser.parse_args(); exe = args.exe.resolve()
    if args.child:
        exercise(exe, *args.child); return
    with tempfile.TemporaryDirectory(prefix='LumaRecode-pwsh-cancel-') as temporary:
        folder = Path(temporary); source = folder/'中文 & source.mp4'; output = folder/'keep.mkv'
        subprocess.run([shutil.which('ffmpeg'), '-v', 'error', '-f', 'lavfi', '-i',
            'testsrc2=size=1920x1080:rate=30:duration=10', '-c:v', 'libx265', '-preset', 'ultrafast',
            '-pix_fmt', 'yuv420p10le', '-x265-params',
            'colorprim=bt709:transfer=bt709:colormatrix=bt709:log-level=error', str(source)], check=True)
        output.write_bytes(b'existing output must survive')
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW; startup.wShowWindow = 0
        subprocess.run([sys.executable, __file__, '--exe', str(exe), '--child', str(source), str(output)],
            creationflags=subprocess.CREATE_NEW_CONSOLE, startupinfo=startup, check=True, timeout=90)


if __name__ == '__main__': main()
