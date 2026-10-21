"""Build the compiler image from an exact locally qualified native image, without a mutable source selection."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import uuid


def cleanup_container(native_image, name, ownership, acknowledged):
    inspect = subprocess.run(['docker', 'container', 'inspect', name], text=True, capture_output=True)
    if inspect.returncode == 0:
        current = json.loads(inspect.stdout)[0]
        if current['Image'] != native_image or current['Name'] != '/' + name or current['Config']['Labels'].get('io.weft.compiler.source-owner') != ownership or not re.fullmatch(r'[a-f0-9]{64}', current['Id']):
            raise RuntimeError('Extraction container ownership mismatch; cleanup refused.')
        subprocess.run(['docker', 'container', 'rm', current['Id']], check=True, stdout=subprocess.DEVNULL)
    elif acknowledged:
        raise RuntimeError('Extraction container cleanup could not be verified.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native-image', required=True, help='Exact sha256 native Docker image ID')
    parser.add_argument('--tag', required=True, help='Resulting compiler image tag')
    parser.add_argument('--work-root', required=True, type=Path, help='Existing disposable-work directory')
    args = parser.parse_args()
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', args.native_image):
        parser.error('An exact native image ID is required.')
    root = Path(__file__).resolve().parents[2]
    if not args.work_root.is_absolute() or not args.work_root.is_dir():
        parser.error('The work root must already exist and be absolute.')
    if os.name != 'nt':
        mount = json.loads(subprocess.check_output(['findmnt', '-J', '-T', str(args.work_root), '-o', 'TARGET,FSTYPE,UUID,FSROOT,OPTIONS']))['filesystems'][0]
        if mount['target'] != '/srv/example-build' or mount['fstype'] != 'btrfs' or mount['uuid'] != '00000000-0000-4000-8000-000000000001' or mount['fsroot'] != '/@example-build' or 'compress=zstd:3' not in mount['options'] or 'rw' not in mount['options'].split(','):
            raise RuntimeError('The compiler build work root is not on the approved Btrfs data volume.')
        host = os.statvfs('/mnt/example-host')
        if host.f_bavail * host.f_frsize < 25 * 2**30:
            raise RuntimeError('The Windows host free-space floor is not met.')
    def output(*command):
        return subprocess.check_output(['docker', *command], text=True).strip()
    if output('image', 'inspect', '--format', '{{.Id}}', args.native_image) != args.native_image:
        raise RuntimeError('Native image identity mismatch.')
    reference = 'weft-compiler-source:' + args.native_image[7:] + '-' + uuid.uuid4().hex[:12]
    name = 'weft-build-source-' + uuid.uuid4().hex
    ownership = uuid.uuid4().hex
    container = None
    attempted = False
    # BuildKit FROM resolves registry names, not local configuration IDs. Bind this unique local
    # reference to the verified ID and independently verify the binary copied into the final image.
    subprocess.run(['docker', 'tag', args.native_image, reference], check=True)
    try:
        with tempfile.TemporaryDirectory(prefix='native-source-', dir=args.work_root) as directory:
            attempted = True
            container = output('container', 'create', '--name', name, '--label', 'io.weft.compiler.source-owner=' + ownership,
                               '--network', 'none', '--entrypoint', '/ferrule-host', args.native_image)
            metadata = json.loads(output('container', 'inspect', container))[0]
            if metadata['Image'] != args.native_image or metadata['Name'] != '/' + name:
                raise RuntimeError('Native extraction container identity mismatch.')
            path = Path(directory) / 'ferrule-host'
            subprocess.run(['docker', 'cp', container + ':/ferrule-host', str(path)], check=True)
            if path.is_symlink() or not path.is_file() or path.stat().st_size > 256 * 2**20 or path.read_bytes()[:4] != b'\x7fELF':
                raise RuntimeError('Native source binary refused.')
            with path.open('rb') as binary:
                digest = hashlib.file_digest(binary, 'sha256').hexdigest()
            if output('image', 'inspect', '--format', '{{.Id}}', reference) != args.native_image:
                raise RuntimeError('Native source reference changed.')
            subprocess.run(['docker', 'build', '--progress=plain', '--tag', args.tag,
                            '--build-arg', 'WEFT_NATIVE_IMAGE=' + reference,
                            '--build-arg', 'WEFT_NATIVE_BINARY_SHA256=' + digest,
                            '--file', 'engines/build-worker/Dockerfile', '.'], cwd=root, check=True)
            if output('image', 'inspect', '--format', '{{.Id}}', reference) != args.native_image:
                raise RuntimeError('Native source reference changed during compilation.')
            print('Compiler image:', output('image', 'inspect', '--format', '{{.Id}}', args.tag))
    finally:
        if attempted:
            cleanup_container(args.native_image, name, ownership, container is not None)
        if output('image', 'inspect', '--format', '{{.Id}}', reference) == args.native_image:
            subprocess.run(['docker', 'image', 'rm', reference], check=True, stdout=subprocess.DEVNULL)


if __name__ == '__main__':
    main()
