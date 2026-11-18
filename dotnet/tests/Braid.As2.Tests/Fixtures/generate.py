"""Regenerate offline partner vectors with Python/OpenSSL; no Braid implementation or retained key."""
import base64
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile


def b64(data):
    return base64.b64encode(data).decode('ascii')


with tempfile.TemporaryDirectory() as work:
    def ssl(*args, data=None):
        return subprocess.run(['openssl', *args], cwd=work, input=data, stdout=subprocess.PIPE, check=True).stdout

    ssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=offline-mdn-fixture',
        '-days', '3650', '-keyout', 'key', '-out', 'cert')
    payload = b'ST*945*0001~\r\nSE*2*0001~'
    entity = b'Content-Type: application/edi-x12\r\nContent-Transfer-Encoding: base64\r\n\r\n' + base64.b64encode(payload)
    mic = b64(hashlib.sha256(entity).digest())
    vectors = dict(payload=b64(payload), cert=b64(ssl('x509', '-in', 'cert', '-outform', 'DER')), mic=mic)
    for label, status in [('success', 'processed'), ('failure', 'processed/error: unexpected-processing-error')]:
        report = ('Content-Type: multipart/report; report-type=disposition-notification; boundary=report\r\n\r\n'
                  '--report\r\nContent-Type: text/plain\r\n\r\nOffline receipt\r\n'
                  '--report\r\nContent-Type: message/disposition-notification\r\n\r\n'
                  'Reporting-UA: offline-as2\r\nFinal-Recipient: rfc822; PARTNER\r\n'
                  'Original-Message-ID: <11111111111111111111111111111111@braid.invalid>\r\n'
                  f'Disposition: automatic-action/MDN-sent-automatically; {status}\r\n'
                  f'Received-content-MIC: {mic}, sha-256\r\n\r\n--report--\r\n').encode('ascii')
        sig = ssl('cms', '-sign', '-binary', '-md', 'sha256', '-signer', 'cert', '-inkey', 'key', '-outform', 'DER', data=report)
        vectors[label] = b64(b'--outer\r\n' + report + b'\r\n--outer\r\nContent-Type: application/pkcs7-signature\r\n'
                             b'Content-Transfer-Encoding: base64\r\n\r\n' + base64.b64encode(sig) + b'\r\n--outer--\r\n')
    Path(__file__).with_name('mdns.json').write_text(json.dumps(vectors, indent=2) + '\n')
