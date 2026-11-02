"""Small launcher checks, including actual POSIX descriptor isolation."""
import os
import subprocess
import sys
import unittest
from unittest.mock import patch

import run_tests


class LauncherTests(unittest.TestCase):
    def test_command_exit_and_explicit_global_tools_opt_out_are_preserved(self):
        with patch.dict(os.environ, {"DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "true", "WEFT_TEST_PIN": "retained"}):
            with patch.object(run_tests.subprocess, "run", return_value=subprocess.CompletedProcess(["fixture"], 7)) as launch:
                self.assertEqual(7, run_tests.run(["fixture", "argument"]))
            args, options = launch.call_args
            self.assertEqual((["fixture", "argument"],), args)
            self.assertTrue(options["close_fds"])
            self.assertFalse(options["check"])
            self.assertEqual("false", options["env"]["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"])
            self.assertEqual("retained", options["env"]["WEFT_TEST_PIN"])
            self.assertEqual("true", os.environ["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"])

    def test_selection_and_diagnostic_logging_do_not_change_deadlines(self):
        with patch.object(run_tests, "run", return_value=0) as launch:
            self.assertEqual(0, run_tests.main())
        self.assertEqual([
            "dotnet", "test", "dotnet/Weft.slnx", "--configuration", "Release", "--no-build", "--no-restore",
            "--logger", "console;verbosity=normal",
        ], launch.call_args.args[0])

    @unittest.skipUnless(os.name == "posix", "POSIX descriptor inheritance")
    def test_an_unrelated_inheritable_descriptor_does_not_reach_the_child(self):
        descriptor = os.open(os.devnull, os.O_RDONLY)
        try:
            os.set_inheritable(descriptor, True)
            child = (
                "import os,sys\n"
                f"try: os.fstat({descriptor})\n"
                "except OSError: sys.exit(0)\n"
                "sys.exit(1)\n"
            )
            self.assertEqual(0, run_tests.run([sys.executable, "-c", child]))
            os.fstat(descriptor)
        finally:
            os.close(descriptor)


if __name__ == "__main__":
    unittest.main()
