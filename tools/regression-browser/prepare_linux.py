"""Prepare a fresh Linux source export and ordinary publications for the real browser fixture."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile


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


def main():
    data = json.loads(Path(sys.argv[1]).read_text())
    mount = json.loads(subprocess.check_output(['findmnt', '-J', '-T', '/srv/example-build', '-o', 'TARGET,FSTYPE,UUID,FSROOT,OPTIONS']))['filesystems'][0]
    assert mount['target'] == '/srv/example-build' and mount['fstype'] == 'btrfs' and mount['uuid'] == '00000000-0000-4000-8000-000000000001' and mount['fsroot'] == '/@example-build'
    assert 'rw' in mount['options'].split(',') and 'compress=zstd:3' in mount['options']
    host = os.statvfs('/mnt/example-host')
    assert host.f_bavail * host.f_frsize >= 25 * 2**30
    base = Path('/srv/example-build/work') / ('weft-regression-' + data['fixtureId'])
    private = Path('/srv/example-build/data') / ('weft-regression-' + data['fixtureId'])
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
    web = source / 'apps/web/dist/weft/browser'
    web.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(data['web'], web)
    for name, digest in data['webHashes'].items():
        assert sha(web / name) == digest

    tooling = Path('/srv/example-build/cache/weft-tooling')
    dotnet = tooling / 'dotnet/dotnet'
    node = tooling / 'node-v22.23.3-linux-x64/bin/node'
    home = tooling / 'browser-home'; home.mkdir(mode=0o700, exist_ok=True)
    temporary = private / 'build-temp'; temporary.mkdir(mode=0o700)
    packages = tooling / 'nuget'; packages.mkdir(mode=0o755, exist_ok=True)
    environment = {'PATH': str(dotnet.parent) + ':' + str(node.parent) + ':/usr/bin:/bin', 'HOME': str(home),
                   'TMPDIR': str(temporary), 'DOTNET_ROOT': str(dotnet.parent), 'DOTNET_CLI_HOME': str(home),
                   'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH': 'false', 'DOTNET_NOLOGO': '1',
                   'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
                   'DOTNET_PROCESSOR_COUNT': '2', 'MSBUILDDISABLENODEREUSE': '1',
                   'NUGET_PACKAGES': str(packages), 'NUGET_HTTP_CACHE_PATH': str(tooling / 'nuget-http'), 'LANG': 'C.UTF-8'}
    assert subprocess.check_output([str(dotnet), '--version'], env=environment, text=True).strip() == '10.0.112'
    assert subprocess.check_output([str(node), '--version'], env=environment, text=True).strip() == 'v22.23.3'
    products = {}
    for key, folder, assembly in [('server', 'apps', 'Weft.Server'), ('worker', 'apps', 'Weft.Worker'),
                                  ('databaseTool', 'tools', 'Weft.DatabaseTool'), ('storageTool', 'tools', 'Weft.StorageTool')]:
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
    private_json(product_admission, {'sourceTree': data['tree'], 'sdk': '10.0.112', 'ordinaryPublishSucceeded': list(products), 'fileHashes': products})
    manifest_path = private / 'manifest.json'
    config = {'schemaVersion': 'weft.regression-browser-operator-config.v1', 'fixtureId': data['fixtureId'],
              'privateDirectory': str(private), 'manifestPath': str(manifest_path), 'readOrigin': data['readOrigin'],
              'mutationOrigin': data['mutationOrigin'], 'docker': '/usr/bin/docker', 'daemonSocket': '/var/run/docker.sock',
              'privateStorageRoot': '/run/weft-regression-' + data['fixtureId'] + '/objects',
              'dotnet': str(dotnet), 'workerImage': data['workerImage'],
              'postgresImage': 'postgres@sha256:ec9f656f17c8b4dfebf49e58cf5c46d493d4846d4ea6d8b85d0637f68ee33bfc',
              'source': {'workspace': str(source), 'commit': data['commit'], 'tree': data['tree'], 'manifest': bound(source_manifest), 'admission': bound(source_admission)},
              'publications': {**products, 'admission': bound(product_admission)}}
    config_path = private / 'config.json'; private_json(config_path, config)
    result = {'fixtureId': data['fixtureId'], 'config': str(config_path), 'manifest': str(manifest_path),
              'readOrigin': data['readOrigin'], 'node': str(node), 'dotnet': str(dotnet),
              'operator': str(source / 'tools/regression-browser/operator.mjs'), 'privateRoot': str(private)}
    Path(data['result']).write_text(json.dumps(result) + '\n')
    print('Prepared regression fixture publications.')


if __name__ == '__main__':
    main()
