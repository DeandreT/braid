"""Compile and call the pinned generated XML interfaces for one fixed, authored mapping."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    here = Path(__file__).resolve().parent
    root = here.parent.parent
    sdk = json.loads((root / 'global.json').read_text())['sdk']['version']
    runtime = '10.0.12'
    dotnet = Path(shutil.which('dotnet')).resolve()
    compiler = dotnet.parent / 'sdk' / sdk / 'Roslyn/bincore/csc.dll'
    refs = sorted((dotnet.parent / 'packs/Microsoft.NETCore.App.Ref' / runtime / 'ref/net10.0').glob('*.dll'))
    if not refs or not compiler.is_file():
        raise RuntimeError('Pinned SDK and reference pack required')
    with tempfile.TemporaryDirectory(prefix='braid-xml-interfaces-') as work:
        directory = Path(work) / 'fixture'
        env = dict(os.environ, BRAID_XML_FIXTURE_DIRECTORY=str(directory), DOTNET_ROLL_FORWARD='Disable',
                   DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false')
        subprocess.run(['cargo', 'test', '--locked', '--offline', '-j2', '--manifest-path',
                        'engines/ferrule-host/Cargo.toml', '--test', 'xml_preview', '--', '--ignored',
                        '--exact', 'emit_compiled_xml_interface_fixture'], cwd=root, env=env, check=True, timeout=900)
        # Never build the emitted project or modify the emitted runtime sources.
        (directory / 'Usings.cs').write_text('global using System; global using System.Collections.Generic; '
                                            'global using System.IO; global using System.Linq; global using System.Net.Http; '
                                            'global using System.Threading; global using System.Threading.Tasks;')
        compile_args = [dotnet, 'exec', compiler, '-noconfig', '-nostdlib+', '-nologo', '-nullable:enable',
                        '-warnaserror+', '-unsafe-', '-langversion:14', *['-r:' + str(ref) for ref in refs]]
        subprocess.run([*compile_args, '-target:library', '-out:Ferrule.Generated.dll', *sorted(directory.rglob('*.cs'))],
                       cwd=directory, env=env, check=True, timeout=90)
        subprocess.run([*compile_args, '-out:Check.dll', '-r:Ferrule.Generated.dll', directory / 'Usings.cs',
                        root / 'dotnet/apps/Braid.CompiledRuntime/CompiledXmlBytes.cs',
                        *[root / 'dotnet/src/Braid.Application' / name for name in
                          ('XmlDocumentSafety.cs', 'XmlDocumentPolicy.cs', 'XmlDocumentMetadata.cs')], here / 'CheckInterfaces.cs'],
                       cwd=directory, env=env, check=True, timeout=90)
        (directory / 'Check.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'tfm': 'net10.0',
            'framework': {'name': 'Microsoft.NETCore.App', 'version': runtime}, 'rollForward': 'Disable'}}))
        subprocess.run([dotnet, 'Check.dll'], cwd=directory, env=env, check=True, timeout=15)


if __name__ == '__main__':
    main()
