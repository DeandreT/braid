"""Prepare a fresh Linux source export and ordinary publications for the real browser fixture."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import qualification_storage as storage


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def private_json(path, value):
    with path.open('x', encoding='utf-8') as stream:
        os.chmod(path, 0o600)
        json.dump(value, stream, separators=(',', ':'))
        stream.write('\n')


def bound(path):
    return {'path': str(path), 'sha256': sha(path)}


def check_reusable_inputs(previous, current, previous_package, current_package, previous_web, current_web):
    observer_prefixes = ('tools/browser-fixture/', 'tools/regression-browser/', 'tools/build-browser/', 'apps/web/tests/')
    def runtime(rows):
        result = {}
        seen = set()
        for row in rows:
            path = row['path']
            if path in seen:
                raise ValueError('Duplicate source path.')
            seen.add(path)
            if (path == 'package.json' or path.startswith(observer_prefixes)
                    or (path.startswith('apps/web/playwright.') and path.endswith('.config.ts'))):
                continue
            result[path] = (row['sha256'], row['mode'])
        return result
    if runtime(previous) != runtime(current):
        raise ValueError('Runtime source inputs changed; fresh publications are required.')
    without_scripts = lambda value: {key: item for key, item in value.items() if key != 'scripts'}
    if without_scripts(previous_package) != without_scripts(current_package):
        raise ValueError('Package inputs changed; fresh publications are required.')
    if not current_web or previous_web != current_web:
        raise ValueError('Emitted web bytes changed; fresh publications are required.')


def main():
    data = json.loads(Path(sys.argv[1]).read_text())
    scenario = data.get('scenario', 'regression')
    assert scenario in ('regression', 'build')
    profile = storage.read_profile(data['storageProfile'])
    storage.require_host_reserve(profile)
    name = 'braid-regression-' + str(uuid.UUID(data['fixtureId']))
    base = storage.admitted_path(profile['workRoot'], profile) / name
    private = storage.admitted_path(profile['dataRoot'], profile) / name
    tooling = storage.admitted_path(profile['cacheRoot'], profile)
    dotnet = storage.admitted_path(profile['dotnet'], profile, directory=False)
    bun = storage.admitted_path(profile['bun'], profile, directory=False)
    storage.admitted_path(profile['dockerRoot'], profile)
    if base == private:
        raise ValueError('Use separate work and private data roots.')
    for dirname in ('browser-home', 'nuget', 'nuget-http'):
        candidate = tooling / dirname
        if candidate.exists() or candidate.is_symlink():
            storage.admitted_path(candidate, profile)
    assert not base.exists() and not private.exists()
    base.mkdir(mode=0o700); private.mkdir(mode=0o700)
    source = base / 'source'; source.mkdir(mode=0o755)
    with tarfile.open(data['archive']) as archive:
        archive.extractall(source, filter='data')
    manifest = data['sourceManifest']
    for item in manifest:
        path = source / item['path']
        assert path.is_file() and not path.is_symlink() and sha(path) == item['sha256']
        os.chmod(path, 0o755 if item['mode'] == '100755' else 0o644)
    web = source / 'apps/web/dist/braid/browser'
    web.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(data['web'], web)
    for name, digest in data['webHashes'].items():
        assert sha(web / name) == digest

    home = tooling / 'browser-home'; home.mkdir(mode=0o700, exist_ok=True)
    temporary = private / 'build-temp'; temporary.mkdir(mode=0o700)
    packages = tooling / 'nuget'; packages.mkdir(mode=0o755, exist_ok=True)
    environment = {'PATH': str(dotnet.parent) + ':' + str(bun.parent) + ':/usr/bin:/bin', 'HOME': str(home),
                   'TMPDIR': str(temporary), 'DOTNET_ROOT': str(dotnet.parent), 'DOTNET_CLI_HOME': str(home),
                   'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH': 'false', 'DOTNET_NOLOGO': '1',
                   'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
                   'DOTNET_PROCESSOR_COUNT': '2', 'MSBUILDDISABLENODEREUSE': '1',
                   'NUGET_PACKAGES': str(packages), 'NUGET_HTTP_CACHE_PATH': str(tooling / 'nuget-http'), 'LANG': 'C.UTF-8'}
    assert subprocess.check_output([str(dotnet), '--version'], env=environment, text=True).strip() == '10.0.112'
    assert subprocess.check_output([str(bun), '--version'], env=environment, text=True).strip() == '1.4.5'
    products = {}
    reused = None
    if data.get('reuseConfig'):
        old_path = Path(data['reuseConfig'])
        old = json.loads(old_path.read_text())
        assert old_path.parent == Path(old['privateDirectory']) and old_path.stat().st_mode & 0o777 == 0o600
        assert sha(Path(old['source']['manifest']['path'])) == old['source']['manifest']['sha256']
        previous = json.loads(Path(old['source']['manifest']['path']).read_text())
        old_source = Path(old['source']['workspace'])
        assert sha(Path(old['source']['admission']['path'])) == old['source']['admission']['sha256']
        for row in previous:
            path = old_source / row['path']
            assert path.is_file() and not path.is_symlink() and sha(path) == row['sha256'], 'Retained source bytes differ.'
            assert path.stat().st_mode & 0o777 == int(row['mode'], 8) & 0o777
        assert sha(Path(old['publications']['admission']['path'])) == old['publications']['admission']['sha256']
        previous_admission = json.loads(Path(old['publications']['admission']['path']).read_text())
        assert previous_admission['sourceTree'] == old['source']['tree']
        assert previous_admission['fileHashes'] == {key: old['publications'][key] for key in ['server', 'worker', 'databaseTool', 'storageTool']}
        old_web = {key.removeprefix('wwwroot/'): value for key, value in old['publications']['server']['hashes'].items() if key.startswith('wwwroot/')}
        check_reusable_inputs(previous, manifest, json.loads((old_source / 'package.json').read_text()),
                              json.loads((source / 'package.json').read_text()), old_web, data['webHashes'])
        for key in ['server', 'worker', 'databaseTool', 'storageTool']:
            product = old['publications'][key]
            output = Path(product['directory'])
            storage.admitted_path(output, profile)
            if not output.is_relative_to(base.parent):
                raise ValueError('Retained publication is outside the configured work root.')
            files = list(output.rglob('*'))
            assert all(not item.is_symlink() for item in files)
            hashes = {str(item.relative_to(output)): sha(item) for item in files if item.is_file()}
            assert hashes == product['hashes'], 'Retained publication bytes differ.'
            products[key] = product
        reused = old['publications']['admission']
    else:
        for key, folder, assembly in [('server', 'apps', 'Braid.Server'), ('worker', 'apps', 'Braid.Worker'),
                                      ('databaseTool', 'tools', 'Braid.DatabaseTool'), ('storageTool', 'tools', 'Braid.StorageTool')]:
            project = f'dotnet/{folder}/{assembly}/{assembly}.csproj'
            subprocess.run([str(dotnet), 'restore', project, '--locked-mode', '--disable-parallel', '--source', 'https://api.nuget.org/v3/index.json', '-m:1', '-nr:false', '-v:q'],
                           cwd=source, env=environment, check=True)
            output = base / 'products' / key
            subprocess.run([str(dotnet), 'publish', project, '-c', 'Release', '--no-restore', '--output', str(output),
                            '-p:UseAppHost=false', '-p:OpenApiGenerateDocuments=false', '-p:UseSharedCompilation=false', '-m:1', '-nr:false', '-v:q'],
                            cwd=source, env=environment, check=True)
            products[key] = {'directory': str(output), 'hashes': {str(path.relative_to(output)): sha(path) for path in sorted(output.rglob('*')) if path.is_file()}}

    # Hashes are checked against the actual source export and successful local publications, not external release flags.
    for item in manifest:
        assert sha(source / item['path']) == item['sha256'], 'The build changed an admitted source file.'
    source_manifest = private / 'source-manifest.json'; private_json(source_manifest, manifest)
    source_admission = private / 'source-check.json'
    private_json(source_admission, {'commit': data['commit'], 'tree': data['tree'], 'manifestSha256': sha(source_manifest), 'ordinarySourceHashesVerified': True})
    product_admission = private / 'publication-check.json'
    evidence = {'sourceTree': data['tree'], 'sdk': '10.0.112', 'fileHashes': products}
    if reused:
        evidence.update({'sourceEquivalentReuse': True, 'previousAdmission': reused, 'runtimeInputsAndWebBytesUnchanged': True})
    else:
        evidence['ordinaryPublishSucceeded'] = list(products)
    private_json(product_admission, evidence)
    manifest_path = private / 'manifest.json'
    config = {'schemaVersion': 'braid.' + scenario + '-browser-operator-config.v1', 'fixtureId': data['fixtureId'],
              'privateDirectory': str(private), 'manifestPath': str(manifest_path), 'readOrigin': data['readOrigin'],
              'mutationOrigin': data['mutationOrigin'], 'docker': '/usr/bin/docker', 'daemonSocket': '/var/run/docker.sock',
              'privateStorageRoot': '/run/braid-regression-' + data['fixtureId'] + '/objects',
              'dotnet': str(dotnet), 'workerImage': data['workerImage'],
              'postgresImage': 'postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc',
              'source': {'workspace': str(source), 'commit': data['commit'], 'tree': data['tree'], 'manifest': bound(source_manifest), 'admission': bound(source_admission)},
              'publications': {**products, 'admission': bound(product_admission)}}
    config_path = private / 'config.json'; private_json(config_path, config)
    result = {'fixtureId': data['fixtureId'], 'distribution': data['distribution'], 'storageProfile': data['storageProfile'],
              'config': str(config_path), 'manifest': str(manifest_path),
              'readOrigin': data['readOrigin'], 'bun': str(bun), 'dotnet': str(dotnet),
              'operator': str(source / ('tools/' + ('build' if scenario == 'build' else 'regression') + '-browser/operator.mjs')),
              'launcher': str(source / 'tools/regression-browser/run_linux.py'), 'privateRoot': str(private)}
    Path(data['result']).write_text(json.dumps(result) + '\n')
    print('Prepared regression fixture publications.')


if __name__ == '__main__':
    main()
