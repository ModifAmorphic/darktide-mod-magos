# Changelog

## 1.0.0 (2026-07-08)


### Features

* **component-a:** Hybrid Rust+C discovery + shell + launcher ([#1](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/1)) ([491e5d1](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/491e5d18982c1e664086fb9292c5e6f40c5146f9))
* **magos-modificus:** implement Phase 1 Enginseer-client launch façade ([#20](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/20)) ([7950ed1](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/7950ed11ec57cf32a79605828436460f67b26171))
* **magos-modificus:** implement Phase 1 Integrations (GitHub Releases client) ([#19](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/19)) ([781d65c](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/781d65cf90765c7f207276ef66e338ee1239cfac))
* **magos-modificus:** implement Phase 1 Profiles library ([#17](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/17)) ([f355ceb](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/f355ceb5d05da51cc4c382a30e9fd0016510dd9d))
* **magos-modificus:** implement Phase 1 Steam discovery library ([#18](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/18)) ([8f6ec00](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/8f6ec00401b7c52a1c318666717497ee734a40f7))
* **magos-modificus:** implement Phase 2 shared-first mod storage ([#22](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/22)) ([cf2af80](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/cf2af80aaca1265452a56727a5a1015b5e4cfe94))
* **magos-modificus:** Phase 3 Track B mod-list, import, source model ([#29](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/29)) ([5075cee](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/5075cee54372bc019ebc2ca53f2b68280f0d8e07))
* **magos-modificus:** Phase 3 Track C launch + Settings + escape-hatch + base-folder mod loading ([#32](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/32)) ([c595700](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/c595700c6fb7ed000cfbef63ebf04789eec4e3ff))
* **magos-modificus:** Phase 4 Stage 1 nxm scheme handler + IPC ([#34](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/34)) ([0017529](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/0017529cd2fbfea11f04999baba438f55d079345))
* **magos-modificus:** Phase 4 Stage 2 Nexus auth + Integrations dialog ([#35](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/35)) ([0790a8e](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/0790a8ef9a06c79be93c0d347dd18ad965c2d86b))
* **magos-modificus:** Phase 4 Stage 3 Nexus mod acquisition ([#36](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/36)) ([d01105f](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/d01105f21b50df00cb42fe7c376b0d081261dc4f))
* **magos-modificus:** Phase 4 Stage 4 Nexus update-check service ([#39](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/39)) ([1749e76](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/1749e7647db76434d17f97c67e9d27701082da1c))
* **magos-modificus:** Phase 4 Stage 5 mod-list update badges + per-mod update ([#43](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/43)) ([423e146](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/423e1460bc52a8c2775e7f3affe1ff3a19f19304))
* **magos-modificus:** Phase 4 Stage 6 DMF new-profile/auth prompt ([#44](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/44)) ([994b4f8](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/994b4f872fcf7e0c6b41243a402a753fd7f76848))
* **magos-modificus:** scaffold .NET 10 + Avalonia 12 app + libraries ([#16](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/16)) ([d1fac91](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/d1fac9164f869df54cc9bfc07093fc96569d226f))
* **mod-loader:** own the load-order contract (mods.lst), drop DMF prepend ([#14](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/14)) ([1ccb891](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/1ccb89194edc693d95c13fc72f386f62504a48fe))
* **release:** add Curator release pipeline ([#49](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/49)) ([01517e4](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/01517e45cf2708b1fce65b1498cd4dfac985debc))
* **runtime:** engine-context proven — trampoline, Enginseer v1, launcher fail-fast ([#4](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/4)) ([4565ba8](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/4565ba8baed55e8ce73de291f0a04c29e4ec10f1))
* **runtime:** Enginseer v2 — mod loader + launcher config + logging ([#5](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/5)) ([1e65b3f](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/1e65b3ffa22c08874b2de15ed84542e19e2aa493))
* **runtime:** package Enginseer with the runtime; relocate build files to runtime/ ([#6](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/6)) ([7221bdb](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/7221bdb3c3b52ba1165d732e893797fa655cec44))
* **ui:** Phase 3 Track A — app shell + profile management ([#27](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/27)) ([f7f8250](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/f7f825093066a1f0f6fe7237364ceb01bfce4e8d))
* **ui:** Phase 3 Track D — Preferences + i18n + custom title bars + icon ([#28](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/28)) ([c921650](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/c9216507e495f3e51124fc17feef1b98fc1632ab))


### Bug Fixes

* **enginseer:** DMF integration fixes — IO re-root, load timing, destroy ([#7](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/7)) ([401759c](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/401759c45cda57c836a3f0f309334b673222f30c))
* **magos-modificus:** multi-format archive import (zip + 7z + rar) ([#41](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/41)) ([ee4f5c6](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/ee4f5c64fc8cffbcbca343bebae7993f89d5bbab))
* **magos-modificus:** search all Steam libraries for the compatdata prefix ([#21](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/21)) ([895fa2b](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/895fa2b8418d2eda79dba79a90f5a2e19ab0ab37))
* **steam:** detect running Darktide via /proc argv[0] under Proton ([#23](https://github.com/ModifAmorphic/darktide-modificus-curator/issues/23)) ([c5f38c4](https://github.com/ModifAmorphic/darktide-modificus-curator/commit/c5f38c4f9c5b6f79e1b92baa22002530dd674bb4))
