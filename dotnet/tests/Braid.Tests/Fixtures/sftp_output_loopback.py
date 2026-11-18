"""Single loopback SFTP directory; no system accounts, broker, or cloud credentials."""
import json
import os
import pathlib
import re
import signal
import socket
import sys
import threading

import paramiko

signal.alarm(90)
os.umask(0o077)
root = pathlib.Path(sys.argv[1]) / 'out'
root.mkdir()
host = paramiko.RSAKey.generate(2048)
user = paramiko.RSAKey.generate(2048)
user.write_private_key_file(str(root.parent / 'key.pem'))


class Auth(paramiko.ServerInterface):
    def get_allowed_auths(self, username):
        return 'publickey'

    def check_auth_publickey(self, username, key):
        return paramiko.AUTH_SUCCESSFUL if username == 'fixture' and key == user else paramiko.AUTH_FAILED

    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED if kind == 'session' else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED


def file(path):
    if not re.fullmatch(r'/out/(\.[a-f0-9-]{36}\.[0-9a-f]{16}\.part|[a-f0-9-]{36})', path):
        raise PermissionError(13, 'invalid fixture path')
    return root / path[5:]


class Out(paramiko.SFTPServerInterface):
    def stat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat((root if path.rstrip('/') == '/out' else file(path)).stat())
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    lstat = stat

    def open(self, path, flags, attr):
        try:
            handle = paramiko.SFTPHandle(flags)
            if flags & os.O_CREAT:
                handle.writefile = os.fdopen(os.open(file(path), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'wb')
            else:
                handle.readfile = file(path).open('rb')
            handle.stat = lambda: self.stat(path)
            return handle
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def rename(self, old, new):
        try:
            os.link(file(old), file(new))  # never replace
            file(old).unlink()
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def remove(self, path):
        try:
            file(path).unlink()
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)


listener = socket.socket()
listener.bind(('127.0.0.1', 0))
listener.listen(4)


def serve():
    while True:
        transport = paramiko.Transport(listener.accept()[0])
        transport.add_server_key(host)
        transport.set_subsystem_handler('sftp', paramiko.SFTPServer, Out)
        try:
            transport.start_server(server=Auth())
        except (EOFError, paramiko.SSHException):
            transport.close()  # the wrong-pin test disconnects during negotiation


threading.Thread(target=serve, daemon=True).start()
print(json.dumps([listener.getsockname()[1], host.fingerprint.removeprefix('SHA256:')]), flush=True)
sys.stdin.readline()
