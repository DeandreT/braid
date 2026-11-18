"""Independent, bounded SFTP inbox and synchronous AS2 receiver; synthetic bytes only."""
import base64
import datetime
import hashlib
import http.server
import io
import json
import os
import pathlib
import signal
import socket
import ssl
import stat
import sys
import threading
import time

import paramiko
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.hazmat.primitives.serialization import pkcs7
from cryptography.x509.oid import NameOID

signal.alarm(180)
os.umask(0o077)
root = pathlib.Path(sys.argv[1])  # unique directory owned by this test
lose_mdn = sys.argv[2:] == ['lose-mdn']
content = base64.b64decode(sys.stdin.readline().strip(), validate=True)
assert 0 < len(content) <= 65536
expected = base64.b64decode(sys.stdin.readline().strip(), validate=True)
assert 0 < len(expected) <= 65536
filename = 'one.' + hashlib.sha256(content).hexdigest() + '.ready'
wrong_name = 'bad.' + '0' * 64 + '.ready'
host = paramiko.RSAKey.generate(2048)
user = paramiko.RSAKey.generate(2048)
user.write_private_key_file(str(root / 'client.pem'))


class Auth(paramiko.ServerInterface):
    def get_allowed_auths(self, username):
        return 'publickey'

    def check_auth_publickey(self, username, key):
        return paramiko.AUTH_SUCCESSFUL if username == 'fixture' and key == user else paramiko.AUTH_FAILED

    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED if kind == 'session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED


class Inbox(paramiko.SFTPServerInterface):
    def lstat(self, path):
        if path not in ('/inbox', '/inbox/' + filename, '/inbox/' + wrong_name):
            return paramiko.SFTP_NO_SUCH_FILE
        item = paramiko.SFTPAttributes()
        item.st_mode = stat.S_IFDIR | 0o500 if path == '/inbox' else stat.S_IFREG | 0o400
        item.st_size = 0 if path == '/inbox' else len(content)
        item.st_atime = item.st_mtime = int(time.time()) - 3600
        item.filename = path.rsplit('/', 1)[-1]
        return item

    stat = lstat

    def list_folder(self, path):
        return [self.stat('/inbox/' + wrong_name), self.stat('/inbox/' + filename)] if path == '/inbox' else paramiko.SFTP_NO_SUCH_FILE

    def open(self, path, flags, attr):
        if flags != os.O_RDONLY or path not in ('/inbox/' + filename, '/inbox/' + wrong_name):
            return paramiko.SFTP_PERMISSION_DENIED
        handle = paramiko.SFTPHandle(flags)
        handle.readfile = io.BytesIO(content)
        handle.stat = lambda: self.stat(path)
        return handle


sock = socket.socket()
sock.bind(('127.0.0.1', 0))
sock.listen(5)


def listen():
    while True:
        transport = paramiko.Transport(sock.accept()[0])
        transport.add_server_key(host)
        transport.set_subsystem_handler('sftp', paramiko.SFTPServer, Inbox)
        transport.start_server(server=Auth())


threading.Thread(target=listen, daemon=True).start()
key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'fixture')])
now = datetime.datetime.now(datetime.timezone.utc)
cert = (x509.CertificateBuilder().issuer_name(name).subject_name(name).public_key(key.public_key())
        .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(hours=1))
        .not_valid_after(now + datetime.timedelta(days=1)).sign(key, hashes.SHA256()))
(root / 'peer.pem').write_bytes(cert.public_bytes(serialization.Encoding.PEM))
(root / 'peer-key.pem').write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))


class Partner(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        size = int(self.headers['Content-Length'])
        assert self.path == '/receive' and 0 < size < 1024 * 1024
        decrypted = pkcs7.pkcs7_decrypt_der(self.rfile.read(size), cert, key, [])
        # Derive the MIC from the received signed entity, not from a sender-provided digest.
        envelope_header, body = decrypted.split(b'\r\n\r\n', 1)
        boundary = b'--' + envelope_header.split(b'boundary="')[1].split(b'"')[0]
        entity = body.split(boundary + b'\r\n')[1].removesuffix(b'\r\n')
        received_bytes = base64.b64decode(entity.split(b'\r\n\r\n', 1)[1].replace(b'\r\n', b''), validate=True)
        assert received_bytes == expected
        # Receiver-side evidence independent of the sender's observation. No payload or key material.
        with (root / 'accepted.log').open('a', encoding='ascii') as received:
            received.write(self.headers['Message-ID'] + ' ' + hashlib.sha256(received_bytes).hexdigest() + '\n')
        mic = base64.b64encode(hashlib.sha256(entity).digest()).decode()
        report = ('Content-Type: multipart/report; report-type=disposition-notification; boundary=r\r\n\r\n'
                  '--r\r\nContent-Type: text/plain\r\n\r\nOK\r\n--r\r\nContent-Type: message/disposition-notification\r\n\r\n'
                  'Final-Recipient: rfc822; PARTNER\r\nDisposition: automatic-action/MDN-sent-automatically; processed\r\n'
                  f'Original-Message-ID: {self.headers["Message-ID"]}\r\nReceived-content-MIC: {mic}, sha-256\r\n--r--\r\n').encode()
        def signed_report(message):
            signature = pkcs7.PKCS7SignatureBuilder().set_data(message).add_signer(cert, key, hashes.SHA256()).sign(
                serialization.Encoding.DER, [pkcs7.PKCS7Options.Binary, pkcs7.PKCS7Options.DetachedSignature])
            return b'--outer\r\n' + message + b'\r\n--outer\r\nContent-Type: application/pkcs7-signature\r\nContent-Transfer-Encoding: base64\r\n\r\n' + base64.b64encode(signature) + b'\r\n--outer--\r\n'
        reply = signed_report(report)
        if lose_mdn:
            (root / 'delayed.mdn').write_bytes(reply)
            (root / 'conflicting.mdn').write_bytes(signed_report(report.replace(mic.encode(), base64.b64encode(hashlib.sha256(b'other').digest()))))
            self.close_connection = True
            self.connection.shutdown(socket.SHUT_RDWR)  # separately supplied later, never a callback
            threading.Thread(target=server.shutdown, daemon=True).start()
            return
        self.send_response(200)
        self.send_header('Content-Type', 'multipart/signed; protocol="application/pkcs7-signature"; micalg=sha-256; boundary=outer')
        self.send_header('AS2-From', 'PARTNER')
        self.send_header('AS2-To', 'LOCAL')
        self.send_header('Content-Length', str(len(reply)))
        self.end_headers()
        self.wfile.write(reply)

    def log_message(self, *args):
        pass


server = http.server.HTTPServer(('127.0.0.1', 0), Partner)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(root / 'peer.pem', root / 'peer-key.pem')
server.socket = context.wrap_socket(server.socket, server_side=True)
threading.Thread(target=server.serve_forever, daemon=True).start()
print(json.dumps([sock.getsockname()[1], host.fingerprint.removeprefix('SHA256:'), server.server_port]), flush=True)
sys.stdin.readline()  # EOF stops both daemon threads
