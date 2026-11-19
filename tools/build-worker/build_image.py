"""Build the compiler image from an exact locally qualified native image, without a mutable source selection."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import tempfile
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import qualification_storage as storage

OWNER_LABEL = 'io.braid.compiler.source-owner'


def admit_work_root(work_root, profile_path=None):
    """On Linux verify storage against the operator's independent expectations."""
    if not work_root.is_absolute() or not work_root.is_dir():
        raise RuntimeError('The work root must already exist and be absolute.')
    work_root = work_root.resolve(strict=True)
    if sys.platform == 'win32':
        if profile_path is not None:
            raise RuntimeError('A Linux storage profile cannot be used on native Windows.')
        return work_root
    if sys.platform != 'linux':
        raise RuntimeError('Unsupported build platform.')
    profile = storage.read_profile(profile_path or os.environ.get('BRAID_STORAGE_PROFILE'))
    wsl = 'microsoft' in platform.release().lower() or 'windowsHostMount' in profile
    storage.require_volume(work_root, profile, wsl=wsl)
    if wsl:
        storage.require_host_reserve(profile)
    return work_root


def container_matches(metadata, native_image, name, ownership, identity=None):
    return (metadata.get('Image') == native_image and metadata.get('Name') == '/' + name
            and (metadata.get('Config', {}).get('Labels') or {}).get(OWNER_LABEL) == ownership
            and re.fullmatch(r'[a-f0-9]{64}', metadata.get('Id', '')) is not None
            and (identity is None or metadata['Id'] == identity))


def missing_resource(result, kind, name):
    # A daemon/transport failure is not evidence of absence after a lost acknowledgement.
    return result.returncode != 0 and any(
        line.strip() in (f'Error response from daemon: No such {kind}: {name}', f'Error: No such {kind}: {name}')
        for line in result.stderr.splitlines())


def cleanup_container(native_image, name, ownership, acknowledged, identity=None):
    inspect = subprocess.run(['docker', 'container', 'inspect', name], text=True, capture_output=True)
    if inspect.returncode == 0:
        current = json.loads(inspect.stdout)[0]
        if not container_matches(current, native_image, name, ownership, identity):
            raise RuntimeError('Extraction container ownership mismatch; cleanup refused.')
        subprocess.run(['docker', 'container', 'rm', current['Id']], check=True, stdout=subprocess.DEVNULL)
    elif acknowledged or not missing_resource(inspect, 'container', name):
        raise RuntimeError('Extraction container cleanup could not be verified.')


def cleanup_reference(native_image, reference, acknowledged):
    inspect = subprocess.run(['docker', 'image', 'inspect', '--format', '{{.Id}}', reference],
                             text=True, capture_output=True)
    if inspect.returncode == 0:
        if inspect.stdout.strip() != native_image:
            raise RuntimeError('Native source reference ownership mismatch; cleanup refused.')
        subprocess.run(['docker', 'image', 'rm', reference], check=True, stdout=subprocess.DEVNULL)
    elif acknowledged or not missing_resource(inspect, 'image', reference):
        raise RuntimeError('Native source reference cleanup could not be verified.')


def prepare_image(native_image, tag, work_root, profile_path=None, build_docker_config=None):
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', native_image):
        raise RuntimeError('An exact native image ID is required.')
    work_root = admit_work_root(work_root, profile_path)
    root = Path(__file__).resolve().parents[2]
    def output(*command):
        return subprocess.check_output(['docker', *command], text=True).strip()
    if output('image', 'inspect', '--format', '{{.Id}}', native_image) != native_image:
        raise RuntimeError('Native image identity mismatch.')
    reference = 'braid-compiler-source:' + native_image[7:] + '-' + uuid.uuid4().hex
    name = 'braid-build-source-' + uuid.uuid4().hex
    ownership = uuid.uuid4().hex
    existing = subprocess.run(['docker', 'image', 'inspect', '--format', '{{.Id}}', reference],
                              text=True, capture_output=True)
    if not missing_resource(existing, 'image', reference):
        raise RuntimeError('Native source reference absence could not be verified; tagging refused.')
    container = None
    attempted = False
    tag_acknowledged = False
    failure = None
    # BuildKit FROM resolves registry names, not local configuration IDs. Bind this unique local
    # reference to the verified ID and independently verify the binary copied into the final image.
    try:
        # Include tagging in the cleanup boundary: it too can succeed with a lost acknowledgement.
        subprocess.run(['docker', 'tag', native_image, reference], check=True)
        tag_acknowledged = True
        with tempfile.TemporaryDirectory(prefix='native-source-', dir=work_root) as directory:
            attempted = True
            container = output('container', 'create', '--name', name, '--label', OWNER_LABEL + '=' + ownership,
                               '--network', 'none', '--entrypoint', '/ferrule-host', native_image)
            if not re.fullmatch(r'[a-f0-9]{64}', container):
                raise RuntimeError('Native extraction container ID refused.')
            metadata = json.loads(output('container', 'inspect', container))[0]
            if not container_matches(metadata, native_image, name, ownership, container):
                raise RuntimeError('Native extraction container identity mismatch.')
            path = Path(directory) / 'ferrule-host'
            subprocess.run(['docker', 'cp', container + ':/ferrule-host', str(path)], check=True)
            if path.is_symlink() or not path.is_file() or path.stat().st_size > 256 * 2**20:
                raise RuntimeError('Native source binary refused.')
            with path.open('rb') as binary:
                if binary.read(4) != b'\x7fELF':
                    raise RuntimeError('Native source binary refused.')
                binary.seek(0)
                digest = hashlib.file_digest(binary, 'sha256').hexdigest()
            if output('image', 'inspect', '--format', '{{.Id}}', reference) != native_image:
                raise RuntimeError('Native source reference changed.')
            build_env = os.environ.copy()
            if build_docker_config:
                build_env['DOCKER_CONFIG'] = build_docker_config
            if build_env.get('DOCKER_CONFIG'):
                # Docker resolves this against its cwd, including inherited config.
                # Preserve the caller's path before building from the repository root.
                build_env['DOCKER_CONFIG'] = str(Path(build_env['DOCKER_CONFIG']).absolute())
            subprocess.run(['docker', 'build', '--progress=plain', '--tag', tag,
                            '--build-arg', 'BRAID_NATIVE_IMAGE=' + reference,
                            '--build-arg', 'BRAID_NATIVE_BINARY_SHA256=' + digest,
                            '--file', 'engines/build-worker/Dockerfile', '.'], cwd=root, env=build_env, check=True)
            if output('image', 'inspect', '--format', '{{.Id}}', reference) != native_image:
                raise RuntimeError('Native source reference changed during compilation.')
            compiler_image = output('image', 'inspect', '--format', '{{.Id}}', tag)
            if not re.fullmatch(r'sha256:[a-f0-9]{64}', compiler_image):
                raise RuntimeError('Compiler image identity refused.')
    except BaseException as error:
        failure = error
    # Attempt both independent cleanup stages even if one fails, and retain the original error.
    errors = []
    if attempted:
        try:
            cleanup_container(native_image, name, ownership, container is not None,
                              container if container and re.fullmatch(r'[a-f0-9]{64}', container) else None)
        except BaseException as error:
            errors.append(error)
    try:
        cleanup_reference(native_image, reference, tag_acknowledged)
    except BaseException as error:
        errors.append(error)
    if errors:
        raise BaseExceptionGroup('Compiler image preparation or cleanup failed.', ([failure] if failure else []) + errors)
    if failure is not None:
        raise failure
    print('Compiler image:', compiler_image)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native-image', required=True, help='Exact sha256 native Docker image ID')
    parser.add_argument('--tag', required=True, help='Resulting compiler image tag')
    parser.add_argument('--work-root', required=True, type=Path, help='Existing disposable-work directory on an approved volume')
    parser.add_argument('--storage-profile', help='Absolute path to Linux mount expectations (or set BRAID_STORAGE_PROFILE)')
    parser.add_argument('--build-docker-config', help='DOCKER_CONFIG override for the build only, not extraction or cleanup')
    args = parser.parse_args()
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', args.native_image):
        parser.error('An exact native image ID is required.')
    prepare_image(args.native_image, args.tag, args.work_root, args.storage_profile, args.build_docker_config)


if __name__ == '__main__':
    main()
