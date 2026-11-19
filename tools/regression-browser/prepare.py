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


def linux(path, distribution):
    text = path.resolve().as_posix()
    if not re.match(r'^[A-Za-z]:/', text):
        raise ValueError('Use a native Windows drive path.')
    result = subprocess.check_output(['wsl.exe', '--distribution', distribution, '--exec',
                                      'wslpath', '-a', '-u', str(path.resolve())], text=True).strip()
    if not result.startswith('/'):
        raise ValueError('The selected distribution could not translate the Windows path.')
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--work-root', type=Path, required=True)
    parser.add_argument('--distribution', required=True, help='Installed WSL distribution name')
    parser.add_argument('--storage-profile', required=True, help='Absolute Linux path to the operator storage profile')
    parser.add_argument('--bun', type=Path, required=True)
    parser.add_argument('--worker-image', required=True)
    parser.add_argument('--scenario', choices=['regression', 'build'], default='regression')
    parser.add_argument('--read-port', type=int, default=4114)
    parser.add_argument('--mutation-port', type=int, default=4115)
    parser.add_argument('--reuse-bindings', type=Path, help='Reuse verified publications only when every runtime input and emitted web byte is unchanged.')
    args = parser.parse_args()
    if not args.distribution.strip() or not args.storage_profile.startswith('/'):
        parser.error('Supply a distribution and an absolute Linux storage profile path.')
    def translate(path):
        return linux(path, args.distribution)
    assert os.name == 'nt' and args.work_root.is_absolute() and args.work_root.is_dir()
    assert re.fullmatch(r'sha256:[a-f0-9]{64}', args.worker_image)
    assert 1024 <= args.read_port <= 65535 and 1024 <= args.mutation_port <= 65535 and args.read_port != args.mutation_port
    root = Path(__file__).resolve().parents[2]
    status = subprocess.check_output(['git', 'status', '--porcelain'], cwd=root, text=True)
    assert not status.strip(), 'Commit the reviewed fixture source before creating an immutable export.'
    assert subprocess.check_output([str(args.bun), '--version'], text=True).strip() == '1.4.5'
    environment = {**os.environ, 'PATH': str(args.bun.parent) + os.pathsep + os.environ['PATH'],
                   'PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD': '1'}
    subprocess.run([str(args.bun), 'run', 'build'], cwd=root, env=environment, check=True)
    identity = str(uuid.uuid4())
    output = args.work_root / ('braid-' + args.scenario + '-browser-' + identity); output.mkdir(mode=0o700)
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
    web = root / 'apps/web/dist/braid/browser'
    web_hashes = {path.relative_to(web).as_posix(): sha(path) for path in sorted(web.rglob('*')) if path.is_file()}
    result = output / 'bindings.json'
    data = {'fixtureId': identity, 'scenario': args.scenario, 'archive': translate(archive), 'web': translate(web), 'webHashes': web_hashes,
            'distribution': args.distribution, 'storageProfile': args.storage_profile,
            'sourceManifest': manifest, 'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
            'tree': subprocess.check_output(['git', 'rev-parse', 'HEAD^{tree}'], cwd=root, text=True).strip(),
            'workerImage': args.worker_image, 'readOrigin': f'http://127.0.0.1:{args.read_port}',
            'mutationOrigin': f'http://127.0.0.1:{args.mutation_port}', 'result': translate(result)}
    if args.reuse_bindings:
        previous = json.loads(args.reuse_bindings.read_text())
        data['reuseConfig'] = previous['config']
    config = output / 'prepare-input.json'; config.write_text(json.dumps(data) + '\n')
    subprocess.run(['wsl.exe', '--distribution', args.distribution, '--user', 'root', '--exec', '/usr/bin/python3', '-I', '-B',
                    translate(root / 'tools/regression-browser/prepare_linux.py'), translate(config)], check=True)
    print('Browser bindings:', result)


if __name__ == '__main__':
    main()
