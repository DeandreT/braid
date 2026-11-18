"""Offline Linux integration: private disposable PostgreSQL, real 0053 migration and worker login.

Usage: python Fixtures/test_fence.py ABSOLUTE_ARTIFACTS_DIRECTORY
The fixture only models the shared outbox columns consumed by the AS2 fence.
"""
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile


def uid(n):
    return f"'{n:08x}-0000-0000-0000-000000000000'"


def run(*cmd, **kwargs):
    subprocess.run(cmd, check=True, timeout=180, **kwargs)


project = Path(__file__).resolve().parents[1]
repo = project.parents[2]
artifacts = Path(sys.argv[1]).resolve(strict=True)
with tempfile.TemporaryDirectory(dir=artifacts) as tmp:
    cluster = Path(tmp) / 'pg'
    run('initdb', '-D', cluster, '-U', 'fixture', '--auth-local=trust', '--auth-host=reject', '--no-locale')
    run('pg_ctl', '-w', '-D', cluster, '-l', Path(tmp) / 'postgres.log', '-o', f"-h '' -k '{tmp}' -p 5678", 'start')
    try:
        psql = ['psql', '-X', '-v', 'ON_ERROR_STOP=1', '-U', 'fixture', '-h', tmp, '-p', '5678', '-d', 'postgres']
        run(*psql, input=f"""
            CREATE ROLE braid_trigger_worker LOGIN NOINHERIT;
            CREATE SCHEMA braid;
            GRANT USAGE ON SCHEMA braid TO braid_trigger_worker;
            CREATE TABLE braid.trigger_work (organization_id uuid, work_id uuid, project_id uuid, mapping_id uuid);
            CREATE TABLE braid.trigger_receipts (organization_id uuid, work_id uuid, receipt_id uuid, direction text, digest text, media_type text);
            CREATE TABLE braid.local_release_targets (organization_id uuid, target_id uuid, environment_id uuid, environment_version bigint);
            CREATE TABLE braid.trigger_deliveries (organization_id uuid, delivery_id uuid, target_id uuid, work_id uuid, receipt_id uuid,
                phase text, sends integer, claim_id uuid, claim_expires_at timestamptz, destination_kind text, destination_binding_id uuid,
                connector_id text, connector_version bigint, PRIMARY KEY(organization_id,delivery_id));
            INSERT INTO braid.trigger_work VALUES({uid(1)},{uid(10)},{uid(2)},{uid(3)});
            INSERT INTO braid.trigger_receipts VALUES({uid(1)},{uid(10)},{uid(8)},'output','{hashlib.sha256(b'A').hexdigest()}','application/edi-x12');
            INSERT INTO braid.local_release_targets VALUES({uid(1)},{uid(5)},{uid(4)},1);
            INSERT INTO braid.trigger_deliveries VALUES({uid(1)},{uid(7)},{uid(5)},{uid(10)},{uid(8)},'sending',1,{uid(9)},
                clock_timestamp()+interval '10 minutes','as2',{uid(6)},'partner',1);
        """, text=True)
        run(*psql, '-f', repo / 'migrations/postgres/0053_as2_send_fence.sql')
        run('dotnet', 'test', project, '--artifacts-path', artifacts, '-m:1', cwd=repo,
            env={**os.environ, 'BRAID_AS2_FIXTURE_SOCKET': tmp, 'TMPDIR': tmp})
    finally:
        run('pg_ctl', '-w', '-D', cluster, '-m', 'fast', 'stop')
