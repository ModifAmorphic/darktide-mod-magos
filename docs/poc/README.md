# POC Handoff — frozen historical record

Proof-of-concept docs from the team that validated the Lua VM Injection
approach. Read as a **handoff** — "here's what we did, learned, and
recommend" — not as a spec to execute or a prescription to adopt code.

- `production-summary.md`, `production-spec.md` — summarized findings
  and the POC team's recommended next steps.
- `lua-vm-injection-theory.md`, `-anchors.md`, `-poc.md`,
  `-poc-results.md`, `poc-postmortem.md` — full POC record.
- `DEPLOYMENT_OPTIONS_SURVEY.md` — why Lua VM Injection was chosen.

Production decisions are made fresh; see `../architecture/` and
`../decisions/`. The validated game-binary facts in these docs (LuaJIT
addresses/struct layouts, sandboxed `_G` behavior, timing) remain valid
**constraints** — properties of `Darktide.exe`, not of POC code.

Do not edit these files. They are the record of what that team produced.
