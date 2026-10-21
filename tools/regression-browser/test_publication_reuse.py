import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('prepare_linux', Path(__file__).with_name('prepare_linux.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def row(path, digest='a'):
    return {'path': path, 'sha256': digest, 'mode': '100644'}


class PublicationReuseTests(unittest.TestCase):
    def test_only_observer_and_script_changes_are_accepted(self):
        before = [row('dotnet/server.cs'), row('package.json'), row('tools/browser-fixture/runtime.mjs')]
        after = [row('dotnet/server.cs'), row('package.json', 'b'), row('tools/browser-fixture/runtime.mjs', 'b')]
        module.check_reusable_inputs(before, after, {'dependencies': {'x': '1'}, 'scripts': {}},
                                     {'dependencies': {'x': '1'}, 'scripts': {'check': 'test'}}, {'main.js': 'a'}, {'main.js': 'a'})

    def test_changed_added_deleted_or_repermissioned_runtime_input_is_refused(self):
        before = [row('dotnet/server.cs')]
        for after in [[row('dotnet/server.cs', 'b')], [], before + [row('new.props')], [{**before[0], 'mode': '100755'}]]:
            with self.subTest(after=after), self.assertRaises(ValueError):
                module.check_reusable_inputs(before, after, {}, {}, {'main.js': 'a'}, {'main.js': 'a'})

    def test_package_and_web_changes_are_refused(self):
        for old_package, new_package, old_web, new_web in [
            ({'dependencies': {'x': '1'}}, {'dependencies': {'x': '2'}}, {'main.js': 'a'}, {'main.js': 'a'}),
            ({}, {}, {'main.js': 'a'}, {'main.js': 'b'}),
            ({}, {}, {'main.js': 'a'}, {}),
        ]:
            with self.subTest(new_web=new_web), self.assertRaises(ValueError):
                module.check_reusable_inputs([], [], old_package, new_package, old_web, new_web)

    def test_browser_observers_are_not_runtime_inputs(self):
        before = [row('apps/web/tests/workbench.spec.ts'), row('apps/web/playwright.workbench.config.ts'), row('apps/web/src/main.ts')]
        after = [row('apps/web/tests/workbench.spec.ts', 'b'), row('apps/web/playwright.workbench.config.ts', 'b'), row('apps/web/src/main.ts')]
        module.check_reusable_inputs(before, after, {}, {}, {'main.js':'a'}, {'main.js':'a'})
        with self.assertRaises(ValueError):
            module.check_reusable_inputs(before, [*after[:-1],row('apps/web/src/main.ts','b')], {}, {}, {'main.js':'a'}, {'main.js':'a'})

    def test_duplicate_source_paths_are_refused(self):
        for name in ['dotnet/server.cs', 'tools/browser-fixture/runtime.mjs']:
            with self.subTest(name=name), self.assertRaises(ValueError):
                module.check_reusable_inputs([row(name), row(name)], [], {}, {}, {'main.js':'a'}, {'main.js':'a'})


if __name__ == '__main__':
    unittest.main()
