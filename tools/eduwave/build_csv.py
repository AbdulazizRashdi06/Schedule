"""Turns the compact EduWave lines into Classy's upload CSV, and refreshes the fingerprints for the next run's diff.

    python tools/eduwave/build_csv.py imports/ew-dated-YYYYMMDD.txt --catalog imports/classy-current.csv [--names imports/offered-codes.md] [--out imports/upload-....csv]

Lines look like  CS 1 107|3|Nabil Sahli|09:45-11:15|GU1 240|51.60w4
days are counted from 1 Aug 2026; "60w4" is day 60 and every 7 days after it, 4 days in all.
Names come from the offered-codes table (| Major | Course | Code |). Type and major follow the usual rules:
LS for Life Skills; Elective for IBSM-FE, IBSM-TM, LOG-E and the listed electives; otherwise Core, with the code's
prefix as the major. Years come only from a current Classy CSV export, never from course-code digits.
A new lab ending in -L can use its parent course's stored year. Unknown years stay blank and are reported.
Writes the CSV, and <input stem>-hashes.txt (first 10 hex of each line's SHA-1, comma-separated).
"""
import argparse, csv, datetime as dt, hashlib, pathlib, re, sys

EPOCH = dt.date(2026, 8, 1)
ELECTIVE_PREFIXES = ('IBSM-FE ', 'IBSM-TM ', 'LOG-E ')
ELECTIVES = {'AI 4 568', 'CS 3 322', 'CS 3 346', 'CS 3 367', 'CS 3 469'}


def days(spec):
    out = []
    for part in spec.split('.'):
        if not part:
            continue
        if 'w' in part:
            start, n = part.split('w')
            out += [int(start) + 7 * i for i in range(int(n))]
        else:
            out.append(int(part))
    return out


def names(path):
    found = {}
    if path and pathlib.Path(path).exists():
        for line in pathlib.Path(path).read_text(encoding='utf-8').splitlines():
            cells = [c.strip() for c in line.strip().strip('|').split('|')]
            if len(cells) == 3 and re.match(r'^[A-Z]', cells[2]) and cells[2] != 'Code':
                found.setdefault(cells[2], cells[1])
    return found


def details(code):
    base = code[:-2] if code.endswith('-L') else code
    if base.startswith('LS '):
        return 'LS', ''
    if base.startswith(ELECTIVE_PREFIXES) or base in ELECTIVES:
        return 'Elective', ''
    return 'Core', base.split(' ')[0]


def code_key(code):
    return ''.join(code.split()).upper()


def catalog_years(path):
    found = {}
    with pathlib.Path(path).open(encoding='utf-8-sig', newline='') as f:
        rows = csv.DictReader(f)
        if not {'Course Code', 'Year'}.issubset(rows.fieldnames or []):
            raise ValueError('The Classy export must contain Course Code and Year columns.')
        for row in rows:
            code, year = code_key(row['Course Code']), row['Year'].strip()
            if not code:
                continue
            if code in found and found[code] != year:
                raise ValueError(f'Conflicting stored years for {code} in the Classy export.')
            found[code] = year
    if not found:
        raise ValueError('The Classy export contains no courses.')
    return found


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('lines')
    ap.add_argument('--names', default='imports/offered-codes.md')
    ap.add_argument('--catalog', required=True, help='Current Classy CSV export used to preserve stored course years')
    ap.add_argument('--out')
    a = ap.parse_args()
    try:
        years = catalog_years(a.catalog)
    except (OSError, ValueError, AttributeError) as e:
        ap.error(str(e))
    src = pathlib.Path(a.lines)
    lines = [l for l in src.read_text(encoding='utf-8').splitlines() if l.strip()]
    title = names(a.names)
    out = pathlib.Path(a.out or src.with_name(f'upload-{src.stem}.csv'))
    missing, unknown_years, classes, sections, courses = set(), set(), 0, set(), set()
    with out.open('w', encoding='utf-8', newline='') as f:
        w = csv.writer(f)
        w.writerow(['Course Code', 'Course Name', 'Major', 'Type', 'Year', 'Credits', 'Section', 'Instructor', 'Date', 'Time', 'Room'])
        for line in lines:
            code, section, teacher, time, room, spec = line.split('|')
            base = code[:-2] if code.endswith('-L') else code
            name = title.get(code) or (title[base] + ' Lab' if code.endswith('-L') and base in title else '')
            if not name:
                missing.add(code)
                name = code
            kind, major = details(code)
            key = code_key(code)
            if key in years:
                year = years[key]
            elif key.endswith('-L') and key[:-2] in years:
                year = years[key[:-2]]
            else:
                year = ''
                unknown_years.add(code)
            for d in days(spec):
                w.writerow([code, name, major, kind, year, '', section, teacher, (EPOCH + dt.timedelta(days=d)).isoformat(), time, room])
                classes += 1
            sections.add((code, section))
            courses.add(code)
    hashes = src.with_name(f'{src.stem}-hashes.txt')
    hashes.write_text(','.join(hashlib.sha1(l.encode('utf-8')).hexdigest()[:10] for l in lines), encoding='utf-8')
    print(f'{len(lines)} lines -> {len(courses)} courses, {len(sections)} sections, {classes} classes')
    print(f'CSV: {out}')
    print(f'Fingerprints: {hashes}')
    if missing:
        print(f'No name for {len(missing)} codes (the code is used as the name): {", ".join(sorted(missing))}', file=sys.stderr)
    if unknown_years:
        print(f'No stored year for {len(unknown_years)} codes (left blank; review before applying): {", ".join(sorted(unknown_years))}', file=sys.stderr)


if __name__ == '__main__':
    main()
