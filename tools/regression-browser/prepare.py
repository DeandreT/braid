"""Native Windows launcher: build Angular, export one clean commit, and publish the Linux fixture products."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import uuid


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def linux(path):
    text = path.resolve().as_posix()
    if not re.match(r'^[A-Za-z]:/', text):
        raise ValueError('Use a native Windows drive path.')
    return '/mnt/' + text[0].lower() + text[2:]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--work-root', type=Path, required=True)
    parser.add_argument('--node', type=Path, required=True)
    parser.add_argument('--npm-cli', type=Path, required=True)
    parser.add_argument('--worker-image', required=True)
    parser.add_argument('--read-port', type=int, default=4114)
    parser.add_argument('--mutation-port', type=int, default=4115)
    args = parser.parse_args()
    assert os.name == 'nt' and args.work_root.is_absolute() and args.work_root.is_dir()
    assert re.fullmatch(r'sha256:[a-f0-9]{64}', args.worker_image)
    assert 1024 <= args.read_port <= 65535 and 1024 <= args.mutation_port <= 65535 and args.read_port != args.mutation_port
    root = Path(__file__).resolve().parents[2]
    status = subprocess.check_output(['git', 'status', '--porcelain'], cwd=root, text=True)
    assert not status.strip(), 'Commit the reviewed fixture source before creating an immutable export.'
    assert subprocess.check_output([str(args.node), '--version'], text=True).strip() == 'v22.23.3'
    environment = {**os.environ, 'PATH': str(args.node.parent) + os.pathsep + str(args.npm_cli.parent.parent.parent / '.bin') + os.pathsep + os.environ['PATH'],
                   'PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD': '1'}
    assert subprocess.check_output([str(args.node), str(args.npm_cli), '--version'], env=environment, text=True).strip() == '12.1.0'
    subprocess.run([str(args.node), str(args.npm_cli), 'run', 'build'], cwd=root, env=environment, check=True)
    identity = str(uuid.uuid4())
    output = args.work_root / ('weft-regression-browser-' + identity); output.mkdir(mode=0o700)
    archive = output / 'source.tar'
    subprocess.run(['git', 'archive', '--format=tar', '--output=' + str(archive), 'HEAD'], cwd=root, check=True)
    manifest = []
    with subprocess.Popen(['git', 'cat-file', '--batch'], cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE) as reader:
        try:
            for row in subprocess.check_output(['git', 'ls-files', '--stage', '-z'], cwd=root).split(b'\0'):
                if not row:
                    continue
                metadata, name = row.split(b'\t', 1); mode, digest, stage = metadata.decode().split()
                name = name.decode(); assert mode in ('100644', '100755') and stage == '0'
                reader.stdin.write(digest.encode() + b'\n'); reader.stdin.flush()
                actual, kind, size = reader.stdout.readline().decode().split()
                assert actual == digest and kind == 'blob' and 0 <= int(size) <= 64 * 1024 * 1024
                content = reader.stdout.read(int(size))
                assert len(content) == int(size) and reader.stdout.read(1) == b'\n'
                manifest.append({'path': name, 'sha256': hashlib.sha256(content).hexdigest(), 'mode': mode})
        finally:
            reader.stdin.close()
        assert reader.wait(timeout=10) == 0
    web = root / 'apps/web/dist/weft/browser'
    web_hashes = {path.relative_to(web).as_posix(): sha(path) for path in sorted(web.rglob('*')) if path.is_file()}
    result = output / 'bindings.json'
    data = {'fixtureId': identity, 'archive': linux(archive), 'web': linux(web), 'webHashes': web_hashes,
            'sourceManifest': manifest, 'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
            'tree': subprocess.check_output(['git', 'rev-parse', 'HEAD^{tree}'], cwd=root, text=True).strip(),
            'workerImage': args.worker_image, 'readOrigin': f'http://127.0.0.1:{args.read_port}',
            'mutationOrigin': f'http://127.0.0.1:{args.mutation_port}', 'result': linux(result)}
    config = output / 'prepare-input.json'; config.write_text(json.dumps(data) + '\n')
    subprocess.run(['wsl.exe', '--distribution', 'Example-Build-Linux', '--user', 'root', '--exec', '/usr/bin/python3', '-I', '-B',
                    linux(root / 'tools/regression-browser/prepare_linux.py'), linux(config)], check=True)
    print('Browser bindings:', result)


if __name__ == '__main__':
    main()
