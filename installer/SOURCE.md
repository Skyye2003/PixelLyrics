# Bundled component sources

These are unmodified upstream sources accompanying the bundled independent components.

| Component | Repository | Commit |
| --- | --- | --- |
| BetterNCM 1.3.4 | https://github.com/std-microblock/chromatic | e89db61f1254554cf0e9dc5866bfd234c7814f15 |
| InfLink-rs 3.4.0 | https://github.com/apoint123/inflink-rs | e4ad4946ce24f63b99082fe8be3f30ca92d4428e |
| libcef submodule (both projects) | https://github.com/BetterNCM/libcef | 615be4a613a63225aade97ba287cb3f343bff797 |
| js-framework submodule | https://github.com/BetterNCM/js-framework | 44b4df2f14c3dbc536731a9247a4fe84eb23860c |
| Embedded PluginMarket 0.8.0 | https://github.com/BetterNCM/Plugin-Market | 05008bd5435a40df8c512e19bc4ab40d47190cce |

Submodules are populated in their original paths. Each project's build files, lockfiles, scripts, notices and license files are preserved. BetterNCM uses Visual Studio/MSBuild, the Windows SDK, Node.js, and vcpkg (upstream workflow pins vcpkg 69efe9cc2df0015f0bb2d37d55acde4a75c9a25b). Run InstallDependencies.bat, build src/js-framework with its package scripts, then build BetterNCMII.sln in Release x64. InfLink-rs provides pnpm build, Rust MSVC targets and its build scripts; see its README. PluginMarket includes its build scripts and dist files. Build dependencies are resolved by the upstream package managers and may require network access.

These source trees have not been independently rebuilt to verify bit-for-bit correspondence with upstream release binaries. Before public redistribution, complete the dependency-source and embedded-resource correspondence audit; this local installer is a validation candidate. Microsoft VC++ runtime is distributed as Microsoft's unmodified signed installer and is governed by its included Microsoft terms.
