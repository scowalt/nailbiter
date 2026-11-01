# Dependency license evidence

`dependency-inventory.json` records declarations from installed locked npm packages and restored NuGet packages, plus copies of their available LICENSE/COPYING/NOTICE files. It is not a declaration that these packages are MIT, a complete legal opinion, or a claim about every Python/container/CDN component.

After restoring the root/apps Bun dependencies and .NET projects, regenerate with:

```sh
python scripts/licenses/collect.py
```

Review the diff and new dependencies' obligations. The collector reads package metadata/notice files only; it neither installs nor executes dependency code. The resulting browser/runtime notice copies live in `apps/backend/wwwroot/THIRD-PARTY-NOTICES.txt` and `apps/www/public/THIRD-PARTY-NOTICES.txt`, so those notices survive independent builds.

Current evidence includes MIT/Apache/BSD/ISC and other permissive declarations, MPL-2.0 library components, and LGPL-3.0-or-later libvips build-tool binaries. Do not blanket relicense these under the project MIT license. Shipping covered libraries can require more than including notices, including source/modification/relinking obligations depending on the component and distribution. No libvips binary or `node_modules` tree is exported as original source; review any later container/binary packaging separately.

Legacy `xunit.abstractions` 2.0.3 metadata has a license URL rather than a structured license declaration. Supplementary owning-repository evidence declares Apache-2.0; its exact reviewed blob and copied notice are recorded in `supplemental-sources.json`. The null package-declaration field remains an explicit metadata limitation, not an assertion that the test dependency is proprietary or a complete historical/license clearance. Other `license_type=file` entries name their bundled license file rather than an SPDX identifier.

Original project license scope, provider-data boundaries and artwork exclusions are in [THIRD_PARTY.md](../THIRD_PARTY.md). The generated inventory covers the resolved local installation and must be reviewed on dependency/platform updates.
