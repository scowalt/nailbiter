import json
import os
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

candidate = Path(__file__).resolve().parents[2]
output = candidate / 'LICENSES'
output.mkdir(exist_ok=True)
records = []
seen = set()


def notices(directory, ecosystem, identity):
    result = []
    for item in directory.iterdir():
        if item.is_symlink() or not item.is_file() or not item.resolve().is_relative_to(directory.resolve()) or not item.name.upper().startswith(('LICENSE', 'LICENCE', 'COPYING', 'NOTICE')) or item.stat().st_size > 256 * 1024:
            continue
        try:
            text = item.read_text()
        except (ValueError, UnicodeError):
            continue
        dest = output / ecosystem / identity.replace('/', '__') / item.name
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(text)
        result.append(str(dest.relative_to(candidate)))
    return result


def npm(directory):
    if not directory.is_dir():
        return
    for folder in directory.iterdir():
        if folder.name.startswith('.'):
            continue
        if folder.name.startswith('@'):
            packages = list(folder.iterdir())
        else:
            packages = [folder]
        for package in packages:
            manifest = package / 'package.json'
            if not manifest.is_file() or not manifest.resolve().is_relative_to(package.resolve()):
                continue
            obj = json.loads(manifest.read_text())
            identity = obj['name'] + '@' + obj['version']
            if identity in seen:
                continue
            seen.add(identity)
            records.append({'ecosystem': 'npm', 'name': obj['name'], 'version': obj['version'], 'declared_license': obj.get('license'), 'notices': notices(package, 'npm', identity)})
            npm(package / 'node_modules')


for directory in [candidate / 'node_modules', *candidate.glob('apps/*/node_modules')]:
    npm(directory)
nuget = Path(os.environ.get('NUGET_PACKAGES', str(Path.home() / '.nuget/packages')))
for file in candidate.glob('apps/*/obj/project.assets.json'):
    for identity, data in json.loads(file.read_text()).get('libraries', {}).items():
        if data.get('type') != 'package' or identity.lower() in seen:
            continue
        seen.add(identity.lower())
        name, version = identity.rsplit('/', 1)
        directory = nuget / name.lower() / version.lower()
        spec = directory / (name.lower() + '.nuspec')
        if not spec.is_file():
            records.append({'ecosystem': 'nuget', 'name': name, 'version': version, 'status': 'metadata unavailable'})
            continue
        document = ET.parse(spec)
        license_node = next((n for n in document.iter() if n.tag.rsplit('}', 1)[-1] == 'license'), None)
        urls = [n.text for n in document.iter() if n.tag.rsplit('}', 1)[-1] == 'licenseUrl']
        copyrights = [n.text for n in document.iter() if n.tag.rsplit('}', 1)[-1] == 'copyright']
        repositories = [dict(n.attrib) for n in document.iter() if n.tag.rsplit('}', 1)[-1] == 'repository']
        paths = notices(directory, 'nuget', name + '@' + version)
        if license_node is not None and license_node.get('type') == 'file':
            target = (directory / license_node.text).resolve()
            if target.is_relative_to(directory.resolve()) and target.is_file():
                dest = output / 'nuget' / (name + '@' + version) / target.name
                dest.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(target, dest)
                path = str(dest.relative_to(candidate))
                if path not in paths:
                    paths.append(path)
        records.append({'ecosystem': 'nuget', 'name': name, 'version': version, 'declared_license': license_node.text if license_node is not None else None, 'license_type': license_node.get('type') if license_node is not None else None, 'license_urls': urls, 'copyright_notices': copyrights, 'repositories': repositories, 'notices': paths})
(output / 'dependency-inventory.json').write_text(json.dumps({'scope': 'Installed candidate npm and restored NuGet packages; declaration/notice evidence, not legal clearance. Python, containers and CDN distribution need separate review.', 'packages': sorted(records, key=lambda r: (r['ecosystem'], r['name'], r['version']))}, indent=2) + '\n')
assets = candidate / 'apps/backend/obj/project.assets.json'
application_packages = {identity.lower() for identity, item in json.loads(assets.read_text()).get('libraries', {}).items() if item.get('type') == 'package'} if assets.exists() else set()
for app, names in [('backend', {'chart.js', '@kurkle/color', 'tailwindcss'}), ('www', {'astro'})]:
    chunks = ['Nailbiter third-party software notices. Original project license and artwork exclusions are documented in LICENSE-SCOPE.txt. These are upstream declarations, not a relicensing grant by Nailbiter.\n']
    for record in records:
        include = record['ecosystem'] == 'npm' and record['name'] in names
        include = include or (app == 'backend' and record['ecosystem'] == 'nuget' and (record['name'] + '/' + record['version']).lower() in application_packages)
        if include:
            chunks.append(record['name'] + ' ' + record['version'] + '\nDeclared license: ' + str(record.get('declared_license')))
            chunks.extend(record.get('copyright_notices', []))
            chunks.extend((candidate / path).read_text() for path in record.get('notices', []))
    if app == 'backend':
        for name in ['MIT-terms.txt', 'Apache-2.0.txt']:
            chunks.append((output / name).read_text())
    if app == 'www' and (output / 'assets/vite/LICENSE-v6.3.5.txt').exists():
        chunks.append('Vite template artwork\n' + (output / 'assets/vite/LICENSE-v6.3.5.txt').read_text())
    directory = candidate / 'apps' / app / ('wwwroot' if app == 'backend' else 'public')
    (directory / 'THIRD-PARTY-NOTICES.txt').write_text('\n\n'.join(chunks))
    shutil.copyfile(candidate / 'LICENSE', directory / 'LICENSE.txt')
    shutil.copyfile(candidate / 'THIRD_PARTY.md', directory / 'LICENSE-SCOPE.txt')
print(json.dumps({'packages': len(records), 'notice_files': sum(len(r.get('notices', [])) for r in records), 'missing_license_declarations': [{'ecosystem': r['ecosystem'], 'name': r['name'], 'version': r['version']} for r in records if not r.get('declared_license')]}))
