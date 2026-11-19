"""Storage admission for local Linux build and browser qualification tools."""
import json
import os
from pathlib import Path
import re
import stat
import subprocess


def read_profile(filename):
    if not filename or not Path(filename).is_absolute():
        raise ValueError('Supply an absolute operator-owned storage profile path.')
    path = Path(filename)
    if path.resolve(strict=True) != path or not path.is_file():
        raise ValueError('Expected a canonical storage profile file.')
    trusted = {0, os.geteuid()}
    for item in (*reversed(path.parents), path):
        info = item.lstat()
        if (info.st_uid not in trusted or info.st_mode & 0o022 or
                stat.S_ISLNK(info.st_mode)):
            raise ValueError('The storage profile and its ancestry must be operator-controlled.')
    profile = json.loads(path.read_text(encoding='utf-8'))
    if not isinstance(profile, dict):
        raise ValueError('Expected a JSON object for the storage profile.')
    return profile


def mount_at(path):
    rows = json.loads(subprocess.check_output([
        'findmnt', '-J', '-T', str(path), '-o', 'SOURCE,TARGET,FSTYPE,UUID,FSROOT,OPTIONS',
    ]))['filesystems']
    if len(rows) != 1:
        raise ValueError('Expected one containing mount.')
    return rows[0]


def require_volume(path, profile, *, wsl):
    mount = mount_at(path)
    options = set((mount.get('options') or '').split(','))
    required = profile.get('requiredOptions')
    if (not isinstance(required, list) or not required or
            any(not isinstance(o, str) or not o or ',' in o for o in required) or
            not set(required) <= options or 'rw' not in options or 'ro' in options or
            (wsl and not any(re.fullmatch(r'compress(?:-force)?=(?:zstd|zlib|lzo)(?::\d+)?', o) for o in required))):
        raise ValueError('Required writable Btrfs mount options are absent or invalid.')
    if (mount.get('fstype') != 'btrfs' or
            not re.fullmatch(r'[\da-fA-F]{8}(?:-[\da-fA-F]{4}){3}-[\da-fA-F]{12}', profile.get('uuid') or '') or
            any(not isinstance(profile.get(k), str) or not profile[k].startswith('/') for k in ('target', 'source', 'fsroot')) or
            any(mount.get(k) != profile[k] for k in ('target', 'source', 'uuid', 'fsroot'))):
        raise ValueError('The path is not on the configured Btrfs volume and subvolume.')
    return mount


def require_host_reserve(profile):
    host = profile.get('windowsHostMount')
    if not isinstance(host, str) or not host.startswith('/'):
        raise ValueError('Supply the Windows backing drive mount for WSL.')
    mount = mount_at(host)
    if mount.get('target') != host or mount.get('fstype') not in ('9p', 'drvfs'):
        raise ValueError('The configured Windows host drive is not mounted.')
    space = os.statvfs(host)
    if space.f_bavail * space.f_frsize < 25 * 2**30:
        raise ValueError('The Windows host free-space floor is not met.')


def admitted_path(path, profile, *, directory=True):
    """Fixture paths must exist, be canonical, and be on the admitted mount."""
    path = Path(path)
    if (not path.is_absolute() or path != path.resolve(strict=True) or
            not (path.is_dir() if directory else path.is_file())):
        raise ValueError('Expected an existing canonical fixture path.')
    require_volume(path, profile, wsl=True)
    return path
