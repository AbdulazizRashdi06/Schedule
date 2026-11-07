"""Regression checks for preserving Classy study years during timetable refreshes."""
import csv
import pathlib
import subprocess
import sys
import tempfile
import unittest


SCRIPT = pathlib.Path(__file__).with_name('build_csv.py')


class CourseYearTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = pathlib.Path(self.tmp.name)
        self.source = self.root / 'lines.txt'
        self.catalog = self.root / 'catalog.csv'
        self.output = self.root / 'upload.csv'
        self.source.write_text('\n'.join(
            f'{c}|1|Instructor|08:00-09:30|Room|51'
            for c in ['CS 1 107', 'CS 1 107-L', 'CS 2 114', 'CS 2 114-L', 'CS 4 999']
        ), encoding='utf-8')
        self.catalog.write_text('Course Code,Year\nCS1107,2\nCS 1 107,2\nCS2114,\n', encoding='utf-8-sig')

    def run_builder(self, *args):
        return subprocess.run(
            [sys.executable, str(SCRIPT), str(self.source), '--out', str(self.output), *args],
            capture_output=True, text=True,
        )

    def test_stored_years_and_blanks_survive_and_unknown_year_is_not_guessed(self):
        result = self.run_builder('--catalog', str(self.catalog))
        self.assertEqual(0, result.returncode, result.stderr)
        with self.output.open(newline='', encoding='utf-8') as f:
            years = {r['Course Code']: r['Year'] for r in csv.DictReader(f)}
        self.assertEqual({'CS 1 107': '2', 'CS 1 107-L': '2', 'CS 2 114': '',
                          'CS 2 114-L': '', 'CS 4 999': ''}, years)
        self.assertIn('No stored year for 1 codes', result.stderr)

    def test_missing_catalog_cannot_overwrite_an_upload(self):
        self.output.write_text('existing upload', encoding='utf-8')
        result = self.run_builder()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual('existing upload', self.output.read_text())

    def test_conflicting_catalog_years_cannot_overwrite_an_upload(self):
        with self.catalog.open('a', encoding='utf-8') as f:
            f.write('CS1107,1\n')
        self.output.write_text('existing upload', encoding='utf-8')
        result = self.run_builder('--catalog', str(self.catalog))
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Conflicting stored years', result.stderr)
        self.assertEqual('existing upload', self.output.read_text())


if __name__ == '__main__':
    unittest.main()
