import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

import build_image


class StorageProfileTests(unittest.TestCase):
    def test_load_requires_trusted_profile_and_ancestry(self):
        storage = build_image.storage
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder).resolve() / 'storage.json'
            path.write_text('{"requiredOptions": ["rw"]}', encoding='utf-8')
            with patch.object(storage.os, 'geteuid', return_value=42, create=True), \
                    patch.object(Path, 'lstat', autospec=True) as lstat:
                lstat.side_effect = lambda p: SimpleNamespace(st_uid=42, st_mode=0o100600)
                self.assertEqual(storage.read_profile(str(path)), {'requiredOptions': ['rw']})
                for uid, mode in [(7, 0o100600), (42, 0o100620), (42, 0o100602)]:
                    for bad in (path, path.parent):
                        lstat.side_effect = lambda p: SimpleNamespace(st_uid=uid if p == bad else 42,
                                                                      st_mode=mode if p == bad else 0o100600)
                        with self.subTest(uid=uid, mode=mode, bad=bad), self.assertRaises(ValueError):
                            storage.read_profile(str(path))


class AdmissionTests(unittest.TestCase):
    volume = {'target': '/fixture', 'source': '/dev/example', 'fstype': 'btrfs',
              'uuid': '00000000-0000-4000-8000-000000000001', 'fsroot': '/fixture-volume',
              'options': 'rw,relatime,compress=zstd:3'}

    def setUp(self):
        self.root = MagicMock(spec=Path)
        self.root.is_absolute.return_value = True
        self.root.is_dir.return_value = True
        self.root.resolve.return_value = self.root
        self.root.__str__.return_value = '/approved/work'
        self.mount = self.enterContext(patch.object(build_image.subprocess, 'check_output'))
        self.floor = self.enterContext(patch.object(build_image.os, 'statvfs', create=True))
        self.floor.return_value = SimpleNamespace(f_bavail=25 * 2**30, f_frsize=1)
        self.enterContext(patch.object(build_image.sys, 'platform', 'linux'))
        self.kernel = self.enterContext(patch.object(build_image.platform, 'release', return_value='native'))
        self.profile = {k: self.volume[k] for k in ('target', 'source', 'uuid', 'fsroot')}
        self.profile['requiredOptions'] = ['compress=zstd:3']
        self.load = self.enterContext(patch.object(build_image.storage, 'read_profile', return_value=self.profile))

    def admit(self, mount):
        self.mount.return_value = json.dumps({'filesystems': [mount]})
        return build_image.admit_work_root(self.root)

    def test_expected_native_volume(self):
        self.assertIs(self.admit(self.volume), self.root)
        self.floor.assert_not_called()
        self.root.resolve.assert_called_with(strict=True)
        self.assertEqual(self.mount.call_args.args[0], [
            'findmnt', '-J', '-T', '/approved/work', '-o', 'SOURCE,TARGET,FSTYPE,UUID,FSROOT,OPTIONS'])

    def wsl(self, host_type='9p', host_target='/host'):
        self.kernel.return_value = 'microsoft-standard-WSL2'
        self.profile['windowsHostMount'] = '/host'
        self.mount.side_effect = [json.dumps({'filesystems': [mount]}) for mount in
                                 [self.volume, {'target': host_target, 'fstype': host_type}]]
        return build_image.admit_work_root(self.root)

    def test_wsl_checks_actual_windows_mount_and_exact_floor(self):
        self.assertIs(self.wsl(), self.root)
        self.floor.assert_called_once_with('/host')
        for fstype, target in [('ext4', '/host'), ('9p', '/')]:
            with self.subTest(fstype=fstype, target=target), self.assertRaises(ValueError):
                self.wsl(fstype, target)

    def test_wsl_windows_floor_below_threshold_or_unavailable(self):
        for failure in ('low', 'missing'):
            with self.subTest(failure=failure):
                self.floor.return_value = SimpleNamespace(f_bavail=25 * 2**30 - 1, f_frsize=1)
                self.floor.side_effect = OSError('missing Windows host volume') if failure == 'missing' else None
                with self.assertRaises((ValueError, OSError)):
                    self.wsl()

    def test_wsl_floor_uses_available_blocks_times_fragment_size(self):
        self.floor.return_value = SimpleNamespace(f_bavail=25 * 2**18, f_frsize=4096)
        self.assertIs(self.wsl(), self.root)

    def test_each_volume_guard_is_required(self):
        for field, value in [('source', '/dev/other'), ('target', '/other'), ('fstype', 'ext4'), ('uuid', 'foreign'),
                             ('fsroot', '/'), ('options', 'ro,compress=zstd:3'),
                             ('options', 'rw,compress=zstd:30'), ('options', 'rw'),
                             ('options', 'rw,ro,compress=zstd:3')]:
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                self.admit(dict(self.volume, **{field: value}))
        self.floor.assert_not_called()

    def test_wsl_cannot_omit_host_or_compression_settings(self):
        self.kernel.return_value = 'Microsoft-standard'
        with self.assertRaises(ValueError):
            self.admit(self.volume)
        self.profile.update(windowsHostMount='/host', requiredOptions=['rw'])
        with self.assertRaises(ValueError):
            self.admit(self.volume)
        self.floor.assert_not_called()

    def test_missing_expectations_do_not_adopt_observed_mount(self):
        for field in self.profile:
            self.load.return_value = {k: v for k, v in self.profile.items() if k != field}
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.admit(self.volume)

    def test_missing_relative_or_non_directory_root_refused_before_mount_lookup(self):
        for absolute, directory in [(False, True), (True, False), (False, False)]:
            with self.subTest(absolute=absolute, directory=directory):
                self.root.is_absolute.return_value = absolute
                self.root.is_dir.return_value = directory
                with self.assertRaises(RuntimeError):
                    build_image.admit_work_root(self.root)
        self.mount.assert_not_called()

    def test_absent_or_malformed_mount_and_findmnt_failure_refused(self):
        for result in ('{"filesystems": []}', '{}', 'not json', '{"filesystems": [{}]}'):
            with self.subTest(result=result):
                self.mount.return_value = result
                with self.assertRaises((RuntimeError, ValueError, IndexError, KeyError)):
                    build_image.admit_work_root(self.root)
        self.mount.side_effect = subprocess.CalledProcessError(1, ['findmnt'])
        with self.assertRaises(subprocess.CalledProcessError):
            build_image.admit_work_root(self.root)

    def test_resolved_root_is_what_mount_lookup_admits(self):
        real = Path('/fixture/build/real-root')
        self.root.resolve.return_value = real
        self.assertEqual(self.admit(self.volume), real)
        self.assertEqual(self.mount.call_args.args[0][3], str(real))

    def test_native_windows_keeps_existing_path_admission_without_linux_commands(self):
        with patch.object(build_image.sys, 'platform', 'win32'):
            self.assertIs(build_image.admit_work_root(self.root), self.root)
            with self.assertRaises(RuntimeError):
                build_image.admit_work_root(self.root, '/fixture/profile.json')
        self.mount.assert_not_called()
        self.floor.assert_not_called()


class CleanupTests(unittest.TestCase):
    image = 'sha256:' + 'a' * 64
    identity = 'b' * 64

    def metadata(self, owner='owner'):
        return {'Image': self.image, 'Id': self.identity, 'Name': '/owned',
                'Config': {'Labels': {'io.braid.compiler.source-owner': owner}}}

    def test_lost_create_acknowledgement_is_recovered_by_independent_ownership(self):
        inspect = subprocess.CompletedProcess([], 0, json.dumps([self.metadata()]), '')
        with patch.object(build_image.subprocess, 'run', side_effect=[inspect, subprocess.CompletedProcess([], 0)]) as run:
            build_image.cleanup_container(self.image, 'owned', 'owner', False)
            self.assertEqual(['docker', 'container', 'rm', self.identity], run.call_args_list[1].args[0])

    def test_foreign_label_is_never_removed(self):
        inspect = subprocess.CompletedProcess([], 0, json.dumps([self.metadata('foreign')]), '')
        with patch.object(build_image.subprocess, 'run', return_value=inspect) as run:
            with self.assertRaises(RuntimeError):
                build_image.cleanup_container(self.image, 'owned', 'owner', False)
            self.assertEqual(1, run.call_count)

    def test_acknowledged_container_without_verified_cleanup_is_not_success(self):
        inspect = subprocess.CompletedProcess([], 1, '', 'closed failure')
        with patch.object(build_image.subprocess, 'run', return_value=inspect):
            with self.assertRaises(RuntimeError):
                build_image.cleanup_container(self.image, 'owned', 'owner', True)

    def test_unacknowledged_create_with_daemon_failure_is_not_absence(self):
        inspect = subprocess.CompletedProcess([], 1, '', 'daemon unavailable')
        with patch.object(build_image.subprocess, 'run', return_value=inspect):
            with self.assertRaises(RuntimeError):
                build_image.cleanup_container(self.image, 'owned', 'owner', False)

    def test_unacknowledged_create_with_confirmed_absence_is_safe(self):
        inspect = subprocess.CompletedProcess([], 1, '', 'Error response from daemon: No such container: owned\n')
        with patch.object(build_image.subprocess, 'run', return_value=inspect) as run:
            build_image.cleanup_container(self.image, 'owned', 'owner', False)
            self.assertEqual(run.call_count, 1)

    def test_foreign_metadata_variants_are_never_removed(self):
        for change in ({'Image': 'sha256:' + 'c' * 64}, {'Name': '/foreign'}, {'Id': 'bad'},
                       {'Config': {'Labels': None}}, {'Id': 'c' * 64}):
            with self.subTest(change=change):
                metadata = dict(self.metadata(), **change)
                inspect = subprocess.CompletedProcess([], 0, json.dumps([metadata]), '')
                with patch.object(build_image.subprocess, 'run', return_value=inspect) as run:
                    with self.assertRaises(RuntimeError):
                        build_image.cleanup_container(self.image, 'owned', 'owner', True, self.identity)
                    self.assertEqual(run.call_count, 1)

    def test_reference_cleanup_distinguishes_lost_tag_ack_absence_and_unknown_state(self):
        for acknowledged, stderr, refused in [
                (False, 'Error response from daemon: No such image: owned', False),
                (True, 'Error response from daemon: No such image: owned', True),
                (False, 'daemon unavailable', True)]:
            with self.subTest(acknowledged=acknowledged, stderr=stderr):
                inspect = subprocess.CompletedProcess([], 1, '', stderr)
                with patch.object(build_image.subprocess, 'run', return_value=inspect) as run:
                    if refused:
                        with self.assertRaises(RuntimeError):
                            build_image.cleanup_reference(self.image, 'owned', acknowledged)
                    else:
                        build_image.cleanup_reference(self.image, 'owned', acknowledged)
                    self.assertEqual(run.call_count, 1)


class FakeDocker:
    """All Docker interactions are in-memory; no Docker CLI or daemon is contacted."""
    image = 'sha256:' + 'a' * 64
    compiler = 'sha256:' + 'c' * 64
    identity = 'b' * 64
    foreign_image = 'sha256:' + 'f' * 64

    def __init__(self):
        self.calls = []
        self.images = {'native:original': self.image, 'foreign:tag': self.foreign_image}
        self.containers = {'foreign': {'Id': 'f' * 64}}
        self.foreign_container = self.containers['foreign'].copy()
        self.reference = None
        self.name = None
        self.metadata_change = {}
        self.create_result = self.identity
        self.interrupt = None
        self.event_error = None
        self.change_reference = None
        self.reference_checks = 0
        self.container_rm_error = None
        self.tag_rm_error = None
        self.cleanup_inspect_error = None

    def event(self, event):
        if self.interrupt == event:
            if self.event_error is not None:
                raise self.event_error
            raise KeyboardInterrupt(event + ' interrupted')

    def invoke(self, command, **kwargs):
        self.calls.append((command, kwargs))
        assert command[0] == 'docker', 'Unexpected external command'
        cmd = command[1:]
        stdout = ''
        if cmd[0] == 'tag':
            self.reference = cmd[2]
            self.event('tag-before')
            self.images[self.reference] = cmd[1]
            self.event('tag')
        elif cmd[:2] == ['image', 'inspect']:
            image = cmd[-1]
            if image == self.image:
                stdout = self.image
            elif image not in self.images:
                return subprocess.CompletedProcess(command, 1, '', f'Error response from daemon: No such image: {image}')
            else:
                if image == self.reference and kwargs.get('capture_output') is not True:
                    self.reference_checks += 1
                    if self.change_reference == self.reference_checks:
                        self.images[image] = self.foreign_image
                stdout = self.images[image]
        elif cmd[:2] == ['container', 'create']:
            self.name = cmd[cmd.index('--name') + 1]
            owner = cmd[cmd.index('--label') + 1].split('=', 1)[1]
            self.event('create-before')
            if self.name in self.containers:
                raise subprocess.CalledProcessError(1, command, stderr='Name already in use')
            self.containers[self.name] = dict({
                'Image': self.image, 'Id': self.identity, 'Name': '/' + self.name,
                'Config': {'Labels': {build_image.OWNER_LABEL: owner}},
            }, **self.metadata_change)
            self.event('create')
            stdout = self.create_result
        elif cmd[:2] == ['container', 'inspect']:
            if kwargs.get('capture_output') and self.cleanup_inspect_error:
                return subprocess.CompletedProcess(command, 1, '', self.cleanup_inspect_error)
            container = self.containers.get(cmd[-1])
            if container is None:
                container = next((c for c in self.containers.values() if c['Id'] == cmd[-1]), None)
            if container is None:
                return subprocess.CompletedProcess(command, 1, '', f'Error response from daemon: No such container: {cmd[-1]}')
            stdout = json.dumps([container])
        elif cmd[0] == 'cp':
            self.event('extract')
        elif cmd[0] == 'build':
            self.event('build')
            self.images[cmd[cmd.index('--tag') + 1]] = self.compiler
        elif cmd[:2] == ['container', 'rm']:
            if self.container_rm_error:
                raise self.container_rm_error
            owned = next(name for name, c in self.containers.items() if c['Id'] == cmd[-1])
            del self.containers[owned]
        elif cmd[:2] == ['image', 'rm']:
            if self.tag_rm_error:
                raise self.tag_rm_error
            del self.images[cmd[-1]]
        else:
            raise AssertionError('Unexpected Docker command: ' + repr(command))
        return subprocess.CompletedProcess(command, 0, stdout + '\n', '')

    def output(self, command, **kwargs):
        result = self.invoke(command, **kwargs)
        result.check_returncode()
        return result.stdout


class PreparationTests(unittest.TestCase):
    binary_bytes = b'\x7fELFmock-native-worker'

    def setUp(self):
        self.docker = FakeDocker()
        self.run = self.enterContext(patch.object(build_image.subprocess, 'run', side_effect=self.docker.invoke))
        self.output = self.enterContext(patch.object(build_image.subprocess, 'check_output', side_effect=self.docker.output))
        self.admission = self.enterContext(patch.object(build_image, 'admit_work_root', return_value=Path('/approved/work')))
        self.temp = self.enterContext(patch.object(build_image.tempfile, 'TemporaryDirectory'))
        self.temp.return_value.__enter__.return_value = '/approved/work/unique-temp'
        self.binary = MagicMock(spec=Path)
        self.binary.is_symlink.return_value = False
        self.binary.is_file.return_value = True
        self.binary.stat.return_value = SimpleNamespace(st_size=len(self.binary_bytes))
        self.binary.open.side_effect = lambda *args: io.BytesIO(self.binary_bytes)
        directory = MagicMock(spec=Path)
        directory.__truediv__.return_value = self.binary
        real_path = Path
        self.enterContext(patch.object(build_image, 'Path', side_effect=lambda value:
                          directory if value == '/approved/work/unique-temp' else real_path(value)))
        self.print = self.enterContext(patch('builtins.print'))

    def prepare(self, **kwargs):
        build_image.prepare_image(self.docker.image, 'compiler:result', Path('/requested/work'), **kwargs)

    def commands(self, *prefix):
        return [(cmd, options) for cmd, options in self.docker.calls if cmd[:len(prefix)] == list(prefix)]

    def assert_foreign_untouched(self):
        self.assertEqual(self.docker.images['foreign:tag'], self.docker.foreign_image)
        self.assertEqual(self.docker.images['native:original'], self.docker.image)
        self.assertEqual(self.docker.containers['foreign'], self.docker.foreign_container)

    def assert_cleaned(self):
        self.assertNotIn(self.docker.reference, self.docker.images)
        self.assertEqual(list(self.docker.containers), ['foreign'])
        self.assert_foreign_untouched()

    def test_exact_id_digest_owned_cleanup_and_before_after_checks(self):
        self.prepare(profile_path='/fixture/profile.json')
        self.admission.assert_called_once_with(Path('/requested/work'), '/fixture/profile.json')
        self.temp.assert_called_once_with(prefix='native-source-', dir=Path('/approved/work'))
        tag, = self.commands('docker', 'tag')
        self.assertEqual(tag[0][2], self.docker.image)
        create, = self.commands('docker', 'container', 'create')
        self.assertEqual(create[0][-1], self.docker.image)
        self.assertIn(build_image.OWNER_LABEL + '=', ' '.join(create[0]))
        self.assertEqual(create[0][create[0].index('--network') + 1], 'none')
        cp, = self.commands('docker', 'cp')
        self.assertEqual(cp[0][2], self.docker.identity + ':/ferrule-host')
        build, = self.commands('docker', 'build')
        self.assertIn('BRAID_NATIVE_IMAGE=' + self.docker.reference, build[0])
        self.assertIn('BRAID_NATIVE_BINARY_SHA256=' + hashlib.sha256(self.binary_bytes).hexdigest(), build[0])
        self.assertEqual(build[0][-3:], ['--file', 'engines/build-worker/Dockerfile', '.'])
        self.assertEqual(build[1]['cwd'], Path(build_image.__file__).resolve().parents[2])
        self.assertEqual(self.docker.reference_checks, 2)
        built = self.docker.calls.index(build)
        reference_checks = [index for index, (cmd, opts) in enumerate(self.docker.calls)
                            if cmd[:3] == ['docker', 'image', 'inspect'] and cmd[-1] == self.docker.reference
                            and not opts.get('capture_output')]
        self.assertEqual(len(reference_checks), 2)
        self.assertLess(reference_checks[0], built)
        self.assertLess(built, reference_checks[1])
        self.assertEqual(self.commands('docker', 'container', 'rm')[0][0][-1], self.docker.identity)
        self.print.assert_called_once_with('Compiler image:', self.docker.compiler)
        self.assert_cleaned()

    def test_buildkit_and_private_build_config_forward_only_to_build(self):
        with patch.dict(os.environ, {'DOCKER_CONFIG': '/normal/config', 'DOCKER_BUILDKIT': '1'}):
            self.prepare(build_docker_config='/private/build-config')
            self.assertEqual(os.environ['DOCKER_CONFIG'], '/normal/config')
        build, = self.commands('docker', 'build')
        self.assertEqual(build[1]['env']['DOCKER_CONFIG'], str(Path('/private/build-config').absolute()))
        self.assertEqual(build[1]['env']['DOCKER_BUILDKIT'], '1')
        self.assertTrue(all('env' not in options for cmd, options in self.docker.calls if cmd[:2] != ['docker', 'build']))

    def test_default_build_environment_is_preserved(self):
        with patch.dict(os.environ, {'DOCKER_CONFIG': '/normal/config', 'DOCKER_BUILDKIT': '0'}):
            self.prepare()
        build, = self.commands('docker', 'build')
        self.assertEqual(build[1]['env']['DOCKER_CONFIG'], str(Path('/normal/config').absolute()))
        self.assertEqual(build[1]['env']['DOCKER_BUILDKIT'], '0')

    def test_relative_build_config_remains_relative_to_the_caller_not_the_checkout(self):
        caller = Path('/separate/caller')
        with patch.object(build_image.os, 'getcwd', return_value=str(caller)), patch.dict(
                os.environ, {'DOCKER_CONFIG': '/normal/config'}):
            self.prepare(build_docker_config='./private-docker')
            self.assertEqual(os.environ['DOCKER_CONFIG'], '/normal/config')
        build, = self.commands('docker', 'build')
        self.assertEqual(build[1]['env']['DOCKER_CONFIG'], str(caller / 'private-docker'))
        self.assertNotEqual(build[1]['cwd'], caller)
        self.assertTrue(all('env' not in options for cmd, options in self.docker.calls
                            if cmd[:2] != ['docker', 'build']))

    def test_inherited_relative_config_keeps_the_callers_directory_without_mutating_environment(self):
        caller = Path('/separate/caller')
        with patch.object(build_image.os, 'getcwd', return_value=str(caller)), patch.dict(
                os.environ, {'DOCKER_CONFIG': './inherited-docker'}):
            self.prepare()
            self.assertEqual(os.environ['DOCKER_CONFIG'], './inherited-docker')
        build, = self.commands('docker', 'build')
        self.assertEqual(build[1]['env']['DOCKER_CONFIG'], str(caller / 'inherited-docker'))
        self.assertNotEqual(build[1]['cwd'], caller)
        self.assertTrue(all('env' not in options for cmd, options in self.docker.calls
                            if cmd[:2] != ['docker', 'build']))

    def test_interrupted_tag_create_extract_or_build_still_cleans_exact_owned_resources(self):
        for event in ('tag-before', 'tag', 'create-before', 'create', 'extract', 'build'):
            with self.subTest(event=event):
                self.docker.interrupt = event
                with self.assertRaises(KeyboardInterrupt):
                    self.prepare()
                self.assert_cleaned()
                self.print.assert_not_called()

    def test_failed_tag_create_extract_and_build_preserve_original_exit_error(self):
        for event in ('tag', 'create', 'extract', 'build'):
            with self.subTest(event=event):
                self.docker.interrupt = event
                self.docker.event_error = subprocess.CalledProcessError(37, ['docker', event])
                with self.assertRaises(subprocess.CalledProcessError) as raised:
                    self.prepare()
                self.assertIs(raised.exception, self.docker.event_error)
                self.assertEqual(raised.exception.returncode, 37)
                self.assert_cleaned()

    def test_temporary_directory_creation_and_disposal_failures_still_clean_docker_resources(self):
        for stage in ('enter', 'exit'):
            with self.subTest(stage=stage):
                failure = OSError('temporary directory ' + stage + ' failed')
                self.temp.return_value.__enter__.side_effect = failure if stage == 'enter' else None
                self.temp.return_value.__exit__.side_effect = failure if stage == 'exit' else None
                with self.assertRaises(OSError) as raised:
                    self.prepare()
                self.assertIs(raised.exception, failure)
                self.assert_cleaned()
                self.print.assert_not_called()

    def test_preexisting_reference_is_never_overwritten_even_if_it_has_the_same_image(self):
        reference = 'braid-compiler-source:' + self.docker.image[7:] + '-' + 'd' * 32
        for image in (self.docker.foreign_image, self.docker.image):
            with self.subTest(image=image):
                self.docker.images[reference] = image
                with patch.object(build_image.uuid, 'uuid4', return_value=SimpleNamespace(hex='d' * 32)):
                    with self.assertRaisesRegex(RuntimeError, 'tagging refused'):
                        self.prepare()
                self.assertEqual(self.docker.images[reference], image)
                self.assertFalse(self.commands('docker', 'tag'))
                self.assertFalse(self.commands('docker', 'image', 'rm'))
                self.assert_foreign_untouched()

    def test_preexisting_foreign_container_with_generated_name_is_never_removed(self):
        name = 'braid-build-source-' + 'd' * 32
        foreign = {'Id': 'e' * 64, 'Image': self.docker.foreign_image, 'Name': '/' + name,
                   'Config': {'Labels': {build_image.OWNER_LABEL: 'foreign'}}}
        self.docker.containers[name] = foreign.copy()
        with patch.object(build_image.uuid, 'uuid4', return_value=SimpleNamespace(hex='d' * 32)):
            with self.assertRaises(BaseExceptionGroup):
                self.prepare()
        self.assertEqual(self.docker.containers[name], foreign)
        self.assertFalse(self.commands('docker', 'container', 'rm'))
        self.assertNotIn(self.docker.reference, self.docker.images)
        self.assert_foreign_untouched()

    def test_compiler_id_must_be_exact_and_success_not_reported_until_cleanup(self):
        self.docker.compiler = 'compiler:mutable'
        with self.assertRaisesRegex(RuntimeError, 'Compiler image identity refused'):
            self.prepare()
        self.assert_cleaned()
        self.print.assert_not_called()

    def test_before_or_after_reference_replacement_is_refused_and_foreign_tag_retained(self):
        for check in (1, 2):
            with self.subTest(check=check):
                self.docker.change_reference = check
                self.docker.reference_checks = 0
                self.docker.calls.clear()
                with self.assertRaises(BaseExceptionGroup) as raised:
                    self.prepare()
                self.assertEqual(len(raised.exception.exceptions), 2)
                self.assertIn('reference changed', str(raised.exception.exceptions[0]))
                self.assertIn('ownership mismatch', str(raised.exception.exceptions[1]))
                self.assertEqual(len(self.commands('docker', 'build')), check - 1)
                self.assertEqual(self.docker.images[self.docker.reference], self.docker.foreign_image)
                self.assertFalse(self.commands('docker', 'image', 'rm'))
                self.assertEqual(list(self.docker.containers), ['foreign'])
                self.assert_foreign_untouched()

    def test_foreign_extraction_container_never_copied_or_removed(self):
        self.docker.metadata_change = {'Config': {'Labels': {build_image.OWNER_LABEL: 'foreign'}}}
        with self.assertRaises(BaseExceptionGroup):
            self.prepare()
        self.assertFalse(self.commands('docker', 'cp'))
        self.assertFalse(self.commands('docker', 'container', 'rm'))
        self.assertNotIn(self.docker.reference, self.docker.images)
        self.assertIn(self.docker.name, self.docker.containers)
        self.assert_foreign_untouched()

    def test_create_response_must_be_exact_container_id(self):
        self.docker.create_result = 'foreign'
        with self.assertRaisesRegex(RuntimeError, 'container ID refused'):
            self.prepare()
        self.assertFalse(self.commands('docker', 'cp'))
        self.assert_cleaned()

    def test_inspected_id_must_match_create_response(self):
        self.docker.create_result = 'e' * 64
        with self.assertRaises(BaseExceptionGroup):
            self.prepare()
        self.assertFalse(self.commands('docker', 'cp'))
        self.assertFalse(self.commands('docker', 'container', 'rm'))
        self.assertNotIn(self.docker.reference, self.docker.images)
        self.assert_foreign_untouched()

    def test_symlink_nonfile_oversized_and_non_elf_binary_refused(self):
        cases = [('symlink', True), ('nonfile', False), ('oversized', 256 * 2**20 + 1), ('header', b'fake')]
        for case, value in cases:
            with self.subTest(case=case):
                self.binary.is_symlink.return_value = value if case == 'symlink' else False
                self.binary.is_file.return_value = value if case == 'nonfile' else True
                self.binary.stat.return_value = SimpleNamespace(st_size=value if case == 'oversized' else 100)
                self.binary.open.side_effect = lambda *args: io.BytesIO(b'fake' if case == 'header' else self.binary_bytes)
                with self.assertRaisesRegex(RuntimeError, 'binary refused'):
                    self.prepare()
                self.assertFalse(self.commands('docker', 'build'))
                self.assert_cleaned()

    def test_both_cleanup_failures_and_original_interruption_are_retained(self):
        self.docker.interrupt = 'build'
        first = subprocess.CalledProcessError(11, ['docker', 'container', 'rm'])
        second = subprocess.CalledProcessError(12, ['docker', 'image', 'rm'])
        self.docker.container_rm_error = first
        self.docker.tag_rm_error = second
        with self.assertRaises(BaseExceptionGroup) as raised:
            self.prepare()
        errors = raised.exception.exceptions
        self.assertIsInstance(errors[0], KeyboardInterrupt)
        self.assertIs(errors[1], first)
        self.assertIs(errors[2], second)
        self.assertEqual(len(self.commands('docker', 'container', 'rm')), 1)
        self.assertEqual(len(self.commands('docker', 'image', 'rm')), 1)
        self.print.assert_not_called()
        self.assert_foreign_untouched()

    def test_cleanup_failure_after_success_is_still_failure_and_other_stage_runs(self):
        for stage in ('container', 'tag'):
            with self.subTest(stage=stage):
                self.docker.calls.clear()
                self.docker.containers = {'foreign': self.docker.foreign_container.copy()}
                self.docker.container_rm_error = RuntimeError('container cleanup failed') if stage == 'container' else None
                self.docker.tag_rm_error = RuntimeError('tag cleanup failed') if stage == 'tag' else None
                with self.assertRaises(BaseExceptionGroup) as raised:
                    self.prepare()
                self.assertEqual(len(raised.exception.exceptions), 1)
                self.assertTrue(self.commands('docker', 'container', 'rm'))
                self.assertTrue(self.commands('docker', 'image', 'rm'))
                self.print.assert_not_called()

    def test_lost_create_ack_and_unknown_cleanup_state_is_failure_not_success(self):
        self.docker.interrupt = 'create'
        self.docker.cleanup_inspect_error = 'daemon unavailable'
        with self.assertRaises(BaseExceptionGroup) as raised:
            self.prepare()
        self.assertEqual(len(raised.exception.exceptions), 2)
        self.assertIsInstance(raised.exception.exceptions[0], KeyboardInterrupt)
        self.assertIn('could not be verified', str(raised.exception.exceptions[1]))
        self.assertTrue(self.commands('docker', 'image', 'rm'))

    def test_invalid_source_id_and_unapproved_root_never_touch_docker(self):
        with self.assertRaisesRegex(RuntimeError, 'exact native image ID'):
            build_image.prepare_image('native:mutable', 'compiler:result', Path('/requested/work'))
        self.admission.side_effect = RuntimeError('Unapproved root')
        with self.assertRaisesRegex(RuntimeError, 'Unapproved root'):
            self.prepare()
        self.assertFalse(self.docker.calls)

    def test_native_id_mismatch_refused_before_temporary_tag(self):
        self.output.return_value = self.docker.foreign_image
        self.output.side_effect = None
        with self.assertRaisesRegex(RuntimeError, 'Native image identity mismatch'):
            self.prepare()
        self.run.assert_not_called()


class EntrypointTests(unittest.TestCase):
    def test_original_cli_and_explicit_storage_and_build_config_options(self):
        common = ['build_image.py', '--native-image', FakeDocker.image, '--tag', 'compiler:result', '--work-root', '/approved/work']
        for options, policy, config in [([], None, None),
                                        (['--storage-profile', '/fixture/profile.json', '--build-docker-config', '/build/config'],
                                         '/fixture/profile.json', '/build/config')]:
            with self.subTest(options=options), patch.object(build_image.sys, 'argv', common + options), \
                    patch.object(build_image, 'prepare_image') as prepare:
                build_image.main()
                prepare.assert_called_once_with(FakeDocker.image, 'compiler:result', Path('/approved/work'), policy, config)

    def test_dev_up_routes_compiler_build_through_helper_without_duplicate_extraction(self):
        source = (Path(__file__).resolve().parents[2] / 'deploy/local/dev-up.sh').read_text()
        image_block = source.split('# ---- Ferrule images:', 1)[1].split('# ---- PostgreSQL:', 1)[0]
        self.assertIn('python3 "$source_root/tools/build-worker/build_image.py"', image_block)
        self.assertIn('--native-image "$native_id" --tag "$compiler_tag" --work-root "$dev_home/tmp"', image_block)
        self.assertIn('compiler_build_options+=(--build-docker-config "$BRAID_DEV_BUILD_DOCKER_CONFIG")', image_block)
        self.assertIn('DOCKER_BUILDKIT=1 python3', image_block)
        self.assertIn('"${compiler_build_options[@]}" >>"$build_log" 2>&1 || fail', image_block)
        self.assertIn('build_docker build --progress=plain --tag "$native_tag"', image_block)
        self.assertIn('|| $build_key == *-dirty', image_block)
        self.assertNotIn('docker cp', image_block)
        self.assertNotIn('docker container create', image_block)
        self.assertNotIn('BRAID_NATIVE_BINARY_SHA256=', image_block)


if __name__ == '__main__':
    unittest.main()
