import json
import subprocess
import unittest
from unittest.mock import patch

import build_image


class CleanupTests(unittest.TestCase):
    image = 'sha256:' + 'a' * 64
    identity = 'b' * 64

    def metadata(self, owner='owner'):
        return {'Image': self.image, 'Id': self.identity, 'Name': '/owned',
                'Config': {'Labels': {'io.weft.compiler.source-owner': owner}}}

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


if __name__ == '__main__':
    unittest.main()
