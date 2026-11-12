"""CLI translation coverage, locale selection, placeholders and packaged resources."""
import argparse
import ast
import contextlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root/'Worker'))
import cli


def template(node):
    if isinstance(node, ast.Constant) and isinstance(node.value, str): return node.value
    if isinstance(node, ast.JoinedStr):
        values, ordinal = [], 0
        for part in node.values:
            if isinstance(part, ast.Constant): values.append(part.value)
            else:
                values.append('{'+str(ordinal)+'}'); ordinal += 1
        return ''.join(values)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path)
    args = parser.parse_args()
    catalogs = {code:json.loads((root/f'App/Strings.{code}.json').read_text(encoding='utf-8-sig')) for code in ('en','ja','es')}
    tree = ast.parse((root/'Worker/cli.py').read_text(encoding='utf-8-sig'))
    for node in ast.walk(tree):
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == 'tr':
            key = template(node.args[1])
            if key is not None:
                assert all(key in values for values in catalogs.values()), key
    count = 0
    saved_env = {name:os.environ.get(name) for name in ('FRAMEFLOW_LANGUAGE','FRAMEFLOW_SYSTEM_LANGUAGE')}
    try:
        for tag, expected in [('ja-JP','ja'),('JA_jp','ja'),('es-MX','es'),('es-419','es'),('zh-TW','zh'),('fr-FR','en')]:
            os.environ['FRAMEFLOW_LANGUAGE'] = tag
            cli.configure_language([])
            assert cli.LANG == expected
            count += 1
        os.environ['FRAMEFLOW_LANGUAGE'] = 'zh'
        os.environ['FRAMEFLOW_SYSTEM_LANGUAGE'] = 'es-MX'
        cli.configure_language(['--lang','system'])
        assert cli.LANG == 'es'
        cli.configure_language(['--language=ja'])
        assert cli.LANG == 'ja'
        count += 2
        for code in ('en','ja','es'):
            cli.configure_language(['--lang',code])
            for source,target in catalogs[code].items():
                assert cli.translate(source) == target, (code,source)
                if '{0}' in source:
                    values = ['ARG_'+str(i) for i in range(max(map(int,re.findall(r'\{(\d+)\}',source)))+1)]
                    assert cli.translate(source.format(*values)) == target.format(*values), (code,source)
                count += 1
        for code, heading, terms, missing in [('ja','位置引数','第三者バイナリコンポーネント','見つかりません'),
                                             ('es','argumentos posicionales','componentes binarios de terceros','No se encontró')]:
            if args.exe:
                def invoke(*words):
                    result = subprocess.run([str(args.exe.resolve()), '--lang',code,*words,'--no-settings'],
                        capture_output=True,text=True,encoding='utf-8',timeout=30)
                    assert not result.stderr, result.stderr
                    return result.returncode,result.stdout
                # --help exits during parsing; the later --no-settings still protects the host.
                status,text = invoke('--help')
                assert status == 0 and heading in text
                status,text = invoke('license','--text','--json')
                assert status == 0 and terms in json.loads(text)['component_terms']
                status,text = invoke('doctor','--ffmpeg','missing-ffmpeg.exe','--accept-terms','--json')
            else:
                cli.configure_language(['--lang',code])
                assert heading in cli.parser().format_help()
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    assert cli.main(['--lang',code,'license','--text','--json','--no-settings']) == 0
                assert terms in json.loads(out.getvalue())['component_terms']
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    status = cli.main(['--lang',code,'doctor','--ffmpeg','missing-ffmpeg.exe','--accept-terms','--json','--no-settings'])
                text = out.getvalue()
            failure = json.loads(text)
            assert status == 3 and failure['data']['code'] == 'tool_unavailable' and missing in failure['data']['message']
            count += 3
    finally:
        for name,value in saved_env.items():
            if value is None: os.environ.pop(name,None)
            else: os.environ[name] = value
    print(f'PASS {count} CLI language checks'+(' against the packaged EXE.' if args.exe else '.'))


if __name__ == '__main__': main()
