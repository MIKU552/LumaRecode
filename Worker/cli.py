"""LumaRecode public CLI. Engine/worker protocol remains shared with the GUI."""
# SPDX-License-Identifier: MIT
import argparse
from dataclasses import asdict
import glob
import json
import math
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import threading
import time

from engine import ASSETS, CONFIG, PROFILES, Cancelled, Job, Options, build_command, resolve_format, validate
from media import NO_WINDOW, Tools, inspect_media
from worker import public_media

def environment(name, default=''):
    return os.environ.get('LUMARECODE_' + name, os.environ.get('FRAMEFLOW_' + name, default))


VERSION = environment('VERSION', 'development')
VIDEO_TYPES = {'.mp4', '.mov', '.mkv', '.m4v', '.webm', '.avi', '.mts', '.m2ts', '.ts', '.mxf', '.mpg', '.mpeg', '.wmv'}
CHOICES = dict(purpose=('share', 'archive'), codec=('hevc', 'av1', 'avc'),
               range=('auto', 'sdr', 'hdr'), transform=('auto', 'none', 'log-hdr', 'log-sdr', 'hdr-sdr'),
               profile=('auto', *PROFILES), sampling=('auto', 'source', '420', '422', '444', 'gray'), variant=('standard', 'fast'),
               audio=('auto', 'copy', 'aac', 'opus', 'flac'), input_kind=('auto', 'sdr', 'hlg', 'pq', 'log'),
               color_range=('auto', 'source', 'limited', 'full'), bit_depth=('auto', '8', '10', '12'))
CHOICES['container'] = ('auto', 'mp4', 'mkv')
LANG = 'en'
TRANSLATIONS = {}
TRANSLATION_PATTERNS = []
current_job = None
interrupted = threading.Event()


def tr(en, zh):
    if LANG == 'zh': return zh
    if LANG == 'en': return en
    translated = translate(zh)
    return en if translated == zh else translated


def translate(text):
    if LANG == 'zh':
        return text
    if text in TRANSLATIONS:
        return TRANSLATIONS[text]
    for pattern, target in TRANSLATION_PATTERNS:
        match = pattern.fullmatch(text)
        if match:
            return target.format(*match.groups())
    return text


class CliError(Exception):
    def __init__(self, code, message, status=2):
        super().__init__(message)
        self.code, self.status = code, status


class HelpFormatter(argparse.HelpFormatter):
    def add_usage(self, usage, actions, groups, prefix=None):
        super().add_usage(usage, actions, groups, prefix or tr('usage: ', '用法：'))


class Parser(argparse.ArgumentParser):
    def __init__(self, *args, **kwargs):
        add_help = kwargs.pop('add_help', True)
        super().__init__(*args, add_help=False, formatter_class=HelpFormatter, **kwargs)
        self._positionals.title = tr('positional arguments', '位置参数')
        self._optionals.title = tr('options', '选项')
        if add_help:
            self.add_argument('-h', '--help', action='help', default=argparse.SUPPRESS,
                              help=tr('show this help message and exit', '显示帮助并退出'))

    def error(self, message):
        raise CliError('invalid_arguments', message)


def configure_language(argv):
    global LANG, TRANSLATIONS, TRANSLATION_PATTERNS
    selection = environment('LANGUAGE', 'en')
    for index, arg in enumerate(argv):
        if arg == '--':
            break
        if arg in ('--lang', '--language') and index + 1 < len(argv):
            selection = argv[index + 1]
        elif arg.startswith(('--lang=', '--language=')):
            selection = arg.split('=', 1)[1]
    LANG = environment('SYSTEM_LANGUAGE', environment('LANGUAGE', 'en')) if selection == 'system' else selection
    LANG = LANG.strip().replace('_', '-').split('-')[0].lower()
    if LANG not in ('zh', 'en', 'ja', 'es'): LANG = 'en'
    TRANSLATIONS = {}
    for path in (Path(__file__).parent/f'strings.{LANG}.json', Path(__file__).parent.parent/f'App/Strings.{LANG}.json'):
        if path.is_file():
            TRANSLATIONS = json.loads(path.read_text(encoding='utf-8-sig'))
            break
    TRANSLATION_PATTERNS = []
    for source, target in sorted(TRANSLATIONS.items(), key=lambda pair: len(pair[0]), reverse=True):
        if '{0}' not in source: continue
        parts = re.split(r'(\{\d+\})', source)
        pattern = ''.join('(.*?)' if re.fullmatch(r'\{\d+\}', part) else re.escape(part) for part in parts)
        TRANSLATION_PATTERNS.append((re.compile(pattern, re.S), target))


def preset_catalog():
    rows = []
    for purpose in ('share', 'archive'):
        for codec, settings in CONFIG[purpose].items():
            rows.append(dict(id=f'{purpose}-{codec}', options=dict(purpose=purpose, codec=codec, variant='standard'), encoder=settings))
    rows.append(dict(id='archive-av1-fast', options=dict(purpose='archive', codec='av1', variant='fast'), encoder=CONFIG['archive_fast_av1']))
    return rows


def describe(media, tools):
    result = public_media(media, tools)
    result['evidence'] = translate(result['evidence'])
    result['flac_reason'] = translate(result['flac_reason'])
    result.update(streams=media.streams, source_tags=media.tags, first_frames=media.frames)
    return result


def parser():
    common = Parser(add_help=False)
    for flags, params in [
        (('--json',), dict(action='store_true', help=tr('Machine-readable JSON (convert: JSON Lines)', '机器可读 JSON；转换时为逐行 JSON'))),
        (('--lang', '--language'), dict(choices=('system', 'zh', 'en', 'ja', 'es'), help=tr('Output language', '输出语言'))),
        (('--ffmpeg',), dict(metavar='PATH', help=tr('FFmpeg executable (otherwise PATH, then saved GUI path)', 'FFmpeg 路径，默认先 PATH 再使用 GUI 已保存路径'))),
        (('--ffprobe',), dict(metavar='PATH', help=tr('ffprobe executable', 'ffprobe 路径'))),
        (('--python',), dict(metavar='PATH', help=tr('Override the bundled Python interpreter', '指定 Python，覆盖内置解释器'))),
        (('--accept-terms',), dict(action='store_true', help=tr('Accept bundled third-party binary terms', '接受随附第三方二进制组件条款'))),
        (('--no-settings',), dict(action='store_true', help=tr('Ignore saved GUI paths/consent and do not save settings', '忽略 GUI 已保存路径与许可状态，不保存设置'))),
    ]:
        common.add_argument(*flags, default=argparse.SUPPRESS, **params)
    result = Parser(prog='LumaRecode.exe', parents=[common], description=tr(
        'One EXE for GUI and CLI. No arguments: open the GUI. Use a command below for headless operation.',
        '同一个 EXE 提供 GUI 与命令行。无参数时打开 GUI；使用下列命令时无界面运行。'),
        epilog=tr('PowerShell 7: append | Out-Host, or | ConvertFrom-Json for --json, to wait and read $LASTEXITCODE.',
                  'PowerShell 7：接 | Out-Host，或在 --json 后接 | ConvertFrom-Json，以等待完成并读取 $LASTEXITCODE。'))
    result.add_argument('--version', action='version', version=f'LumaRecode {VERSION}', help=tr('Show version and exit', '显示版本并退出'))
    commands = result.add_subparsers(dest='command')
    for name, en, zh in [('probe', 'Inspect video and every audio track', '读取视频与全部音轨信息'),
                         ('presets', 'List tested encoding presets', '列出编码预设'),
                         ('profiles', 'List built-in Log/LUT profiles', '列出内置 Log/LUT 曲线'),
                         ('doctor', 'Check tools and available encoders/filters', '检查工具与编码器、滤镜'),
                         ('license', 'Read component terms and license paths', '读取组件条款与许可路径'),
                         ('convert', 'Convert files, directories or wildcards', '转换文件、目录或通配符'),
                         ('plan', 'Inspect resolved options and FFmpeg arguments without encoding', '查看解析选项和 FFmpeg 参数，不编码')]:
        sub = commands.add_parser(name, help=tr(en, zh), description=tr(en, zh), parents=[common])
        if name == 'probe':
            sub.add_argument('input', type=Path)
        if name == 'license':
            sub.add_argument('--text', action='store_true', help=tr('Print original-project license and component terms', '输出项目许可与组件条款全文'))
        if name in ('convert', 'plan'):
            sub.add_argument('inputs', nargs='+', metavar='INPUT')
            sub.add_argument('-o', '--output', type=Path, help=tr('Output file, for a single input', '单个输入的输出文件'))
            sub.add_argument('--output-dir', type=Path, help=tr('Batch output root; retain scanned subdirectories', '批量输出根目录，保留扫描的子目录结构'))
            sub.add_argument('-r', '--recursive', action='store_true')
            sub.add_argument('--config', type=Path, help=tr('JSON file containing encoding options', '编码选项 JSON 文件'))
            sub.add_argument('--preset', choices=[p['id'] for p in preset_catalog()])
            for key, choices in CHOICES.items():
                flags = ['--'+key.replace('_', '-')]
                if key == 'range': flags += ['--dynamic-range']
                sub.add_argument(*flags, choices=choices, default=None)
            sub.add_argument('--cpu-decode', action=argparse.BooleanOptionalAction, default=None)
            sub.add_argument('--duration', type=float, help=tr('Encode only this many seconds', '只转换指定秒数'))
            policy = sub.add_mutually_exclusive_group()
            policy.add_argument('--overwrite', action='store_true', help=tr('Replace only after successful encoding and verification', '成功编码并校验后才替换已有输出'))
            policy.add_argument('--skip-existing', action='store_true')
            sub.add_argument('--continue-on-error', action='store_true')
            sub.add_argument('--dry-run', action='store_true')
            sub.add_argument('--verbose', action='store_true')
            sub.add_argument('--log', type=Path, help=tr('Append JSON Lines events to a log file', '向日志追加逐行 JSON 事件'))
    return result


def tool(name, explicit=None):
    candidate = explicit or shutil.which(name) or environment(name.upper())
    path = Path(candidate).expanduser() if candidate else None
    if path is None or not path.is_file():
        raise CliError('tool_unavailable', tr(f'{name} was not found; use --{name} PATH.', f'未找到 {name}；请使用 --{name} 指定路径。'), 3)
    return str(path.resolve())


def check_terms(args):
    if not getattr(args, 'accept_terms', False) and environment('TERMS_ACCEPTED') != '1':
        raise CliError('terms_not_accepted', tr('Read "LumaRecode license --text", then use --accept-terms. No GUI is opened.',
            '请先阅读 LumaRecode license --text，再使用 --accept-terms 接受组件条款；命令行不会弹出 GUI。'), 6)


def expand_inputs(args):
    found = []
    for raw in args.inputs:
        path = Path(raw).expanduser()
        if path.is_dir():
            paths = sorted(path.rglob('*') if args.recursive else path.glob('*'))
            found += [(p.resolve(), p.relative_to(path)) for p in paths if p.is_file() and p.suffix.lower() in VIDEO_TYPES]
        elif glob.has_magic(raw):
            if '**' in raw and not args.recursive:
                raise CliError('invalid_arguments', tr('Use --recursive with ** patterns.', '** 通配符需要 --recursive。'))
            paths = sorted(Path(p).resolve() for p in glob.glob(str(path), recursive=args.recursive) if Path(p).is_file())
            base = Path(os.path.commonpath([str(p.parent) for p in paths])) if paths else Path.cwd()
            found += [(p, p.relative_to(base)) for p in paths]
        elif path.is_file():
            found.append((path.resolve(), Path(path.name)))
        else:
            if args.continue_on_error:
                found.append((path.resolve(), Path(path.name)))
            else:
                raise CliError('input_unavailable', tr(f'Input does not exist: {raw}', f'输入不存在：{raw}'), 4)
    unique = {}
    for path, relative in found:
        unique.setdefault(os.path.normcase(str(path)), (path, relative))
    if not unique:
        raise CliError('input_unavailable', tr('No video files matched.', '没有找到匹配的视频。'), 4)
    if args.output and (len(unique) != 1 or args.output_dir):
        raise CliError('invalid_arguments', tr('--output requires one input and cannot be combined with --output-dir.', '--output 需要单个输入，且不能与 --output-dir 同时使用。'))
    return list(unique.values())


def option_values(args):
    values = {}
    if args.config:
        try:
            values = json.loads(args.config.read_text(encoding='utf-8-sig'))
        except (OSError, ValueError) as error:
            raise CliError('invalid_config', str(error)) from error
        if not isinstance(values, dict) or set(values) - (set(CHOICES) | {'preset', 'cpu_decode'}):
            raise CliError('invalid_config', tr('Config must be an object containing only documented encoding options.', '配置须为仅包含受支持编码选项的 JSON 对象。'))
    configured_preset = values.pop('preset', None)
    preset = args.preset or configured_preset
    chosen = next((p['options'] for p in preset_catalog() if p['id'] == preset), None)
    if preset and chosen is None:
        raise CliError('invalid_config', tr(f'Unknown preset: {preset}', f'未知预设：{preset}'))
    result = dict(chosen or {})
    result.update(values)
    result.update({key: getattr(args, key) for key in (*CHOICES, 'cpu_decode') if getattr(args, key) is not None})
    if isinstance(result.get('bit_depth'), int) and not isinstance(result['bit_depth'], bool):
        result['bit_depth'] = str(result['bit_depth'])
    for key, choices in CHOICES.items():
        if key in result and result[key] not in choices:
            raise CliError('invalid_config', tr(f'Invalid {key}: {result[key]}', f'无效的 {key}：{result[key]}'))
    if 'cpu_decode' in result and not isinstance(result['cpu_decode'], bool):
        raise CliError('invalid_config', tr('cpu_decode must be true or false.', 'cpu_decode 必须为 true 或 false。'))
    return result


def resolve_options(values, media):
    fields = dict(values)
    kind = fields.pop('input_kind', 'auto')
    kind = media.kind if kind == 'auto' else kind
    profile = fields.pop('profile', 'auto')
    profile = media.profile if profile == 'auto' else profile
    if kind == 'unknown' and profile:
        kind = 'log'
    if kind == 'unknown':
        raise CliError('ambiguous_input', tr('Unknown transfer function; specify --input-kind.', '无法确认传递曲线，请指定 --input-kind。'), 4)
    target = fields.pop('range', 'auto')
    target = ('hdr' if kind in ('hlg', 'pq', 'log') else 'sdr') if target == 'auto' else target
    transform = fields.pop('transform', 'auto')
    if transform == 'auto':
        if kind == 'log': transform = 'log-hdr' if target == 'hdr' else 'log-sdr'
        elif kind in ('hlg', 'pq') and target == 'sdr': transform = 'hdr-sdr'
        else: transform = 'none'
    if kind == 'log' and not profile:
        raise CliError('ambiguous_log', tr('Log was detected without an identified curve; choose --profile (see profiles).',
            '读到了 Log，但无法确认曲线；请指定 --profile（见 profiles）。'), 4)
    if values.get('profile', 'auto') != 'auto' and not transform.startswith('log-'):
        raise CliError('invalid_arguments', tr('--profile requires a Log transform; use --input-kind log to override incorrect tags.',
            '--profile 需要 Log 转换；要覆盖错误的色彩标签，请指定 --input-kind log。'))
    options = Options(**fields, output=target, transform=transform, profile=profile, source_kind=kind)
    try:
        validate(options, media)
    except ValueError as error:
        raise CliError('invalid_options', str(error)) from error
    return resolve_format(options, media)


class Reporter:
    def __init__(self, args):
        self.args, self.started, self.context = args, time.monotonic(), {}
        self.log = None

    def emit(self, event, data):
        if event == 'progress':
            data = dict(data)
            def number(value):
                try:
                    value = float(value)
                    return value if math.isfinite(value) else 0
                except (TypeError, ValueError): return 0
            elapsed = time.monotonic() - self.started
            position, duration = max(0, number(data.get('out_time_us'))/1e6), number(data.get('duration'))
            speed = number(str(data.get('speed', '')).rstrip('x'))
            data.update(position_seconds=position, duration_seconds=duration, elapsed_seconds=elapsed,
                        percent=min(100, 100*position/duration) if duration > 0 else None,
                        eta_seconds=max(0, (duration-position)/speed) if duration > 0 and speed > 0 else None,
                        fps=number(data.get('fps')) or number(data.get('frame'))/max(elapsed, .001))
        record = dict(schema_version=1, event=event, **self.context, data=data)
        # ASCII escapes preserve Unicode paths when PowerShell decodes native
        # output using a legacy Windows console code page. JSON values are intact.
        line = json.dumps(record, ensure_ascii=True, allow_nan=False)
        if getattr(self.args, 'json', False):
            print(line, flush=True)
        elif event == 'progress':
            eta = f'{data["eta_seconds"]:.0f}s' if data['eta_seconds'] is not None else '?'
            percent = f'{data["percent"]:.1f}%' if data['percent'] is not None else '?%'
            text = f'{percent}  {data["position_seconds"]:.1f}/{data["duration_seconds"]:.1f}s  {data["fps"]:.1f} fps  {tr("elapsed", "已用")} {data["elapsed_seconds"]:.0f}s  {tr("ETA", "剩余")} {eta}'
            print(('\r' if sys.stderr.isatty() else '')+text, end='' if sys.stderr.isatty() else '\n', file=sys.stderr, flush=True)
        elif event == 'log':
            if getattr(self.args, 'verbose', False): print(data, file=sys.stderr, flush=True)
        elif event in ('error', 'status'):
            print(translate(data['message'] if isinstance(data, dict) else data), file=sys.stderr, flush=True)
        elif event in ('done', 'skipped', 'cancelled'):
            if sys.stderr.isatty(): print(file=sys.stderr)
            label = {'done':tr('done', '完成'), 'skipped':tr('skipped', '已跳过'), 'cancelled':tr('cancelled', '已取消')}[event]
            print(f'{label}: {data}', flush=True)
        elif event == 'summary':
            print(json.dumps(data, ensure_ascii=True), flush=True)
        if self.log is not None:
            try:
                self.log.write(line+'\n'); self.log.flush()
            except OSError as error:
                self.log.close(); self.log = None
                raise CliError('log_unavailable', str(error), 5) from error


def cancel(*_):
    interrupted.set()
    if current_job is not None: current_job.cancel()


def main(argv=None):
    global current_job
    argv = sys.argv[1:] if argv is None else argv
    configure_language(argv)
    args = argparse.Namespace(json='--json' in argv)
    report = Reporter(args)
    try:
        command_parser = parser()
        args = command_parser.parse_args(argv)
        report = Reporter(args)
        if args.command is None:
            command_parser.print_help(); return 0
        def output(value):
            print(json.dumps(value, ensure_ascii=True, indent=2, allow_nan=False), flush=True)
        if args.command == 'presets':
            output(dict(schema_version=1, presets=preset_catalog())); return 0
        if args.command == 'profiles':
            output(dict(schema_version=1, profiles=[dict(id=k, name=translate(v[0]), lut=v[1], input_matrix=v[2], input_range=v[3], targets=['hdr', 'sdr']) for k, v in PROFILES.items()])); return 0
        if args.command == 'license':
            folder = Path(environment('LICENSE_DIR', Path(__file__).parent.parent/'licenses'))
            if args.text:
                terms_file = folder / {'zh':'THIRD-PARTY-TERMS.zh-CN.txt', 'ja':'THIRD-PARTY-TERMS.ja.txt',
                                       'es':'THIRD-PARTY-TERMS.es.txt'}.get(LANG, 'THIRD-PARTY-TERMS.txt')
                if getattr(args, 'json', False):
                    output(dict(schema_version=1, project_license=(folder/'LICENSE').read_text(encoding='utf-8-sig'), component_terms=terms_file.read_text(encoding='utf-8-sig'), directory=str(folder)))
                else:
                    print((folder/'LICENSE').read_text(encoding='utf-8-sig')); print(terms_file.read_text(encoding='utf-8-sig'))
            else:
                output(dict(schema_version=1, project_license='MIT', directory=str(folder), accepted=getattr(args, 'accept_terms', False) or environment('TERMS_ACCEPTED') == '1', manifest=json.loads((folder/'manifest.json').read_text(encoding='utf-8-sig'))))
            return 0
        signal.signal(signal.SIGINT, cancel)
        if hasattr(signal, 'SIGBREAK'): signal.signal(signal.SIGBREAK, cancel)
        if environment('HOSTED') == '1':
            def monitor():
                while True:
                    data = os.read(sys.stdin.fileno(), 4096)
                    if not data or b'cancel' in data: cancel(); return
            threading.Thread(target=monitor, daemon=True).start()
        check_terms(args)
        tools = Tools(tool('ffmpeg', getattr(args, 'ffmpeg', None)), tool('ffprobe', getattr(args, 'ffprobe', None)), interrupted)
        tool_paths = dict(ffmpeg=tools.ffmpeg, ffprobe=tools.ffprobe)
        if args.command == 'doctor':
            values = dict(schema_version=1, python=sys.version.split()[0], tools=tool_paths, encoders={}, filters={})
            for name, executable in tool_paths.items():
                result = subprocess.run([executable, '-version'], capture_output=True, encoding='utf-8', errors='replace', creationflags=NO_WINDOW, timeout=15)
                if result.returncode or f'{name} version' not in result.stdout:
                    raise CliError('tool_unavailable', tr(f'Invalid {name} executable: {executable}', f'无效的 {name} 程序：{executable}'), 3)
                values[name+'_version'] = result.stdout.splitlines()[0]
            for flag, key, wanted in [('-encoders', 'encoders', ('hevc_qsv','av1_qsv','h264_qsv','libx265','libsvtav1','libx264','aac','libopus','flac')), ('-filters','filters',('zscale','lut3d','libplacebo'))]:
                result = subprocess.run([tools.ffmpeg, '-hide_banner', flag], capture_output=True, encoding='utf-8', errors='replace', creationflags=NO_WINDOW, timeout=15)
                if result.returncode: raise CliError('tool_unavailable', result.stderr, 3)
                values[key] = {name: bool(re.search(r'\b'+re.escape(name)+r'\b', result.stdout)) for name in wanted}
            values['note'] = tr('Hardware encoding modes depend on the GPU and driver.', '硬件编码模式取决于显卡与驱动。')
            output(values); return 0
        if args.command == 'probe':
            if not args.input.is_file(): raise CliError('input_unavailable', str(args.input), 4)
            output(dict(schema_version=1, **describe(inspect_media(tools, args.input), tools))); return 0
        if args.duration is not None and (not math.isfinite(args.duration) or args.duration <= 0):
            raise CliError('invalid_arguments', tr('--duration must be a finite positive number.', '--duration 必须为有限正数。'))
        inputs = expand_inputs(args)
        values = option_values(args)
        plans, failures, seen = [], 0, set()
        sources = {os.path.normcase(str(p)) for p, _ in inputs}
        for path, relative in inputs:
            try:
                media = inspect_media(tools, path)
                options = resolve_options(values, media)
                name = f'{path.stem}_{options.output.upper()}_{options.codec}_{options.purpose}{options.suffix}'
                destination = (args.output or (args.output_dir/relative.parent/name if args.output_dir else path.parent/name)).resolve()
                identity = os.path.normcase(str(destination))
                if identity in seen or identity in sources or (destination.exists() and destination.samefile(path)):
                    raise CliError('output_conflict', tr(f'Conflicting output: {destination}', f'输出路径冲突：{destination}'), 5)
                if destination.suffix.lower() != options.suffix or destination.exists() and not destination.is_file():
                    raise CliError('output_conflict', tr(f'Output must be a {options.suffix} file: {destination}', f'输出必须为 {options.suffix} 文件：{destination}'), 5)
                if destination.exists() and not args.overwrite and not args.skip_existing:
                    raise CliError('output_exists', tr(f'Output exists: {destination}', f'输出已存在：{destination}'), 5)
                seen.add(identity)
                plans.append((media, options, destination))
            except (ValueError, OSError, CliError) as error:
                if interrupted.is_set(): raise Cancelled() from error
                if isinstance(error, CliError) and error.status == 5 or not args.continue_on_error: raise
                failures += 1
                report.emit('error', dict(code=getattr(error, 'code', 'input_error'), input=str(path), message=translate(str(error))))
        if args.log:
            log_path = os.path.normcase(str(args.log.resolve()))
            if log_path in sources or log_path in seen or (args.log.is_file() and any(args.log.samefile(p) for p, _ in inputs)):
                raise CliError('output_conflict', tr('The log path conflicts with input/output.', '日志路径与输入或输出冲突。'), 5)
            try:
                report.log = args.log.open('a', encoding='utf-8')
            except OSError as error:
                raise CliError('log_unavailable', str(error), 5) from error
        complete = skipped = 0
        for index, (media, options, destination) in enumerate(plans, 1):
            if interrupted.is_set(): raise Cancelled()
            report.context = dict(job=index, total_jobs=len(plans), input=str(media.path), output=str(destination))
            if destination.exists() and args.skip_existing:
                skipped += 1; report.emit('skipped', str(destination)); continue
            command, colors = build_command(tools, media, options, destination)
            if args.duration: command[-1:-1] = ['-t', str(args.duration)]
            plan = dict(input=describe(media, tools), output=str(destination), options=asdict(options), pixel_format=options.pixel_format, cwd=str(ASSETS), argv=command, expected_colors=colors)
            if args.command == 'plan' or args.dry_run:
                if getattr(args, 'json', False): report.emit('plan', plan)
                else: output(plan)
                continue
            try:
                destination.parent.mkdir(parents=True, exist_ok=True)
                report.started = time.monotonic()
                report.emit('start', dict(options=asdict(options), duration=min(args.duration, media.duration) if args.duration else media.duration))
                current_job = Job()
                if interrupted.is_set(): current_job.cancel()
                current_job.run(tools, media, options, destination, emit=report.emit, seconds=args.duration, overwrite=args.overwrite)
                complete += 1
                report.emit('done', str(destination))
            except (ValueError, RuntimeError, OSError) as error:
                if interrupted.is_set(): raise Cancelled() from error
                failures += 1
                report.emit('error', dict(code='conversion_failed', message=translate(str(error))))
                if not args.continue_on_error: return 1
            finally:
                current_job = None
        report.context = {}
        if args.command != 'plan' and not args.dry_run:
            report.emit('summary', dict(completed=complete, skipped=skipped, failed=failures))
        return 1 if failures else 0
    except (Cancelled, KeyboardInterrupt):
        cancel(); report.emit('cancelled', None); return 130
    except (CliError, ValueError, OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        if interrupted.is_set(): report.emit('cancelled', None); return 130
        report.emit('error', dict(code=getattr(error, 'code', 'operation_failed'), message=translate(str(error))))
        return getattr(error, 'status', 1)
    finally:
        if report.log: report.log.close()


if __name__ == '__main__':
    raise SystemExit(main())
