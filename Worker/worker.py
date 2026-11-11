"""Local JSON-lines bridge. Native WinUI owns all user interaction."""
from dataclasses import asdict
from pathlib import Path
import json
import os
import sys
import threading

from engine import Cancelled, Job, Options
from media import Tools, inspect_media

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
sys.stdin.reconfigure(encoding='utf-8', errors='replace')
lock = threading.Lock()


def send(event, value):
    with lock:
        print(json.dumps({'event': event, 'data': value}, ensure_ascii=False, default=str), flush=True)


def public_media(media):
    fields = {'codec_name', 'width', 'height', 'pix_fmt', 'avg_frame_rate', 'r_frame_rate',
              'color_range', 'color_primaries', 'color_transfer', 'color_space'}
    return {'path': str(media.path), 'kind': media.kind, 'profile': media.profile,
            'evidence': media.evidence, 'duration': media.duration,
            'video': {key: value for key, value in media.video.items() if key in fields}}


def main():
    incoming = b''
    while b'\n' not in incoming:
        block = os.read(sys.stdin.fileno(), 4096)
        if not block or len(incoming) + len(block) > 1024 * 1024:
            raise ValueError('Invalid local request')
        incoming += block
    line, _, pending = incoming.partition(b'\n')
    request = json.loads(line.decode('utf-8-sig'))
    tools = Tools(**request['tools'])
    media = inspect_media(tools, request['path'])
    if request['action'] == 'probe':
        send('media', public_media(media))
        return
    if request['action'] != 'convert':
        raise ValueError('Unknown local worker action')
    job = Job()

    def monitor():
        # An explicit cancel or a disconnected GUI cancels this one job.
        if b'cancel' in pending:
            job.cancel()
            return
        while True:
            line = os.read(sys.stdin.fileno(), 4096)
            if not line or b'cancel' in line:
                job.cancel()
                return

    threading.Thread(target=monitor, daemon=True).start()
    try:
        job.run(tools, media, Options(**request['options']), Path(request['output']), emit=send)
        send('done', request['output'])
    except Cancelled:
        send('cancelled', None)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        send('error', str(error))
        sys.exit(1)
