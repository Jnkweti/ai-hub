# Preference memory — 0.30.0 checklist

- [x] Preferences are written or confirmed by the developer, scoped, versioned on edit, enabled or disabled with a reason.
- [x] Enabled preferences in scope are supplied below the active instructions with a stated lower precedence; input manifests record `pref:<id>:v<n>`.
- [x] Preferences window (new, edit, enable/disable, delete, export) and Make preference from feedback; feedback deletion disables dependent preferences.
- [x] Regression cases added; full suite passes (268). Smoke extended to the preference path.
- [x] Live acceptance: a project-scoped preference changes the reply on the pilot task (and is recorded in the manifest) and does not when scoped to another project.
- [x] Package built, smoke checks passed, installed on October 3, 2026 and reopened as 0.30.0.0.
- [ ] Reserved-task evaluation against runs without preference memory (remains open).
